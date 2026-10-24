using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The per-chunk gate's own commands use the migration timeout, and a failed unlock does not replace the
/// command error (gh#756).
/// </summary>
public sealed class TradeInstrumentIndexBuildInterceptorTests
{
    [Fact]
    public void NonQueryExecuting_WhenTheStatementIsPerChunk_CopiesTheCommandTimeoutOntoTheProbe()
    {
        using ScriptedConnection connection = new() { Scalar = "0" };
        using ScriptedCommand command = new(connection)
        {
            CommandText = "CREATE INDEX WITH (" + TradeInstrumentIndexBuildInterceptor.PerChunkMarker + ")",
            CommandTimeout = 123,
        };

        new TradeInstrumentIndexBuildInterceptor().NonQueryExecuting(command, null!, default);

        connection.Created.Should().ContainSingle();
        connection.Created[0].CommandTimeout.Should().Be(123);
        connection.Created[0].CommandText.Should().Contain(TradeInstrumentIndexBuildInterceptor.SessionSetting);
    }

    [Fact]
    public async Task NonQueryExecutingAsync_WhenTheStatementIsPerChunk_CopiesTheCommandTimeoutOntoTheProbe()
    {
        using ScriptedConnection connection = new() { Scalar = "0" };
        using ScriptedCommand command = new(connection)
        {
            CommandText = "CREATE INDEX WITH (" + TradeInstrumentIndexBuildInterceptor.PerChunkMarker + ")",
            CommandTimeout = 123,
        };

        await new TradeInstrumentIndexBuildInterceptor().NonQueryExecutingAsync(command, null!, default);

        connection.Created.Should().ContainSingle();
        connection.Created[0].CommandTimeout.Should().Be(123);
        connection.Created[0].CommandText.Should().Contain(TradeInstrumentIndexBuildInterceptor.SessionSetting);
    }

    [Fact]
    public void CommandFailed_WhenTheBuildFails_CopiesTheCommandTimeoutOntoTheUnlock()
    {
        using ScriptedConnection connection = new() { Scalar = false };
        using ScriptedCommand command = new(connection)
        {
            CommandText = "CALL public.marqspec_build_ix_trades_instrument_time();",
            CommandTimeout = 123,
        };

        new TradeInstrumentIndexBuildInterceptor().CommandFailed(command, Error(command, context: null));

        connection.Created.Should().ContainSingle();
        connection.Created[0].CommandTimeout.Should().Be(123);
        connection.Created[0].CommandText.Should().Be(TradeInstrumentIndexBuildInterceptor.ReleaseLockSql);
    }

    [Fact]
    public void CommandFailed_WhenTheUnlockThrows_LogsAndLeavesTheOriginalError()
    {
        UnlockFailure failure = new("the server is gone");
        using ScriptedConnection connection = new() { ScalarFailure = failure };
        using ScriptedCommand command = new(connection)
        {
            CommandText = TradeInstrumentIndexBuildInterceptor.PerChunkMarker,
            CommandTimeout = 123,
        };
        CollectingLoggerProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using TopstepXDbContext context = new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseLoggerFactory(factory)
            .Options);
        Exception original = new InvalidOperationException("the build failed");

        Action act = () => new TradeInstrumentIndexBuildInterceptor().CommandFailed(
            command,
            Error(command, context, original));

        act.Should().NotThrow();
        connection.Created.Should().ContainSingle().Which.CommandTimeout.Should().Be(123);
        provider.Lines.Should().Contain(line =>
            line.Level == LogLevel.Warning
            && line.Exception == failure
            && line.Message.Contains("was not released", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandFailed_WhenNoLoggerIsRegistered_StillLeavesTheOriginalError()
    {
        using ScriptedConnection connection = new() { ScalarFailure = new UnlockFailure("the server is gone") };
        using ScriptedCommand command = new(connection)
        {
            CommandText = TradeInstrumentIndexBuildInterceptor.PerChunkMarker,
            CommandTimeout = 123,
        };

        Action act = () => new TradeInstrumentIndexBuildInterceptor().CommandFailed(
            command,
            Error(command, context: null));

        act.Should().NotThrow();
    }

    [Fact]
    public void CommandFailed_WhenTheUnlockIsCancelled_DoesNotSwallowTheCancellation()
    {
        using ScriptedConnection connection = new() { ScalarFailure = new OperationCanceledException("stop") };
        using ScriptedCommand command = new(connection)
        {
            CommandText = TradeInstrumentIndexBuildInterceptor.PerChunkMarker,
        };

        Action act = () => new TradeInstrumentIndexBuildInterceptor().CommandFailed(
            command,
            Error(command, context: null));

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void CommandFailed_WhenTheUnlockFailsFatally_DoesNotSwallowIt()
    {
        using ScriptedConnection connection = new() { ScalarFailure = new OutOfMemoryException("unlock") };
        using ScriptedCommand command = new(connection)
        {
            CommandText = TradeInstrumentIndexBuildInterceptor.PerChunkMarker,
        };

        Action act = () => new TradeInstrumentIndexBuildInterceptor().CommandFailed(
            command,
            Error(command, context: null));

        act.Should().Throw<OutOfMemoryException>();
    }

    [Fact]
    public async Task CommandFailedAsync_WhenTheBuildFails_CopiesTheCommandTimeoutOntoTheUnlock()
    {
        using ScriptedConnection connection = new() { Scalar = false };
        using ScriptedCommand command = new(connection)
        {
            CommandText = "CALL public.marqspec_build_ix_trades_instrument_time();",
            CommandTimeout = 123,
        };

        await new TradeInstrumentIndexBuildInterceptor().CommandFailedAsync(command, Error(command, context: null));

        connection.Created.Should().ContainSingle();
        connection.Created[0].CommandTimeout.Should().Be(123);
        connection.Created[0].CommandText.Should().Be(TradeInstrumentIndexBuildInterceptor.ReleaseLockSql);
    }

    [Fact]
    public async Task CommandFailedAsync_WhenTheUnlockThrows_LogsAndLeavesTheOriginalError()
    {
        UnlockFailure failure = new("the server is gone");
        using ScriptedConnection connection = new() { ScalarFailure = failure };
        using ScriptedCommand command = new(connection)
        {
            CommandText = TradeInstrumentIndexBuildInterceptor.PerChunkMarker,
            CommandTimeout = 123,
        };
        CollectingLoggerProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using TopstepXDbContext context = new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseLoggerFactory(factory)
            .Options);

        Func<Task> act = () => new TradeInstrumentIndexBuildInterceptor().CommandFailedAsync(
            command,
            Error(command, context, new InvalidOperationException("the build failed")));

        await act.Should().NotThrowAsync();
        connection.Created.Should().ContainSingle().Which.CommandTimeout.Should().Be(123);
        provider.Lines.Should().Contain(line =>
            line.Level == LogLevel.Warning
            && line.Exception == failure
            && line.Message.Contains("was not released", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommandFailedAsync_WhenNoLoggerIsRegistered_StillLeavesTheOriginalError()
    {
        using ScriptedConnection connection = new() { ScalarFailure = new UnlockFailure("the server is gone") };
        using ScriptedCommand command = new(connection)
        {
            CommandText = TradeInstrumentIndexBuildInterceptor.PerChunkMarker,
            CommandTimeout = 123,
        };

        Func<Task> act = () => new TradeInstrumentIndexBuildInterceptor().CommandFailedAsync(
            command,
            Error(command, context: null));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CommandFailedAsync_WhenTheUnlockIsCancelled_DoesNotSwallowTheCancellation()
    {
        using ScriptedConnection connection = new() { ScalarFailure = new OperationCanceledException("stop") };
        using ScriptedCommand command = new(connection)
        {
            CommandText = TradeInstrumentIndexBuildInterceptor.PerChunkMarker,
        };

        Func<Task> act = () => new TradeInstrumentIndexBuildInterceptor().CommandFailedAsync(
            command,
            Error(command, context: null));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CommandFailedAsync_WhenTheUnlockFailsFatally_DoesNotSwallowIt()
    {
        using ScriptedConnection connection = new() { ScalarFailure = new OutOfMemoryException("unlock") };
        using ScriptedCommand command = new(connection)
        {
            CommandText = TradeInstrumentIndexBuildInterceptor.PerChunkMarker,
        };

        Func<Task> act = () => new TradeInstrumentIndexBuildInterceptor().CommandFailedAsync(
            command,
            Error(command, context: null));

        await act.Should().ThrowAsync<OutOfMemoryException>();
    }

    [Fact]
    public async Task CommandFailedAsync_WhenWritingTheUnlockWarningThrows_LeavesTheOriginalError()
    {
        using ScriptedConnection connection = new() { ScalarFailure = new UnlockFailure("the server is gone") };
        using ScriptedCommand command = new(connection)
        {
            CommandText = TradeInstrumentIndexBuildInterceptor.PerChunkMarker,
        };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(new ThrowingLoggerProvider()));
        using TopstepXDbContext context = new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseLoggerFactory(factory)
            .Options);

        Func<Task> act = () => new TradeInstrumentIndexBuildInterceptor().CommandFailedAsync(
            command,
            Error(command, context, new InvalidOperationException("the build failed")));

        await act.Should().NotThrowAsync();
    }

    private static CommandErrorEventData Error(DbCommand command, DbContext? context, Exception? original = null) =>
        new(
            new StubEventDefinition(),
            static (_, _) => "test",
            command.Connection!,
            command,
            command.Connection!.ConnectionString,
            context!,
            DbCommandMethod.ExecuteNonQuery,
            Guid.Empty,
            Guid.Empty,
            original ?? new InvalidOperationException("the build failed"),
            false,
            false,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            CommandSource.Unknown);

    private sealed class StubEventDefinition()
        : EventDefinitionBase(new EmptyLoggingOptions(), new EventId(1, "test"), LogLevel.Warning, "test");

    private sealed class EmptyLoggingOptions : ILoggingOptions
    {
        public bool IsSensitiveDataLoggingEnabled => false;

        public bool IsSensitiveDataLoggingWarned { get; set; }

        public bool DetailedErrorsEnabled => false;

        public WarningsConfiguration WarningsConfiguration { get; } = new();

        public bool ShouldWarnForStringEnumValueInJson(Type type) => false;

        public void Initialize(IDbContextOptions options)
        {
        }

        public void Validate(IDbContextOptions options)
        {
        }
    }

    private sealed class UnlockFailure(string message) : DbException(message);

    private sealed class ThrowingLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger();

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (formatter(state, exception).Contains("was not released", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("the unlock warning could not be written");
                }
            }
        }
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(Lines);

        public void Dispose()
        {
        }

        private sealed class Logger(List<(LogLevel Level, string Message, Exception? Exception)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lines.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class ScriptedConnection : DbConnection
    {
        public List<ScriptedCommand> Created { get; } = [];

        public object? Scalar { get; set; }

        public Exception? ScalarFailure { get; set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand()
        {
            ScriptedCommand command = new(this);
            Created.Add(command);
            return command;
        }
    }

    private sealed class ScriptedCommand : DbCommand
    {
        private readonly ScriptedConnection _connection;

        public ScriptedCommand(ScriptedConnection connection)
        {
            _connection = connection;
            DbConnection = connection;
        }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override bool DesignTimeVisible { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => 0;

        public override object? ExecuteScalar()
        {
            if (_connection.ScalarFailure is not null)
            {
                throw _connection.ScalarFailure;
            }

            return _connection.Scalar;
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ExecuteScalar());
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();
    }
}
