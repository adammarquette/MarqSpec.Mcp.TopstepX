using System.Diagnostics.Metrics;
using System.Reflection;
using MarqSpec.Mcp.TopstepX.Telemetry;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>One measurement a driven instrument produced, with the tag set it carried.</summary>
/// <param name="Instrument">The instrument's name, as a backend stores it.</param>
/// <param name="Tags">The tag set, copied out of the callback's span.</param>
internal sealed record DrivenMeasurement(
    string Instrument,
    IReadOnlyList<KeyValuePair<string, object?>> Tags);

/// <summary>What one pass over <see cref="HostTelemetry"/> saw.</summary>
/// <param name="Instruments">
/// Every instrument published on the meter — <b>discovered, not listed</b>, so a new one is in this set the
/// moment its <c>CreateCounter</c> or <c>CreateHistogram</c> call is written.
/// </param>
/// <param name="Measurements">Every measurement the drive produced, in the order they were recorded.</param>
internal sealed record TelemetryDrive(
    IReadOnlyList<string> Instruments,
    IReadOnlyList<DrivenMeasurement> Measurements);

/// <summary>
/// Drives every instrument <see cref="HostTelemetry"/> owns and collects what they recorded, so the
/// cardinality gates in <see cref="HostTelemetryTests"/> assert over the instruments that <i>exist</i> rather
/// than over the ones somebody remembered to add to a list (gh#559).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is enumerated by hand, and that is the whole point.</b> The gate this replaced listed its
/// collectors and wrote out its own drive calls, so it protected exactly the instruments in that list: the
/// <c>double</c> histogram <c>mcp.venue.call.duration</c> was outside it — a <c>MetricCollector&lt;long&gt;</c>
/// cannot see a <c>double</c> — and a ninth instrument added tomorrow would have been outside it too, silently
/// and greenly. Cardinality is the failure mode that does not announce itself: an unbounded tag breaks no
/// build and fails no request, it multiplies time series until a bill or a query timeout is the first signal.
/// A gate that only guards what it was told about is not a gate.
/// </para>
/// <para>
/// <b>Instruments are discovered through a <see cref="MeterListener"/>, which is type-blind.</b>
/// <c>InstrumentPublished</c> fires for every instrument on the meter whatever its measurement type, and
/// <see cref="MeterListener.SetMeasurementEventCallback{T}"/> is registered for all seven types the API
/// admits — so <c>double</c> and <c>decimal</c> instruments are covered <i>by construction</i> rather than by
/// somebody noticing. The listener is scoped by <c>Meter.Scope</c> reference equality to the
/// <see cref="HostTelemetry"/> this drive built, so nothing another suite in this assembly emits under the
/// same meter name can reach it.
/// </para>
/// <para>
/// <b>Instruments are driven through <see cref="HostTelemetry"/>'s own public methods, reflectively.</b>
/// That is the half that makes "every instrument is driven" enforceable: a new instrument reaches a backend
/// only by way of a public method that records to it, so driving every public method reaches every instrument
/// that is wired up at all — and <see cref="HostTelemetryTests"/> fails on any published instrument that
/// produced no measurement, which is precisely the case of an instrument created and then recorded from
/// somewhere this gate cannot see.
/// </para>
/// <para>
/// <b>A parameter shape this driver cannot synthesise is an error, never a skip</b> (see
/// <see cref="ArgumentFor"/>). A driver whose failure mode is silent success is worse than the hand-written
/// list it replaces, so every way of driving nothing is loud: an unsynthesisable parameter throws here, and
/// an empty instrument set, an empty measurement set or an undriven instrument each fail an assertion there.
/// </para>
/// <para>
/// <b>What this gate can and cannot decide.</b> It decides everything <see cref="HostTelemetry"/> itself
/// controls: which instruments exist, which tag <i>keys</i> each one writes, and any tag value the class
/// <i>manufactures</i> rather than forwards — a machine name, a formatted instant, a contract id built from a
/// symbol. It cannot decide that a value the class merely forwards is drawn from a closed vocabulary, because
/// that is a property of the <i>call sites</i> in the host and not of this class, which forwards whatever
/// string it is handed. For the one tag where the call sites are decidable, they are decided:
/// <c>VenueCallGuardTests</c> reads <c>ProjectXMarketDataGateway</c>'s compiled call sites for the
/// <c>operation</c> tag.
/// </para>
/// </remarks>
internal static class HostTelemetryDriver
{
    /// <summary>The venue-neutral instrument symbol every drive uses for a <c>symbol</c> tag.</summary>
    internal const string Symbol = "ES";

    /// <summary>The indicator name every drive uses for an <c>indicator</c> tag.</summary>
    internal const string Indicator = "atr";

    /// <summary>The resolution — and every other integer — a drive passes. Inside the tool guard's bound.</summary>
    internal const int Resolution = 5;

    /// <summary>The tag keys this repository declares, read off the <c>…Tag</c> constants.</summary>
    /// <remarks>
    /// <b>The declarations are the list.</b> A tag key that is not one of these is a dimension nobody wrote
    /// down, no dashboard knows about and no reviewer priced — and declaring one is a deliberate edit to the
    /// file whose header says every value comes from a closed vocabulary, which is the moment the question
    /// gets asked.
    /// </remarks>
    internal static IReadOnlyList<string> DeclaredTagKeys { get; } = ConstantsNamedWith("Tag");

    /// <summary>The instrument names this repository declares, read off the <c>…Instrument</c> constants.</summary>
    internal static IReadOnlyList<string> DeclaredInstrumentNames { get; } = ConstantsNamedWith("Instrument");

    /// <summary>Every value of every closed vocabulary in the telemetry namespace.</summary>
    /// <remarks>
    /// Discovered as "a public static class beside <see cref="HostTelemetry"/> exposing
    /// <c>public static IReadOnlyList&lt;string&gt; All</c>", which is the shape all six are written in — so a
    /// seventh vocabulary joins the gate by existing, and a vocabulary that stops exposing <c>All</c> takes
    /// its values out of the allowed set rather than out of the check.
    /// </remarks>
    internal static IReadOnlyList<string> ClosedVocabularyValues { get; } = DiscoverVocabularies();

    /// <summary>The values a drive passes for every <see cref="string"/> parameter, one drive each.</summary>
    internal static IReadOnlyList<string> Candidates { get; } =
        [.. ClosedVocabularyValues, Symbol, Indicator];

    /// <summary>Drives every public recording method once per candidate value and returns what was recorded.</summary>
    /// <returns>The instruments published on the meter, and every measurement the drive produced.</returns>
    /// <exception cref="InvalidOperationException">
    /// A public method on <see cref="HostTelemetry"/> takes a parameter shape this driver cannot synthesise.
    /// </exception>
    internal static TelemetryDrive Run()
    {
        using HostTelemetry telemetry = new();

        List<string> published = [];
        List<DrivenMeasurement> measurements = [];

        using (MeterListener listener = new())
        {
            listener.InstrumentPublished = (instrument, subscriber) =>
            {
                // Reference equality on the SCOPE, not a name match: every HostTelemetry in the process
                // carries a meter with the same name, and a name match would collect another suite's.
                if (!ReferenceEquals(instrument.Meter.Scope, telemetry))
                {
                    return;
                }

                published.Add(instrument.Name);
                subscriber.EnableMeasurementEvents(instrument);
            };

            // All seven measurement types the Metrics API admits. Registering only the ones in use today is
            // exactly the bug this gate exists to close.
            RecordInto<byte>(listener, measurements);
            RecordInto<short>(listener, measurements);
            RecordInto<int>(listener, measurements);
            RecordInto<long>(listener, measurements);
            RecordInto<float>(listener, measurements);
            RecordInto<double>(listener, measurements);
            RecordInto<decimal>(listener, measurements);

            listener.Start();

            foreach (MethodInfo method in RecordingMethods())
            {
                foreach (string candidate in Candidates)
                {
                    object?[] arguments =
                        [.. method.GetParameters().Select(parameter => ArgumentFor(method, parameter, candidate))];

                    object? returned = method.Invoke(telemetry, arguments);

                    // A scope records on DISPOSE — that is where mcp.venue.calls and mcp.venue.call.duration
                    // are written — so a drive that only called the method would measure nothing at all.
                    (returned as IDisposable)?.Dispose();
                }
            }
        }

        return new TelemetryDrive(published, measurements);
    }

    /// <summary>Every public instance method that could record a measurement.</summary>
    /// <remarks>
    /// <c>Dispose</c> is the one exclusion, and it is not an exception to the rule: it tears the meter down,
    /// so driving it would end the pass rather than measure anything. Property accessors are excluded by
    /// <see cref="MethodBase.IsSpecialName"/>, and inherited <see cref="object"/> members by
    /// <see cref="BindingFlags.DeclaredOnly"/>.
    /// </remarks>
    private static IEnumerable<MethodInfo> RecordingMethods() =>
        typeof(HostTelemetry)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Where(method => method.Name != nameof(IDisposable.Dispose));

    /// <summary>Synthesises one argument, or refuses loudly.</summary>
    /// <param name="method">The method being driven, for the message.</param>
    /// <param name="parameter">The parameter to fill.</param>
    /// <param name="candidate">The string value this pass is driving with.</param>
    /// <returns>The argument.</returns>
    /// <exception cref="InvalidOperationException">The parameter's type has no synthesis rule.</exception>
    /// <remarks>
    /// <b>Refusing is the point.</b> Skipping a method this driver did not understand would take every
    /// instrument that method records straight out of both gates, quietly and greenly — the exact shape of
    /// the defect gh#559 is about.
    /// </remarks>
    private static object ArgumentFor(MethodInfo method, ParameterInfo parameter, string candidate)
    {
        if (parameter.ParameterType == typeof(string))
        {
            return candidate;
        }

        if (parameter.ParameterType == typeof(int))
        {
            // Every integer parameter on this class is either a resolution or a positive count, and a count
            // of zero is not recorded at all — so a shared positive value drives both.
            return Resolution;
        }

        if (parameter.ParameterType == typeof(double))
        {
            return (double)Resolution;
        }

        throw new InvalidOperationException(
            $"HostTelemetry.{method.Name} takes a {parameter.ParameterType.Name} parameter "
            + $"'{parameter.Name}' that this gate cannot synthesise. Teach HostTelemetryDriver.ArgumentFor "
            + "about it — skipping the method would take every instrument it records out of the cardinality "
            + "gates without failing anything.");
    }

    /// <summary>Records every measurement of one type into <paramref name="into"/>.</summary>
    /// <typeparam name="T">The measurement type.</typeparam>
    /// <param name="listener">The listener to register on.</param>
    /// <param name="into">Where the measurements go.</param>
    private static void RecordInto<T>(MeterListener listener, List<DrivenMeasurement> into)
        where T : struct =>
        listener.SetMeasurementEventCallback<T>(
            (instrument, measurement, tags, state) =>
                into.Add(new DrivenMeasurement(instrument.Name, tags.ToArray())));

    /// <summary>The values of every <c>public const string</c> on <see cref="HostTelemetry"/> so named.</summary>
    /// <param name="suffix">The name suffix that marks the constant's role.</param>
    /// <returns>The declared values.</returns>
    private static IReadOnlyList<string> ConstantsNamedWith(string suffix) =>
    [
        .. typeof(HostTelemetry)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Where(field => field.Name.EndsWith(suffix, StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!),
    ];

    /// <summary>Every closed vocabulary declared beside <see cref="HostTelemetry"/>, flattened.</summary>
    /// <returns>Every value of every vocabulary.</returns>
    private static IReadOnlyList<string> DiscoverVocabularies() =>
    [
        .. typeof(HostTelemetry).Assembly
            .GetTypes()
            .Where(type => type.IsClass && type.IsAbstract && type.IsSealed && type.IsPublic)
            .Where(type => type.Namespace == typeof(HostTelemetry).Namespace)
            .Select(type => type.GetProperty("All", BindingFlags.Public | BindingFlags.Static))
            .Where(property => property?.PropertyType == typeof(IReadOnlyList<string>))
            .SelectMany(property => (IReadOnlyList<string>)property!.GetValue(null)!),
    ];
}
