using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>Shared account-stage source for reads and trading gates (gh#710, gh#737).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class AccountStageResolverTests
{
    private readonly ProjectXAccountStageResolver _resolver = new();

    [Theory]
    [InlineData("PRAC-50K-1234", AccountStage.Practice)]
    [InlineData("EXPRESS-50K-9", AccountStage.Funded)]
    public void ResolveFromAccountName_MatchesProjectXMapping(string name, AccountStage expected)
    {
        _resolver.ResolveFromAccountName(name).Should().Be(expected);
        ProjectXMapping.ResolveStage(name).Should().Be(expected);
    }

    [Fact]
    public void ToAccount_UsesTheSameStageAsTheResolver()
    {
        const string name = "PRAC-V2-12345-12345678";

        AccountStage fromResolver = _resolver.ResolveFromAccountName(name);

        VenueAccount mapped = ProjectXMapping.ToAccount(
            new MarqSpec.Client.ProjectX.Api.Models.TradingAccount
            {
                Id = 1,
                Name = name,
                Balance = 1m,
                CanTrade = true,
                IsVisible = true,
            },
            _resolver);

        mapped.Stage.Should().Be(fromResolver);
        mapped.Stage.Should().Be(AccountStage.Practice);
    }
}
