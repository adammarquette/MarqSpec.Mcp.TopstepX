using System.Globalization;
using System.Text.RegularExpressions;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Makes one string safe to place in a log record.
/// </summary>
/// <remarks>
/// <para>
/// CR, LF, the other C0/C1 controls, the Unicode line and paragraph separators (U+2028, U+2029), the bidi
/// overrides and isolates (U+202A–U+202E, U+2066–U+2069), and a literal backslash are escaped as a
/// backslash, the letter u, and four hexadecimal digits. The backslash is escaped in the same pass, so
/// those six characters written out for a carriage return are not what a real carriage return becomes. A
/// value is then logged as a structured parameter, so the characters cannot open a second entry or hide one.
/// </para>
/// <para>
/// The escaped text is capped at <see cref="MaxSanitizedLength"/> characters, and
/// <see cref="TruncationMarker"/> is appended when it is longer. The cap is on that output: one control
/// becomes six characters, so an 8 KB path of controls would otherwise be about 48 KB.
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
    /// C0/C1 controls, the line and paragraph separators, the bidi overrides and isolates, and a literal
    /// backslash. Built from code points: a unicode escape in this file would be invisible to the order-name
    /// scan and is rejected for that reason.
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

        // Regex.Replace is the barrier CodeQL's cs/log-forging query treats as sanitising the result.
        // One pass covers the controls, the line separators, the bidi marks and a literal backslash.
        // The replacement is not scanned again, so the backslash this escape introduces stays put.
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

        // An escape is a backslash, the letter u, and four hex digits. Cutting inside one would leave a
        // trailing fragment that looks like the start of an escape. End before that fragment.
        int window = Math.Min(5, keep);
        int slash = escaped.LastIndexOf('\\', keep - 1, window);
        if (slash >= 0 && slash + 6 > keep)
        {
            keep = slash;
        }

        return string.Concat(escaped.AsSpan(0, keep), TruncationMarker);
    }

    private static string BuildPattern()
    {
        char[] extras =
        [
            (char)0x2028,
            (char)0x2029,
            (char)0x202A,
            (char)0x202B,
            (char)0x202C,
            (char)0x202D,
            (char)0x202E,
            (char)0x2066,
            (char)0x2067,
            (char)0x2068,
            (char)0x2069,
        ];

        return "[" + @"\p{Cc}" + new string(extras) + @"\\]";
    }
}
