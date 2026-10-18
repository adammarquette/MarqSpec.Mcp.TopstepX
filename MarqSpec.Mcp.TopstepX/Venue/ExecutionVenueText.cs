using System.Text;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>Sanitises vendor free text before it can reach callers (ADR-0008).</summary>
internal static class ExecutionVenueText
{
    private const int MaxLength = 256;

    /// <summary>Returns a bounded, control-stripped copy, or null when nothing safe remains.</summary>
    public static string? Sanitize(string? vendorText)
    {
        if (string.IsNullOrWhiteSpace(vendorText))
        {
            return null;
        }

        StringBuilder builder = new(vendorText.Length);
        foreach (char character in vendorText)
        {
            if (char.IsControl(character) && character is not '\t' and not '\n' and not '\r')
            {
                continue;
            }

            builder.Append(character);
            if (builder.Length >= MaxLength)
            {
                break;
            }
        }

        string trimmed = builder.ToString().Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
