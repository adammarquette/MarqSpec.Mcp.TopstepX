namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Which market-data universe the gateway answers from.
/// </summary>
/// <remarks>
/// A <b>data-entitlement</b> axis, not an account one. It says which data you are allowed to see, not what an
/// account is.
/// </remarks>
public enum ProjectXDataTier
{
    /// <summary>
    /// Unset. Never valid — startup fails rather than picking one.
    /// </summary>
    /// <remarks>
    /// There is no default because a wrong tier does not error: it returns an <b>empty universe</b>. Practice
    /// credentials asking for the live tier see zero contracts, and the failure surfaces far away as "no
    /// contract matches ES". A silent default here would be indistinguishable from a missing instrument.
    /// </remarks>
    Unspecified = 0,

    /// <summary>The practice / simulated data universe.</summary>
    Simulated = 1,

    /// <summary>The live data universe. Requires an entitlement.</summary>
    Live = 2,
}

/// <summary>
/// This server's own venue settings, alongside the ones the vendor client binds for itself.
/// </summary>
/// <remarks>
/// The client reads <c>ProjectX:ApiKey</c>, <c>ApiSecret</c> and <c>BaseUrl</c> from the same section. This
/// type adds only what the client has no concept of.
/// </remarks>
public sealed class VenueOptions
{
    /// <summary>The configuration section this binds to — shared with the vendor client.</summary>
    public const string SectionName = "ProjectX";

    /// <summary>
    /// The market-data tier. <b>Required</b>; startup fails when it is unset.
    /// </summary>
    public ProjectXDataTier DataTier { get; init; } = ProjectXDataTier.Unspecified;

    /// <summary>The username, sent as the gateway's <c>username</c>. Named ApiKey by the vendor.</summary>
    /// <remarks>
    /// Read here only to decide whether the venue is configured at all. The vendor client binds the same
    /// values independently for its own use.
    /// <para>
    /// <b>The names are inverted from what they read like.</b> The gateway's endpoint is "log in as the
    /// specified user using the specified API key", so this field is the USERNAME. Putting the API key in
    /// both fields authenticates as a user who does not exist, and fails with a bare "Unknown error"
    /// delivered on an HTTP 200.
    /// </para>
    /// </remarks>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The API key, sent as the gateway's <c>apikey</c>. Named ApiSecret by the vendor.</summary>
    public string ApiSecret { get; init; } = string.Empty;

    /// <summary>Whether both credentials are present.</summary>
    /// <remarks>
    /// When they are not, the server still starts and serves everything that needs no venue — the same shape
    /// a missing database takes. A trading server that refuses to boot without credentials is one an operator
    /// cannot inspect before configuring.
    /// </remarks>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}
