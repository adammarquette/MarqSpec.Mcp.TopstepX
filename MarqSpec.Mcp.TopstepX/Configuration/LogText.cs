using System.Globalization;
using System.Text.RegularExpressions;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Makes one string safe to place in a log record.
/// </summary>
/// <remarks>
/// CR, LF and the other C0/C1 controls are escaped as <c>\u00XX</c>. A value is then logged as a structured
/// parameter, so the characters cannot open a second entry or hide one. This does not redact secrets — do
/// not pass a token. A missing value stays missing: <see langword="null"/> and empty both come back empty,
/// rather than a stand-in.
/// </remarks>
public static partial class LogText
{
    /// <summary>
    /// Escapes CR, LF and the other control characters in <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The text about to be logged. May be <see langword="null"/>.</param>
    /// <returns>The same text with each control character replaced by <c>\u00XX</c>, or empty when there was none.</returns>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Regex.Replace is the barrier CodeQL's cs/log-forging query treats as sanitising the result.
        // One pattern covers CR, LF and the rest of \p{Cc}; the caller logs the result, not this input.
        return Controls().Replace(value, static match => Escape(match.Value[0]));
    }

    private static string Escape(char control)
        => "\\u" + ((ushort)control).ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>C0 and C1 controls, one character at a time. CR and LF are in this class.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"\p{Cc}", RegexOptions.CultureInvariant)]
    private static partial Regex Controls();
}
