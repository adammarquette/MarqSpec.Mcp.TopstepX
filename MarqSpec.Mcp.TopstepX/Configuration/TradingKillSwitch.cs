using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Combines the configured kill switch, an optional watched file, and a live read of <c>Trading__KillSwitch</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The file path is for hot reload.</b> Touching or removing <see cref="TradingExecutionOptions.KillSwitchFile"/>
/// flips the gate without redeploying. The bound <see cref="TradingExecutionOptions.KillSwitch"/> property is
/// read from <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> on every check, and the
/// <c>Trading__KillSwitch</c> environment variable is read the same way, so a configuration reload or an
/// orchestrator that can mutate the process environment is honoured without a restart. A reload that changes
/// <see cref="TradingExecutionOptions.KillSwitchFile"/> retargets the watcher. A reload that fails validation
/// engages the switch: the last good value is not kept.
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

    private readonly IOptionsMonitor<TradingExecutionOptions> _options;
    private readonly IDisposable? _reload;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private volatile bool _fileEngaged;

    /// <summary>Creates the switch from the options monitor the composition root supplies.</summary>
    /// <param name="options">The bound trading configuration. Read on every check.</param>
    public TradingKillSwitch(IOptionsMonitor<TradingExecutionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        RefreshFileEngaged();
        StartWatcherIfConfigured();
        _reload = options.OnChange(_ => ReloadFromOptions());
    }

    /// <summary>
    /// Accepts the options wrapper the stage tests on <c>develop</c> pass to <c>new</c>. It does not observe
    /// a configuration reload.
    /// </summary>
    /// <param name="options">A fixed options instance.</param>
    public TradingKillSwitch(IOptions<TradingExecutionOptions> options)
        : this(new FixedTradingOptionsMonitor(options))
    {
    }

    /// <inheritdoc />
    public bool IsEngaged
    {
        get
        {
            try
            {
                return _options.CurrentValue.KillSwitch
                    || ReadEnvironmentKillSwitch()
                    || _fileEngaged;
            }
            catch (OptionsValidationException)
            {
                return true;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _reload?.Dispose();
        lock (_sync)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void ReloadFromOptions()
    {
        lock (_sync)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        RefreshFileEngaged();
        StartWatcherIfConfigured();
    }

    private void StartWatcherIfConfigured()
    {
        string? path = ReadKillSwitchFile();
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
        string? path = ReadKillSwitchFile();
        if (string.IsNullOrWhiteSpace(path))
        {
            _fileEngaged = false;
            return;
        }

        _fileEngaged = File.Exists(Path.GetFullPath(path.Trim()));
    }

    private string? ReadKillSwitchFile()
    {
        try
        {
            return _options.CurrentValue.KillSwitchFile;
        }
        catch (OptionsValidationException)
        {
            return null;
        }
    }

    private static bool ReadEnvironmentKillSwitch()
    {
        string? raw = Environment.GetEnvironmentVariable(KillSwitchEnvironmentVariable);
        return bool.TryParse(raw, out bool engaged) && engaged;
    }
}
