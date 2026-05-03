using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Helper;

public class MarkdownServiceHelper
{


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
            Console.WriteLine($"Title Hyrarchy header: {chunk.TittleHirarchy}");
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

}