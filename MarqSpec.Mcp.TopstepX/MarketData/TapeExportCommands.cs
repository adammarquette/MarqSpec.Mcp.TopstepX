using System.Globalization;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>The <c>export-tape</c> and <c>import-tape</c> verbs (gh#755).</summary>
public static class TapeExportCommands
{
    /// <summary>Writes eligible days to the volume and prunes only what verified.</summary>
    public const string ExportVerb = "export-tape";

    /// <summary>Restores verified days from the volume into Postgres.</summary>
    public const string ImportVerb = "import-tape";

    /// <summary>The pass finished and every day it touched verified.</summary>
    public const int ExitOk = 0;

    /// <summary>At least one day failed verification or was refused on import.</summary>
    public const int ExitRefused = 1;

    /// <summary>The day argument is not <c>yyyy-MM-dd</c>.</summary>
    public const int ExitUsage = 2;

    /// <summary>The store could not be migrated.</summary>
    public const int ExitStore = 3;

    /// <summary>
    /// Reads an optional UTC day. No day means every eligible day on export, or every manifest on import.
    /// </summary>
    /// <param name="args">The process arguments. <c>args[0]</c> is the verb.</param>
    /// <returns>Whether this is an export, and the day when one was given.</returns>
    /// <exception cref="ArgumentException">The day is present and is not <c>yyyy-MM-dd</c>.</exception>
    public static (bool IsExport, DateOnly? Day) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            throw new ArgumentException("export-tape and import-tape take an optional yyyy-MM-dd day.");
        }

        bool isExport = string.Equals(args[0], ExportVerb, StringComparison.Ordinal);
        bool isImport = string.Equals(args[0], ImportVerb, StringComparison.Ordinal);
        if (!isExport && !isImport)
        {
            throw new ArgumentException(
                "'" + args[0] + "' is not " + ExportVerb + " or " + ImportVerb + ".");
        }

        if (args.Length > 2)
        {
            throw new ArgumentException(
                args[0] + " takes one optional yyyy-MM-dd day and nothing after it.");
        }

        if (args.Length == 1)
        {
            return (isExport, null);
        }

        if (!DateOnly.TryParseExact(
            args[1],
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateOnly day))
        {
            throw new ArgumentException(
                args[0] + " day '" + args[1] + "' is not yyyy-MM-dd. It is parsed EXACTLY, so "
                + "'17/10/2026' and '2026-10-17T00:00:00Z' are refused rather than read charitably.");
        }

        return (isExport, day);
    }
}
