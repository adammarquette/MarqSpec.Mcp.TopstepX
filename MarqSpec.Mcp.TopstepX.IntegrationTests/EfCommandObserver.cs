using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>One command EF was about to send.</summary>
/// <param name="Text">The command text after interceptors rewrote it.</param>
/// <param name="Timeout">The client command timeout, in seconds.</param>
/// <param name="Host">The server host.</param>
/// <param name="Port">The server port.</param>
/// <param name="Database">The database name.</param>
internal sealed record ObservedSql(string Text, int Timeout, string? Host, int Port, string? Database);

/// <summary>
/// Records EF commands as they are sent. A process-wide listener, so callers filter on host, port and
/// database: other collections migrate other containers at the same time.
/// </summary>
internal sealed class EfCommandObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly object _gate = new();
    private readonly List<IDisposable> _sources = [];

    /// <summary>Commands observed since this listener was subscribed.</summary>
    public List<ObservedSql> Commands { get; } = [];

    /// <inheritdoc />
    public void OnCompleted()
    {
    }

    /// <inheritdoc />
    public void OnError(Exception error)
    {
    }

    /// <inheritdoc />
    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "Microsoft.EntityFrameworkCore")
        {
            _sources.Add(value.Subscribe(
                this,
                static name => name == RelationalEventId.CommandExecuting.Name));
        }
    }

    /// <inheritdoc />
    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Value is not CommandEventData data || data.Command.Connection is not NpgsqlConnection connection)
        {
            return;
        }

        lock (_gate)
        {
            Commands.Add(new ObservedSql(
                data.Command.CommandText,
                data.Command.CommandTimeout,
                connection.Host,
                connection.Port,
                connection.Database));
        }
    }

    /// <summary>Stops listening to sources already seen. The <c>AllListeners</c> subscription is the caller's.</summary>
    public void DisposeSources()
    {
        foreach (IDisposable source in _sources)
        {
            source.Dispose();
        }

        _sources.Clear();
    }
}
