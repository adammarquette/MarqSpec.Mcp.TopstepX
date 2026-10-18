using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Combines the configured kill switch, an optional watched file, and a live read of <c>Trading__KillSwitch</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The file path is for hot reload.</b> Touching or removing <see cref="TradingExecutionOptions.KillSwitchFile"/>
/// flips the gate without redeploying. The bound <see cref="TradingExecutionOptions.KillSwitch"/> property and
/// the <c>Trading__KillSwitch</c> environment variable are re-read on every check so an orchestrator that can
/// mutate the process environment without restart is honoured when it does.
/// </para>
/// <para>
/// When <see cref="TradingExecutionOptions.KillSwitchFile"/> is set, the switch is engaged if that path exists
/// on disk at check time.
/// </para>
/// </remarks>
public sealed class TradingKillSwitch : ITradingKillSwitch, IDisposable
{
    /// <summary>The environment variable checked on every read, alongside bound configuration.</summary>
    public const string KillSwitchEnvironmentVariable = "Trading__KillSwitch";

    private readonly TradingExecutionOptions _options;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private volatile bool _fileEngaged;

    /// <summary>Creates the switch from validated trading options.</summary>
    /// <param name="options">The bound trading configuration.</param>
    public TradingKillSwitch(IOptions<TradingExecutionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        RefreshFileEngaged();
        StartWatcherIfConfigured();
    }

    /// <inheritdoc />
    public bool IsEngaged =>
        _options.KillSwitch
        || ReadEnvironmentKillSwitch()
        || _fileEngaged;

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void StartWatcherIfConfigured()
    {
        string? path = _options.KillSwitchFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath = Path.GetFullPath(path.Trim());
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);

        lock (_sync)
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            _watcher.Created += (_, _) => RefreshFileEngaged();
            _watcher.Changed += (_, _) => RefreshFileEngaged();
            _watcher.Deleted += (_, _) => RefreshFileEngaged();
            _watcher.Renamed += (_, _) => RefreshFileEngaged();
        }
    }

    private void RefreshFileEngaged()
    {
        string? path = _options.KillSwitchFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            _fileEngaged = false;
            return;
        }

        _fileEngaged = File.Exists(Path.GetFullPath(path.Trim()));
    }

    private static bool ReadEnvironmentKillSwitch()
    {
        string? raw = Environment.GetEnvironmentVariable(KillSwitchEnvironmentVariable);
        return bool.TryParse(raw, out bool engaged) && engaged;
    }
}
