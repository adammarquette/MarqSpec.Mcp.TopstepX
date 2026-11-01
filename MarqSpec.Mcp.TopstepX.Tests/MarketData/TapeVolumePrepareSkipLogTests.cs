using System.Diagnostics;
using FluentAssertions;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The entrypoint's prepare skip is Information when recording is off (gh#894).
/// </summary>
/// <remarks>
/// Railway shows stderr as Error and stdout as Information. On stdio, stdout is the protocol frame, so an
/// expected skip uses stdout only when <c>Mcp__Transport</c> is HTTP. A symlink refusal stays on stderr;
/// that path needs uid 0 and is pinned by <c>scripts/check-tape-volume-prepare.sh</c>.
/// </remarks>
public sealed class TapeVolumePrepareSkipLogTests
{
    private const string Skip = "tape volume prepare skipped: running as uid";

    /// <summary>
    /// HTTP with recording off is the server. The skip is expected, so it is written to stdout.
    /// </summary>
    /// <param name="recordTape">An off value, including unset.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("False")]
    public void PrepareSkip_WhenHttpAndRecordingIsOff_LogsAtInformation(string? recordTape)
    {
        (int code, string stdout, string stderr) = Run(recordTape, "Http");

        code.Should().Be(0);
        stdout.Should().Contain(Skip);
        stderr.Should().NotContain(Skip);
    }

    /// <summary>
    /// Recording on is not an expected skip. The line stays on stderr, which Railway shows as Error.
    /// </summary>
    /// <param name="recordTape">A true value, in either case.</param>
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void PrepareSkip_WhenHttpAndRecordingIsOn_LogsAtError(string recordTape)
    {
        (int code, string stdout, string stderr) = Run(recordTape, "Http");

        code.Should().Be(0);
        stderr.Should().Contain(Skip);
        stdout.Should().NotContain(Skip);
    }

    /// <summary>Stdio stdout is the protocol. Recording off must not move the skip onto it.</summary>
    /// <param name="transport">Unset, or an explicit stdio value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("Stdio")]
    [InlineData("stdio")]
    public void PrepareSkip_WhenStdioAndRecordingIsOff_StaysOffStdout(string? transport)
    {
        (int code, string stdout, string stderr) = Run("false", transport);

        code.Should().Be(0);
        stderr.Should().Contain(Skip);
        stdout.Should().BeEmpty();
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string? recordTape, string? transport)
    {
        if (CurrentUid() == "0")
        {
            throw new InvalidOperationException(
                "This pins the non-root skip the server logs. The root refusal is "
                + "scripts/check-tape-volume-prepare.sh.");
        }

        string volume = Path.Combine(Path.GetTempPath(), "tape-prepare-" + Guid.NewGuid().ToString("N"));
        ProcessStartInfo start = new()
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(LocateScript());
        start.Environment["PREPARE_TAPE_VOLUME_ONLY"] = "1";
        start.Environment["TAPE_VOLUME_ROOT"] = volume;
        SetOrClear(start, "MarketData__RecordTape", recordTape);
        SetOrClear(start, "Mcp__Transport", transport);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The tape-volume entrypoint did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The tape-volume entrypoint did not exit.");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static void SetOrClear(ProcessStartInfo start, string name, string? value)
    {
        if (value is null)
        {
            start.Environment.Remove(name);
            return;
        }

        start.Environment[name] = value;
    }

    private static string LocateScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "scripts", "prepare-tape-volume.sh");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "scripts/prepare-tape-volume.sh was not found above the test assembly.");
    }

    private static string CurrentUid()
    {
        using Process id = Process.Start(
            new ProcessStartInfo
            {
                FileName = "/usr/bin/id",
                ArgumentList = { "-u" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("id did not start.");
        string uid = id.StandardOutput.ReadToEnd().Trim();
        id.WaitForExit();
        return uid;
    }
}
