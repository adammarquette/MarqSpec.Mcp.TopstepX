using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_key_levels</c> — support and resistance zones, scored for confluence across whichever methods the
/// caller names.
/// </summary>
public sealed partial class MarketDataTools
{
    /// <summary>Detects support and resistance zones.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The timeframe in minutes.</param>
    /// <param name="lookbackBars">How much history to detect over.</param>
    /// <param name="pivotSource">Which price on a bar a pivot is measured from, or null for the configured one.</param>
    /// <param name="pivotLookback">How many bars to its left a pivot must dominate, or null for the configured one.</param>
    /// <param name="pivotRightLookback">How many bars to its right a pivot must dominate, or null for the configured one.</param>
    /// <param name="methods">
    /// The level methods to run, comma-separated, or null for <c>swing</c>. Unknown names are an error
    /// listing the known ones.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The levels, ordered by price, plus the confluence score over the requested methods.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get key levels")]
    [Description(
        "Detects support and resistance as ZONES rather than lines, sized in ATR multiples so a zone is "
        + "comparably wide across instruments. Significance is prominence in ATR multiples, so a 2.0 on ES "
        + "and a 2.0 on NQ mean the same thing. A zone's support/resistance label is assigned relative to the "
        + "CURRENT price, not to how it formed — a broken resistance is today's support. Detection is "
        + "confined to the contract in front: if the lookback spans a quarterly roll, `detectedOverBars` is "
        + "smaller than the lookback asked for, because a level from the expiring contract sits at a price "
        + "the current one has never traded. The SAME truncation also happens when the window holds bars "
        + "with no recorded contract — history cached before this server tracked provenance. Read "
        + "`contracts.span` to tell the two apart: `SpansRoll` means the store has two DIFFERENT recorded "
        + "contracts — a real roll — even when an unattributed run also sits in the window. `Unknown` means "
        + "at least one run's contract was never recorded and the known ones never disagree — genuinely "
        + "cannot tell whether a roll happened there, NOT a statement that it did not. Read `detectedOverBars` "
        + "— fewer bars behind a level is less weight for it either way. "
        + "Overlapping zones MERGE whichever side of price they formed on, so one reported "
        + "zone can be a support and a resistance that ran into each other; `touchCount` is how many pivots "
        + "went into it. `pivotSource`, `pivotLookback` and `pivotRightLookback` tune the detection for one "
        + "call; OMIT them and this server's configured defaults apply. They carry no default of their own, "
        + "because the default is an operator setting rather than a constant — omitting one asks for the "
        + "configured value, it does not name a particular one. Zone width, the significance floor and the "
        + "two caps are operator settings only, so every level this server reports is sized, filtered and "
        + "capped alike and two of them can be compared. Each method returns at most `detection.maxLevels` "
        + "levels, the most significant ones; `methods[i].levels.length == detection.maxLevels` is the "
        + "per-method signal that that method was cut, and `capped` is true when any requested method "
        + "stopped there. The top-level `levels` array is the union, ordered by price — its length is "
        + "not a completeness signal. Levels below a method's cap are absent rather than folded into "
        + "the ones you can see. "
        + "The response reports the detection it actually ran under as `detection`, so an empty `levels` can "
        + "be told from a market with no structure — read it with `detectedOverBars`. "
        + "`methods` selects which detectors run — `swing`, `session`, `pivot-classic`, `pivot-fibonacci`, "
        + "`pivot-camarilla`, `pivot-woodie`, `pivot-demark`, `volume-poc`, `volume-vah`, `volume-val`, "
        + "`volume-traded` — comma-separated; Omit for swing. The "
        + "response names each method's zones and a family-aware confluence score, with the tolerance "
        + "it was computed against. Methods that share a family share one budget. A requested method "
        + "that contributed nothing is named, with why.")]
    public async Task<ToolPayloads.LevelSet> GetKeyLevels(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The timeframe in minutes.")] int resolutionMinutes,
        [Description("How many bars of history to detect over. Omit for 500.")]
        int lookbackBars = DefaultLookbackBars,
        [Description(
            "Which price on a bar a pivot is measured from: HeikinAshiBody, Body or HighLow. Omit to use "
            + "this server's configured source. HeikinAshiBody smooths single-bar noise into structure and "
            + "is the shipped default. Body reads open and close only, HighLow reads the raw wicks. NOTE: on "
            + "a continuous intraday series, where a bar opens at the previous close, a body high ties with "
            + "its neighbour's on every bar and Body can find NO pivots at all — an empty level set there is "
            + "a property of the source, not a market without structure. An unknown name is an error listing "
            + "the three.")]
        string? pivotSource = null,
        [Description(
            "How many bars to its LEFT a pivot must dominate; larger means fewer, more structural levels. "
            + "Omit to use this server's configured lookback. The window is asymmetric: detection needs "
            + "this + `pivotRightLookback` + 1 bars to find even one pivot — and the window it runs over "
            + "is whatever the store holds, cut back to the contract in front, which can be far less than "
            + "`lookbackBars` asked for. When that happens the answer is an EMPTY level set, not an error: "
            + "compare `detection.pivotLookback` against `detectedOverBars` to tell that from a market with "
            + "no structure.")]
        int? pivotLookback = null,
        [Description(
            "How many bars to its RIGHT a pivot must dominate — the confirmation window. Omit to use this "
            + "server's configured value. It is shorter than the left one by default because the two sides "
            + "answer different questions: the left asks how much history the level stood clear of, the "
            + "right only has to show the extreme held. It is also the lag: the last this-many bars of the "
            + "series can never produce a pivot, so the newest structure is always missing from the answer. "
            +             "There is no zero — a pivot judged only by the bars before it repaints as soon as the next "
            + "one arrives.")]
        int? pivotRightLookback = null,
        [Description(
            "Which level methods to run, comma-separated: swing, session, pivot-classic, pivot-fibonacci, "
            + "pivot-camarilla, pivot-woodie, pivot-demark, volume-poc, volume-vah, volume-val, "
            + "volume-traded. Omit for swing. An unknown name is an error "
            + "listing the known ones — never an empty level set. Session and every pivot-* method refuse "
            + "when a bucket of this resolutionMinutes overhangs a session close. Volume-* methods consume "
            + "the tape-derived profile for the window; they never spread a bar's volume across its range.")]
        string? methods = null,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = Resolve(symbol);
        ToolGuards.ValidateResolution(resolutionMinutes);
        int wanted = _guards.ValidateCount(lookbackBars);

        // Before the read, not after it. Every check below is a fact about the REQUEST, and a store with no
        // bars returns early -- so validating after the read is how an Unknown source arriving from
        // configuration would be answered with an empty level set instead of a refusal.
        KeyLevelOptions detection = ResolveDetection(pivotSource, pivotLookback, pivotRightLookback)
            with
        {
            ResolutionMinutes = resolutionMinutes,
        };
        IReadOnlyList<ILevelMethod> requested = ResolveMethods(methods);

        List<Bar> bars = await _database.Bars
            .Where(b => b.Venue == _gateway.VenueId
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes)
            .OrderByDescending(b => b.BucketStart)
            .Take(wanted)
            .Select(b => new Bar(b.BucketStart, b.Open, b.High, b.Low, b.Close, b.Volume, b.ContractId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (bars.Count == 0)
        {
            return AssembleLevelSet(
                [],
                new ToolPayloads.ContractCoverage(ToolPayloads.ContractSpan.Unknown, []),
                0,
                detection,
                requested,
                overhang: false,
                scale: []);
        }

        // Reversed FIRST, then described. Coverage over a descending series would give every segment a
        // FirstBucket later than its LastBucket -- harmless while nothing reads it, and a shape not worth
        // leaving available to the next edit.
        bars.Reverse();
        ToolPayloads.ContractCoverage coverage = ToolPayloads.ToCoverage(bars);

        // Only the contract in front. A zone detected across a roll is built partly from a quarter that no
        // longer trades, and it lands at a price the current contract has never been near -- which reads
        // exactly like a level price is about to touch. The lookback is reported alongside, because silently
        // halving the history behind a level changes how much weight it deserves (ADR-0011).
        IReadOnlyList<Bar> detectable = ContractRollDetector.Newest(bars);

        // Levels are scaled and scored in ATR, so they are computed from the same bars rather than read from
        // the store: an ATR row is keyed to the configured period, and detection needs it aligned one-to-one
        // with exactly these bars.
        IIndicator atr = _catalog.Resolve("atr");
        IReadOnlyList<decimal?> scale = atr.Compute(detectable);

        bool overhang = SessionBucketGuard.OverhangsClose(
            resolutionMinutes, _levelMethods.Calendar, detectable);

        VolumeProfile? profile = null;
        string? volumeAbsent = null;
        if (detectable.Count > 0 && requested.Any(static m => m.Family == VolumeLevels.FamilyName))
        {
            try
            {
                DateTimeOffset windowStart = detectable[0].OpenTime;
                DateTimeOffset windowEnd = detectable[^1].OpenTime.AddMinutes(resolutionMinutes);
                VolumeProfileRead read = await _volumeProfiles
                    .ReadAsync(
                        _gateway.VenueId,
                        instrument,
                        resolutionMinutes,
                        windowStart,
                        windowEnd,
                        cancellationToken)
                    .ConfigureAwait(false);

                // Narrowed is gh#221's confinement signal. Binding the confined profile would
                // report a POC of the listened subset as a POC of the key-levels window —
                // detectedOverBars still names the full bar series.
                if (read.Window.Narrowed)
                {
                    volumeAbsent = VolumeLevels.NarrowedReason;
                }
                else
                {
                    profile = read.Profile;
                }
            }
            catch (InvalidOperationException)
            {
                volumeAbsent = VolumeLevels.NoTapeReason;
            }
            catch (ArgumentException)
            {
                volumeAbsent = VolumeLevels.NoTapeReason;
            }
        }

        using VolumeProfileScope? bind = profile is { } bound ? new VolumeProfileScope(bound) : null;
        return AssembleLevelSet(
            detectable, coverage, detectable.Count, detection, requested, overhang, scale, volumeAbsent);
    }

    /// <summary>
    /// Runs the requested methods, scores their agreement, and builds the payload.
    /// </summary>
    private ToolPayloads.LevelSet AssembleLevelSet(
        IReadOnlyList<Bar> detectable,
        ToolPayloads.ContractCoverage coverage,
        int detectedOverBars,
        KeyLevelOptions detection,
        IReadOnlyList<ILevelMethod> requested,
        bool overhang,
        IReadOnlyList<decimal?> scale,
        string? volumeAbsent = null)
    {
        List<ConfluenceMethodInput> inputs = [];
        List<ToolPayloads.LevelInfo> combined = [];
        List<ToolPayloads.LevelMethodResult> methodResults = [];
        int timeframe = detection.ResolutionMinutes;

        foreach (ILevelMethod method in requested)
        {
            decimal weight = _detection.WeightOf(method.Name);
            bool anchored = method.Name == "session" || method.Family == PivotLevels.FamilyName;

            // The three ways a method contributes zero zones without running: no bars at all, the
            // tape-derived volume profile it needs is absent, or it is anchored to a session close the
            // window overhangs. Each used to add the same two records by hand -- one to `inputs` for
            // confluence scoring, one to `methodResults` for the payload -- naming a different reason.
            void Refuse(string reason)
            {
                inputs.Add(new ConfluenceMethodInput(method.Name, method.Family, [], reason));
                methodResults.Add(new ToolPayloads.LevelMethodResult(method.Name, method.Family, weight, [], reason));
            }

            if (detectable.Count == 0)
            {
                Refuse("no data");
                continue;
            }

            if (method.Family == VolumeLevels.FamilyName && volumeAbsent is not null)
            {
                Refuse(volumeAbsent);
                continue;
            }

            if (anchored && overhang)
            {
                Refuse(SessionBucketGuard.RefusalReason);
                continue;
            }

            IReadOnlyList<KeyLevelZone> zones = method.Detect(detectable, scale, detection);
            inputs.Add(new ConfluenceMethodInput(method.Name, method.Family, zones));

            List<ToolPayloads.LevelInfo> infos = [];
            foreach (KeyLevelZone zone in zones)
            {
                ToolPayloads.LevelInfo info = new(
                    timeframe, zone.Bottom, zone.Top, zone.Midpoint, zone.Kind, zone.Significance,
                    zone.TouchCount, zone.FormedAtBucket, method.Name, zone.Period);
                infos.Add(info);
                combined.Add(info);
            }

            methodResults.Add(new ToolPayloads.LevelMethodResult(
                method.Name,
                method.Family,
                weight,
                infos,
                zones.Count == 0 ? ConfluenceScoring.NoLevelsReason : null,
                Capped: zones.Count == detection.MaxLevels));
        }

        combined.Sort(static (left, right) =>
        {
            int byPrice = left.Midpoint.CompareTo(right.Midpoint);
            if (byPrice != 0)
            {
                return byPrice;
            }

            int byBottom = left.Bottom.CompareTo(right.Bottom);
            return byBottom != 0
                ? byBottom
                : string.CompareOrdinal(left.Method, right.Method);
        });

        ConfluenceResult scored = ConfluenceScoring.Score(
            inputs, _detection.Weights, detection.ZoneAtrMultiple);

        return new ToolPayloads.LevelSet(
            combined,
            coverage,
            detectedOverBars,
            Reported(detection),
            methodResults,
            new ToolPayloads.ConfluenceScore(
                scored.Score,
                scored.Tolerance,
                [.. scored.Constituents.Select(c =>
                    new ToolPayloads.ConfluenceConstituentInfo(c.Method, c.Family, c.Weight, c.ZoneCount))],
                [.. scored.Absent.Select(a => new ToolPayloads.ConfluenceAbsenceInfo(a.Method, a.Reason))]),
            Capped: methodResults.Exists(static m => m.Capped));
    }

    /// <summary>
    /// Resolves the requested method names, defaulting to <see cref="DefaultLevelMethodName"/>.
    /// </summary>
    /// <exception cref="McpException">A name is not in the vocabulary.</exception>
    private IReadOnlyList<ILevelMethod> ResolveMethods(string? methods)
    {
        IEnumerable<string> names = string.IsNullOrWhiteSpace(methods)
            ? [DefaultLevelMethodName]
            : methods.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static name => name.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal);

        List<ILevelMethod> resolved = [];
        foreach (string name in names)
        {
            resolved.Add(ExceptionTranslation.Try(
                () => _levelMethods.Resolve(name),
                static ex => ex is KeyNotFoundException));
        }

        return resolved.Count == 0 ? [_levelMethods.Resolve(DefaultLevelMethodName)] : resolved;
    }

    /// <summary>
    /// Merges what the caller asked for over what the operator configured, and refuses what neither can mean.
    /// </summary>
    /// <param name="pivotSource">The requested source name, or null to take the configured one.</param>
    /// <param name="pivotLookback">The requested left lookback, or null to take the configured one.</param>
    /// <param name="pivotRightLookback">The requested right lookback, or null to take the configured one.</param>
    /// <returns>The options detection will run under.</returns>
    /// <exception cref="McpException">
    /// The named source is not in the vocabulary, the configured source is not either, or either lookback is
    /// below one.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The configured source is checked on the same terms as the caller's.</b> Startup validation already
    /// refuses an unservable one (<see cref="KeyLevelDetectionOptions.Validate"/>), so this second door is
    /// closed on a room that should be locked — which is the point: the two doors are opened by different
    /// keys. A value that never went through <c>ValidateOnStart</c> otherwise reaches
    /// <c>KeyLevels.PivotPrices</c>, which reads anything it does not recognise as Heikin-Ashi and returns
    /// an ordinary-looking level set measured from a source nobody chose.
    /// </para>
    /// <para>
    /// <b>Everything refused here is a fact about the REQUEST, decidable before a single bar is read, and
    /// that is now the rule rather than an accident.</b> A lookback below one is not a lookback under any
    /// data. Whether a lookback is <i>satisfiable</i> is a different kind of question and this is the wrong
    /// place for it: it depends on <c>detectable.Count</c> — what the store actually holds, cut back to the
    /// contract in front — which is not known until after the read, and is not something every caller can
    /// change. An earlier revision bounded the lookback against <c>lookbackBars</c> instead, and bounding
    /// the requested window rather than the detected one is wrong twice over. It refused calls that would
    /// have succeeded, because a caller may ask for 500 bars over a store holding 40; and it refused calls
    /// nobody could fix, because <c>get_market_snapshot</c> passes a fixed <c>max(barCount, 200)</c> and
    /// exposes neither knob — a configured <c>PivotLookback</c> of 100, legal on its own range, made every
    /// snapshot call fail with advice to change two arguments that tool does not have.
    /// </para>
    /// <para>
    /// <b>An unsatisfiable lookback is therefore answered, not refused — and the answer says so.</b>
    /// <see cref="ToolPayloads.LevelSet.Detection"/> reports all four parameters beside
    /// <c>detectedOverBars</c>, so an empty level set carries its own explanation. That covers strictly more
    /// than the refusal did: too few bars, a roll that cut the window down, a source whose candidates all
    /// tie, and a significance floor that filtered every zone all arrive explicable, where the bound reached
    /// only the first and only when the caller had asked for exactly what was stored.
    /// </para>
    /// </remarks>
    private KeyLevelOptions ResolveDetection(string? pivotSource, int? pivotLookback, int? pivotRightLookback)
    {
        KeyLevelOptions defaults = _detection.Defaults();

        PivotSource source;
        if (pivotSource is null)
        {
            source = PivotSources.IsServable(defaults.Source)
                ? defaults.Source
                : throw new McpException(
                    "This server's configured pivot source, '" + defaults.Source
                    + "', is not one it can detect through. Known sources: " + PivotSources.KnownNames
                    + ". Set " + KeyLevelDetectionOptions.SectionName + "__Source to one of them, or name a "
                    + "source on the call.");
        }
        else
        {
            source = ExceptionTranslation.Try(
                () => PivotSources.Resolve(pivotSource),
                static ex => ex is KeyNotFoundException);
        }

        int lookback = pivotLookback ?? defaults.Lookback;
        int rightLookback = pivotRightLookback ?? defaults.RightLookback;

        if (lookback < 1)
        {
            throw new McpException(
                "pivotLookback must be at least 1; got "
                + lookback.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". A pivot dominates that many bars to its left, so there is no such thing as a pivot "
                + "that dominates none.");
        }

        return rightLookback < 1
            ? throw new McpException(
                "pivotRightLookback must be at least 1; got "
                + rightLookback.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". The right window is the confirmation, so a pivot with none is a guess about the bars "
                + "that have not arrived and it repaints as soon as they do.")
            : defaults with { Source = source, Lookback = lookback, RightLookback = rightLookback };
    }

    /// <summary>
    /// The detection options as the payload reports them.
    /// </summary>
    /// <param name="options">The options detection ran under.</param>
    /// <returns>The reported detection.</returns>
    /// <remarks>
    /// Projected from the same record detection was handed, never rebuilt from configuration. Read back from
    /// <see cref="_detection"/> instead, this would report the operator's defaults on a call that overrode
    /// them — a payload describing a detection that did not happen, which is worse than reporting nothing.
    /// </remarks>
    private static ToolPayloads.LevelDetection Reported(KeyLevelOptions options) =>
        new(
            options.Source,
            options.Lookback,
            options.ZoneAtrMultiple,
            options.MinSignificance,
            options.RightLookback,
            options.MaxZoneWidthPercent,
            options.MaxLevels);
}
