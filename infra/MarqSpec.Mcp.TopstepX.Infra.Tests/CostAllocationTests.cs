using System.Text.Json.Nodes;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Infra;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// Cost-allocation tags and the account monthly budget (gh#527, ADR-0023). Every taggable environment
/// resource carries <c>Project=topstepx-mcp</c> and <c>Environment=&lt;env&gt;</c>; the budget lives on the
/// OIDC (account) stack with notification subscribers as parameter references, never email literals.
/// </summary>
public sealed class CostAllocationTests : IClassFixture<EnvironmentTemplates>
{
    private readonly EnvironmentTemplates _envs;

    public CostAllocationTests(EnvironmentTemplates envs) => _envs = envs;

    [Theory]
    [InlineData("staging")]
    [InlineData("production")]
    public void Every_taggable_environment_resource_carries_Project_and_Environment(string env)
    {
        var stack = env == "staging" ? _envs.Staging : _envs.Production;
        var taggable = TaggableResources(stack).ToList();
        taggable.Should().NotBeEmpty("the environment stack must emit at least one taggable resource");

        foreach (var (logicalId, tags) in taggable)
        {
            TagValue(tags, "Project").Should().Be("topstepx-mcp", "{0} must carry Project", logicalId);
            TagValue(tags, "Environment").Should().Be(env, "{0} must carry Environment={1}", logicalId, env);
        }
    }

    [Fact]
    public void A_fixture_missing_Environment_yields_null_for_that_key()
    {
        // The red half of the AC: the same TagValue helper the green tests use returns null when
        // Environment is absent, so a missing tag cannot silently pass as present.
        var stripped = new JsonArray(
            new JsonObject { ["Key"] = "Project", ["Value"] = "topstepx-mcp" });

        TagValue(stripped, "Project").Should().Be("topstepx-mcp");
        TagValue(stripped, "Environment").Should().BeNull();
    }

    [Fact]
    public void The_oidc_stack_carries_Project_on_every_taggable_resource_and_no_Environment()
    {
        var oidc = Synthesised.GitHubOidc();
        var taggable = TaggableResources(oidc).ToList();
        taggable.Should().NotBeEmpty();

        foreach (var (logicalId, tags) in taggable)
        {
            TagValue(tags, "Project").Should().Be("topstepx-mcp", "{0}", logicalId);
            TagValue(tags, "Environment").Should().BeNull(
                "{0} is account-scoped — Environment would invent a third env", logicalId);
        }
    }

    [Fact]
    public void The_oidc_stack_has_one_monthly_cost_budget_with_parameterised_amount_and_email()
    {
        var oidc = Synthesised.GitHubOidc();
        var (logicalId, budget) = oidc.Single("AWS::Budgets::Budget");
        var props = oidc.Properties(budget);

        oidc.Parameter("BudgetAmount").Should().NotBeNull();
        var amountDefault = oidc.Parameter("BudgetAmount")!["Default"];
        amountDefault.Should().NotBeNull();
        (amountDefault!.GetValueKind() == System.Text.Json.JsonValueKind.Number
                ? amountDefault.GetValue<double>().ToString("0")
                : amountDefault.GetValue<string>())
            .Should().Be("300");
        oidc.Parameter("AlertsEmail").Should().NotBeNull();
        oidc.Parameter("AlertsEmail")!.AsObject().ContainsKey("Default").Should().BeFalse(
            "an alerts email default would be a credential-shaped literal in the template");

        var budgetData = props["Budget"]!.AsObject();
        budgetData["BudgetType"]!.GetValue<string>().Should().Be("COST");
        budgetData["TimeUnit"]!.GetValue<string>().Should().Be("MONTHLY");
        Synthesised.Text(budgetData["BudgetLimit"]!["Amount"]).Should().Contain("BudgetAmount",
            "{0} amount must Ref the BudgetAmount parameter", logicalId);

        var notifications = props["NotificationsWithSubscribers"]!.AsArray();
        notifications.Should().HaveCountGreaterThanOrEqualTo(4,
            "50/80/100% actual and 100% forecast — four notifications");

        foreach (var note in notifications)
        {
            var subscribers = note!["Subscribers"]!.AsArray();
            subscribers.Should().ContainSingle();
            Synthesised.Text(subscribers[0]!["Address"]).Should().Contain("AlertsEmail",
                "subscriber must Ref AlertsEmail, not a literal");
        }

        // No subscriber Address is a literal email — AllowedPattern on AlertsEmail contains `@` by design.
        var resourcesText = oidc.Json["Resources"]!.ToJsonString();
        resourcesText.Should().NotMatchRegex(@"""Address""\s*:\s*""[^""]+@[^""]+""",
            "notification Address must Ref AlertsEmail, never a literal");
    }

    private static IEnumerable<(string LogicalId, JsonArray Tags)> TaggableResources(Synthesised stack) =>
        stack.Json["Resources"]!.AsObject()
            .Select(kv => (LogicalId: kv.Key, Resource: kv.Value!.AsObject()))
            .Select(r => (r.LogicalId, TagsNode: stack.Properties(r.Resource)["Tags"]))
            .Where(r => r.TagsNode is JsonArray)
            .Select(r => (r.LogicalId, (JsonArray)r.TagsNode!));

    private static string? TagKey(JsonNode? tag) =>
        tag?["Key"]?.GetValue<string>() ?? tag?["key"]?.GetValue<string>();

    private static string? TagValue(JsonArray tags, string key) =>
        tags.Select(t => t!.AsObject())
            .Where(t => TagKey(t) == key)
            .Select(t => t["Value"]?.GetValue<string>() ?? t["value"]?.GetValue<string>())
            .FirstOrDefault();
}
