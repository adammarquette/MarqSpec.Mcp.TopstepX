namespace MarqSpec.Mcp.TopstepX.Domain;

/// <summary>
/// A venue-neutral instrument symbol — the key every stored series is written and read under.
/// </summary>
/// <remarks>
/// <para>
/// Normalisation happens <b>here, at construction</b>, and not at each call site: the symbol is trimmed and
/// upper-cased so <c>"es"</c>, <c>" ES "</c> and <c>"ES"</c> are one series rather than three. A writer that
/// stored the raw configured string and a reader that looked up the upper-cased one would produce a store whose
/// contents are unfindable by their own symbol — which is exactly the defect this type exists to make
/// unrepresentable.
/// </para>
/// <para>
/// This is <b>not</b> a venue contract id. <c>ES</c> is the instrument; <c>CON.F.US.EP.U26</c> is one ProjectX
/// contract that happens to quote it this quarter. The two are resolved against each other at the venue seam,
/// never conflated in the store.
/// </para>
/// </remarks>
public readonly record struct InstrumentId
{
    private readonly string? _symbol;

    /// <summary>Creates an instrument id from a raw symbol.</summary>
    /// <param name="symbol">The symbol; trimmed and upper-cased.</param>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is null, empty or whitespace.</exception>
    public InstrumentId(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("An instrument symbol cannot be blank.", nameof(symbol));
        }

        _symbol = symbol.Trim().ToUpperInvariant();
    }

    /// <summary>The normalised symbol, e.g. <c>ES</c>.</summary>
    /// <exception cref="InvalidOperationException">The value is the uninitialised default.</exception>
    public string Symbol => _symbol
        ?? throw new InvalidOperationException("This InstrumentId is the uninitialised default and has no symbol.");

    /// <summary>Whether this value was constructed rather than defaulted.</summary>
    public bool HasValue => _symbol is not null;

    /// <summary>Returns the normalised symbol.</summary>
    /// <returns>The symbol.</returns>
    public override string ToString() => Symbol;
}
