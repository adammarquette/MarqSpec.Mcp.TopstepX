namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The months a product lists contracts for — quarterly <c>HMUZ</c> for the equity indices, <c>GJMQVZ</c>
/// for the metals, every month for energy — and which listed expiries are candidates for a trade date
/// (ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// The venue's search and available-contracts calls return only the <i>active</i> expiry of each product
/// (gh#494, measured 2026-09-06), so the contract that was front on a historical date cannot be discovered.
/// It has to be <b>constructed</b> from the product's cycle and then confirmed by id. This type is the
/// construction; the confirmation is the gateway's.
/// </para>
/// <para>
/// <b>The cycle names candidates, not the answer.</b> Which candidate the market was actually trading is
/// decided by volume (<see cref="HistoricalContractPolicy"/>), because the cycle cannot know: October gold
/// is listed every year and never front, and a monthly energy contract expires before the month it is named
/// for, so on 2026-08-18 the front crude contract was October's. That is why <see cref="CandidatesFor"/>
/// takes a depth rather than answering "the nearest one".
/// </para>
/// <para>
/// A cycle is a <b>set</b> of months. <see cref="Parse"/> accepts them in any order and renders them in
/// calendar order, so two spellings of one cycle are one value; a letter listed twice, a letter off the
/// exchange's table or a lower-case one is refused rather than read around.
/// </para>
/// </remarks>
public sealed class ContractMonthCycle : IEquatable<ContractMonthCycle>
{
    private readonly int[] _months;

    private ContractMonthCycle(int[] months)
    {
        _months = months;
        Code = string.Concat(months.Select(month => ContractExpiry.MonthCodes[month - 1]));
    }

    /// <summary>The listed months, ascending, 1 to 12.</summary>
    public IReadOnlyList<int> Months => _months;

    /// <summary>The cycle as month letters in calendar order, e.g. <c>HMUZ</c>.</summary>
    public string Code { get; }

    /// <summary>
    /// Reads a cycle written as month letters, e.g. <c>HMUZ</c>.
    /// </summary>
    /// <param name="code">The month letters, in any order.</param>
    /// <returns>The cycle.</returns>
    /// <exception cref="FormatException">
    /// The code is blank, contains a letter that is not a month code, or lists a month twice.
    /// </exception>
    public static ContractMonthCycle Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new FormatException("A contract month cycle needs at least one month letter.");
        }

        SortedSet<int> months = [];
        foreach (char letter in code)
        {
            int month = ContractExpiry.MonthCodes.IndexOf(letter, StringComparison.Ordinal) + 1;
            if (month == 0)
            {
                throw new FormatException(
                    "Contract month cycle '" + code + "' contains '" + letter
                    + "', which is not a futures month code; the table is " + ContractExpiry.MonthCodes + ".");
            }

            if (!months.Add(month))
            {
                throw new FormatException(
                    "Contract month cycle '" + code + "' lists '" + letter + "' more than once.");
            }
        }

        return new ContractMonthCycle([.. months]);
    }

    /// <summary>Whether the cycle lists a month.</summary>
    /// <param name="monthCode">The month letter.</param>
    /// <returns><see langword="true"/> when the month is listed.</returns>
    public bool Contains(char monthCode) => Code.Contains(monthCode, StringComparison.Ordinal);

    /// <summary>
    /// The <paramref name="depth"/> nearest listed expiries whose contract month is at or after the trade
    /// date's month, counting on into later years as the cycle wraps.
    /// </summary>
    /// <param name="tradeDate">The trade date the bars are wanted for.</param>
    /// <param name="depth">How many candidates to name. At least one.</param>
    /// <returns>The candidates, nearest first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is below one.</exception>
    /// <remarks>
    /// <para>
    /// <b>At or after, not after.</b> Most of March trades the March contract; a rule that skipped the trade
    /// date's own month would miss the front month for three weeks of every quarter. The contract that has
    /// already expired inside that month is harmless as a candidate — it answers no bars for the dates
    /// after its expiry, and the volume rule never picks a contract with no bars.
    /// </para>
    /// <para>
    /// <b>The depth is the caller's, per product.</b> Two reaches the front and the next quarterly for an
    /// equity index. A monthly energy cycle needs three, because the contract named for the trade date's
    /// month and the one after it can both be behind the market by the third week; the metals need three
    /// because a listed month can be skipped by the market altogether. The registry carries the number; this
    /// type only counts.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ContractExpiry> CandidatesFor(DateOnly tradeDate, int depth)
    {
        if (depth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "At least one candidate is needed.");
        }

        List<ContractExpiry> candidates = new(depth);
        int year = tradeDate.Year;
        int index = Array.FindIndex(_months, month => month >= tradeDate.Month);
        if (index < 0)
        {
            index = 0;
            year++;
        }

        while (candidates.Count < depth)
        {
            candidates.Add(new ContractExpiry(year, ContractExpiry.MonthCodes[_months[index] - 1]));
            index++;
            if (index == _months.Length)
            {
                index = 0;
                year++;
            }
        }

        return candidates;
    }

    /// <inheritdoc />
    public bool Equals(ContractMonthCycle? other) =>
        other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ContractMonthCycle);

    /// <inheritdoc />
    public override int GetHashCode() => string.GetHashCode(Code, StringComparison.Ordinal);

    /// <summary>Returns <see cref="Code"/>.</summary>
    /// <returns>The code.</returns>
    public override string ToString() => Code;
}
