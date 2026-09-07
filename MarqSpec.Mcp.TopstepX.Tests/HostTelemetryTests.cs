using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;
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
/// <b>The cardinality gate at the foot of this file enumerates no instrument</b>, and its header says what it
/// does rather than what it wishes it did. It reads one pass of <see cref="HostTelemetryDriver"/>, which
/// discovers the instruments off the meter and drives them reflectively, so a <c>double</c> histogram and a
/// ninth instrument are both inside it — neither was, before gh#559.
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

    /// <summary>A configured session name — what a <see cref="SeriesKey.Session"/> tags with.</summary>
    private const string SessionName = HostTelemetryDriver.Session;

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

    // ── The series-key overloads ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AResolutionKeyProjectionEmitsTheSameTagsAsTheIntOverload()
    {
        // THE NEUTRALITY PIN. Every existing panel is built on these three tag keys and these three values,
        // and the key-taking overload is a refactor of the call site rather than a change to the series it
        // feeds: a fourth tag, or a renamed one, retires every stored series in every backend scraping it.
        using HostTelemetry telemetry = new();
        using MetricCollector<long> projections = Collect(
            telemetry, HostTelemetry.IndicatorProjectionsInstrument);

        telemetry.IndicatorProjected("atr", Symbol, 5, values: 240);
        telemetry.IndicatorProjected(
            "atr", Symbol, new SeriesKey.Resolution("test", Symbol, 5), values: 240);

        IReadOnlyList<CollectedMeasurement<long>> measured = projections.GetMeasurementSnapshot();
        measured.Should().HaveCount(2);

        measured[1].Value.Should().Be(measured[0].Value);
        measured[1].Tags.Should().Equal(measured[0].Tags);

        measured[1].Tags.Keys.Should().BeEquivalentTo(
            [HostTelemetry.IndicatorTag, HostTelemetry.SymbolTag, HostTelemetry.ResolutionTag]);
        measured[1].Tags[HostTelemetry.ResolutionTag].Should().Be(5);
    }

    [Fact]
    public void ASessionKeyProjectionNamesTheSession_AndCarriesNoResolution()
    {
        // A session is a wall-clock window whose minutes move with daylight saving, so there is no honest
        // number to put in `resolution` — and a sentinel would silently merge the session series into
        // whatever real resolution it collided with.
        using HostTelemetry telemetry = new();
        using MetricCollector<long> projections = Collect(
            telemetry, HostTelemetry.IndicatorProjectionsInstrument);

        telemetry.IndicatorProjected("atr", Symbol, new SeriesKey.Session("test", Symbol, SessionName), values: 7);

        CollectedMeasurement<long> measurement = projections.LastMeasurement!;
        measurement.Value.Should().Be(7);
        measurement.Tags[HostTelemetry.IndicatorTag].Should().Be("atr");
        measurement.Tags[HostTelemetry.SymbolTag].Should().Be(Symbol);
        measurement.Tags[HostTelemetry.SessionTag].Should().Be(SessionName);
        measurement.Tags.Keys.Should().NotContain(HostTelemetry.ResolutionTag);
    }

    [Fact]
    public void ACacheReadOverAResolutionKeyEmitsTheSameTagsAsTheIntOverload()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = Collect(telemetry, HostTelemetry.CacheReadsInstrument);

        telemetry.CacheRead(CacheSeries.Indicators, Symbol, 5, CacheOutcome.Hit);
        telemetry.CacheRead(
            CacheSeries.Indicators,
            Symbol,
            new SeriesKey.Resolution("test", Symbol, 5),
            CacheOutcome.Hit);

        IReadOnlyList<CollectedMeasurement<long>> measured = reads.GetMeasurementSnapshot();
        measured.Should().HaveCount(2);
        measured[1].Tags.Should().Equal(measured[0].Tags);
    }

    [Fact]
    public void ACacheReadOverASessionKeyNamesTheSession_AndCarriesNoResolution()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> reads = Collect(telemetry, HostTelemetry.CacheReadsInstrument);

        telemetry.CacheRead(
            CacheSeries.Indicators,
            Symbol,
            new SeriesKey.Session("test", Symbol, SessionName),
            CacheOutcome.Miss);

        CollectedMeasurement<long> measurement = reads.LastMeasurement!;
        measurement.Tags[HostTelemetry.SeriesTag].Should().Be(CacheSeries.Indicators);
        measurement.Tags[HostTelemetry.SymbolTag].Should().Be(Symbol);
        measurement.Tags[HostTelemetry.SessionTag].Should().Be(SessionName);
        measurement.Tags[HostTelemetry.OutcomeTag].Should().Be(CacheOutcome.Miss);
        measurement.Tags.Keys.Should().NotContain(HostTelemetry.ResolutionTag);
    }

    [Fact]
    public void ACacheReadSpanOverASeriesKeyCarriesTheKeysOwnDimension()
    {
        using HostTelemetry telemetry = new();

        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        using (telemetry.StartCacheRead(
            CacheSeries.Indicators, Symbol, new SeriesKey.Resolution("test", Symbol, 15)))
        {
        }

        using (telemetry.StartCacheRead(
            CacheSeries.Indicators, Symbol, new SeriesKey.Session("test", Symbol, SessionName)))
        {
        }

        spans.Should().HaveCount(2);

        spans[0].OperationName.Should().Be("cache." + CacheSeries.Indicators);
        spans[0].GetTagItem(HostTelemetry.ResolutionTag).Should().Be(15);
        spans[0].GetTagItem(HostTelemetry.SessionTag).Should().BeNull();

        spans[1].OperationName.Should().Be("cache." + CacheSeries.Indicators);
        spans[1].GetTagItem(HostTelemetry.SessionTag).Should().Be(SessionName);
        spans[1].GetTagItem(HostTelemetry.ResolutionTag).Should().BeNull();
    }

    // ── The cardinality gate ─────────────────────────────────────────────────────────────────────────
    //
    // What follows guards CARDINALITY, which is the failure mode that never announces itself: an unbounded
    // tag breaks no build and fails no request, it multiplies time series until a backend bill or a query
    // timeout is the first signal. A Counter<T> keeps one accumulator per distinct tag set FOR THE LIFE OF
    // THE PROCESS, so it is a leak here before it is a cost anywhere else.
    //
    // All three read one pass of HostTelemetryDriver, which DISCOVERS the instruments off the meter through
    // a MeterListener and drives them through HostTelemetry's own public methods by reflection. Nothing
    // below enumerates an instrument, and that is the whole point: the gate this replaced listed its
    // collectors, so mcp.venue.call.duration -- a double histogram a MetricCollector<long> cannot see --
    // reached neither half of it, and a ninth instrument would have reached neither either, silently and
    // greenly (gh#559). The driver's header states exactly what this can and cannot decide.

    [Fact]
    public void EveryInstrumentOnTheMeterIsDrivenByThisGate()
    {
        // THE GATE'S OWN FOUNDATION, and it is asserted rather than assumed. Every way of driving nothing is
        // a failure here: no instruments discovered, no measurements recorded, an instrument declared but
        // never created, or -- the one that matters -- an instrument created on the meter that no public
        // method records to, which is an instrument the two gates below would never see.
        TelemetryDrive drive = HostTelemetryDriver.Run();

        drive.Instruments.Should().NotBeEmpty(
            "the listener must be subscribed at all, or every gate below asserts over an empty list and "
            + "passes forever");
        drive.Measurements.Should().NotBeEmpty(
            "the drive must reach the instruments at all, or every gate below asserts over an empty list "
            + "and passes forever");

        drive.Instruments.Should().BeEquivalentTo(
            HostTelemetryDriver.DeclaredInstrumentNames,
            "every instrument created on the meter is named by a `…Instrument` constant and every constant "
            + "names an instrument -- one without the other is a name a dashboard cannot find, or an "
            + "instrument nothing declares");

        drive.Measurements.Select(measurement => measurement.Instrument).Distinct()
            .Should().BeEquivalentTo(
                drive.Instruments,
                "an instrument no public method on HostTelemetry records to is one the cardinality gates "
                + "never see, whatever it is tagged with");
    }

    [Fact]
    public void EveryTagKeyEveryInstrumentEmitsIsOneThisRepositoryDeclares()
    {
        // THE GATE, half one: the allowed tag KEYS are the `…Tag` constants on HostTelemetry, read off the
        // declarations rather than copied beside them. So an instrument that quietly grows a tag fails here,
        // and the only way to green is to DECLARE the tag -- a deliberate edit to the file whose header says
        // every value comes from a closed vocabulary, which is the moment the cost gets priced.
        //
        // Both directions: an undeclared key is a dimension nobody wrote down, and a declared key nothing
        // emits is a constant that has outlived its instrument and would launder the next one.
        TelemetryDrive drive = HostTelemetryDriver.Run();

        foreach (DrivenMeasurement measurement in drive.Measurements)
        {
            measurement.Tags.Select(tag => tag.Key).Should().BeSubsetOf(
                HostTelemetryDriver.DeclaredTagKeys,
                "instrument '{0}' emitted a tag key this repository never declared",
                measurement.Instrument);
        }

        drive.Measurements.SelectMany(measurement => measurement.Tags).Select(tag => tag.Key).Distinct()
            .Should().BeEquivalentTo(
                HostTelemetryDriver.DeclaredTagKeys,
                "every declared tag key is emitted by something, or the declaration is a dead allowance");
    }

    [Fact]
    public void NoTagValueIsATimestampAContractIdOrVendorFreeText()
    {
        // THE GATE, half two. Each of these turns ONE time series into an unbounded family: a timestamp is a
        // new series per read, a contract id is a new one per quarter per instrument, and a vendor message is
        // a new one per distinct sentence -- which is also how a credential or an account number reaches a
        // backend an operator screenshots.
        //
        // The allowed set is the closed vocabularies THEMSELVES, discovered off the telemetry namespace, plus
        // the symbol and the indicator name the drive passes. What this catches is any value HostTelemetry
        // MANUFACTURES rather than forwards -- a formatted instant, a machine name, a contract id built from
        // a symbol. What it cannot catch is a forwarded value whose closedness lives at the call site; for
        // the one tag where the call sites are decidable, VenueCallGuardTests decides them against
        // ProjectXMarketDataGateway's compiled body.
        HashSet<string> vocabulary =
        [
            .. HostTelemetryDriver.ClosedVocabularyValues,
            HostTelemetryDriver.Symbol,
            HostTelemetryDriver.Indicator,

            // A session NAME, and it is bounded the way `resolution` is rather than closed the way `series`
            // is: MarketData__Sessions names them, so the set is whatever an operator configured. It is a
            // lowercase word from configuration, never a wall-clock window and never a contract.
            HostTelemetryDriver.Session,
        ];

        vocabulary.Should().NotBeEmpty("the vocabularies must be discovered, or this admits everything");

        TelemetryDrive drive = HostTelemetryDriver.Run();

        foreach (DrivenMeasurement measurement in drive.Measurements)
        {
            foreach (KeyValuePair<string, object?> tag in measurement.Tags)
            {
                if (tag.Value is int resolution)
                {
                    // A resolution is a number, and the only tag value that is BOUNDED rather than CLOSED.
                    // Everything else here comes from a vocabulary this repository writes down; this one is
                    // chosen by the CALLER, and the only thing standing over it is
                    // ToolGuards.ValidateResolution, which admits any integer from 1 to
                    // ToolGuards.MaxResolutionMinutes (660). There is no configured resolution set and no
                    // MarketData__Resolutions key -- an earlier version of this comment named one, and the
                    // sentence read as though `resolution` were closed the way `series` and `outcome` are.
                    //
                    // What that costs, stated rather than implied: a caller walking r = 1..660 pins on the
                    // order of 660 x symbols x 3 series x 3 outcomes accumulators on mcp.cache.reads for
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
                    "instrument '{0}' tagged '{1}' with something shaped like an instant",
                    measurement.Instrument,
                    tag.Key);
                value.Should().NotContain(
                    "CON.F.US.",
                    "instrument '{0}' tagged '{1}' with a venue contract id",
                    measurement.Instrument,
                    tag.Key);
                value.Should().NotContain(
                    " ",
                    "instrument '{0}' tagged '{1}' with something shaped like a sentence",
                    measurement.Instrument,
                    tag.Key);
                value.Should().BeOneOf(
                    vocabulary,
                    "instrument '{0}' tagged '{1}' with a value no closed vocabulary names",
                    measurement.Instrument,
                    tag.Key);
            }
        }
    }

    [Fact]
    public void EveryClosedVocabularyValueIsLowercaseAndStable()
    {
        // These strings are storage keys in every backend already scraping them, exactly as an IIndicator's
        // Name is in the store. A rename orphans the panels built on the old one, where they read back as an
        // absence rather than an error -- so the shape is pinned rather than left to house style.
        //
        // The vocabularies are DISCOVERED, so a seventh one is held to this the day it is written.
        IReadOnlyList<string> all = HostTelemetryDriver.ClosedVocabularyValues;

        all.Should().NotBeEmpty("the vocabularies must be discovered, or this pins nothing");
        all.Should().OnlyHaveUniqueItems(
            "two vocabularies sharing a value would make one tag's series answer to the other's name");

        foreach (string value in all)
        {
            value.Should().NotBeNullOrWhiteSpace();
            value.Should().Be(value.ToLower(CultureInfo.InvariantCulture));
            value.Should().MatchRegex("^[a-z][a-z_]*$");
        }
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
