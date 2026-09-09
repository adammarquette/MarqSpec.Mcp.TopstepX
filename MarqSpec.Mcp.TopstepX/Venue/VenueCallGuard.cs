using MarqSpec.Mcp.TopstepX.Telemetry;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// The single funnel every vendor call goes through: it translates the vendor's failures into one exception
/// type carrying the vendor's numeric code, and it is where a venue call becomes a count, a duration and a
/// span.
/// </summary>
/// <remarks>
/// <para>
/// <b>This was <c>ProjectXMarketDataGateway.Guarded</c>, and it is here now so it has a test</b> (gh#536).
/// The behaviour is unchanged. What moved is where it lives: a private local could only be reached by
/// building the gateway, which needs an <c>IProjectXApiClient</c> — an interface that carries the venue's
/// whole order-transmitting surface as well as its reads, so writing a fake for it would spell those method
/// names out inside this repository merely to test a counter, which is precisely what ADR-0002 exists to
/// prevent. A funnel that takes a delegate is driven by a plain lambda instead.
/// </para>
/// <para>
/// <b>The vendor's own message string is deliberately not carried through.</b> It is free text on a channel a
/// language model reads (ADR-0008), and the code carries the diagnostic value without the surface. What the
/// caller gets instead is <i>what this server was doing</i>, which is more useful anyway — and what a span
/// gets is a status and the operation name, never the sentence and never the contract id inside it.
/// </para>
/// <para>
/// <b>The call is counted however it settles.</b> A vendor allowance is spent by the request rather than by
/// the answer, so a refused call is a call that was issued and still took time. Counting only successes would
/// make an outage read as an idle server.
/// </para>
/// </remarks>
/// <param name="telemetry">The app-owned meter and activity source.</param>
public sealed class VenueCallGuard(HostTelemetry telemetry)
{
    private readonly HostTelemetry _telemetry = telemetry;

    /// <summary>Runs one vendor call, counted, timed and traced.</summary>
    /// <typeparam name="T">What the call answers with.</typeparam>
    /// <param name="operation">Which read this is, from <see cref="VenueOperation"/>.</param>
    /// <param name="call">The vendor call.</param>
    /// <param name="what">
    /// What this server was doing, for the caller's message. <b>Free text, and it never reaches a tag</b> —
    /// it names contract ids and instrument symbols, and a span attribute built from it would be unbounded.
    /// </param>
    /// <returns>The vendor's answer.</returns>
    /// <exception cref="VenueException">The vendor refused, rejected the credentials, or was unreachable.</exception>
    public async Task<T> RunAsync<T>(string operation, Func<Task<T>> call, string what)
    {
        ArgumentNullException.ThrowIfNull(call);

        using HostTelemetry.VenueCallScope scope = _telemetry.VenueCall(operation);

        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (MarqSpec.Client.ProjectX.Exceptions.ProjectXApiException ex)
        {
            scope.Failed();

            // The vendor's STATUS CODE, never its message string -- the code carries the
            // diagnostic value without putting vendor free text on a channel a model reads.
            throw ex.StatusCode is { } code
                ? new VenueException("The gateway refused while " + what + ".", code)
                : new VenueException("The gateway refused while " + what + ".");
        }
        catch (MarqSpec.Client.ProjectX.Exceptions.AuthenticationException)
        {
            scope.Failed();

            throw new VenueException(
                "The gateway rejected the credentials. Note that ProjectX__ApiKey is the USERNAME and "
                + "ProjectX__ApiSecret is the API key -- putting the key in both authenticates as a user who "
                + "does not exist, and the gateway reports that as a bare unknown error.");
        }
        catch (HttpRequestException ex)
        {
            scope.Failed();

            throw new VenueException("The gateway could not be reached while " + what + ".", ex);
        }
    }
}
