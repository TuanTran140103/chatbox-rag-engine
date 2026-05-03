using System.Text;
using System.Text.RegularExpressions;
using GenQAServer.Options;
using MarkdownGenQAs.Application.Interfaces.Services;
using Microsoft.Extensions.AI;

namespace MarkdownGenQAs.Helper;
public class LlmChatHelper
{

    /// <summary>
    /// Extracts a valid choice from a raw LLM response using a 4-tier fallback strategy.
    /// </list>
    /// </summary>
    /// <param name="rawResponse">Raw text returned by the LLM (may include thinking tags, punctuation, etc.).</param>
    /// <param name="validChoices">Allowed choice values (case-insensitive comparison).</param>
    /// <returns>The matched choice in its original casing from <paramref name="validChoices"/>, or <c>null</c>.</returns>
    public static string? ExtractChoiceFromResponse(string rawResponse, List<string> validChoices)
    {
        if (string.IsNullOrWhiteSpace(rawResponse) || validChoices.Count == 0)
            return null;

        string trimmed = rawResponse.Trim();

        // ── Tier 1: exact match ──────────────────────────────────────────────
        var exact = validChoices.FirstOrDefault(c =>
            string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // ── Tier 2: last whole-word match (rightmost \bChoice\b) ─────────────
        string? lastWordMatch = null;
        int lastIndex = -1;
        foreach (var choice in validChoices)
        {
            var m = Regex.Match(
                rawResponse,
                $@"\b{Regex.Escape(choice)}\b",
                RegexOptions.IgnoreCase | RegexOptions.RightToLeft);

            if (m.Success && m.Index > lastIndex)
            {
                lastIndex = m.Index;
                lastWordMatch = choice;
            }
        }
        if (lastWordMatch != null) return lastWordMatch;

        // ── Tier 3: walk tokens from the end, return first that is a valid choice
        // Splits on whitespace + common punctuation so "Good." → "Good"
        var tokens = Regex
            .Split(trimmed, @"[\s,;:.!?()\[\]'""]+")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Reverse();

        foreach (var token in tokens)
        {
            var match = validChoices.FirstOrDefault(c =>
                string.Equals(c, token, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        // ── Tier 4: no match found ────────────────────────────────────────────
        return null;
    }

    public static List<ChatMessage> CreateChatMessageChoice(string table1, string table2, Prompt prompt)
    {
        string path = prompt.PathTemplatePrompt;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("File not found for prompt choice: ", path);
        }
        string templatePromptChoice = File.ReadAllText(path);
        string finalPrompt = string.Format(templatePromptChoice, table1, table2);
        List<ChatMessage> result = new List<ChatMessage>()
        {
            new ChatMessage(ChatRole.System, prompt.SystemPrompt),
            new ChatMessage(ChatRole.User, finalPrompt)
        };
        return result;

    }

    public static string CleanJsonWithWindowsPath(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        var sb = new StringBuilder();
        bool escaped = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '\\')
            {
                if (escaped)
                {
                    // Dấu \ này đang escape cho dấu \ trước đó. Hợp lệ.
                    sb.Append('\\');
                    escaped = false;
                }
                else
                {
                    // Bắt đầu một chuỗi escape hoặc dấu \ đơn
                    if (i + 1 < json.Length)
                    {
                        char next = json[i + 1];
                        if ("\"\\/bfnrt".Contains(next))
                        {
                            // Escape chuẩn (/, n, r, t, ", \)
                            sb.Append('\\');
                            escaped = true;
                        }
                        else if (next == 'u')
                        {
                            // Kiểm tra xem có phải \uXXXX (Unicode) hợp lệ không
                            if (i + 5 < json.Length && IsHex(json.Substring(i + 2, 4)))
                            {
                                sb.Append('\\');
                                escaped = true;
                            }
                            else
                            {
                                // Không phải Unicode (vd: \users), double nó lên
                                sb.Append(@"\\");
                            }
                        }
                        else
                        {
                            // Ký tự không phải escape chuẩn (vd: \p), double nó lên
                            sb.Append(@"\\");
                        }
                    }
                    else
                    {
                        // Dấu \ ở cuối chuỗi
                        sb.Append(@"\\");
                    }
                }
            }
            else
            {
                sb.Append(c);
                escaped = false;
            }
        }
        return sb.ToString();
    }

    private static bool IsHex(string str)
    {
        return str.All(c => "0123456789abcdefABCDEF".Contains(c));
    }

    public static string EscapeNewlinesInsideJsonStrings(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var sb = new StringBuilder();
        bool inString = false;
        bool escaped = false;
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                sb.Append(ch);
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                sb.Append(ch);
                inString = !inString;
                continue;
            }

            if (inString)
            {
                if (ch == '\r')
                {
                    if (i + 1 < input.Length && input[i + 1] == '\n') i++; // skip \n của \r\n
                    sb.Append("\\n");
                    continue;
                }
                if (ch == '\n')
                {
                    sb.Append("\\n");
                    continue;
                }
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}