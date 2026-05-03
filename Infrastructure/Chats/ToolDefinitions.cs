using System.ComponentModel;
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

        var msg = response.Messages[0];
        logger.LogInformation("[{Method}] === RAW RESPONSE ===", method);
        logger.LogInformation("[{Method}] Message role: {Role}", method, msg.Role);
        logger.LogInformation("[{Method}] Message text: {Text}", method, msg.Text ?? "(null)");

        if (msg.Contents != null && msg.Contents.Count > 0)
        {
            logger.LogInformation("[{Method}] Contents count: {Count}", method, msg.Contents.Count);
            for (int i = 0; i < msg.Contents.Count; i++)
            {
                var c = msg.Contents[i];
                logger.LogInformation("[{Method}]   Content[{Idx}] Type: {Type}", method, i, c.GetType().Name);

                if (c is FunctionCallContent fcc)
                {
                    logger.LogInformation("[{Method}]   -> FunctionCall: Name={Name}, CallId={CallId}", method, fcc.Name, fcc.CallId);
                    if (fcc.Arguments != null)
                    {
                        var argsJson = JsonSerializer.Serialize(fcc.Arguments);
                        logger.LogInformation("[{Method}]   -> Arguments: {Args}", method, argsJson);
                    }
                }

                if (c is FunctionResultContent frc)
                {
                    logger.LogInformation("[{Method}]   -> FunctionResult: Name={Name}, CallId={CallId}, Result={Result}", method, frc.CallId, frc.CallId, frc.Result);
                }
            }
        }
        else
        {
            logger.LogInformation("[{Method}] Contents is null or empty", method);
        }
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
                logger?.LogInformation("[ParseChoiceFromResponse] Parsed choice from FunctionCallContent: {Choice}", choiceObj);
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

        var toolCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault(c => c.Name == "SubmitData");
        if (toolCall != null && toolCall.Arguments != null)
        {
            if (toolCall.Arguments.TryGetValue("data", out var dataObj) && dataObj != null)
            {
                var dataJson = JsonSerializer.Serialize(dataObj);
                logger?.LogInformation("[ParseQaFromResponse] Parsed data from FunctionCallContent.data: {Json}", dataJson);
                return JsonSerializer.Deserialize<TModel>(dataJson, jsonOptions);
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
                logger?.LogInformation("[ParseSummaryFromResponse] Parsed summary from FunctionCallContent: {Summary}", summaryObj);
                return summaryObj.ToString();
            }
        }

        logger?.LogWarning("[ParseSummaryFromResponse] No FunctionCallContent found, falling back to text transform");
        return fallbackTransform?.Invoke(response.Messages[0].Text ?? "");
    }
}