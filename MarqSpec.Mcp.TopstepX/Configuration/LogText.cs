using System.Globalization;
using System.Text.RegularExpressions;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Makes one string safe to place in a log record.
/// </summary>
/// <remarks>
/// <para>
/// CR, LF, the other C0/C1 controls, the Unicode line and paragraph separators (U+2028, U+2029), the bidi
/// overrides and isolates (U+202A–U+202E, U+2066–U+2069), LRM and RLM (U+200E, U+200F), ALM (U+061C),
/// ZWSP (U+200B), BOM (U+FEFF), a lone surrogate, a literal backslash, and an opening bracket are escaped
/// as a backslash, the letter u, and four hexadecimal digits. The backslash is escaped in the same pass, so those six characters
/// written out for a carriage return are not what a real carriage return becomes. A well-formed surrogate
/// pair is left as one character, so the cap below can see it. A value is then logged as a structured
/// parameter, so the characters cannot open a second entry or hide one.
/// </para>
/// <para>
/// The escaped text is capped at <see cref="MaxSanitizedLength"/> characters, and
/// <see cref="TruncationMarker"/> is appended when it is longer. The cap is on that output: one control
/// becomes six characters, so an 8 KB path of controls would otherwise be about 48 KB. An opening bracket
/// is escaped in the same pass, so a caller who writes the marker does not get it back, and a bracket
/// followed by a backslash stays distinct from a bracket alone. The exact marker occurs only when this
/// method appended it.
/// </para>
/// <para>
/// This does not redact secrets — do not pass a token. A missing value stays missing:
/// <see langword="null"/> and empty both come back empty, rather than a stand-in.
/// </para>
/// </remarks>
public static class LogText
{
    /// <summary>
    /// The longest escaped string returned, not counting <see cref="TruncationMarker"/>.
    /// </summary>
    public const int MaxSanitizedLength = 1024;

    /// <summary>Appended when the escaped text is longer than <see cref="MaxSanitizedLength"/>.</summary>
    public const string TruncationMarker = " [truncated]";

    /// <summary>
    /// C0/C1 controls, line and paragraph separators, bidi overrides and isolates, LRM, RLM, ALM, ZWSP, BOM,
    /// a lone surrogate, a literal backslash, and an opening bracket. Built from code points: a unicode
    /// escape in this file would be invisible to the order-name scan and is rejected for that reason.
    /// </summary>
    private static readonly Regex _unsafeCharacters = new(
        BuildPattern(),
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Escapes the characters that can forge or hide a log line, and caps the escaped text.
    /// </summary>
    /// <param name="value">The text about to be logged. May be <see langword="null"/>.</param>
    /// <returns>
    /// The same text with each unsafe character replaced by a backslash, the letter u, and four hexadecimal
    /// digits, cut to <see cref="MaxSanitizedLength"/> with <see cref="TruncationMarker"/> when longer, or
    /// empty when there was none.
    /// </returns>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Regex.Replace is the barrier CodeQL's cs/log-forging query treats as sanitising the result,
        // and it is the last one. The cap only cuts this result and appends a constant.
        string escaped = _unsafeCharacters.Replace(value, static match => Escape(match.Value[0]));
        return escaped.Length <= MaxSanitizedLength ? escaped : Cap(escaped);
    }

    private static string Escape(char unsafeCharacter)
        => "\\u" + ((ushort)unsafeCharacter).ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>
    /// Cuts <paramref name="escaped"/> on an escape boundary and appends <see cref="TruncationMarker"/>.
    /// </summary>
    private static string Cap(string escaped)
    {
        int keep = MaxSanitizedLength;

        // An escape is a backslash, the letter u, and four hex digits — six characters. The cut is
        // exclusive, so the backslash of an escape that still overlaps the cut sits at most five
        // characters before it. A shorter lookback would miss that backslash and leave a partial escape.
        int window = Math.Min(5, keep);
        int slash = escaped.LastIndexOf('\\', keep - 1, window);
        if (slash >= 0 && slash + 6 > keep)
        {
            keep = slash;
        }

        // A supplementary character is two UTF-16 units. Leaving the high surrogate as the last kept
        // unit would split the pair.
        if (keep > 0 && char.IsHighSurrogate(escaped[keep - 1]))
        {
            keep--;
        }

        return string.Concat(escaped.AsSpan(0, keep), TruncationMarker);
    }

    private static string BuildPattern()
    {
        char[] extras =
        [
            // Line and paragraph separators.
            (char)0x2028,
            (char)0x2029,

            // Bidi overrides.
            (char)0x202A,
            (char)0x202B,
            (char)0x202C,
            (char)0x202D,
            (char)0x202E,

            // Bidi isolates.
            (char)0x2066,
            (char)0x2067,
            (char)0x2068,
            (char)0x2069,

            // LRM, RLM, ALM, ZWSP, BOM.
            (char)0x200E,
            (char)0x200F,
            (char)0x061C,
            (char)0x200B,
            (char)0xFEFF,
        ];

        string marks = "[" + @"\p{Cc}" + new string(extras) + @"\\]";

        // Lone surrogates are Cs. A well-formed pair is two Cs units and must stay one character, so only
        // a high surrogate with no low partner, or a low surrogate with no high partner, is matched.
        string high = "[" + (char)0xD800 + "-" + (char)0xDBFF + "]";
        string low = "[" + (char)0xDC00 + "-" + (char)0xDFFF + "]";
        string lone = "(?<!" + high + ")" + low + "|" + high + "(?!" + low + ")";

        // One more alternative in this same pattern: an opening bracket. Cap appends the truncation
        // marker after the replace, so a caller cannot write that marker by sending the bracket.
        string openBracket = Regex.Escape(((char)0x005B).ToString());
        return "(?:" + marks + "|" + lone + "|" + openBracket + ")";
    }
}
