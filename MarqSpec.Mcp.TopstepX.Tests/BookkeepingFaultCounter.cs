using System.Diagnostics.Metrics;
using MarqSpec.Mcp.TopstepX.Telemetry;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>
/// Counts bookkeeping faults on one <see cref="HostTelemetry"/> meter. A meter with the same name and a
/// different scope is ignored, and concurrent measurements add exactly.
/// </summary>
public sealed class BookkeepingFaultCounter
{
    private long _faults;

    public long Value => Interlocked.Read(ref _faults);

    /// <summary>Subscribes to <paramref name="telemetry"/> and returns the listener. The caller disposes it.</summary>
    public MeterListener Listen(HostTelemetry telemetry)
    {
        MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, telemetry)
                && instrument.Name == HostTelemetry.BookkeepingFaultsInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, _, _) => Interlocked.Add(ref _faults, measurement));
        listener.Start();
        return listener;
    }
}
