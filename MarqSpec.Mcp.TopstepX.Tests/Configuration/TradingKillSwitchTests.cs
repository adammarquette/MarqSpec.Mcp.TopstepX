using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Runtime kill switch — env re-read and watched file (gh#708).</summary>
public sealed class TradingKillSwitchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "topstepx-kill-" + Guid.NewGuid().ToString("N"));
    private readonly string _killFile;

    /// <summary>Creates an isolated directory for file-based kill switch tests.</summary>
    public TradingKillSwitchTests()
    {
        Directory.CreateDirectory(_directory);
        _killFile = Path.Combine(_directory, "kill.switch");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for a temp directory another test may still be watching.
        }
    }

    /// <summary>Creating the watched file engages the switch without restart.</summary>
    [Fact]
    public void KillSwitchFile_EngagesWhenTheFileAppears()
    {
        using TradingKillSwitch killSwitch = CreateKillSwitch(killSwitchFile: _killFile);

        killSwitch.IsEngaged.Should().BeFalse();

        File.WriteAllText(_killFile, "halt");

        WaitUntil(() => killSwitch.IsEngaged);

        killSwitch.IsEngaged.Should().BeTrue();
    }

    /// <summary>Removing the watched file disengages the file layer.</summary>
    [Fact]
    public void KillSwitchFile_DisengagesWhenTheFileIsRemoved()
    {
        File.WriteAllText(_killFile, "halt");
        using TradingKillSwitch killSwitch = CreateKillSwitch(killSwitchFile: _killFile);

        WaitUntil(() => killSwitch.IsEngaged);
        killSwitch.IsEngaged.Should().BeTrue();

        File.Delete(_killFile);

        WaitUntil(() => !killSwitch.IsEngaged);
        killSwitch.IsEngaged.Should().BeFalse();
    }

    /// <summary>The environment variable is honoured on every read.</summary>
    [Fact]
    public void EnvironmentVariable_IsReadOnEveryCheck()
    {
        string variable = TradingKillSwitch.KillSwitchEnvironmentVariable;
        string? previous = Environment.GetEnvironmentVariable(variable);

        try
        {
            using TradingKillSwitch killSwitch = CreateKillSwitch();

            Environment.SetEnvironmentVariable(variable, "false");
            killSwitch.IsEngaged.Should().BeFalse();

            Environment.SetEnvironmentVariable(variable, "true");
            killSwitch.IsEngaged.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static TradingKillSwitch CreateKillSwitch(bool configuredKillSwitch = false, string? killSwitchFile = null)
    {
        TradingExecutionOptions options = new()
        {
            KillSwitch = configuredKillSwitch,
            KillSwitchFile = killSwitchFile,
        };

        return new TradingKillSwitch(Options.Create(options));
    }

    private static void WaitUntil(Func<bool> predicate, int timeoutMs = 5_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            Thread.Sleep(50);
        }

        predicate().Should().BeTrue("the kill switch state should have changed within the timeout");
    }
}
