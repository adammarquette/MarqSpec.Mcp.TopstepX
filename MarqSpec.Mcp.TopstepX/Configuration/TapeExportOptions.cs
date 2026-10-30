using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Scheduled copy of recorded tape, coverage and footprint cells so a container reset does not
/// take the only copy with it (gh#755, <c>R-10</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless an operator asks.</b> <see cref="TapeExportOptions.Enabled"/> defaults to false, the
/// same shape as <see cref="MarketDataOptions.RecordTape"/>: a stdio child and a laptop must not start
/// writing files. The worker runs only when the transport is HTTP <i>and</i> this switch is on. The
/// <c>export-tape</c> and <c>import-tape</c> verbs are the explicit path and do not read the switch.
/// </para>
/// <para>
/// <b>The path is a directory on a mounted volume, not object storage.</b>
/// <see cref="Path"/> defaults to <see cref="DefaultPath"/> and binds from <c>TapeExport__Path</c>.
/// The process creates subdirectories there; it does not create the volume. A Railway volume has to be
/// mounted at that path by a person, writable by the image user (uid 64198), or the files land on the
/// container filesystem and die with it. An <c>s3://</c> URI is refused. Google Drive is a separate
/// target (<see cref="TapeExportDriveOptions"/>), off unless a folder id and a service account are
/// both set, and it does not replace the volume.
/// </para>
/// <para>
/// <b>The export is additive.</b> A finished UTC day is copied out. Local rows stay. Timescale
/// <c>policy_retention</c> on <c>Trades</c> stays empty, and this process has no retention or prune
/// switch. Deleting raw tape is a separate product decision and needs its own ADR (gh#758).
/// </para>
/// </remarks>
public sealed class TapeExportOptions : IValidatableObject
{
    /// <summary>The configuration section these bind to (<c>TapeExport__*</c>).</summary>
    public const string SectionName = "TapeExport";

    /// <summary>Daily UTC time the worker aims at, <c>HH:mm</c>. Binds from <c>TapeExport__Schedule</c>.</summary>
    public const string DefaultSchedule = "06:00";

    /// <summary>
    /// Directory the files are written under. On Railway this is the volume mount path.
    /// Binds from <c>TapeExport__Path</c>.
    /// </summary>
    public const string DefaultPath = "/data/tape-exports";

    /// <summary>
    /// Both formats, gzip CSV and Parquet. Binds from <c>TapeExport__Format</c>. One format alone is
    /// refused: the volume would be missing the other copy.
    /// </summary>
    public const string DefaultFormat = "csv.gz+parquet";

    /// <summary>
    /// Whether the HTTP host runs the daily export. Default false. Does not delete rows.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// When the daily pass runs, <c>HH:mm</c> in UTC. Default <see cref="DefaultSchedule"/>.
    /// The worker also runs once at process start, then waits for the next occurrence.
    /// </summary>
    public string Schedule { get; init; } = DefaultSchedule;

    /// <summary>
    /// Absolute directory the gzip files and manifests are written to. Default
    /// <see cref="DefaultPath"/>, the mounted-volume path.
    /// </summary>
    public string Path { get; init; } = DefaultPath;

    /// <summary>File formats. Only <see cref="DefaultFormat"/> is accepted.</summary>
    public string Format { get; init; } = DefaultFormat;

    /// <summary>Google Drive cold storage. Off when the folder or the service account is unset.</summary>
    public TapeExportDriveOptions Drive { get; init; } = new();

    /// <summary>The schedule, parsed. Startup validation has already refused a bad one.</summary>
    /// <returns>The UTC time of day.</returns>
    /// <exception cref="FormatException"><see cref="Schedule"/> is not <c>HH:mm</c>.</exception>
    public TimeOnly ParsedSchedule()
    {
        if (!TryParseSchedule(Schedule, out TimeOnly schedule))
        {
            throw new FormatException(ScheduleRefusal(Schedule));
        }

        return schedule;
    }

    /// <summary>
    /// Refuses a path that is not a local directory, or a format this process cannot write.
    /// </summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>One result per broken rule, otherwise none.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        List<ValidationResult> results = [];

        if (!TryParseSchedule(Schedule, out _))
        {
            results.Add(new ValidationResult(ScheduleRefusal(Schedule), [nameof(Schedule)]));
        }

        if (!IsMountedDirectory(Path))
        {
            results.Add(new ValidationResult(PathRefusal(Path), [nameof(Path)]));
        }

        if (!string.Equals(Format, DefaultFormat, StringComparison.Ordinal))
        {
            results.Add(new ValidationResult(
                SectionName + "__Format is '" + Format + "'. This process writes both gzip CSV and "
                + "Parquet, so the only accepted value is " + DefaultFormat + ".",
                [nameof(Format)]));
        }

        if (!string.IsNullOrWhiteSpace(Drive.ServiceAccountJsonPath)
            && !System.IO.Path.IsPathRooted(Drive.ServiceAccountJsonPath))
        {
            results.Add(new ValidationResult(
                SectionName + "__Drive__ServiceAccountJsonPath must be an absolute path when it is set. "
                + "Leave the Drive keys empty to keep the upload off.",
                [nameof(Drive)]));
        }

        return results;
    }

    /// <summary>Parses <c>HH:mm</c> exactly. <c>6:00</c> and <c>06:00:00</c> are refused.</summary>
    /// <param name="text">The configured text.</param>
    /// <param name="schedule">The time, when the method returns true.</param>
    /// <returns>Whether <paramref name="text"/> is an exact <c>HH:mm</c>.</returns>
    public static bool TryParseSchedule(string? text, out TimeOnly schedule) =>
        TimeOnly.TryParseExact(
            text ?? string.Empty,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out schedule);

    private static bool IsMountedDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (!System.IO.Path.IsPathRooted(path))
        {
            return false;
        }

        string[] parts = path.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);

        return !parts.Contains("..");
    }

    private static string ScheduleRefusal(string? schedule) =>
        SectionName + "__Schedule is '" + schedule + "', which is not HH:mm in UTC. "
        + "It is parsed EXACTLY, so '6:00' and '06:00:00' are refused rather than read charitably: "
        + "a pass that runs at a different hour than the one the operator typed exports on a clock "
        + "nobody chose.";

    private static string PathRefusal(string? path) =>
        SectionName + "__Path is '" + path + "'. Exports write to a directory on a mounted "
        + "volume (default " + DefaultPath + "), not to an object-storage URI and not to a relative "
        + "path. Mount the volume at that path before the daily worker is switched on; this process "
        + "does not create the volume.";
}

/// <summary>
/// Google Drive as a second copy of the export files (gh#755, <c>R-10.6</c>). Disabled when
/// <see cref="FolderId"/> is empty or neither credential source is set. Never a reason to delete
/// local tape.
/// </summary>
public sealed class TapeExportDriveOptions
{
    /// <summary>The Drive folder id. Empty leaves the upload off. Binds from <c>TapeExport__Drive__FolderId</c>.</summary>
    public string FolderId { get; init; } = string.Empty;

    /// <summary>
    /// Service-account JSON, injected by the environment. Empty is off. Never commit a value.
    /// A path (<see cref="ServiceAccountJsonPath"/>) wins when both are set.
    /// </summary>
    public string ServiceAccountJson { get; init; } = string.Empty;

    /// <summary>
    /// Absolute path of a service-account JSON file that is not in the image. Empty is off
    /// unless <see cref="ServiceAccountJson"/> is set. Binds from
    /// <c>TapeExport__Drive__ServiceAccountJsonPath</c>.
    /// </summary>
    public string ServiceAccountJsonPath { get; init; } = string.Empty;

    /// <summary>True only when a folder and one credential source are both present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(FolderId)
        && (!string.IsNullOrWhiteSpace(ServiceAccountJson)
            || !string.IsNullOrWhiteSpace(ServiceAccountJsonPath));
}
