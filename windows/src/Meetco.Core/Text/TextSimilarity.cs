using System.Globalization;
using System.Text;

namespace Meetco.Core.Text;

/// <summary>Lightweight text comparison used to avoid duplicate questions and segments.</summary>
public static class TextSimilarity
{
    /// <summary>Lowercases, removes diacritics and punctuation, and joins words with single spaces.</summary>
    public static string Normalized(string text)
    {
        // Turkish dotless/dotted i are separate letters, not diacritics, so map them explicitly.
        var mapped = text.Replace('ı', 'i').Replace('İ', 'i').ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(mapped.Length);
        foreach (var character in mapped)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }
        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static HashSet<string> Words(string text) =>
        Normalized(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToHashSet();

    /// <summary>Jaccard similarity of the word sets (0…1).</summary>
    public static double Jaccard(string lhs, string rhs)
    {
        var a = Words(lhs);
        var b = Words(rhs);
        if (a.Count == 0 && b.Count == 0) return Normalized(lhs) == Normalized(rhs) ? 1 : 0;
        var union = new HashSet<string>(a);
        union.UnionWith(b);
        if (union.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        return (double)intersection / union.Count;
    }
}
