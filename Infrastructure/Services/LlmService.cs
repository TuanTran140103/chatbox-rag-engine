using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenQAServer.Infrastructure.Chats;
using GenQAServer.Infrastructure.Factories;
using GenQAServer.Options;
using MarkdownGenQAs.Helper;
using MarkdownGenQAs.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Utils;

namespace MarkdownGenQAs.Infrastructure.Services;

public class LlmService
{
    private readonly LlmClientFactory _llmClientFactory;
    private readonly DocumentProcessOption _documentProcessOption;
    private readonly LlmProviderOptions _llmProviderOptions;
    private readonly ILogger<LlmService> _logger;
    private readonly string _baseDir = AppContext.BaseDirectory;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    private static SemaphoreSlim GetOrCreateSemaphore(string key, int maxConcurrency)
    {
        return _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(maxConcurrency, maxConcurrency));
    }

    public LlmService(
        LlmClientFactory llmClientFactory,
        IOptions<DocumentProcessOption> documentProcessOption,
        IOptions<LlmProviderOptions> llmProviderOptions,
        ILogger<LlmService> logger)
    {
        _llmClientFactory = llmClientFactory;
        _documentProcessOption = documentProcessOption.Value;
        _llmProviderOptions = llmProviderOptions.Value;
        _logger = logger;
    }

    private JsonSerializerOptions GetJsonSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
    }

    private ChatOptions MapToOptions(LlmProviderItem model)
    {
        var options = new ChatOptions()
        {
            Temperature = (float?)model.Temperature,
            MaxOutputTokens = model.MaxTokens,
            ModelId = model.ModelName
        };

        if (model.HaveThinking)
        {
            options.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["chat_template_kwargs"] = BinaryData.FromObjectAsJson(
                    new { enable_thinking = true })
            };

#pragma warning disable SCME0001
            options.RawRepresentationFactory = (_) =>
            {
                var chatOptions = new OpenAI.Chat.ChatCompletionOptions();
                chatOptions.Patch.Set(
                    "$.chat_template_kwargs"u8,
                    """{"enable_thinking":true}"""u8);
                return chatOptions;
            };
#pragma warning restore SCME0001
        }

        return options;
    }

    private (IChatClient client, LlmProviderItem modelItem, string providerName) ResolveClient(bool isChoice)
    {
        string providerModel = isChoice
            ? _documentProcessOption.SelectionModel
            : _documentProcessOption.ExtractionModel;

        (string providerName, string modelName) = LlmProviderUtil.ParseProviderModel(providerModel);

        var llmProviderItem = LlmProviderUtil.GetLlmProviderItem(_llmProviderOptions, providerName, modelName)
            ?? throw new InvalidOperationException(
                $"Provider '{providerName}' / Model '{modelName}' is missing in appsettings.json");

        var client = _llmClientFactory.GetClient(providerName, modelName);
        return (client, llmProviderItem, providerName);
    }

    public string CleanResponse(string response, bool haveThinking = false)
    {
        if (string.IsNullOrEmpty(response))
        {
            return string.Empty;
        }
        if (haveThinking)
        {
            response = Regex.Replace(response, @"<think>[\s\S]*?", "", RegexOptions.IgnoreCase);
        }
        return response.Replace("<|return|>", "").Trim();
    }

    public async Task<string> ChatCompletionAsync(List<ChatMessage> messagesRequest, CancellationToken cancellationToken = default)
    {
        var (client, modelItem, providerName) = ResolveClient(isChoice: false);
        _logger.LogInformation("Starting ChatCompletionAsync");

        var semaphore = GetOrCreateSemaphore($"{providerName}__{modelItem.ModelName}", modelItem.MaxConcurrency);

        const int maxRetry = 3;
        int retryCount = 0;

        while (true)
        {
            try
            {
                var options = MapToOptions(modelItem);
                options.MaxOutputTokens = modelItem.MaxTokens;
                options.Tools = [ToolDefinitions.GetSubmitSummaryTool()];

                ChatResponse? res;
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    res = await client.GetResponseAsync(messagesRequest, options, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }

                var summary = ToolDefinitions.ParseSummaryFromResponse(
                    res,
                    text => CleanResponse(text ?? "", modelItem.HaveThinking),
                    _logger);

                if (!string.IsNullOrEmpty(summary)) return summary;

                _logger.LogWarning("ChatCompletionAsync attempt {Attempt}: No summary tool call. Adding feedback.", retryCount + 1);
                messagesRequest.Add(new ChatMessage(ChatRole.Assistant, res.Messages[0].Text ?? "No text response."));
                messagesRequest.Add(new ChatMessage(ChatRole.User, "Error: You must use the 'SubmitSummary' tool to provide the summary. Please try again using the tool."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "ChatCompletionAsync timeout/error - failing immediately");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChatCompletionAsync error on attempt {Attempt}", retryCount + 1);
            }

            if (++retryCount > maxRetry) throw new InvalidOperationException("Failed to get response after multiple retries.");
        }
    }

    public async Task<string> ChatChoiceAsync(List<ChatMessage> request, List<string> choices, CancellationToken cancellationToken = default, Action? onStarted = null)
    {
        if (choices is null || choices.Count == 0)
        {
            throw new ArgumentException("Choices cannot be null or empty", nameof(choices));
        }

        var (client, modelItem, providerName) = ResolveClient(isChoice: true);
        var semaphore = GetOrCreateSemaphore($"{providerName}__{modelItem.ModelName}", modelItem.MaxConcurrency);
        const int maxRetry = 3;
        int retryCount = 0;

        while (true)
        {
            try
            {
                ChatOptions options = MapToOptions(modelItem);
                options.Tools = [ToolDefinitions.GetSubmitChoiceTool()];

                ChatResponse res;
                await semaphore.WaitAsync(cancellationToken);
                onStarted?.Invoke();
                try
                {
                    res = await client.GetResponseAsync(request, options, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }

                var stringChoice = ToolDefinitions.ParseChoiceFromResponse(
                    res,
                    text => LlmChatHelper.ExtractChoiceFromResponse(CleanResponse(text, modelItem.HaveThinking), choices),
                    _logger);

                if (!string.IsNullOrEmpty(stringChoice))
                {
                    _logger.LogInformation("ChatChoiceAsync completed with choice: {Choice}", stringChoice);
                    return stringChoice;
                }

                _logger.LogWarning("ChatChoiceAsync attempt {Attempt}/{MaxRetry}: no valid choice found. Adding feedback to AI.", retryCount + 1, maxRetry + 1);
                request.Add(new ChatMessage(ChatRole.Assistant, res.Messages[0].Text ?? "No text response."));
                request.Add(new ChatMessage(ChatRole.User, "Error: You must use the 'SubmitChoice' tool to provide your answer. Please try again using the tool."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                _logger.LogWarning("ChatChoiceAsync rate limited (429) on attempt {Attempt}. Waiting {Seconds}", retryCount + 1, 15 * (retryCount + 1));
                await Task.Delay(TimeSpan.FromSeconds(15 * (retryCount + 1)), cancellationToken);
            }
            catch (ClientResultException ex) when (ex.Status >= 500)
            {
                var raw = ex.GetRawResponse()?.Content?.ToString();
                _logger.LogError(ex, "ChatChoiceAsync HTTP {Status} on attempt {Attempt}. Raw: {Raw}", ex.Status, retryCount + 1, raw ?? "N/A");
                await Task.Delay(TimeSpan.FromSeconds(15 * (retryCount + 1)), cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "ChatChoiceAsync timeout/error - failing immediately");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChatChoiceAsync attempt {Attempt} failed: {Message}", retryCount + 1, ex.Message);
                if (retryCount >= maxRetry)
                {
                    _logger.LogWarning("ChatChoiceAsync failed after {MaxRetry} attempts. Returning empty fallback.", maxRetry);
                    return string.Empty;
                }
            }

            if (retryCount >= maxRetry)
            {
                _logger.LogWarning("ChatChoiceAsync exhausted after {MaxRetry} attempts. Returning empty fallback.", maxRetry);
                throw new InvalidOperationException("Failed to get response after multiple retries.");
            }
            retryCount++;
            // if (retryCount > 0)
            //     await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)), cancellationToken);
        }
    }

    public async Task<TModel?> ChatGenQAsAsync<TModel>(List<ChatMessage> messagesRequest, CancellationToken cancellationToken = default, Action? onStarted = null) where TModel : class
    {
        var (client, modelItem, providerName) = ResolveClient(isChoice: false);
        _logger.LogDebug("ChatGenQAsAsync: {Provider}/{Model}", providerName, modelItem.ModelName);

        var semaphore = GetOrCreateSemaphore($"{providerName}__{modelItem.ModelName}", modelItem.MaxConcurrency);

        const int maxRetry = 3;
        int retryCount = 0;

        while (true)
        {
            try
            {
                ChatOptions options = MapToOptions(modelItem);
                options.Tools = [ToolDefinitions.GetSubmitDataTool<TModel>()];

                ChatResponse res;
                await semaphore.WaitAsync(cancellationToken);
                onStarted?.Invoke();
                try
                {
                    res = await client.GetResponseAsync(messagesRequest, options, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }

                var result = ToolDefinitions.ParseQaFromResponse<TModel>(res, GetJsonSerializerOptions(), _logger);
                if (result != null) return result;

                var cleanText = CleanResponse(res.Messages[0].Text ?? "", modelItem.HaveThinking);
                try
                {
                    var fallbackResult = ToolDefinitions.ParseDataFromTextFallback<TModel>(
                        cleanText,
                        GetJsonSerializerOptions(),
                        null,
                        _logger);
                    if (fallbackResult != null) return fallbackResult;
                }
                catch { }

                _logger.LogWarning("ChatGenQAsAsync attempt {Attempt}: No tool call found. Adding feedback.", retryCount + 1);
                messagesRequest.Add(new ChatMessage(ChatRole.Assistant, res.Messages[0].Text ?? "No text response."));
                messagesRequest.Add(new ChatMessage(ChatRole.User, "Error: You must use the 'SubmitData' tool to return the structured JSON data. Please try again using the tool."));
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                _logger.LogWarning("ChatGenQAsAsync rate limited (429) on attempt {Attempt}. Waiting {Seconds}s.", retryCount + 1, 15 * (retryCount + 1));
                await Task.Delay(TimeSpan.FromSeconds(15 * (retryCount + 1)), cancellationToken);
            }
            catch (ClientResultException ex) when (ex.Status >= 500)
            {
                var raw = ex.GetRawResponse()?.Content?.ToString();
                _logger.LogWarning(ex, "ChatGenQAsAsync HTTP {Status} on attempt {Attempt}. Raw: {Raw}", ex.Status, retryCount + 1, raw ?? "N/A");
                // _logger.LogWarning("ChatGenQAsAsync HTTP {Status} on attempt {Attempt}. Waiting {Seconds}s before retry.", ex.Status, retryCount + 1, 15 * (retryCount + 1));
                await Task.Delay(TimeSpan.FromSeconds(15 * (retryCount + 1)), cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is HttpRequestException)
            {
                _logger.LogError(ex, "ChatGenQAsAsync timeout/error - failing immediately");
                throw;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogError(ex, "ChatGenQAsAsync error on attempt {Attempt}", retryCount + 1);
            }

            if (++retryCount > maxRetry)
            {
                _logger.LogWarning("ChatGenQAsAsync failed after {MaxRetry} attempts. Returning null.", maxRetry);
                throw new Exception($"ChatGenQAsAsync failed after {maxRetry} attempts.");
            }
            // await Task.Delay(TimeSpan.FromSeconds(15 * (retryCount+1)), cancellationToken);
        }
    }

    public async Task<string> GenSummaryAsync(string dataSource, string? nameFile, SystemPrompts systemPrompts, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(_baseDir, systemPrompts.GenSummaryDocument.PathTemplatePrompt);
        string templatePrompt = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrEmpty(nameFile))
        {
            nameFile = "SummaryDocument";
        }
        string prompt = string.Format(templatePrompt, nameFile, dataSource);

        List<ChatMessage> messagesRequest = new List<ChatMessage>()
        {
            new ChatMessage(ChatRole.System, systemPrompts.GenSummaryDocument.SystemPrompt),
            new ChatMessage(ChatRole.User, prompt)
        };

        return await ChatCompletionAsync(messagesRequest, cancellationToken);
    }

    public async Task<string> ChatMetadataExtractionAsync(List<ChatMessage> messagesRequest, string? jsonSchema = null, CancellationToken cancellationToken = default)
    {
        var (client, modelItem, providerName) = ResolveClient(isChoice: false);
        _logger.LogInformation("Starting ChatMetadataExtractionAsync");

        var semaphore = GetOrCreateSemaphore($"{providerName}__{modelItem.ModelName}", modelItem.MaxConcurrency);
        const int maxRetry = 3;
        int retryCount = 0;

        while (true)
        {
            try
            {
                var options = MapToOptions(modelItem);
                options.MaxOutputTokens = modelItem.MaxTokens;
                options.Tools = [ToolDefinitions.GetSubmitMetadataTool()];

                ChatResponse? res;
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    res = await client.GetResponseAsync(messagesRequest, options, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }

                var metadata = ToolDefinitions.ParseMetadataFromResponse(res, _logger);

                if (!string.IsNullOrEmpty(metadata))
                {
                    if (jsonSchema == null)
                        return metadata;

                    var (fixedMetadata, patchedSchema) = MetadataSchemaHelper.CoerceRequiredNulls(metadata, jsonSchema);
                    var (isValid, errorMessage) = MetadataSchemaHelper.ValidateJsonAgainstSchema(fixedMetadata, patchedSchema);
                    if (isValid)
                    {
                        if (fixedMetadata != metadata)
                            _logger.LogDebug("ChatMetadataExtractionAsync coerced null/empty values for required fields");
                        return fixedMetadata;
                    }

                    _logger.LogDebug("ChatMetadataExtractionAsync JsonSchema: {Schema}", patchedSchema);  
                    _logger.LogWarning("ChatMetadataExtractionAsync attempt {Attempt}: Invalid JSON schema: {Error}", retryCount + 1, errorMessage);
                    messagesRequest.Add(new ChatMessage(ChatRole.Assistant, $"```json\n{fixedMetadata}\n```"));
                    messagesRequest.Add(new ChatMessage(ChatRole.User, $"Error: The metadata JSON does not conform to the required schema. Errors: {errorMessage}. Please fix these issues and return valid JSON using the SubmitMetadata tool."));
                    continue;
                }

                _logger.LogWarning("ChatMetadataExtractionAsync attempt {Attempt}: No metadata tool call. Adding feedback.", retryCount + 1);
                messagesRequest.Add(new ChatMessage(ChatRole.Assistant, res.Messages[0].Text ?? "No text response."));
                messagesRequest.Add(new ChatMessage(ChatRole.User, "Error: You must use the 'SubmitMetadata' tool to provide the metadata. Please try again using the tool."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "ChatMetadataExtractionAsync timeout/error - failing immediately");
                throw;
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                _logger.LogWarning("ChatMetadataExtractionAsync rate limited (429) on attempt {Attempt}. Waiting 15s.", retryCount + 1);
                await Task.Delay(TimeSpan.FromSeconds(15 * (retryCount + 1)), cancellationToken);
            }
            catch (ClientResultException ex) when (ex.Status >= 500)
            {
                var raw = ex.GetRawResponse()?.Content?.ToString();
                _logger.LogError(ex, "ChatMetadataExtractionAsync HTTP {Status} on attempt {Attempt}. Raw: {Raw}", ex.Status, retryCount + 1, raw ?? "N/A");
                await Task.Delay(TimeSpan.FromSeconds(15 * (retryCount + 1)), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChatMetadataExtractionAsync error on attempt {Attempt}", retryCount + 1);
            }

            if (++retryCount > maxRetry)
            {
                _logger.LogWarning("ChatMetadataExtractionAsync failed after {MaxRetry} attempts. Returning default JSON.", maxRetry);
                return jsonSchema != null
                    ? MetadataSchemaHelper.GenerateDefaultJson(jsonSchema)
                    : string.Empty;
            }
        }
    }
}
