using System.Text.Encodings.Web;
using System.Text.Json;
using GenQAServer.Options;
using Markdig;
using Markdig.Syntax;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Helper;
using MarkdownGenQAs.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace MarkdownGenQAs.Controllers;

[ApiController]
[Route("api/v1/test/table-continuation")]
public class TestTableContinuationController : ControllerBase
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IUnitOfWork _uow;
    private readonly LlmService _llmService;
    private readonly SystemPrompts _systemPrompts;
    private readonly ILogger<TestTableContinuationController> _logger;

    public TestTableContinuationController(
        IUnitOfWork uow,
        LlmService llmService,
        SystemPrompts systemPrompts,
        ILogger<TestTableContinuationController> logger)
    {
        _uow = uow;
        _llmService = llmService;
        _systemPrompts = systemPrompts;
        _logger = logger;
    }

    [HttpGet("{documentId}")]
    public async Task<ActionResult<TableContinuationTestResult>> Run(Guid documentId, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation("=== TABLE CONTINUATION TEST START: DocumentId={DocumentId} ===", documentId);

        var document = await _uow.Documents.GetByIdAsync(documentId);
        if (document == null)
            return NotFound(new { error = "Document not found" });

        if (string.IsNullOrWhiteSpace(document.OcrContent))
            return BadRequest(new { error = "Document has no OCR content" });

        var source = document.OcrContent!;
        _logger.LogInformation("Document loaded: '{FileName}', OCR content length={Length}", document.FileName, source.Length);

        // ── 1. Parse blocks ──────────────────────────────────────────────────
        _logger.LogInformation("Parsing markdown blocks...");
        var blocks = MarkdownServiceHelper.GetAllBlock(source, Pipeline);
        var tables = blocks
            .Select((block, i) => (block, index: i))
            .Where(x => MarkdownServiceHelper.IsTableBlock(x.block))
            .Select(x =>
            {
                var info = MarkdownServiceHelper.ExtractTableInfo(x.block, source);
                return (x.block, info, x.index);
            })
            .ToList();

        _logger.LogInformation("Found {TableCount} table(s) in document", tables.Count);

        if (tables.Count == 0)
        {
            _logger.LogInformation("No tables found, returning empty result");
            var empty = new TableContinuationTestResult(
                documentId, document.FileName, 0, 0, 0, new(), new(), new());
            return Ok(empty);
        }

        // ── 2. Build table debug info ────────────────────────────────────────
        var tableDebugList = tables.Select(t => new TableDebugInfo(
            Index: t.index,
            HasHeader: t.info.HasHeader,
            ColumnCount: t.info.ColumnCount,
            HeaderCells: t.info.HeaderCells,
            RowCount: t.info.RowCount,
            ContentPreview: Truncate(MarkdownServiceHelper.GetBlockText(source, t.block), 150)
        )).ToList();

        foreach (var td in tableDebugList)
        {
            _logger.LogInformation(
                "  Table[blockIndex={Index}]: hasHeader={HasHeader}, {ColCount} cols, {RowCount} rows, headers=[{Headers}]",
                td.Index, td.HasHeader, td.ColumnCount, td.RowCount,
                td.HeaderCells != null ? string.Join(" | ", td.HeaderCells) : "(none)");
        }

        // ── 3. Run merging loop ──────────────────────────────────────────────
        var segments = new List<List<(Block block, MarkdownServiceHelper.TableContinuationInfo info, int index)>>();
        var decisions = new List<DecisionDebugInfo>();
        int aiCalls = 0;
        int heuristicDecisions = 0;

        var currentSegment = new List<(Block block, MarkdownServiceHelper.TableContinuationInfo info, int index)>();

        _logger.LogInformation("Starting merging loop over {Count} tables...", tables.Count);

        for (int ti = 0; ti < tables.Count; ti++)
        {
            var (block, info, idx) = tables[ti];

            if (currentSegment.Count == 0)
            {
                currentSegment.Add((block, info, idx));
                _logger.LogInformation("[Segment start] Table blockIndex={Idx} added as first segment", idx);
                continue;
            }

            _logger.LogInformation(
                "[Decision #{Ti}/{Total}] Comparing candidate table blockIndex={Idx} (hasHeader={HasHeader}, {ColCount} cols) vs segment of {SegCount} table(s)",
                ti + 1, tables.Count, idx, info.HasHeader, info.ColumnCount, currentSegment.Count);

            // Filter header-only tables in current segment
            var headerRefs = currentSegment
                .Select((s, si) => (s.info, s.index, si))
                .Where(x => x.info.HasHeader)
                .ToList();

            _logger.LogInformation("  Header references in segment: {Count} table(s)", headerRefs.Count);

            bool isContinuation;
            string method;
            double minScore = 0;
            double maxScore = 0;
            var pairScores = new List<PairScore>();

            if (headerRefs.Count == 0)
            {
                // No header in segment → AI fallback
                _logger.LogInformation("  -> No header references, falling back to AI...");
                var choice = await CallAiFallbackAsync(source, currentSegment, block, idx, ct);
                isContinuation = choice;
                method = choice ? "AI (no header ref, yes)" : "AI (no header ref, no)";
                aiCalls++;
                _logger.LogInformation("  -> AI result: {Result} (total AI calls: {AiCalls})", choice, aiCalls);
            }
            else
            {
                // Min/max heuristic
                var scores = new List<double>();
                foreach (var (hInfo, hIdx, _) in headerRefs)
                {
                    double score = MarkdownServiceHelper.CalculateSimilarity(hInfo, info);
                    scores.Add(score);
                    pairScores.Add(new PairScore(hIdx, Math.Round(score, 4)));
                    _logger.LogInformation("  Pair (header table idx={HIdx}) similarity: {Score:F4}", hIdx, score);
                }

                minScore = scores.Min();
                maxScore = scores.Max();

                _logger.LogInformation("  Range: min={Min:F4}, max={Max:F4}", minScore, maxScore);

                if (minScore >= 0.70)
                {
                    isContinuation = true;
                    method = "Heuristic (min ≥ 0.70)";
                    heuristicDecisions++;
                    _logger.LogInformation("  -> Heuristic: CONTINUE (min={Val:F4} >= 0.70)", minScore);
                }
                else if (maxScore <= 0.25)
                {
                    isContinuation = false;
                    method = "Heuristic (max ≤ 0.25)";
                    heuristicDecisions++;
                    _logger.LogInformation("  -> Heuristic: NOT CONTINUE (max={Val:F4} <= 0.25)", maxScore);
                }
                else
                {
                    // Grey zone → AI fallback
                    _logger.LogInformation("  -> Grey zone (min={Min:F4}, max={Max:F4}), falling back to AI...", minScore, maxScore);
                    var choice = await CallAiFallbackAsync(source, currentSegment, block, idx, ct);
                    isContinuation = choice;
                    method = $"AI (grey zone min={minScore:F4}, max={maxScore:F4})" + (choice ? " - yes" : " - no");
                    aiCalls++;
                    _logger.LogInformation("  -> AI result: {Result} (total AI calls: {AiCalls})", choice, aiCalls);
                }
            }

            decisions.Add(new DecisionDebugInfo(
                CandidateTableIndex: idx,
                SegmentTableIndices: currentSegment.Select(s => s.index).ToList(),
                HeaderTableIndices: headerRefs.Select(h => h.index).ToList(),
                PairScores: pairScores,
                MinScore: Math.Round(minScore, 4),
                MaxScore: Math.Round(maxScore, 4),
                Method: method,
                IsContinuation: isContinuation
            ));

            if (isContinuation)
            {
                currentSegment.Add((block, info, idx));
                _logger.LogInformation("  => MERGED into current segment (now {Count} tables)", currentSegment.Count);
            }
            else
            {
                segments.Add(currentSegment);
                _logger.LogInformation("  => FINALIZED segment with {Count} tables, starting new segment", currentSegment.Count);
                currentSegment = new List<(Block block, MarkdownServiceHelper.TableContinuationInfo info, int index)>
                {
                    (block, info, idx)
                };
            }
        }

        if (currentSegment.Count > 0)
            segments.Add(currentSegment);

        // ── 4. Build chunk result ────────────────────────────────────────────
        var resultChunks = segments.Select((seg, ci) =>
        {
            var firstBlock = seg[0].block;
            var lastBlock = seg[^1].block;
            var content = source.Substring(firstBlock.Span.Start,
                lastBlock.Span.End - firstBlock.Span.Start + 1);
            return new ChunkDebugInfo(
                ChunkIndex: ci + 1,
                TableIndices: seg.Select(s => s.index).ToList(),
                ContentLength: content.Length,
                ContentPreview: Truncate(content, 200)
            );
        }).ToList();

        // ── 5. Build result and save to file ─────────────────────────────────
        sw.Stop();
        var result = new TableContinuationTestResult(
            DocumentId: documentId,
            FileName: document.FileName,
            TotalTables: tables.Count,
            TotalAICalls: aiCalls,
            TotalHeuristicDecisions: heuristicDecisions,
            Tables: tableDebugList,
            Decisions: decisions,
            ResultChunks: resultChunks
        );

        _logger.LogInformation(
            "=== DONE: {Tables} tables → {Chunks} chunks | Heuristic={H}, AI={Ai} | Elapsed={Elapsed}s ===",
            tables.Count, resultChunks.Count, heuristicDecisions, aiCalls, sw.Elapsed.TotalSeconds.ToString("F2"));

        await SaveResultToFileAsync(documentId, result);

        var jsonResult = JsonSerializer.Serialize(result, JsonOptions);
        return Content(jsonResult, "application/json", System.Text.Encoding.UTF8);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private async Task<bool> CallAiFallbackAsync(
        string source,
        List<(Block block, MarkdownServiceHelper.TableContinuationInfo info, int index)> segment,
        Block candidate,
        int candidateIndex,
        CancellationToken ct)
    {
        var firstBlock = segment[0].block;
        var t1 = source.Substring(firstBlock.Span.Start,
            candidate.Span.End - firstBlock.Span.Start + 1);
        var t2 = MarkdownServiceHelper.GetBlockText(source, candidate);

        _logger.LogInformation(
            "    [AI Call] Segment [idx {SegFirst}..{SegLast}] vs candidate idx={CandIdx}, context={T1Len}ch, target={T2Len}ch",
            segment[0].index, segment[^1].index, candidateIndex, t1.Length, t2.Length);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chatMessages = LlmChatHelper.CreateChatMessageChoice(t1, t2, _systemPrompts.Choice);
        var choice = await _llmService.ChatChoiceAsync(chatMessages, new() { "Yes", "No" }, ct);
        sw.Stop();

        bool result = !string.IsNullOrEmpty(choice) && choice.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation(
            "    [AI Call] Done: choice='{Choice}', parsed={Result}, elapsed={Elapsed:F2}s",
            choice ?? "(null)", result, sw.Elapsed.TotalSeconds);

        return result;
    }

    private async Task SaveResultToFileAsync(Guid documentId, TableContinuationTestResult result)
    {
        try
        {
            var logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Directory.CreateDirectory(logDir);
            var fileName = $"table-test-{documentId:N}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var filePath = Path.Combine(logDir, fileName);

            var json = JsonSerializer.Serialize(result, JsonOptions);
            await System.IO.File.WriteAllTextAsync(filePath, json);

            _logger.LogInformation("Saved table continuation test result to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save table continuation test result to file");
        }
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";
}

// ═══════════════════════════════════════════════════════════════════════════
//  DTOs for test output
// ═══════════════════════════════════════════════════════════════════════════

public record TableDebugInfo(
    int Index,
    bool HasHeader,
    int ColumnCount,
    List<string>? HeaderCells,
    int RowCount,
    string ContentPreview
);

public record PairScore(
    int ReferenceTableIndex,
    double Score
);

public record DecisionDebugInfo(
    int CandidateTableIndex,
    List<int> SegmentTableIndices,
    List<int> HeaderTableIndices,
    List<PairScore> PairScores,
    double MinScore,
    double MaxScore,
    string Method,
    bool IsContinuation
);

public record ChunkDebugInfo(
    int ChunkIndex,
    List<int> TableIndices,
    int ContentLength,
    string ContentPreview
);

public record TableContinuationTestResult(
    Guid DocumentId,
    string FileName,
    int TotalTables,
    int TotalAICalls,
    int TotalHeuristicDecisions,
    List<TableDebugInfo> Tables,
    List<DecisionDebugInfo> Decisions,
    List<ChunkDebugInfo> ResultChunks
);
