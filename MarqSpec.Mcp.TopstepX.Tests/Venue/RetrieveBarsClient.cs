using System.Reflection;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// A dispatch proxy that answers <c>History/retrieveBars</c> and refuses every other client method.
/// </summary>
/// <remarks>
/// <para>
/// The vendor client interface carries the order surface. Implementing it by hand would spell those method
/// names in this repository, which is the opposite of what ADR-0002 asks for — even in a test project
/// (<c>scripts/check-paced-paging.sh</c> records the same refusal). Dispatching on the history method's
/// <i>name</i> keeps those identifiers out of the tree while still driving
/// <c>ProjectXMarketDataGateway.GetBarsAsync</c> against a scripted retrieveBars.
/// </para>
/// <para>
/// Anything other than retrieveBars throws, so a paging test that accidentally reached contract search or
/// an order method fails loudly instead of returning a default.
/// </para>
/// </remarks>
internal class RetrieveBarsClient : DispatchProxy
{
    /// <summary>Every retrieveBars invocation, in order.</summary>
    public List<HistoryPageRequest> Requests { get; } = [];

    /// <summary>
    /// The scripted answer for one page. Defaults to empty, which is what the vendor returns past its
    /// depth rather than an error.
    /// </summary>
    public Func<HistoryPageRequest, IReadOnlyList<AggregateBar>> Answer { get; set; } = static _ => [];

    /// <summary>Builds a proxy implementing the vendor client and this double as the same instance.</summary>
    /// <returns>The double, and the client the gateway takes.</returns>
    public static (RetrieveBarsClient Fake, IProjectXApiClient Client) Create()
    {
        IProjectXApiClient client = Create<IProjectXApiClient, RetrieveBarsClient>();
        return ((RetrieveBarsClient)(object)client, client);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (!string.Equals(targetMethod.Name, "GetHistoricalBarsAsync", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This double answers retrieveBars only; '" + targetMethod.Name
                + "' is not on the history seam.");
        }

        ArgumentNullException.ThrowIfNull(args);

        HistoryPageRequest request = new(
            (string)args[0]!,
            (DateTime)args[1]!,
            (DateTime)args[2]!,
            (AggregateBarUnit)args[3]!,
            (int)args[4]!,
            (int)args[5]!,
            (bool)args[6]!,
            (bool)args[7]!);

        Requests.Add(request);

        CancellationToken cancellation = (CancellationToken)args[8]!;
        cancellation.ThrowIfCancellationRequested();

        return Task.FromResult<IEnumerable<AggregateBar>>(Answer(request));
    }
}

/// <summary>One retrieveBars call the gateway issued.</summary>
/// <param name="ContractId">The contract asked about.</param>
/// <param name="Start">The page start, as the gateway passed it.</param>
/// <param name="End">The page end, as the gateway passed it.</param>
/// <param name="Unit">The vendor bar unit.</param>
/// <param name="UnitNumber">How many of <paramref name="Unit"/> make one bar.</param>
/// <param name="Limit">The row cap the gateway asked for.</param>
/// <param name="Live">The data-tier flag.</param>
/// <param name="IncludePartialBar">Whether a still-forming bar was requested.</param>
internal sealed record HistoryPageRequest(
    string ContractId,
    DateTime Start,
    DateTime End,
    AggregateBarUnit Unit,
    int UnitNumber,
    int Limit,
    bool Live,
    bool IncludePartialBar);
