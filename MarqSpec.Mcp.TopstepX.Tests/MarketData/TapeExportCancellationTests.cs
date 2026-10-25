using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Cancellation is not a lock fault. A dropped token from the take or the probe leaves the pass,
/// writes no failed receipt, and records no <c>lock_fault</c> measurement.
/// </summary>
public sealed class TapeExportCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tape-cancel-" + Guid.NewGuid().ToString("N"));
    private readonly DateOnly _day = new(2026, 1, 2);
    private readonly DayConnection _connection;
    private readonly TopstepXDbContext _database;

    public TapeExportCancellationTests()
    {
        _connection = new DayConnection("Data Source=tape-cancel;Mode=Memory;Cache=Shared", _day);
        _connection.Open();
        _database = new TopstepXDbContext(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, CoverageTicks>()
            .Options);
        _database.Database.EnsureCreated();
        Directory.CreateDirectory(_root);
        string manifest = TapeExportArchive.ManifestPath(_root, _day);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            """
            {
              "day": "2026-01-02",
              "format": "csv.gz+parquet/v1",
              "generation": 1,
              "exportedAtUtc": "2026-01-03T00:00:00+00:00",
              "files": []
            }
            """);
    }

    [Fact]
    public async Task Export_WhenTryLockIsCanceled_PropagatesAndWritesNoFailedReceipt()
    {
        await AssertCancellationPropagates(new CancelingSession { CancelTake = true }, expectNoReceipt: true);
    }

    [Fact]
    public async Task Export_WhenTheLockProbeIsCanceled_PropagatesAndWritesNoFailedReceipt()
    {
        await AssertCancellationPropagates(new CancelingSession { CancelProbe = true }, expectNoReceipt: false);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task AssertCancellationPropagates(CancelingSession session, bool expectNoReceipt)
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);
        FakeTimeProvider clock = new();
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        TapeExportPass pass = new(
            _database,
            Options.Create(new TapeExportOptions
            {
                Path = _root,
                Schedule = TapeExportOptions.DefaultSchedule,
                Format = TapeExportOptions.DefaultFormat,
            }),
            clock,
            telemetry,
            new IdleUploader(),
            NullLogger<TapeExportPass>.Instance);
        pass.IdentitySession = session;

        Func<Task> act = () => pass.ExportAsync(_day, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _database.ChangeTracker.Clear();
        List<TapeExportReceipt> receipts = await _database.TapeExportReceipts.AsNoTracking().ToListAsync();
        receipts.Should().NotContain(row => row.UploadState == TapeExportReceipt.StateFailed);
        if (expectNoReceipt)
        {
            receipts.Should().BeEmpty();
        }

        uploads.GetMeasurementSnapshot().Should().NotContain(measurement =>
            (string?)measurement.Tags.GetValueOrDefault(HostTelemetry.ReasonTag) == TapeUploadReason.LockFault);
        spans.Should().NotContain(span =>
            span.OperationName == "tape.upload"
            && (string?)span.GetTagItem(HostTelemetry.ReasonTag) == TapeUploadReason.LockFault);
    }

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// SQLite does not translate <see cref="DateTimeOffset"/> comparisons. The open-run check is the only
    /// one on the way to the lock, and an empty coverage table answers it once the instants are ticks.
    /// </summary>
    private sealed class CoverageTicks(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<TapeCoverageRecord>().Property(row => row.RangeStart).HasConversion(
                value => value.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
            modelBuilder.Entity<TapeCoverageRecord>().Property(row => row.RangeEnd).HasConversion(
                value => value.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
        }
    }

    private sealed class CancelingSession : ITapeIdentitySession
    {
        public bool CancelTake { get; init; }

        public bool CancelProbe { get; init; }

        public Task<TapeIdentityLockTake> TryLockAsync(string key, CancellationToken cancellationToken)
        {
            if (CancelTake)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(TapeIdentityLockTake.Held);
        }

        public Task<bool> ProbeAsync()
        {
            if (CancelProbe)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(true);
        }

        public Task UnlockAsync(string key) => Task.CompletedTask;

        public Task ReopenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdleUploader : ITapeDriveUploader
    {
        public bool IsEnabled => true;

        public Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken) =>
            Task.FromResult(new TapeDriveUploadResult(TapeDriveUploadStatus.Uploaded, "id", 1, null, null));
    }

    /// <summary>
    /// SQLite cannot parse the day-discovery statement. That statement is answered here; every other
    /// command, including the receipt insert, runs on SQLite.
    /// </summary>
    private sealed class DayConnection : SqliteConnection
    {
        public DayConnection(string connectionString, DateOnly day)
            : base(connectionString)
        {
            Day = day;
        }

        public DateOnly Day { get; }

        protected override DbCommand CreateDbCommand() => new DayCommand(this, (SqliteCommand)base.CreateDbCommand());
    }

    private sealed class DayCommand : DbCommand
    {
        private readonly DayConnection _connection;
        private readonly SqliteCommand _inner;
        private readonly ParameterList _parameters = new();

        public DayCommand(DayConnection connection, SqliteCommand inner)
        {
            _connection = connection;
            _inner = inner;
            DbConnection = connection;
        }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; } = CommandType.Text;

        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override bool DesignTimeVisible { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override void Prepare()
        {
        }

        public override int ExecuteNonQuery() => Execute(static command => command.ExecuteNonQuery(), 0);

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            ExecuteAsync(command => command.ExecuteNonQueryAsync(cancellationToken), 0, cancellationToken);

        public override object? ExecuteScalar() => Execute<object?>(static command => command.ExecuteScalar(), null);

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
            ExecuteAsync<object?>(command => command.ExecuteScalarAsync(cancellationToken), null, cancellationToken);

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            Execute<DbDataReader>(command => command.ExecuteReader(behavior), new OneDayReader(_connection.Day));

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken) =>
            ExecuteAsync<DbDataReader>(
                async command => await command.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false),
                new OneDayReader(_connection.Day),
                cancellationToken);

        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private T Execute<T>(Func<SqliteCommand, T> call, T dayResult)
        {
            if (IsDayQuery())
            {
                return dayResult;
            }

            CopyToInner();
            return call(_inner);
        }

        private async Task<T> ExecuteAsync<T>(Func<SqliteCommand, Task<T>> call, T dayResult, CancellationToken cancellationToken)
        {
            if (IsDayQuery())
            {
                cancellationToken.ThrowIfCancellationRequested();
                return dayResult;
            }

            CopyToInner();
            return await call(_inner).ConfigureAwait(false);
        }

        private bool IsDayQuery() =>
            CommandText.Contains("::date", StringComparison.Ordinal);

        private void CopyToInner()
        {
            _inner.CommandText = CommandText;
            _inner.CommandTimeout = CommandTimeout;
            if (CommandType == CommandType.Text)
            {
                _inner.CommandType = CommandType.Text;
            }
            _inner.Transaction = DbTransaction as SqliteTransaction;
            _inner.Parameters.Clear();
            foreach (SqliteParameter source in _parameters.Items)
            {
                SqliteParameter copy = _inner.CreateParameter();
                copy.ParameterName = source.ParameterName;
                copy.Value = source.Value;
                copy.DbType = source.DbType;
                _inner.Parameters.Add(copy);
            }
        }

        private sealed class ParameterList : DbParameterCollection
        {
            private readonly List<DbParameter> _items = [];

            public IReadOnlyList<SqliteParameter> Items => _items.Cast<SqliteParameter>().ToList();

            public override int Count => _items.Count;

            public override object SyncRoot => this;

            public override bool IsFixedSize => false;

            public override bool IsReadOnly => false;

            public override bool IsSynchronized => false;

            public override int Add(object? value)
            {
                _items.Add((DbParameter)value!);
                return _items.Count - 1;
            }

            public override void AddRange(Array values)
            {
                foreach (object value in values)
                {
                    Add(value);
                }
            }

            public override void Clear() => _items.Clear();

            public override bool Contains(object? value) => _items.Contains((DbParameter)value!);

            public override bool Contains(string value) => IndexOf(value) >= 0;

            public override void CopyTo(Array array, int index) => _items.ToArray().CopyTo(array, index);

            public override IEnumerator GetEnumerator() => _items.GetEnumerator();

            public override int IndexOf(object? value) => _items.IndexOf((DbParameter)value!);

            public override int IndexOf(string parameterName) =>
                _items.FindIndex(parameter => parameter.ParameterName == parameterName);

            public override void Insert(int index, object? value) => _items.Insert(index, (DbParameter)value!);

            public override void Remove(object? value) => _items.Remove((DbParameter)value!);

            public override void RemoveAt(int index) => _items.RemoveAt(index);

            public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

            protected override DbParameter GetParameter(int index) => _items[index];

            protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

            protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

            protected override void SetParameter(string parameterName, DbParameter value) =>
                _items[IndexOf(parameterName)] = value;
        }
    }

    private sealed class OneDayReader : DbDataReader
    {
        private readonly DateOnly _day;
        private bool _pending = true;

        public OneDayReader(DateOnly day)
        {
            _day = day;
        }

        public override int Depth => 0;

        public override int FieldCount => 1;

        public override bool HasRows => true;

        private bool _closed;

        public override bool IsClosed => _closed;

        public override int RecordsAffected => -1;

        public override object this[int ordinal] => GetValue(ordinal);

        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool Read()
        {
            if (!_pending)
            {
                return false;
            }

            _pending = false;
            return true;
        }

        public override bool NextResult() => false;

        public override string GetName(int ordinal) => "day";

        public override int GetOrdinal(string name) => 0;

        public override string GetDataTypeName(int ordinal) => "date";

        public override Type GetFieldType(int ordinal) => typeof(DateOnly);

        public override object GetValue(int ordinal) => _day;

        public override int GetValues(object[] values)
        {
            values[0] = _day;
            return 1;
        }

        public override bool IsDBNull(int ordinal) => false;

        public override T GetFieldValue<T>(int ordinal)
        {
            if (typeof(T) == typeof(DateOnly))
            {
                return (T)(object)_day;
            }

            return (T)GetValue(ordinal);
        }

        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

        public override byte GetByte(int ordinal) => throw new NotSupportedException();

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();

        public override char GetChar(int ordinal) => throw new NotSupportedException();

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();

        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

        public override double GetDouble(int ordinal) => throw new NotSupportedException();

        public override float GetFloat(int ordinal) => throw new NotSupportedException();

        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

        public override short GetInt16(int ordinal) => throw new NotSupportedException();

        public override int GetInt32(int ordinal) => throw new NotSupportedException();

        public override long GetInt64(int ordinal) => throw new NotSupportedException();

        public override string GetString(int ordinal) => throw new NotSupportedException();

        public override void Close() => _closed = true;

        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
