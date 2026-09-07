namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// How the <b>historical</b> half of one read decided which contracts to ask — and whether that decision ran
/// over the contracts the product's cycle names, or only over what the venue happened to list (gh#592).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a value of <c>ContractSpan</c>.</b> That enum answers a question about the <i>bars</i>:
/// do they cross a roll, and can the store tell? Its <c>Unknown</c> is a carefully protected "cannot tell
/// whether a roll happened there", and folding a narrowed candidate set into it would make two entirely
/// different unknowns indistinguishable — which is the same defect this type exists to close, one level up.
/// A window can perfectly well be <c>SingleContract</c> and still have been decided among survivors.
/// </para>
/// <para>
/// <b>Why it is a property of the read rather than of the store.</b> The narrowing is knowable only at the
/// moment the plan is cut: nothing in <c>Bars</c> records that a bucket was written under a narrowed set, and
/// ADR-0020 §5 forbids a read from re-deciding attributed history to work it out afterwards. So a read that
/// plans nothing — every bucket already stored — reports <see cref="NotDecidedHere"/>, which is a statement
/// about <i>this read</i> and emphatically not a statement that the stored history is whole. Repairing a run
/// laid down by a degraded read is an operator's verb, <c>reselect-bars</c> (gh#506).
/// </para>
/// </remarks>
public enum HistorySelection
{
    /// <summary>
    /// <b>This read decided no history, so it has nothing to say about how any was decided.</b> Every bucket
    /// the window asked for was already stored, or the window sits entirely in the present band the venue's
    /// own pick answers for (ADR-0020 §1), or the instrument carries no cycle to plan against.
    /// <para>
    /// <b>Not "the cycle was whole".</b> The bars in the answer may well have been fetched by an earlier,
    /// degraded read, and nothing in the store records that. Reading this as a clean bill of health is the
    /// mistake the value is named to prevent; <see cref="AsTheCycleNames"/> is the clean bill of health, and
    /// only a read that actually ran the decision can give one.
    /// </para>
    /// </summary>
    NotDecidedHere = 0,

    /// <summary>
    /// <b>Every expiry the cycle named for these trade dates was listed by the venue, and the volume decision
    /// ran over all of them.</b> This is the undegraded historical answer ADR-0020 §2 describes.
    /// </summary>
    AsTheCycleNames = 1,

    /// <summary>
    /// <b>The venue did not list some of the expiries the cycle named, so the decision ran over the
    /// survivors.</b> At least one candidate did survive and was asked, so the bars are a real series from a
    /// real contract — and when the sole survivor is the venue's own pick, that series is the pre-ADR-0020
    /// answer for the stretch, complete-looking and indistinguishable by its shape from a correct one.
    /// <para>
    /// The expiries that fell away are named in <see cref="HistoryCandidates.Unresolved"/>. A venue negative
    /// lapses, so the same window read later can be decided differently; the bars already stored are not
    /// rewritten by a read (<c>reselect-bars</c>, gh#506).
    /// </para>
    /// </summary>
    NarrowedByTheVenue = 2,

    /// <summary>
    /// <b>The venue listed none of the expiries the cycle named, so the stretch was fetched from the venue's
    /// own pick and the volume decision never ran at all.</b>
    /// <para>
    /// A strictly worse fact than <see cref="NarrowedByTheVenue"/> and deliberately not folded into it: a
    /// narrowed set still had a candidate the cycle named, and ADR-0020 §2's rule chose among what it had.
    /// Here it chose nothing — the answer is today's behaviour for that stretch, which is the very defect
    /// ADR-0020 was written to fix. Nothing permanent is recorded about such a range being empty, so it is
    /// re-asked on a later read.
    /// </para>
    /// </summary>
    FellBackToTheFront = 3,
}

/// <summary>
/// What one read can say about the candidate sets its historical half was decided over.
/// </summary>
/// <param name="Selection">
/// The <b>worst</b> thing that happened to any historical slice of this read. A single value has to rank the
/// slices, and it ranks them by severity: a caller told only the mildest degradation in a read would act on a
/// stretch that may have had no decision run over it at all.
/// </param>
/// <param name="Unresolved">
/// Every expiry the cycle named that the venue did not list, nearest first and named once however many slices
/// dropped it — e.g. <c>M26</c>. Empty unless <see cref="Selection"/> is a degradation.
/// <para>
/// These are codes <b>this server constructed</b> from the product's <c>ContractMonthCycle</c>, never vendor
/// text echoed back, which is what keeps them inside ADR-0008's closed vocabulary.
/// </para>
/// <para>
/// Deduplicated deliberately. One venue negative narrows every slice under the cycle month it belongs to, so
/// repeating the code per slice would make this read as a severity count it is not — a wider window would
/// report a longer list for the same single fact.
/// </para>
/// </param>
public sealed record HistoryCandidates(HistorySelection Selection, IReadOnlyList<string> Unresolved)
{
    /// <summary>The answer for a read that planned no historical fetch.</summary>
    public static HistoryCandidates NotDecidedHere { get; } = new(HistorySelection.NotDecidedHere, []);
}
