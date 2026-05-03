using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenQAServer.Infrastructure.Chats;
using GenQAServer.Infrastructure.Factories;
using GenQAServer.Options;
using MarkdownGenQAs.Helper;
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
        return new ChatOptions()
        {
            Temperature = (float?)model.Temperature,
            MaxOutputTokens = model.MaxTokens,
            ModelId = model.ModelName
        };
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChatCompletionAsync error on attempt {Attempt}", retryCount + 1);
            }

            if (++retryCount > maxRetry) throw new InvalidOperationException("Failed to get response after multiple retries.");
        }
    }

    public async Task<string> ChatChoiceAsync(List<ChatMessage> request, List<string> choices, CancellationToken cancellationToken = default)
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChatChoiceAsync attempt {Attempt} failed: {Message}", retryCount + 1, ex.Message);
                if (retryCount >= maxRetry) throw;
            }

            if (retryCount >= maxRetry) throw new InvalidOperationException("API response did not contain any valid choice.");
            retryCount++;
        }
    }

    public async Task<TModel> ChatGenQAsAsync<TModel>(List<ChatMessage> messagesRequest, CancellationToken cancellationToken = default) where TModel : class
    {
        var (client, modelItem, providerName) = ResolveClient(isChoice: false);
        _logger.LogInformation("Starting ChatGenQAsAsync");

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
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogError(ex, "ChatGenQAsAsync error on attempt {Attempt}", retryCount + 1);
            }

            if (++retryCount > maxRetry) throw new InvalidOperationException("Failed to get valid structured response after multiple retries.");
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
}
