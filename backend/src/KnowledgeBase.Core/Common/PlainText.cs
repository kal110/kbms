using System.Text.RegularExpressions;

namespace KnowledgeBase.Core.Common;

public static partial class PlainText
{
    /// <summary>
    /// Whitespace runs as single spaces and NULs dropped (PostgreSQL text can't hold them): how page
    /// text is stored and how an exact phrase is matched against it, so a line break in the PDF
    /// still matches a space in the query.
    /// </summary>
    public static string Collapse(string text) => Whitespace().Replace(text.Replace("\0", ""), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
