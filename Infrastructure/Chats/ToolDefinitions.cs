using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using Microsoft.Extensions.AI;
using MarkdownGenQAs.Helper;

namespace GenQAServer.Infrastructure.Chats;

public enum YesNoChoice
{
    Yes,
    No
}

public static class ToolDefinitions
{
    public static AIFunction GetSubmitMetadataTool() =>
        AIFunctionFactory.Create(
            ([Description("The extracted metadata as a JSON string conforming to the schema described in the prompt")] string metadataJson) => { },
            "SubmitMetadata",
            "Submits the extracted metadata.");

    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static object? ReflectValue(object? val, HashSet<object> visited, int depth)
    {
        if (val == null || val is string || val.GetType().IsPrimitive || val is decimal || val is DateTime || val is DateTimeOffset) return val;
        if (depth > 3 || !visited.Add(val)) return null;

        var type = val.GetType();
        if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var list = new List<object?>();
            foreach (var item in (System.Collections.IEnumerable)val)
            {
                list.Add(ReflectValue(item, visited, depth + 1));
            }
            return list;
        }

        var dict = new Dictionary<string, object?>();
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            try { dict[prop.Name] = ReflectValue(prop.GetValue(val), visited, depth + 1); } catch { }
        }
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
        {
            try { dict["<field>" + field.Name] = ReflectValue(field.GetValue(val), visited, depth + 1); } catch { }
        }
        return dict;
    }

    private static void LogRawRepresentation(ILogger logger, string method, string prefix, object? raw)
    {
        if (raw == null) return;
        try
        {
            var dict = ReflectValue(raw, new HashSet<object>(), 0);
            var json = JsonSerializer.Serialize(dict, LogJsonOptions);
            if (json.Length > 64000)
            {
                logger.LogDebug("[{Method}] {Prefix}RawRepresentation (reflection, {Len} chars): {Json}",
                    method, prefix, json.Length, json[..64000]);
            }
            else
            {
                logger.LogDebug("[{Method}] {Prefix}RawRepresentation (reflection): {Json}", method, prefix, json);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("[{Method}] {Prefix}RawRepresentation (reflection error): {Msg}", method, prefix, ex.Message);
            try
            {
                var str = raw.ToString();
                if (!string.IsNullOrEmpty(str))
                    logger.LogDebug("[{Method}] {Prefix}RawRepresentation (ToString): {Str}", method, prefix, str);
            }
            catch { }
        }
    }
    public static AIFunction GetSubmitChoiceTool() =>
        AIFunctionFactory.Create(
            ([Description("The selected choice: Yes or No")] YesNoChoice choice) => { },
            "SubmitChoice",
            "Submits the selected choice (Yes or No).");

    public static AIFunction GetSubmitDataTool<TModel>() where TModel : class =>
        AIFunctionFactory.Create(
            ([Description("The extracted data object")] TModel data) => { },
            "SubmitData",
            "Submits the extracted data.");

    public static AIFunction GetSubmitSummaryTool() =>
        AIFunctionFactory.Create(
            ([Description("The generated summary text")] string summary) => { },
            "SubmitSummary",
            "Submits the generated summary.");

    private static void LogResponse(ILogger? logger, Microsoft.Extensions.AI.ChatResponse response, string method)
    {
        if (logger == null) return;

        logger.LogDebug("[{Method}] === {Method} ===", method, method);
        logger.LogDebug("[{Method}]   ModelId: {ModelId}, FinishReason: {FinishReason}",
            method, response.ModelId ?? "(null)", response.FinishReason?.ToString() ?? "(null)");

        if (response.Usage != null)
        {
            logger.LogDebug("[{Method}]   Usage: Input={Input}, Output={Output}, Total={Total}",
                method, response.Usage.InputTokenCount, response.Usage.OutputTokenCount, response.Usage.TotalTokenCount);
        }

        for (int m = 0; m < response.Messages.Count; m++)
        {
            var msg = response.Messages[m];
            logger.LogDebug("[{Method}]   Message[{MsgIdx}] Role: {Role}, Text: {Text}",
                method, m, msg.Role, msg.Text ?? "(null)");

            if (msg.Contents != null)
            {
                for (int i = 0; i < msg.Contents.Count; i++)
                {
                    var c = msg.Contents[i];
                    switch (c)
                    {
                        case TextReasoningContent trc:
                            logger.LogDebug("[{Method}]     Content[{Idx}] **REASONING**: {Text}", method, i, trc.Text ?? "(null)");
                            break;
                        case TextContent tc:
                            logger.LogDebug("[{Method}]     Content[{Idx}] TEXT: {Text}", method, i, tc.Text ?? "(null)");
                            break;
                        case FunctionCallContent fcc:
                            var argsJson = fcc.Arguments != null ? JsonSerializer.Serialize(fcc.Arguments, LogJsonOptions) : "";
                            logger.LogDebug("[{Method}]     Content[{Idx}] FUNC_CALL: {Name}({Args})", method, i, fcc.Name, argsJson);
                            break;
                        case FunctionResultContent frc:
                            logger.LogDebug("[{Method}]     Content[{Idx}] FUNC_RESULT: {Result}", method, i, frc.Result);
                            break;
                        default:
                            try
                            {
                                var json = JsonSerializer.Serialize(c, c.GetType(), LogJsonOptions);
                                logger.LogDebug("[{Method}]     Content[{Idx}] {Type}: {Json}", method, i, c.GetType().Name, json);
                            }
                            catch
                            {
                                logger.LogDebug("[{Method}]     Content[{Idx}] {Type}: (serialization failed)", method, i, c.GetType().Name);
                            }
                            break;
                    }
                }
            }
        }

        LogRawRepresentation(logger, method, "  RAW: ", response.RawRepresentation);
    }

    public static string? ParseChoiceFromResponse(
        Microsoft.Extensions.AI.ChatResponse response,
        Func<string, string?> fallbackExtract,
        ILogger? logger = null)
    {
        LogResponse(logger, response, nameof(ParseChoiceFromResponse));

        var toolCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault(c => c.Name == "SubmitChoice");
        if (toolCall != null && toolCall.Arguments != null)
        {
            if (toolCall.Arguments.TryGetValue("choice", out var choiceObj) && choiceObj != null)
            {
                // logger?.LogInformation("[ParseChoiceFromResponse] Parsed choice from FunctionCallContent: {Choice}", choiceObj);
                return choiceObj.ToString();
            }
        }

        logger?.LogWarning("[ParseChoiceFromResponse] No FunctionCallContent found, falling back to text parse");
        var cleanedResponse = response.Messages[0].Text ?? "";
        return fallbackExtract(cleanedResponse);
    }

    public static YesNoChoice? ParseChoiceFromToolCall(Microsoft.Extensions.AI.ChatResponse response)
    {
        var toolCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault(c => c.Name == "SubmitChoice");
        if (toolCall != null && toolCall.Arguments != null)
        {
            if (toolCall.Arguments.TryGetValue("choice", out var choiceObj) && choiceObj != null)
            {
                var choiceStr = choiceObj.ToString();
                if (Enum.TryParse<YesNoChoice>(choiceStr, ignoreCase: true, out var result))
                {
                    return result;
                }
            }
        }
        return null;
    }

    public static TModel? ParseQaFromResponse<TModel>(
        Microsoft.Extensions.AI.ChatResponse response,
        JsonSerializerOptions jsonOptions,
        ILogger? logger = null) where TModel : class
    {
        LogResponse(logger, response, nameof(ParseQaFromResponse));
        // string reasingContent = response.Messages[0].Contents.OfType<TextReasoningContent>().FirstOrDefault()?.Text ?? ""; // <reasoning>
        
        // logger?.LogDebug("[ParseQaFromResponse] ReasingContent: {ReasingContent}", reasingContent);
        
        var toolCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault(c => c.Name == "SubmitData");
        if (toolCall != null && toolCall.Arguments != null)
        {
            if (toolCall.Arguments.TryGetValue("data", out var dataObj) && dataObj != null)
            {
                var dataJson = JsonSerializer.Serialize(dataObj, LogJsonOptions);
                try
                {
                    return JsonSerializer.Deserialize<TModel>(dataJson, jsonOptions);
                }
                catch (JsonException ex)
                {
                    logger?.LogWarning("[ParseQaFromResponse] JSON deserialization failed, returning default: {Message}. Data: {Data}", ex.Message, dataJson);
                    try
                    {
                        return Activator.CreateInstance<TModel>();
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }

        logger?.LogWarning("[ParseQaFromResponse] No FunctionCallContent found, returning null");
        return null;
    }

    public static TModel? ParseDataFromTextFallback<TModel>(
        string rawText,
        JsonSerializerOptions jsonOptions,
        MarkdownPipeline? pipeline = null,
        ILogger? logger = null) where TModel : class
    {
        var cleanText = rawText.Replace("<|return|>", "").Trim();
        cleanText = LlmChatHelper.EscapeNewlinesInsideJsonStrings(cleanText);
        cleanText = LlmChatHelper.CleanJsonWithWindowsPath(cleanText);

        try
        {
            return JsonSerializer.Deserialize<TModel>(cleanText, jsonOptions);
        }
        catch (JsonException)
        {
            if (pipeline != null)
            {
                FencedCodeBlock? fencedCodeBlock = null;
                foreach (var block in MarkdownServiceHelper.GetAllBlock(cleanText, pipeline))
                {
                    if (block is FencedCodeBlock fenced) { fencedCodeBlock = fenced; break; }
                }

                if (fencedCodeBlock != null)
                {
                    cleanText = string.Join("\n", fencedCodeBlock.Lines);
                    return JsonSerializer.Deserialize<TModel>(cleanText, jsonOptions);
                }
            }
            throw;
        }
    }

    public static string? ParseMetadataFromResponse(
        Microsoft.Extensions.AI.ChatResponse response,
        ILogger? logger = null)
    {
        LogResponse(logger, response, nameof(ParseMetadataFromResponse));

        var toolCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault(c => c.Name == "SubmitMetadata");
        if (toolCall != null && toolCall.Arguments != null)
        {
            if (toolCall.Arguments.TryGetValue("metadataJson", out var metadataObj) && metadataObj != null)
            {
                logger?.LogDebug("[ParseMetadataFromResponse] Parsed metadata from tool call");
                return metadataObj.ToString();
            }
        }

        logger?.LogWarning("[ParseMetadataFromResponse] No FunctionCallContent found, falling back to text");
        return response.Messages[0].Text;
    }

    public static string? ParseSummaryFromResponse(
        Microsoft.Extensions.AI.ChatResponse response,
        Func<string, string?>? fallbackTransform = null,
        ILogger? logger = null)
    {
        LogResponse(logger, response, nameof(ParseSummaryFromResponse));

        var toolCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault(c => c.Name == "SubmitSummary");
        if (toolCall != null && toolCall.Arguments != null)
        {
            if (toolCall.Arguments.TryGetValue("summary", out var summaryObj) && summaryObj != null)
            {
                // logger?.LogDebug("[ParseSummaryFromResponse] Parsed summary from FunctionCallContent: {Summary}", summaryObj);
                return summaryObj.ToString();
            }
        }

        logger?.LogWarning("[ParseSummaryFromResponse] No FunctionCallContent found, falling back to text transform");
        return fallbackTransform?.Invoke(response.Messages[0].Text ?? "");
    }
}