using System.Globalization;
using System.Text;

namespace Meetco.Core.Assistant;

/// <summary>Incrementally extracts values from a JSON object that is still being streamed.</summary>
public static class PartialJson
{
    public readonly record struct StringValue(string Value, bool IsComplete);

    /// <summary>Returns the (possibly incomplete) string value for <paramref name="key"/>, and whether it is complete.</summary>
    public static StringValue? String(string key, string text)
    {
        var keyIndex = text.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        var index = keyIndex + key.Length + 2;

        void SkipWhitespace()
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }

        SkipWhitespace();
        if (index >= text.Length || text[index] != ':') return null;
        index++;
        SkipWhitespace();
        if (index >= text.Length || text[index] != '"') return null;
        index++;

        var result = new StringBuilder();
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '\\')
            {
                var next = index + 1;
                if (next >= text.Length) return new StringValue(result.ToString(), false);
                switch (text[next])
                {
                    case 'n': result.Append('\n'); break;
                    case 't': result.Append('\t'); break;
                    case 'r': break;
                    case 'u':
                        var hexStart = next + 1;
                        if (hexStart + 4 > text.Length
                            || !int.TryParse(text.AsSpan(hexStart, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            return new StringValue(result.ToString(), false);
                        }
                        result.Append((char)code);
                        index = hexStart + 4;
                        continue;
                    default: result.Append(text[next]); break;
                }
                index = next + 1;
                continue;
            }
            if (character == '"') return new StringValue(result.ToString(), true);
            result.Append(character);
            index++;
        }
        return new StringValue(result.ToString(), false);
    }

    public static bool? Bool(string key, string text)
    {
        var keyIndex = text.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        var rest = text.AsSpan(keyIndex + key.Length + 2).TrimStart(" \t\r\n:");
        if (rest.StartsWith("true")) return true;
        if (rest.StartsWith("false")) return false;
        return null;
    }
}
