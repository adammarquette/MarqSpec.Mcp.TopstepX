using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Telemetry;

/// <summary>Tape-export counters record a closed outcome and ignore a zero count.</summary>
public sealed class TapeExportTelemetryTests
{
    [Fact]
    public void TapeExport_WhenCountIsPositive_RecordsTheOutcome()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> exports = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeExportsInstrument);

        telemetry.TapeExport(TapeExportOutcome.RowsExported, 4);

        CollectedMeasurement<long> measurement = exports.LastMeasurement!;
        measurement.Value.Should().Be(4);
        measurement.Tags[HostTelemetry.OutcomeTag].Should().Be(TapeExportOutcome.RowsExported);
        measurement.Tags.Keys.Should().Equal(HostTelemetry.OutcomeTag);
    }

    [Fact]
    public void TapeExport_WhenCountIsZero_RecordsNothing()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> exports = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeExportsInstrument);

        telemetry.TapeExport(TapeExportOutcome.RowsExported, 0);

        exports.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void TapeExportDuration_WhenAPassFinishes_RecordsSeconds()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<double> duration =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeExportDurationInstrument);

        telemetry.TapeExportDuration(1.25);

        duration.LastMeasurement!.Value.Should().Be(1.25);
        duration.LastMeasurement.Tags.Should().BeEmpty();
    }

    [Fact]
    public void TapeUpload_WhenCountIsPositive_RecordsTheOutcome()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);

        telemetry.TapeUpload(TapeUploadOutcome.Uploaded, 2);

        CollectedMeasurement<long> measurement = uploads.LastMeasurement!;
        measurement.Value.Should().Be(2);
        measurement.Tags[HostTelemetry.OutcomeTag].Should().Be(TapeUploadOutcome.Uploaded);
        measurement.Tags.Keys.Should().Equal(HostTelemetry.OutcomeTag);
    }

    [Fact]
    public void TapeUpload_WhenCountIsZero_RecordsNothing()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);

        telemetry.TapeUpload(TapeUploadOutcome.Failed, 0);

        uploads.GetMeasurementSnapshot().Should().BeEmpty();
    }
}
