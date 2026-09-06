using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Turns a caller's symbol into an <see cref="InstrumentId"/>, and refuses first if the store is not there.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one member every market-data concern calls, and the reason it is a collaborator rather than a base
/// class or an extension</b> (gh#414). The five tool types this serves are siblings, not a hierarchy: a base
/// class would put <see cref="InstrumentRegistry"/> and <see cref="StoreAvailabilityHolder"/> back into all
/// five constructors — base-class parameters are still each derived type's parameters at every call site —
/// and it would hand every future concern <c>protected</c> reach at the base, which is the same
/// everything-reaches-everything complaint one level down. An extension method cannot hold state at all, so
/// it would take both dependencies as arguments and leave the two fields on all five types. Injecting one
/// collaborator costs each type a single parameter and makes the registry and the availability holder
/// <b>unreachable</b> from any tool type — a boundary the compiler holds, which is the whole point of the
/// card.
/// </para>
/// <para>
/// <b>The store check sits here, on the path every tool takes</b>, exactly as it did when this was a private
/// method on the one type. A per-tool check is a check a new tool forgets; a tool that never resolves a
/// symbol never needed one.
/// </para>
/// </remarks>
/// <param name="registry">The configured instruments.</param>
/// <param name="store">Whether this server's store answered at startup.</param>
public sealed class InstrumentResolver(InstrumentRegistry registry, StoreAvailabilityHolder store)
{
    private readonly InstrumentRegistry _registry = registry;
    private readonly StoreAvailabilityHolder _store = store;

    /// <summary>Normalises a caller's symbol, refusing an unknown one by name.</summary>
    /// <param name="symbol">The symbol as the caller wrote it.</param>
    /// <returns>The normalised instrument.</returns>
    /// <exception cref="ModelContextProtocol.McpException">
    /// The store is unavailable, or the symbol is not one this server is configured for.
    /// </exception>
    public InstrumentId Resolve(string symbol)
    {
        _store.Value.Require();

        return ExceptionTranslation.Try(
            () => _registry.Resolve(symbol),
            static ex => ex is KeyNotFoundException or ArgumentException);
    }
}
