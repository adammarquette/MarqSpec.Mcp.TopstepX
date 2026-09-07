using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// Everything stateful carries <c>RemovalPolicy.RETAIN</c> (ADR-0023 §6), and both halves of it: a property
/// change that <i>replaces</i> the EFS reads <c>UpdateReplacePolicy</c>, not <c>DeletionPolicy</c>, and the
/// tape is original data with no backfill.
/// </summary>
public sealed class StatefulResourceTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    private static readonly string[] _statefulTypes =
    [
        "AWS::EFS::FileSystem",
        "AWS::EFS::AccessPoint",
        "AWS::SecretsManager::Secret",
        "AWS::Backup::BackupVault",
        "AWS::S3::Bucket",
        "AWS::Logs::LogGroup",
    ];

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Every_stateful_resource_is_retained_on_delete_and_on_replace(string env, string _)
    {
        var t = templates.For(env);
        foreach (var type in _statefulTypes)
        {
            var resources = t.Resources(type);
            resources.Should().NotBeEmpty($"the stack owns at least one {type}");
            foreach (var (id, resource) in resources)
            {
                resource["DeletionPolicy"]?.GetValue<string>().Should().Be("Retain", $"{id} ({type})");
                resource["UpdateReplacePolicy"]?.GetValue<string>().Should().Be("Retain", $"{id} ({type}) — replacement is what deletes a tape");
            }
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Three_secret_shells_exist_under_the_environment_prefix_and_carry_no_value(string env, string _)
    {
        var t = templates.For(env);
        var secrets = t.Resources("AWS::SecretsManager::Secret").Values.Select(t.Properties).ToList();

        secrets.Select(s => s["Name"]!.GetValue<string>())
            .Should().BeEquivalentTo([$"topstepx-mcp/{env}/postgres", $"topstepx-mcp/{env}/projectx", $"topstepx-mcp/{env}/cohere"]);

        foreach (var secret in secrets)
        {
            // The template holds the SHAPE the tasks read — every JSON key a valueFrom names — and nothing
            // else. A value here would be a credential in a public repository; a generated one would be a
            // credential nobody wrote down. gh#519 writes the values by hand, once.
            secret.ContainsKey("GenerateSecretString").Should().BeFalse("a generated value is a credential nobody wrote down");
            var shell = JsonDocument.Parse(secret["SecretString"]!.GetValue<string>()).RootElement;
            shell.EnumerateObject().Should().NotBeEmpty();
            shell.EnumerateObject().Should().OnlyContain(p => p.Value.GetString() == string.Empty, "every value in the shell is empty");
        }

        JsonDocument.Parse(secrets.Single(s => s["Name"]!.GetValue<string>().EndsWith("/postgres", StringComparison.Ordinal))["SecretString"]!.GetValue<string>())
            .RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["password", "connectionString"]);
        JsonDocument.Parse(secrets.Single(s => s["Name"]!.GetValue<string>().EndsWith("/projectx", StringComparison.Ordinal))["SecretString"]!.GetValue<string>())
            .RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["apiKey", "apiSecret"]);
        JsonDocument.Parse(secrets.Single(s => s["Name"]!.GetValue<string>().EndsWith("/cohere", StringComparison.Ordinal))["SecretString"]!.GetValue<string>())
            .RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["apiKey"]);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Nothing_in_the_template_reads_like_a_credential(string env, string _)
    {
        // The only strings a credential could hide in: a literal ProjectX or Cohere key shape, a Postgres
        // password in a connection string, a bearer token. None may appear anywhere in the template.
        var text = templates.For(env).Json.ToJsonString();
        text.Should().NotContain("Password=", "a connection string with a password is a credential");
        text.Should().NotContain("changeme");
        text.Should().NotContain("HttpBearerToken");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Log_groups_keep_30_days(string env, string _)
    {
        var t = templates.For(env);
        var groups = t.Resources("AWS::Logs::LogGroup").Values.Select(t.Properties).ToList();

        groups.Select(g => g["LogGroupName"]!.GetValue<string>())
            .Should().BeEquivalentTo([$"/topstepx-mcp/{env}/server", $"/topstepx-mcp/{env}/postgres"]);
        groups.Should().OnlyContain(g => g["RetentionInDays"]!.GetValue<int>() == 30);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void No_output_names_a_secret(string env, string _)
    {
        var outputs = templates.For(env).Json["Outputs"]?.AsObject();
        if (outputs is null)
        {
            return;
        }

        foreach (var (name, output) in outputs)
        {
            Synthesised.Text(output).Should().NotContain("SecretString", name);
        }
    }
}
