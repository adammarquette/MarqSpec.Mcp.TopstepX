using System.Globalization;
using System.Text.RegularExpressions;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Makes one string safe to place in a log record.
/// </summary>
/// <remarks>
/// <para>
/// CR, LF, the other C0/C1 controls, the Unicode line and paragraph separators (U+2028, U+2029), the bidi
/// overrides and isolates (U+202A–U+202E, U+2066–U+2069), and a literal backslash are escaped as
/// <c>\uXXXX</c>. The backslash is escaped in the same pass, so the six characters <c>\u000D</c> in the input
/// are not the escape this method emits for a real CR. A value is then logged as a structured parameter, so
/// the characters cannot open a second entry or hide one.
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
public static partial class LogText
{
    /// <summary>
    /// The longest escaped string returned, not counting <see cref="TruncationMarker"/>.
    /// </summary>
    public const int MaxSanitizedLength = 1024;

    /// <summary>Appended when the escaped text is longer than <see cref="MaxSanitizedLength"/>.</summary>
    public const string TruncationMarker = " [truncated]";

    /// <summary>
    /// Escapes the characters that can forge or hide a log line, and caps the escaped text.
    /// </summary>
    /// <param name="value">The text about to be logged. May be <see langword="null"/>.</param>
    /// <returns>
    /// The same text with each unsafe character replaced by <c>\uXXXX</c>, cut to
    /// <see cref="MaxSanitizedLength"/> with <see cref="TruncationMarker"/> when longer, or empty when
    /// there was none.
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
        string escaped = Unsafe().Replace(value, static match => Escape(match.Value[0]));
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

        // An escape is '\' + 'u' + four hex digits. Cutting inside one would leave a trailing fragment
        // that looks like the start of an escape. End before that fragment.
        int window = Math.Min(5, keep);
        int slash = escaped.LastIndexOf('\\', keep - 1, window);
        if (slash >= 0 && slash + 6 > keep)
        {
            keep = slash;
        }

        return string.Concat(escaped.AsSpan(0, keep), TruncationMarker);
    }

    /// <summary>
    /// C0/C1 controls, U+2028, U+2029, the bidi overrides and isolates, and a literal backslash.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"[\p{Cc}\u2028\u2029\u202A-\u202E\u2066-\u2069\\]", RegexOptions.CultureInvariant)]
    private static partial Regex Unsafe();
}
