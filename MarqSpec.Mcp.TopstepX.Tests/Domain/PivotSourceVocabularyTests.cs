using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The closed vocabulary of pivot sources, and the two holes through which an unchosen one used to reach a
/// price series (gh#244).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither hole was about <c>Unknown</c> arriving at <c>FindPivots</c></b> — that has been refused since
/// the type existed. They are the ways a source reaches detection <i>without going through that check</i>:
/// <c>Detect</c> returned early on an empty series before validating anything, and
/// <c>PivotPrices</c> selects High/Low and Body explicitly and reads <b>everything else</b> as Heikin-Ashi,
/// so a value outside the enum was never <c>Unknown</c> and never refused.
/// </para>
/// <para>
/// Both failures are silent in the same way: the caller gets an ordinary-looking answer — an empty level set,
/// or a real one measured from a source nobody named — and nothing anywhere says the configuration was wrong.
/// <c>KeyLevelsTests</c> pins what the pipeline computes; this pins what it refuses to compute at all.
/// </para>
/// </remarks>
public sealed class PivotSourceVocabularyTests
{
    private static DateTimeOffset At(int index) =>
        new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero).AddMinutes(5 * index);

    /// <summary>Five bars with a single peak in the middle — enough for one pivot at lookback 2.</summary>
    /// <remarks>
    /// Every price is set explicitly so all three sources have something distinct to read: the body is
    /// <c>open</c>..<c>close</c>, the wicks reach two further either side.
    /// </remarks>
    private static IReadOnlyList<Bar> Peak =>
    [
        new(At(0), Open: 99m, High: 103m, Low: 97m, Close: 101m, Volume: 100),
        new(At(1), Open: 101m, High: 105m, Low: 99m, Close: 103m, Volume: 100),
        new(At(2), Open: 107m, High: 113m, Low: 105m, Close: 111m, Volume: 100),
        new(At(3), Open: 103m, High: 107m, Low: 101m, Close: 105m, Volume: 100),
        new(At(4), Open: 100m, High: 104m, Low: 98m, Close: 102m, Volume: 100),
    ];

    private static KeyLevelOptions With(PivotSource source) => new(Lookback: 2, Source: source);

    // ── The vocabulary ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheVocabularyIsEverySourceExceptUnknown()
    {
        // Derived from the enum rather than written out, so a fourth source is servable the moment it exists.
        // `Enum.GetValues` orders by UNDERLYING VALUE, which coincides with declaration order here only
        // because PivotSource's values ascend -- so this pins the order the error messages actually print,
        // and a source numbered out of sequence reddens here rather than silently reordering the advice a
        // caller is given. What matters is that the DEFAULT is named first.
        PivotSources.Servable.Should().Equal(
            PivotSource.HeikinAshiBody, PivotSource.Body, PivotSource.HighLow);

        PivotSources.KnownNames.Should().Be("HeikinAshiBody, Body, HighLow");
    }

    [Fact]
    public void EverySourceInTheVocabularyIsOneTheVocabularyAccepts()
    {
        // The list and the predicate are two readings of the same fact, and only one of them is used at each
        // call site. Disagreeing, they would refuse a source the error message had just recommended.
        foreach (PivotSource source in PivotSources.Servable)
        {
            PivotSources.IsServable(source).Should().BeTrue();
            PivotSources.Resolve(source.ToString()).Should().Be(source);
        }
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("unknown")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("heikin-ashi")]
    public void ANameOutsideTheVocabulary_IsAnError_AndListsTheKnownOnes(string name)
    {
        // "Unknown" and the numbers are the point. Enum.TryParse accepts every one of them -- "0" and
        // "Unknown" both land on the value that exists precisely so it can be refused, and "99" produces a
        // member that does not exist at all -- so the resolution is written against the NAMES rather than
        // against the parser.
        Action resolve = () => PivotSources.Resolve(name);

        resolve.Should().Throw<KeyNotFoundException>()
            .WithMessage("*HeikinAshiBody, Body, HighLow*");
    }

    [Fact]
    public void ASourceNameIsResolvedRegardlessOfCasingAndPadding()
    {
        PivotSources.Resolve("  hIgHlOw  ").Should().Be(PivotSource.HighLow);
        PivotSources.Resolve("body").Should().Be(PivotSource.Body);
    }

    [Fact]
    public void ANullNameIsRefusedRatherThanTreatedAsUnset()
    {
        // An absent value is exactly what Unknown = 0 is for, and honouring either would pick a price series
        // by accident. The tool decides what an OMITTED argument means; this layer never guesses.
        Action resolve = () => PivotSources.Resolve(null);

        resolve.Should().Throw<KeyNotFoundException>();
    }

    // ── The hole in Detect: an empty series answered before the options were looked at ────────────────

    [Fact]
    public void Detect_RefusesAnUnsetSource_EvenWhenThereAreNoBarsToDetectOver()
    {
        // The whole gh#244 hole in one line. Detect returned [] for an empty series BEFORE looking at the
        // options, so a server configured with an unset source answered every level call with "no levels" --
        // which is what an empty store looks like anyway. The misconfiguration was invisible until bars
        // arrived, and then it changed the answers rather than announcing itself.
        Action detect = () => KeyLevels.Detect([], [], With(PivotSource.Unknown));

        detect.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Detect_StillAnswersEmpty_ForAnEmptySeriesUnderAServableSource()
    {
        // The counterweight. The refusal above must be about the SOURCE, not about the empty series -- a
        // guard that turned "nothing to detect over" into an exception would break every caller reading a
        // symbol whose bars have not been fetched yet.
        foreach (PivotSource source in PivotSources.Servable)
        {
            KeyLevels.Detect([], [], With(source)).Should().BeEmpty();
        }
    }

    // ── The hole in PivotPrices: anything unrecognised was read as Heikin-Ashi ────────────────────────

    [Fact]
    public void FindPivots_RefusesASourceOutsideTheEnumEntirely()
    {
        // (PivotSource)99 is neither Unknown nor a defined member, so the Unknown check waved it through and
        // PivotPrices -- which tests for HighLow, then Body, then falls through -- read it as Heikin-Ashi.
        // The caller got real pivots, measured from a source it had not asked for, with nothing to see.
        Action find = () => KeyLevels.FindPivots(Peak, With((PivotSource)99));

        find.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*HeikinAshiBody, Body, HighLow*");
    }

    [Fact]
    public void Detect_RefusesASourceOutsideTheEnumEntirely()
    {
        Action detect = () => KeyLevels.Detect(Peak, [null, null, 2m, 2m, 2m], With((PivotSource)99));

        detect.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*HeikinAshiBody, Body, HighLow*");
    }

    [Fact]
    public void FindPivots_StillReadsEachServableSourceItsOwnWay()
    {
        // The counterweight to the two refusals above, and the thing that proves the widened check did not
        // simply close the door on everything. Hand-checked, bar 2 against a two-bar window either side:
        //
        //   HighLow  high 113, best rival bar 3's 107      -> pivot at 113, prominence  6
        //   Body     max(O,C) 111, best rival bar 3's 105  -> pivot at 111, prominence  6
        //
        // Same series, different level and the same score, which is what says the source was consulted.
        KeyLevels.FindPivots(Peak, With(PivotSource.HighLow))
            .Should().Equal(new SwingPivot(2, At(2), 113m, KeyLevelKind.Resistance, 6m));

        KeyLevels.FindPivots(Peak, With(PivotSource.Body))
            .Should().Equal(new SwingPivot(2, At(2), 111m, KeyLevelKind.Resistance, 6m));

        // Heikin-Ashi smooths, so it lands somewhere else again; the arithmetic that pins WHERE is
        // KeyLevelsTests' job. What matters here is that it still finds the peak rather than being refused.
        KeyLevels.FindPivots(Peak, With(PivotSource.HeikinAshiBody))
            .Should().ContainSingle().Which.Kind.Should().Be(KeyLevelKind.Resistance);
    }
}
