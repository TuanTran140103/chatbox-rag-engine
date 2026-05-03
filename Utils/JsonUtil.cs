using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownGenQAs.Utils;

public class jsonUtil
{

    public static string CleanJsonWithWindowsPath(string jsonString)
    {
        if (string.IsNullOrEmpty(jsonString))
            return jsonString;

        // Phương pháp 1: Thay thế tất cả single backslash thành double backslash
        // trừ những backslash đã được escape đúng
        string cleaned = Regex.Replace(jsonString, @"(?<!\\)\\(?![""\\/bfnrtu])", @"\\");

        return cleaned;
    }

    public static string EscapeNewlinesInsideJsonStrings(string input)
    {
        var sb = new StringBuilder();
        bool inString = false;
        bool escaped = false;
        foreach (var ch in input) 
        { 
            if (escaped) 
            { 
                sb.Append(ch); escaped = false; continue; 
            } 
            if (ch == '\\') 
            { 
                sb.Append(ch); escaped = true; continue; 
            } 
            if (ch == '"') 
            { 
                sb.Append(ch); inString = !inString; continue; 
            } 
            if (inString && (ch == '\n' || ch == '\r')) 
            { 
                sb.Append("\\n"); continue; 
            } 
            
            sb.Append(ch); }
        return sb.ToString();
    }

}