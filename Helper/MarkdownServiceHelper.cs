using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Helper;

public class MarkdownServiceHelper
{
    private static readonly Regex PageSplitRegex = new(
        @"(?:\r?\n|\r)Page\s+\d+(?:\r?\n|\r){2}-{3}(?:\r?\n|\r)",
        RegexOptions.Compiled);

    public static List<string> SplitIntoPages(string ocrContent)
    {
        if (string.IsNullOrEmpty(ocrContent))
            return [];

        var parts = PageSplitRegex.Split(ocrContent);
        var pages = parts
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        return pages.Count > 0 ? pages : [ocrContent];
    }




    public static string RemoveAllTables(string source, MarkdownPipeline pipeline)
    {
        var blocks = GetAllBlock(source, pipeline);
        var tableSpans = blocks.Where(b => b is Markdig.Extensions.Tables.Table || (b is HtmlBlock hb && hb.Lines.ToString().TrimStart().StartsWith("<table")))
                               .Select(b => b.Span)
                               .OrderByDescending(s => s.Start);

        var result = source;
        foreach (var span in tableSpans)
        {
            result = result.Remove(span.Start, span.Length);
        }
        return result.Trim();
    }

    public void ShowChunks(List<ChunkInfo> chunks, int maxChar = 100)
    {

        string underline = new string('-', 50);

        foreach (var chunk in chunks)
        {
            Console.WriteLine($"{chunk.Type} - {chunk.TokensCount} tokens");
            Console.WriteLine($"Title Hyrarchy header: {chunk.TitleHierarchy}");
            if (chunk.Content.Length > maxChar)
            {

                Console.WriteLine(chunk.Content[..maxChar]);
                Console.WriteLine("|||||||||");
                Console.WriteLine(chunk.Content[^maxChar..]);
            }
            else
            {
                Console.WriteLine(chunk.Content);

            }

            Console.WriteLine(underline);
        }
    }

    public static List<Block> GetAllBlock(string source, MarkdownPipeline pipeline, bool isAllHeader = false)
    {
        MarkdownDocument document = Markdown.Parse(source, pipeline);

        if (isAllHeader)
        {
            return document.ToList<Block>().Where(b => b is HeadingBlock).ToList();
        }

        return document.ToList<Block>();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Table Continuation Detection — struct & static helpers
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Structured metadata extracted from a table block for continuation comparison.
    /// </summary>
    public sealed record TableContinuationInfo(
        int ColumnCount,
        List<string>? HeaderCells,
        bool HasHeader,
        int RowCount
    );

    /// <summary>
    /// Extract structured info from a table-like block (native Markdig Table or HTML &lt;table&gt;).
    /// </summary>
    public static TableContinuationInfo ExtractTableInfo(Block block, string source)
    {
        if (block is Markdig.Extensions.Tables.Table table)
            return ExtractFromMarkdigTable(table, source);

        if (block is HtmlBlock hb)
        {
            var html = source.Substring(hb.Span.Start, hb.Span.Length);
            return ExtractFromHtmlTable(html);
        }

        return new TableContinuationInfo(0, null, false, 0);
    }

    private static TableContinuationInfo ExtractFromMarkdigTable(Markdig.Extensions.Tables.Table table, string source)
    {
        var rows = table.OfType<Markdig.Extensions.Tables.TableRow>().ToList();
        var headerRow = rows.FirstOrDefault(r => r.IsHeader);

        List<string>? headers = null;
        if (headerRow != null)
        {
            headers = headerRow.OfType<Markdig.Extensions.Tables.TableCell>()
                .Select(c => source.Substring(c.Span.Start, c.Span.Length).Trim())
                .ToList();
        }

        int columnCount = headers?.Count
            ?? (table.ColumnDefinitions?.Count ?? 0);

        if (columnCount == 0 && rows.Count > 0)
        {
            columnCount = rows.Max(r => r.OfType<Markdig.Extensions.Tables.TableCell>().Count());
        }

        int dataRowCount = rows.Count(r => !r.IsHeader);

        return new TableContinuationInfo(columnCount, headers, headerRow != null, dataRowCount);
    }

    private static TableContinuationInfo ExtractFromHtmlTable(string html)
    {
        // Strip nested <table>...</table> before regex parsing to avoid false matches from inner tables
        var outerOnly = StripNestedTables(html);

        bool hasHeader = Regex.IsMatch(outerOnly, @"<th[\s>]", RegexOptions.IgnoreCase);
        var rowMatches = Regex.Matches(outerOnly, @"<tr[^>]*>(.*?)</tr>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        List<string>? headers = null;
        int maxColumns = 0;

        foreach (Match rowMatch in rowMatches)
        {
            var rowHtml = rowMatch.Groups[1].Value;
            var cellMatches = Regex.Matches(rowHtml, @"<(?:td|th)[^>]*>(.*?)</(?:td|th)>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var cells = cellMatches.Select(m => m.Groups[1].Value.Trim()).ToList();

            if (hasHeader && headers == null
                && cellMatches.Any(m => m.Value.StartsWith("<th", StringComparison.OrdinalIgnoreCase)))
            {
                headers = cells;
            }

            maxColumns = Math.Max(maxColumns, cells.Count);
        }

        int dataRowCount = hasHeader ? Math.Max(0, rowMatches.Count - 1) : rowMatches.Count;

        return new TableContinuationInfo(maxColumns, headers, hasHeader, dataRowCount);
    }

    /// <summary>
    /// Remove all nested &lt;table&gt;...&lt;/table&gt; content so only the outermost table structure remains.
    /// Uses a depth counter so nested table tags are skipped entirely.
    /// </summary>
    private static string StripNestedTables(string html)
    {
        var sb = new StringBuilder();
        int depth = 0;
        int i = 0;

        while (i < html.Length)
        {
            // Detect opening <table or <TABLE (with any attributes)
            if (i + 6 <= html.Length
                && string.Equals(html.Substring(i, 6), "<table", StringComparison.OrdinalIgnoreCase)
                && (i + 6 >= html.Length || IsTagBoundary(html[i + 6])))
            {
                depth++;
                if (depth == 1)
                {
                    sb.Append("<table>"); // keep a minimal placeholder for outer table
                }
                // skip to end of opening tag
                while (i < html.Length && html[i] != '>') i++;
                i++;
                continue;
            }

            // Detect closing </table>
            if (i + 8 <= html.Length
                && string.Equals(html.Substring(i, 8), "</table>", StringComparison.OrdinalIgnoreCase))
            {
                depth--;
                if (depth == 0)
                {
                    sb.Append("</table>");
                }
                i += 8;
                continue;
            }

            if (depth == 1)
            {
                sb.Append(html[i]);
            }
            i++;
        }

        return sb.ToString();
    }

    private static bool IsTagBoundary(char c)
    {
        return char.IsWhiteSpace(c) || c == '>' || c == '/';
    }

    // ─── Similarity calculation ──────────────────────────────────────────

    /// <summary>
    /// Compute similarity between two table's structural info.
    /// 0.0 = completely different, 1.0 = identical.
    /// </summary>
    public static double CalculateSimilarity(TableContinuationInfo t1, TableContinuationInfo t2)
    {
        double colSim = ColumnSimilarity(t1.ColumnCount, t2.ColumnCount);

        if (t1.HasHeader && t2.HasHeader)
            return CompareHeaders(t1.HeaderCells!, t2.HeaderCells!) * 0.8 + colSim * 0.2;

        if (t1.HasHeader && !t2.HasHeader)
            return 0.7 + colSim * 0.2;

        if (!t1.HasHeader && t2.HasHeader)
            return 0.1 + colSim * 0.2;

        return 0.5 + colSim * 0.4;
    }

    private static double CompareHeaders(List<string> h1, List<string> h2)
    {
        if (h1.Count == 0 || h2.Count == 0) return 0.0;

        int minCols = Math.Min(h1.Count, h2.Count);
        int maxCols = Math.Max(h1.Count, h2.Count);

        double pairwiseAvg = Enumerable.Range(0, minCols)
            .Select(i => CellSimilarity(h1[i], h2[i]))
            .Average();

        double colPenalty = (double)minCols / maxCols;
        return pairwiseAvg * colPenalty;
    }

    private static double CellSimilarity(string c1, string c2)
    {
        var n1 = c1.Trim().ToLowerInvariant();
        var n2 = c2.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(n1) || string.IsNullOrEmpty(n2)) return 0.0;
        if (n1 == n2) return 1.0;
        if (n1.Contains(n2) || n2.Contains(n1)) return 0.8;

        return DiceBigram(n1, n2) >= 0.6 ? 0.6 : 0.0;
    }

    private static double ColumnSimilarity(int c1, int c2)
    {
        if (c1 == c2) return 1.0;
        if (Math.Abs(c1 - c2) == 1) return 0.4;
        return 0.0;
    }

    // ─── Dice bigram coefficient ─────────────────────────────────────────

    /// <summary>
    /// Dice coefficient on character bigrams — tolerant to OCR insertion / deletion noise.
    /// </summary>
    private static double DiceBigram(string a, string b)
    {
        if (a == b) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

        var bigramsA = GetBigrams(a);
        var bigramsB = GetBigrams(b);

        if (bigramsA.Count == 0 || bigramsB.Count == 0) return 0.0;

        int intersection = bigramsA.Intersect(bigramsB).Count();
        return 2.0 * intersection / (bigramsA.Count + bigramsB.Count);
    }

    private static HashSet<string> GetBigrams(string s)
    {
        var result = new HashSet<string>(s.Length - 1);
        for (int i = 0; i < s.Length - 1; i++)
            result.Add(s.Substring(i, 2));
        return result;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Debug helpers for table continuation test
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Check whether a block is any kind of table (native or HTML).
    /// </summary>
    public static bool IsTableBlock(Block block)
    {
        if (block is Markdig.Extensions.Tables.Table) return true;
        if (block is HtmlBlock hb)
        {
            var text = hb.Lines.ToString().TrimStart();
            return text.StartsWith("<table", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// Get raw source text for a block.
    /// </summary>
    public static string GetBlockText(string source, Block block)
    {
        return source.Substring(block.Span.Start, block.Span.Length);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Table block extraction for batch processing
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses source with Markdig, extracts all table blocks with their metadata.
    /// Returns list of (block, info, index) for all tables found.
    /// </summary>
    public static List<(Block block, TableContinuationInfo info, int index)> GetTableBlocks(
        string source, MarkdownPipeline pipeline)
    {
        var blocks = GetAllBlock(source, pipeline);
        return blocks
            .Select((b, i) => (b, i))
            .Where(x => IsTableBlock(x.b))
            .Select(x => (x.b, ExtractTableInfo(x.b, source), x.i))
            .ToList();
    }

    /// <summary>
    /// Duyệt ngược từ table lên, skip các block không phải text có nghĩa
    /// (HtmlBlock, ThematicBreak, List, Code, Table), lấy text từ block
    /// đầu tiên không bị exclude làm title cho table.
    /// </summary>
    public static string GetPrecedingTextForTable(string source, MarkdownPipeline pipeline, int tableBlockStart)
    {
        if (tableBlockStart <= 0) return string.Empty;

        var precedingContent = source[..tableBlockStart];
        if (string.IsNullOrWhiteSpace(precedingContent)) return string.Empty;

        var blocks = GetAllBlock(precedingContent, pipeline);

        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i] is HtmlBlock or
                ThematicBreakBlock or
                ListBlock or
                CodeBlock or
                Markdig.Extensions.Tables.Table or
                ParagraphBlock)
            {
                continue;
            }

            var text = GetBlockText(precedingContent, blocks[i]).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Heuristic decision helpers for table continuation
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get min and max scores from a list of similarity scores.
    /// </summary>
    public static (double minScore, double maxScore) GetScoreRange(List<double> scores)
    {
        if (scores.Count == 0) return (0, 0);
        return (scores.Min(), scores.Max());
    }

    /// <summary>
    /// Apply heuristic thresholds based on min/max scores.
    /// If even the lowest score is high enough → continuation.
    /// If even the highest score is low enough → not continuation.
    /// Otherwise → grey zone (needs AI).
    /// </summary>
    public static bool? HeuristicDecisionByRange(double minScore, double maxScore)
    {
        if (minScore >= 0.70) return true;
        if (maxScore <= 0.25) return false;
        return null;
    }
}