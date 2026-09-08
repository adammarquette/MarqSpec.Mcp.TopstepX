using Amazon.CDK;
using MarqSpec.Mcp.TopstepX.Infra;

// The CDK app (ADR-0023): the same EnvironmentStack twice, differing only in what its props say may differ,
// and the OIDC stack that lets GitHub Actions deploy them. Run through `infra/cdk.json`; never by hand.
var app = new App();

// Cost-allocation tag shared by every stack (gh#527, ADR-0023). Environment= is applied per EnvironmentStack;
// the account-scoped OIDC stack carries Project alone. Activation as CE cost-allocation tags is an account
// setting outside the template — recorded on ADR-0023 with the read-back call.
Amazon.CDK.Tags.Of(app).Add("Project", "topstepx-mcp");

// The account and region come from `cdk.json`'s context, or from `-c account=… -c region=…` on the command
// line, which is how gh#519 overrides the placeholders there. The placeholder account is AWS's own
// documentation example and the region is only the cost basis ADR-0023 priced on, not a chosen one -- the
// region is gh#519's dated entry. A deploy against the wrong account is refused by the CDK itself, which
// checks the credentials' account against the stack's, so a forgotten override fails loudly.
var env = new Amazon.CDK.Environment
{
    Account = RequiredContext("account"),
    Region = RequiredContext("region"),
};

// NOT DECIDED HERE. ADR-0023's decision log records the tasks' outbound path as a fork the maintainer has
// not settled, and this app refuses to synthesise until one is named. Every shape is template-tested; CI
// synthesises each of them so the proof does not depend on a choice. When the maintainer decides, the
// choice becomes a literal here, the dated entry lands on ADR-0023 (and on ADR-0021 if it is a public IP),
// and `-c outbound=` goes away.
var outbound = Enum.TryParse<OutboundPath>(RequiredContext("outbound"), ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
    ? parsed
    : throw new InvalidOperationException(
        $"Context value 'outbound' must be one of {string.Join(", ", Enum.GetNames<OutboundPath>())} -- the tasks' outbound path is " +
        "undecided (ADR-0023 decision log, 2026-09-06) and nothing here chooses for the maintainer.");

_ = new EnvironmentStack(app, "topstepx-mcp-production", new EnvironmentStackProps
{
    EnvName = "production",
    RootDomain = "marqspec.com",
    ZoneMode = ZoneMode.Lookup,
    OutboundPath = outbound,
    // The OTLP collector sidecar (gh#537, ADR-0019 decision 5). BOTH environments get it, and staging is
    // first only in the order gh#519 fills the two shells -- one stack class, two environments, and a
    // template that is present in one and absent in the other would be a second stack class in disguise.
    // Until a shell is filled the collector fails its own config validation and stops; it is not essential,
    // so the server answers exactly as it does today.
    Telemetry = new TelemetryProps(),
    RecordTapeDefault = true,
    WarmIndicatorsDefault = true,
    Env = env,
});

// `staging.` is the epic's working spelling; gh#519 confirms it before the first `cdk deploy`, because a
// delegated zone renamed later is a re-delegation at the apex (ADR-0023 §1, ADR-0021 Bind).
_ = new EnvironmentStack(app, "topstepx-mcp-staging", new EnvironmentStackProps
{
    EnvName = "staging",
    RootDomain = "staging.marqspec.com",
    ZoneMode = ZoneMode.CreateAndDelegate,
    OutboundPath = outbound,
    // The OTLP collector sidecar (gh#537, ADR-0019 decision 5). BOTH environments get it, and staging is
    // first only in the order gh#519 fills the two shells -- one stack class, two environments, and a
    // template that is present in one and absent in the other would be a second stack class in disguise.
    // Until a shell is filled the collector fails its own config validation and stops; it is not essential,
    // so the server answers exactly as it does today.
    Telemetry = new TelemetryProps(),
    RecordTapeDefault = false,
    WarmIndicatorsDefault = false,
    Env = env,
});

_ = new GitHubOidcStack(app, "topstepx-mcp-github-oidc", new StackProps { Env = env });

app.Synth();

string RequiredContext(string key) =>
    app.Node.TryGetContext(key) as string
    ?? throw new InvalidOperationException($"Context value '{key}' is required: set it in infra/cdk.json or pass -c {key}=<value>.");
