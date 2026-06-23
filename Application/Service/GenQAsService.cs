using GenQAServer.Options;
using MarkdownGenQAs.Infrastructure.Services;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Models.QA;
using Microsoft.Extensions.AI;

namespace MarkdownGenQAs.Application.Service;

public class GenQAsService
{
    private readonly SystemPrompts _systemPrompts;
    private readonly LlmService _llmService;
    private readonly ILogger<GenQAsService> _logger;
    private readonly string BASE_DIR = AppContext.BaseDirectory;

    public GenQAsService(
        SystemPrompts systemPrompts,
        LlmService llmService,
        ILogger<GenQAsService> logger)
    {
        _systemPrompts = systemPrompts;
        _llmService = llmService;
        _logger = logger;
    }

    public async Task<List<ChunkQA>> GenQAsTextAsync(ChunkInfo chunkInfo, string summaryDocument, string nameFile, CancellationToken cancellationToken = default, Action? onStarted = null)
    {
        string path = Path.Combine(BASE_DIR, _systemPrompts.GenQAsText.PathTemplatePrompt);
        string templatePrompt = await File.ReadAllTextAsync(path, cancellationToken);
        string prompt = string.Format(templatePrompt, nameFile, summaryDocument, chunkInfo.Content);

        List<ChatMessage> messagesRequest = new List<ChatMessage>()
        {
            new ChatMessage(ChatRole.System, _systemPrompts.GenQAsText.SystemPrompt),
            new ChatMessage(ChatRole.User, prompt)
        };

        try
        {
            var documentSummaryPackage = await _llmService.ChatGenQAsAsync<List<ChunkQA>>(messagesRequest, cancellationToken, onStarted);
            return documentSummaryPackage ?? new List<ChunkQA>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "GenQAsTextAsync error for file {FileName}", nameFile);
            return new List<ChunkQA>();
        }
    }

    public async Task<string> GenSummaryDocumentAsync(string dataSource, string? nameFile, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(BASE_DIR, _systemPrompts.GenSummaryDocument.PathTemplatePrompt);
        string templatePromptGenSummary = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrEmpty(nameFile))
        {
            nameFile = "SummaryDocument";
        }
        string prompt = string.Format(templatePromptGenSummary, nameFile, dataSource);

        List<ChatMessage> messagesRequest = new List<ChatMessage>()
        {
            new ChatMessage(ChatRole.System, _systemPrompts.GenSummaryDocument.SystemPrompt),
            new ChatMessage(ChatRole.User, prompt)
        };

        try
        {
            var result = await _llmService.ChatCompletionAsync(messagesRequest, cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "GenSummaryDocumentAsync error for file {FileName}", nameFile);
            return string.Empty;
        }
    }

    public async Task<List<ChunkQA>> GenQAsTableAsync(ChunkInfo chunkInfo, string summaryDocument, string nameFile, CancellationToken cancellationToken = default, Action? onStarted = null)
    {
        try
        {
            string path = Path.Combine(BASE_DIR, _systemPrompts.GenQAsTable.PathTemplatePrompt);
            string templatePrompt = await File.ReadAllTextAsync(path, cancellationToken);
            var prompt = string.Format(templatePrompt, nameFile, summaryDocument, chunkInfo.Title, chunkInfo.TitleHierarchy, chunkInfo.Content);

            List<ChatMessage> messagesRequest = new List<ChatMessage>()
            {
                new ChatMessage(ChatRole.System, _systemPrompts.GenQAsTable.SystemPrompt),
                new ChatMessage(ChatRole.User, prompt)
            };

            return await _llmService.ChatGenQAsAsync<List<ChunkQA>>(messagesRequest, cancellationToken, onStarted) ?? new List<ChunkQA>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "GenQAsTableAsync error for file {FileName}", nameFile);
            return new List<ChunkQA>();
        }
    }

    public async Task<List<ChunkQA>> GenQAsCombinedAsync(ChunkInfo chunkInfo, string summaryDocument, string nameFile, CancellationToken cancellationToken = default, Action? onStarted = null)
    {
        string path = Path.Combine(BASE_DIR, _systemPrompts.GenQAsCombined.PathTemplatePrompt);
        string templatePrompt = await File.ReadAllTextAsync(path, cancellationToken);
        string prompt = string.Format(templatePrompt, nameFile, summaryDocument, chunkInfo.Title, chunkInfo.TitleHierarchy, chunkInfo.Content);

        List<ChatMessage> messagesRequest = new List<ChatMessage>()
        {
            new ChatMessage(ChatRole.System, _systemPrompts.GenQAsCombined.SystemPrompt),
            new ChatMessage(ChatRole.User, prompt)
        };

        try
        {
            var result = await _llmService.ChatGenQAsAsync<List<ChunkQA>>(messagesRequest, cancellationToken, onStarted);
            return result ?? new List<ChunkQA>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch(Exception)
        {
            throw;
        }
    }

    public async Task<string> MergeSummaryChunksAsync(
        List<SummaryChunk> chunks,
        string? nameFile,
        CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(BASE_DIR, _systemPrompts.MergeSummaryDocument.PathTemplatePrompt);
        string templatePrompt = await File.ReadAllTextAsync(path, cancellationToken);

        if (string.IsNullOrEmpty(nameFile)) nameFile = "SummaryDocument";

        var hierarchyLines = new List<string>();
        var summaryLines = new List<string>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var sectionLabel = string.IsNullOrEmpty(chunk.Title) ? $"Section {i + 1}" : chunk.Title;
            hierarchyLines.Add($"- {sectionLabel}");
            if (!string.IsNullOrEmpty(chunk.HierarchyPath))
                hierarchyLines.Add($"  Path: {chunk.HierarchyPath}");

            summaryLines.Add($"### {sectionLabel}");
            if (!string.IsNullOrEmpty(chunk.HierarchyPath))
                summaryLines.Add($"*Hierarchy: {chunk.HierarchyPath}*");
            summaryLines.Add(chunk.Content);
            summaryLines.Add("");
        }

        string hierarchyText = string.Join("\n", hierarchyLines);
        string summariesText = string.Join("\n", summaryLines);
        string prompt = string.Format(templatePrompt, nameFile, hierarchyText, summariesText);

        List<ChatMessage> messagesRequest = new List<ChatMessage>()
        {
            new ChatMessage(ChatRole.System, _systemPrompts.MergeSummaryDocument.SystemPrompt),
            new ChatMessage(ChatRole.User, prompt)
        };

        try
        {
            var result = await _llmService.ChatCompletionAsync(messagesRequest, cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "MergeSummaryChunksAsync error for file {FileName}", nameFile);
                return string.Empty;
        }
    }

}
