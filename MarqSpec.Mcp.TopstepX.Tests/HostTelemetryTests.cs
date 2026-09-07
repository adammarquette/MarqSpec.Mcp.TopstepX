using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>
/// The app-owned meter and activity source: what each instrument records, what it is tagged with, and — the
/// sharper half — what a tag value may never be (ADR-0019 decision 6, gh#536).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every assertion goes through a real listener.</b> An instrument nothing subscribes to records nothing,
/// so a test that asserted a method was called would pass over an instrument that had been created with the
/// wrong name, the wrong tags, or on a meter the pipeline never subscribes to. <c>MetricCollector</c>
/// subscribes to the instrument this test's own <see cref="HostTelemetry"/> owns — the meter carries the
/// instance as its <c>Scope</c>, so nothing another test in this assembly emits can reach these collectors.
/// </para>
/// <para>
/// <b>Nothing here reaches a network.</b> The unit tier needs no container and must stay that way; the
/// compose-stack measurement is recorded on the pull request.
/// </para>
/// </remarks>
[Collection(HostTelemetryCollection.Name)]
public sealed class HostTelemetryTests
{
    private const string Symbol = "ES";

    /// <summary>Anything that looks like an instant. A tag value matching this is a cardinality bomb.</summary>
    private static readonly Regex _looksLikeATimestamp = new(
        @"\d{4}-\d{2}-\d{2}|\d{2}:\d{2}|T\d{2}Z?|^\d{10,}$",
        RegexOptions.CultureInvariant);

    [Fact]
    public void TheMeterAndTheActivitySourceCarryTheRepositoryName()
    {
        // The name is the storage key in every backend, and both signals must carry the SAME one: a
        // dashboard filters spans and metrics by it, and two names would make a trace and its numbers two
        // unrelated things. ConfigureTelemetry subscribes by this string.
        using HostTelemetry telemetry = new();

        HostTelemetry.Name.Should().Be("MarqSpec.Mcp.TopstepX");
        telemetry.Activities.Name.Should().Be(HostTelemetry.Name);
    }

    [Theory]
    [InlineData(CacheSeries.Bars, CacheOutcome.Hit)]
    [InlineData(CacheSeries.Indicators, CacheOutcome.Miss)]
    [InlineData(CacheSeries.Footprint, CacheOutcome.Partial)]
    public void ACacheReadIsCountedWithItsSeriesSymbolResolutionAndOutcome(string series, string outcome)
    {
        // R-1.1 and R-1.3's number. Whether a read was served from the store or turned into a venue round
        // trip is invisible from outside the process, and it is the single number that says the cache-aside
        // design is working.
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = Collect(telemetry, HostTelemetry.CacheReadsInstrument);

        telemetry.CacheRead(series, Symbol, 5, outcome);

        CollectedMeasurement<long> measurement = reads.LastMeasurement!;
        measurement.Value.Should().Be(1);
        measurement.Tags[HostTelemetry.SeriesTag].Should().Be(series);
        measurement.Tags[HostTelemetry.SymbolTag].Should().Be(Symbol);
        measurement.Tags[HostTelemetry.ResolutionTag].Should().Be(5);
        measurement.Tags[HostTelemetry.OutcomeTag].Should().Be(outcome);
    }

    [Fact]
    public void AVenueCallIsCountedAndTimedWhenItsScopeCloses()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> calls = Collect(telemetry, HostTelemetry.VenueCallsInstrument);
        using MetricCollector<double> duration = Collect<double>(
            telemetry, HostTelemetry.VenueCallDurationInstrument);

        using (telemetry.VenueCall(VenueOperation.GetBars))
        {
            // Recorded on DISPOSE, not on entry: a duration taken at the start would be zero, and the whole
            // point of the histogram is where the time went.
            calls.LastMeasurement.Should().BeNull();
        }

        calls.LastMeasurement!.Value.Should().Be(1);
        calls.LastMeasurement.Tags[HostTelemetry.OperationTag].Should().Be(VenueOperation.GetBars);

        duration.LastMeasurement!.Value.Should().BeGreaterThanOrEqualTo(0);
        duration.LastMeasurement.Tags[HostTelemetry.OperationTag].Should().Be(VenueOperation.GetBars);
    }

    [Fact]
    public void AFailedVenueCallIsStillCounted_AndItsSpanCarriesNoVendorText()
    {
        // A vendor allowance is spent by the REQUEST, not by the answer, so a call that threw is a call that
        // was issued and took time. And the failure is a STATUS, never a message: the vendor's own string is
        // free text on a channel a language model reads (ADR-0008), and one has already come to rest holding
        // a credential in the sibling client.
        using HostTelemetry telemetry = new();
        using MetricCollector<long> calls = Collect(telemetry, HostTelemetry.VenueCallsInstrument);

        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        using (HostTelemetry.VenueCallScope scope = telemetry.VenueCall(VenueOperation.ResolveContracts))
        {
            scope.Failed();
        }

        calls.LastMeasurement!.Value.Should().Be(1);

        Activity span = spans.Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().BeNull();
    }

    [Fact]
    public void AVenueCallOpensAChildSpanNamedForItsOperation()
    {
        // "so a tool-call trace in Tempo shows where the time went" (gh#536). The SDK's tools/call span is
        // the parent in the running host; here an ambient activity stands in for it, and what is pinned is
        // that the venue span is a CHILD rather than a second root.
        using HostTelemetry telemetry = new();

        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        using ActivitySource parentSource = new("test.parent");
        using ActivityListener parentListener = Listen([], parentSource);
        using Activity? parent = parentSource.StartActivity("tools/call");

        parent.Should().NotBeNull("the stand-in parent has to be sampled, or this asserts nothing");

        using (telemetry.VenueCall(VenueOperation.FindContract))
        {
        }

        Activity span = spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("venue." + VenueOperation.FindContract);
        span.Kind.Should().Be(ActivityKind.Client);
        span.Parent.Should().Be(parent);
    }

    [Fact]
    public void ACacheReadOpensAChildSpanNamedForItsSeries()
    {
        using HostTelemetry telemetry = new();

        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        using ActivitySource parentSource = new("test.parent");
        using ActivityListener parentListener = Listen([], parentSource);
        using Activity? parent = parentSource.StartActivity("tools/call");

        parent.Should().NotBeNull("the stand-in parent has to be sampled, or this asserts nothing");

        using (telemetry.StartCacheRead(CacheSeries.Bars, Symbol, 15))
        {
        }

        Activity span = spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("cache." + CacheSeries.Bars);
        span.Parent.Should().Be(parent);
        span.GetTagItem(HostTelemetry.SymbolTag).Should().Be(Symbol);
        span.GetTagItem(HostTelemetry.ResolutionTag).Should().Be(15);
    }

    [Fact]
    public void NoSpanIsAllocatedWhenNothingIsListening()
    {
        // The zero-cost claim, and it is why the instrumentation is UNCONDITIONAL at every call site: with no
        // Otel__Endpoint there is no "is telemetry on" branch to get wrong, because the runtime already has
        // one and it is free. A stdio session on a laptop is exactly as quiet as it was.
        using HostTelemetry telemetry = new();

        telemetry.StartCacheRead(CacheSeries.Bars, Symbol, 5).Should().BeNull();

        using (telemetry.VenueCall(VenueOperation.GetAccounts))
        {
            Activity.Current.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(GapReason.Absent)]
    [InlineData(GapReason.Gap)]
    [InlineData(GapReason.Unattributed)]
    public void AGapFillCountsTheRangesFetchedAndWhyTheyWereOutstanding(string reason)
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> fills = Collect(telemetry, HostTelemetry.GapFillsInstrument);

        telemetry.GapFilled(Symbol, 5, reason, ranges: 3);

        CollectedMeasurement<long> measurement = fills.LastMeasurement!;
        measurement.Value.Should().Be(3);
        measurement.Tags[HostTelemetry.SymbolTag].Should().Be(Symbol);
        measurement.Tags[HostTelemetry.ResolutionTag].Should().Be(5);
        measurement.Tags[HostTelemetry.ReasonTag].Should().Be(reason);
    }

    [Fact]
    public void AReadThatFilledNothingRecordsNoGapFill()
    {
        // A zero-valued measurement is not free: it creates the tag set, so a warm read would manufacture a
        // `reason` series that never fired. The absence of a measurement is the honest statement.
        using HostTelemetry telemetry = new();
        using MetricCollector<long> fills = Collect(telemetry, HostTelemetry.GapFillsInstrument);

        telemetry.GapFilled(Symbol, 5, GapReason.Gap, ranges: 0);

        fills.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void ATapeTickIsCountedByInstrumentSymbol_NeverByContractId()
    {
        // A contract id rolls quarterly. Tagging with it would retire one time series and start another every
        // quarter, and the series anyone draws is the instrument's.
        using HostTelemetry telemetry = new();
        using MetricCollector<long> ticks = Collect(telemetry, HostTelemetry.TapeTicksInstrument);

        telemetry.TapeTick(Symbol);

        CollectedMeasurement<long> measurement = ticks.LastMeasurement!;
        measurement.Value.Should().Be(1);
        measurement.Tags.Should().ContainKey(HostTelemetry.SymbolTag)
            .WhoseValue.Should().Be(Symbol);
        measurement.Tags.Keys.Should().NotContain("contract");
    }

    [Theory]
    [InlineData(TapeTransition.Connected)]
    [InlineData(TapeTransition.Disconnected)]
    public void AHubTransitionIsCountedWithoutASymbol(string transition)
    {
        // ONE hub carries every instrument, so a reconnect is a fact about the connection. Attributing it to
        // each subscribed symbol would multiply one event into as many as the deployment happens to be
        // configured for -- the same incident, counted differently on two deployments.
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reconnects = Collect(telemetry, HostTelemetry.TapeReconnectsInstrument);

        telemetry.TapeReconnect(transition);

        CollectedMeasurement<long> measurement = reconnects.LastMeasurement!;
        measurement.Value.Should().Be(1);
        measurement.Tags[HostTelemetry.TransitionTag].Should().Be(transition);
        measurement.Tags.Keys.Should().NotContain(HostTelemetry.SymbolTag);
    }

    [Theory]
    [InlineData(TapeLeaseChange.Acquired)]
    [InlineData(TapeLeaseChange.Refused)]
    [InlineData(TapeLeaseChange.Lost)]
    public void ALeaseChangeIsCountedBySymbolAndByWhatHappened(string change)
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> changes = Collect(telemetry, HostTelemetry.TapeLeaseChangesInstrument);

        telemetry.TapeLeaseChanged(Symbol, change);

        CollectedMeasurement<long> measurement = changes.LastMeasurement!;
        measurement.Value.Should().Be(1);
        measurement.Tags[HostTelemetry.SymbolTag].Should().Be(Symbol);
        measurement.Tags[HostTelemetry.ChangeTag].Should().Be(change);
    }

    [Fact]
    public void AProjectionPassCountsItsValuesByIndicatorName_NeverByPeriod()
    {
        // An operator can configure additional periods (ADR-0018), so a period tag is a cardinality an
        // operator can grow without touching this repository.
        using HostTelemetry telemetry = new();
        using MetricCollector<long> projections = Collect(
            telemetry, HostTelemetry.IndicatorProjectionsInstrument);

        telemetry.IndicatorProjected("atr", Symbol, 5, values: 240);

        CollectedMeasurement<long> measurement = projections.LastMeasurement!;
        measurement.Value.Should().Be(240);
        measurement.Tags[HostTelemetry.IndicatorTag].Should().Be("atr");
        measurement.Tags[HostTelemetry.SymbolTag].Should().Be(Symbol);
        measurement.Tags.Keys.Should().NotContain("period");
    }

    [Fact]
    public void AConfirmingPassThatWroteNothingRecordsNoProjection()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> projections = Collect(
            telemetry, HostTelemetry.IndicatorProjectionsInstrument);

        telemetry.IndicatorProjected("atr", Symbol, 5, values: 0);

        projections.GetMeasurementSnapshot().Should().BeEmpty();
    }

    // ── The vocabulary gate ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryTagKeyEveryInstrumentEmitsIsOneThisRepositoryNames()
    {
        // THE GATE, half one: the tag KEYS are enumerated here, so an instrument that quietly grows a tag has
        // to come through this list. A key nobody enumerated is a dimension no dashboard knows about and no
        // reviewer priced.
        HashSet<string> allowed =
        [
            HostTelemetry.SeriesTag,
            HostTelemetry.OutcomeTag,
            HostTelemetry.SymbolTag,
            HostTelemetry.ResolutionTag,
            HostTelemetry.OperationTag,
            HostTelemetry.ReasonTag,
            HostTelemetry.TransitionTag,
            HostTelemetry.ChangeTag,
            HostTelemetry.IndicatorTag,
        ];

        foreach (CollectedMeasurement<long> measurement in EveryLongMeasurement())
        {
            measurement.Tags.Keys.Should().BeSubsetOf(allowed);
        }
    }

    [Fact]
    public void NoTagValueIsATimestampAContractIdOrVendorFreeText()
    {
        // THE GATE, half two, and the one the issue asks for by name. Each of these turns ONE time series
        // into an unbounded family: a timestamp is a new series per read, a contract id is a new one per
        // quarter per instrument, and a vendor message is a new one per distinct sentence -- which is also
        // how a credential or an account number reaches a backend an operator screenshots.
        //
        // A COUNTER KEEPS ONE ACCUMULATOR PER DISTINCT TAG SET FOR THE LIFE OF THE PROCESS, so this is a
        // memory leak here before it is a bill anywhere else.
        HashSet<string> vocabulary =
        [
            .. CacheSeries.All,
            .. CacheOutcome.All,
            .. VenueOperation.All,
            .. GapReason.All,
            .. TapeTransition.All,
            .. TapeLeaseChange.All,
            Symbol,
            "atr",
        ];

        foreach (CollectedMeasurement<long> measurement in EveryLongMeasurement())
        {
            foreach (KeyValuePair<string, object?> tag in measurement.Tags)
            {
                if (tag.Value is int resolution)
                {
                    // A resolution is a number, and the only tag value that is BOUNDED rather than CLOSED.
                    // Everything else here comes from a vocabulary this repository writes down; this one is
                    // chosen by the CALLER, and the only thing standing over it is
                    // ToolGuards.ValidateResolution, which admits any integer from 1 to
                    // ToolGuards.MaxResolutionMinutes (1,379). There is no configured resolution set and no
                    // MarketData__Resolutions key -- an earlier version of this comment named one, and the
                    // sentence read as though `resolution` were closed the way `series` and `outcome` are.
                    //
                    // What that costs, stated rather than implied: a caller walking r = 1..1379 pins on the
                    // order of 1,379 x symbols x 3 series x 3 outcomes accumulators on mcp.cache.reads for
                    // the life of the process, and get_market_snapshot takes an uncapped int[] of
                    // resolutions, so it is reachable in a handful of calls. It is ACCEPTED rather than
                    // fixed: the ceiling is enforced BEFORE the tag is ever written, so the set is finite by
                    // construction, no answer is wrong or missing, and the same caller can already create
                    // the same number of distinct stored series -- a larger, pre-existing exposure this
                    // instrumentation neither creates nor worsens (gh#536 review, finding 1).
                    resolution.Should().BePositive();
                    resolution.Should().BeLessThanOrEqualTo(ToolGuards.MaxResolutionMinutes);
                    continue;
                }

                string value = tag.Value.Should().BeOfType<string>().Subject;

                value.Should().NotMatchRegex(
                    _looksLikeATimestamp.ToString(),
                    "tag '{0}' carried something shaped like an instant",
                    tag.Key);
                value.Should().NotContain(
                    "CON.F.US.", "tag '{0}' carried a venue contract id", tag.Key);
                value.Should().NotContain(
                    " ", "tag '{0}' carried something shaped like a sentence", tag.Key);
                value.Should().BeOneOf(vocabulary, "tag '{0}' left its closed vocabulary", tag.Key);
            }
        }
    }

    [Fact]
    public void EveryClosedVocabularyValueIsLowercaseAndStable()
    {
        // These strings are storage keys in every backend already scraping them, exactly as an IIndicator's
        // Name is in the store. A rename orphans the panels built on the old one, where they read back as an
        // absence rather than as an error -- so the shape is pinned rather than left to house style.
        IEnumerable<string> all =
        [
            .. CacheSeries.All,
            .. CacheOutcome.All,
            .. VenueOperation.All,
            .. GapReason.All,
            .. TapeTransition.All,
            .. TapeLeaseChange.All,
        ];

        foreach (string value in all)
        {
            value.Should().NotBeNullOrWhiteSpace();
            value.Should().Be(value.ToLower(CultureInfo.InvariantCulture));
            value.Should().MatchRegex("^[a-z][a-z_]*$");
        }
    }

    /// <summary>Drives every counter once and returns every measurement they produced.</summary>
    /// <remarks>
    /// One <see cref="HostTelemetry"/> and one pass, so the two gates above read the same emissions. A gate
    /// that enumerated the instruments by hand would go green the moment one was added.
    /// </remarks>
    private static List<CollectedMeasurement<long>> EveryLongMeasurement()
    {
        using HostTelemetry telemetry = new();

        List<MetricCollector<long>> collectors =
        [
            Collect(telemetry, HostTelemetry.CacheReadsInstrument),
            Collect(telemetry, HostTelemetry.VenueCallsInstrument),
            Collect(telemetry, HostTelemetry.GapFillsInstrument),
            Collect(telemetry, HostTelemetry.TapeTicksInstrument),
            Collect(telemetry, HostTelemetry.TapeReconnectsInstrument),
            Collect(telemetry, HostTelemetry.TapeLeaseChangesInstrument),
            Collect(telemetry, HostTelemetry.IndicatorProjectionsInstrument),
        ];

        foreach (string series in CacheSeries.All)
        {
            foreach (string outcome in CacheOutcome.All)
            {
                telemetry.CacheRead(series, Symbol, 5, outcome);
            }
        }

        foreach (string operation in VenueOperation.All)
        {
            telemetry.VenueCall(operation).Dispose();
        }

        foreach (string reason in GapReason.All)
        {
            telemetry.GapFilled(Symbol, 5, reason, ranges: 1);
        }

        telemetry.TapeTick(Symbol);

        foreach (string transition in TapeTransition.All)
        {
            telemetry.TapeReconnect(transition);
        }

        foreach (string change in TapeLeaseChange.All)
        {
            telemetry.TapeLeaseChanged(Symbol, change);
        }

        telemetry.IndicatorProjected("atr", Symbol, 5, values: 1);

        List<CollectedMeasurement<long>> measurements = [];
        foreach (MetricCollector<long> collector in collectors)
        {
            measurements.AddRange(collector.GetMeasurementSnapshot());
            collector.Dispose();
        }

        measurements.Should().NotBeEmpty(
            "the collectors must be subscribed at all, or both gates assert over an empty list and pass "
            + "forever");

        return measurements;
    }

    private static MetricCollector<long> Collect(HostTelemetry telemetry, string instrument) =>
        Collect<long>(telemetry, instrument);

    private static MetricCollector<T> Collect<T>(HostTelemetry telemetry, string instrument)
        where T : struct =>
        new(telemetry, HostTelemetry.Name, instrument);

    /// <summary>Subscribes to one <see cref="ActivitySource"/> INSTANCE and records what it starts.</summary>
    /// <param name="into">Where to put the stopped activities.</param>
    /// <param name="source">The exact source to listen to.</param>
    /// <returns>The listener. The caller disposes it.</returns>
    /// <remarks>
    /// <b>Reference equality, not <c>candidate.Name == HostTelemetry.Name</c>.</b> Before gh#562, every cache
    /// service fell back to its own <c>new HostTelemetry()</c> when DI did not supply the singleton, so about a
    /// dozen suites that never mentioned telemetry emitted <c>cache.*</c> spans under that same name — and they
    /// ran in PARALLEL with this one, because <see cref="HostTelemetryCollection"/> only serialises the suites
    /// that listen. Matching by name therefore let another suite's span land in this list, and a
    /// <c>ContainSingle()</c> below saw two whenever the timing lined up (gh#536). The fallback is gone now —
    /// every construction site takes the instance explicitly — but the listener stays reference-equal rather
    /// than reverting to a name match, because a name match is the wrong test on its own terms: two suites can
    /// legitimately build two different <see cref="HostTelemetry"/> instances that happen to share the name.
    /// </remarks>
    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
