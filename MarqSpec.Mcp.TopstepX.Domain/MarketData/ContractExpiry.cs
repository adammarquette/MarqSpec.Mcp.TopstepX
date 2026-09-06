using System.Globalization;

namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The expiry a futures contract is named for — a year and one of the exchange's month letters — and the
/// order those expiries sort in (ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// A venue contract id ends in the expiry: <c>CON.F.US.MES.U26</c> is the September 2026 contract. The
/// month is a letter from a fixed table (<see cref="MonthCodes"/>) and the year is two digits. This type is
/// what reads that, so the reading lives in one place rather than being re-derived at every seam that needs
/// to know which of two contracts is nearer.
/// </para>
/// <para>
/// <b>Year first, then month.</b> The code's own string order compares the month letter before the year,
/// and the letters happen to ascend alphabetically in calendar order — so a string sort agrees with expiry
/// order inside one calendar year and <b>inverts across one</b>: every December, <c>Z25</c> files behind
/// <c>H26</c>, <c>M26</c> and <c>U26</c>, last, at the one moment it is the front month. <see cref="Rank"/>
/// is the total order the string does not give.
/// </para>
/// <para>
/// <b>A code this type cannot read is not read.</b> <see cref="TryParse"/> answers false rather than a
/// guess: a four-digit year, a letter off the table or a lower-case month is a change of shape, and an
/// invented expiry would decide which contract a whole range of history is fetched from. The two-digit year
/// is read into the <see cref="Century"/>, which is all the venue's id can express and all the contracts this
/// server can reach.
/// </para>
/// </remarks>
public sealed record ContractExpiry : IComparable<ContractExpiry>
{
    /// <summary>
    /// The exchange's futures month codes in calendar order, so the index is the month less one.
    /// </summary>
    /// <remarks><c>I</c> and <c>L</c> are absent by convention, being confusable with digits.</remarks>
    public const string MonthCodes = "FGHJKMNQUVXZ";

    /// <summary>The century a two-digit venue year is read into.</summary>
    public const int Century = 2000;

    /// <summary>Creates an expiry.</summary>
    /// <param name="year">The four-digit year, e.g. <c>2026</c>.</param>
    /// <param name="monthCode">The month letter, one of <see cref="MonthCodes"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="monthCode"/> is not on the exchange's table, or <paramref name="year"/> is not positive.
    /// </exception>
    public ContractExpiry(int year, char monthCode)
    {
        if (year <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(year), year, "A contract year must be positive.");
        }

        if (MonthOf(monthCode) == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(monthCode),
                monthCode,
                "'" + monthCode + "' is not a futures month code; the table is " + MonthCodes + ".");
        }

        Year = year;
        MonthCode = monthCode;
    }

    /// <summary>The four-digit year.</summary>
    public int Year { get; }

    /// <summary>The month letter, e.g. <c>U</c> for September.</summary>
    public char MonthCode { get; }

    /// <summary>The calendar month, 1 to 12.</summary>
    public int Month => MonthOf(MonthCode);

    /// <summary>
    /// A rank that ascends with the expiry — <see cref="Year"/> times twelve plus <see cref="Month"/> — so
    /// two expiries compare correctly across a year boundary.
    /// </summary>
    public int Rank => (Year * 12) + Month;

    /// <summary>The code as the venue writes it, e.g. <c>U26</c>.</summary>
    public string Code =>
        MonthCode + (Year % 100).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads an expiry code as the venue writes it: one month letter followed by exactly two digits.
    /// </summary>
    /// <param name="code">The code, e.g. <c>U26</c>.</param>
    /// <param name="expiry">The expiry, when read.</param>
    /// <returns><see langword="true"/> when the code was read.</returns>
    public static bool TryParse(string? code, out ContractExpiry expiry)
    {
        expiry = null!;

        if (code is null || code.Length != 3)
        {
            return false;
        }

        int month = MonthOf(code[0]);
        if (month == 0)
        {
            return false;
        }

        if (!int.TryParse(code.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int twoDigitYear))
        {
            return false;
        }

        expiry = new ContractExpiry(Century + twoDigitYear, code[0]);
        return true;
    }

    /// <summary>
    /// Reads the expiry off a venue contract id, shaped <c>CON.F.US.{product}.{MYY}</c>.
    /// </summary>
    /// <param name="contractId">The contract id.</param>
    /// <param name="expiry">The expiry, when read.</param>
    /// <returns><see langword="true"/> when the id's last segment is an expiry code.</returns>
    /// <remarks>
    /// The expiry is the last dot-separated segment, which stays true if the venue ever lengthens the prefix.
    /// An id with no readable expiry is answered false, never ranked: the caller decides where an id nobody
    /// understands goes, and says so.
    /// </remarks>
    public static bool TryParseContractId(string? contractId, out ContractExpiry expiry)
    {
        if (string.IsNullOrWhiteSpace(contractId))
        {
            expiry = null!;
            return false;
        }

        int lastDot = contractId.LastIndexOf('.');
        return TryParse(lastDot < 0 ? contractId : contractId[(lastDot + 1)..], out expiry);
    }

    /// <summary>The expiry that follows this one in a product's listing cycle, wrapping into the next year.</summary>
    /// <param name="cycle">The months the product lists.</param>
    /// <returns>The next listed expiry after this one.</returns>
    public ContractExpiry Next(ContractMonthCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        foreach (int month in cycle.Months)
        {
            if (month > Month)
            {
                return new ContractExpiry(Year, MonthCodes[month - 1]);
            }
        }

        return new ContractExpiry(Year + 1, MonthCodes[cycle.Months[0] - 1]);
    }

    /// <inheritdoc />
    public int CompareTo(ContractExpiry? other) => other is null ? 1 : Rank.CompareTo(other.Rank);

    /// <summary>Returns <see cref="Code"/>.</summary>
    /// <returns>The code.</returns>
    public override string ToString() => Code;

    /// <summary>The calendar month for a letter, or zero when the letter is not on the table.</summary>
    private static int MonthOf(char monthCode) => MonthCodes.IndexOf(monthCode, StringComparison.Ordinal) + 1;
}
