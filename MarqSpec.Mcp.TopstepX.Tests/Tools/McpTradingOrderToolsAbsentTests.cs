using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The five order tools are the whole write surface, and each one is routed through
/// <see cref="TradingRiskGate"/> (ADR-0024, gh#750). gh#740 asserted the opposite while the tools were withheld.
/// </summary>
[Trait("Gate", "ADR-0024")]
public sealed class McpTradingOrderToolsRegistrationTests
{
    private static readonly string[] _orderTools =
    [
        TradingMcpTools.PlaceOrderOperation,
        TradingMcpTools.ModifyOrderOperation,
        TradingMcpTools.CancelOrderOperation,
        TradingMcpTools.ClosePositionOperation,
        TradingMcpTools.PartialClosePositionOperation,
    ];

    [Fact]
    public void TradingRiskGate_OrderTools_AreExactlyTheFiveRegisteredWrites_AndEachIsGateRouted()
    {
        Assembly assembly = typeof(ToolPayloads).Assembly;
        List<Type> toolTypes =
        [
            .. assembly.GetTypes().Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null),
        ];

        toolTypes.Should().NotContain(typeof(TradingOrderTools));
        typeof(TradingOrderTools).GetCustomAttribute<McpServerToolTypeAttribute>().Should().BeNull(
            "assembly scan would publish the five tools while trading is disabled");

        List<MethodInfo> orderMethods = [];
        foreach (Type toolType in toolTypes)
        {
            foreach (ConstructorInfo ctor in toolType.GetConstructors())
            {
                ParameterInfo[] parameters = ctor.GetParameters();
                if (toolType == typeof(TradingOrderTools))
                {
                    parameters.Select(parameter => parameter.ParameterType).Should().Equal(typeof(TradingRiskGate));
                }
                else
                {
                    parameters.Should().NotContain(
                        parameter => parameter.ParameterType == typeof(TradingRiskGate),
                        because: toolType.Name + " must not take the trading gate");
                }

                parameters.Should().NotContain(
                    parameter => parameter.ParameterType == typeof(IExecutionGateway),
                    because: toolType.Name + " must call TradingRiskGate rather than the execution gateway");
            }

            foreach (MethodInfo method in toolType.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                McpServerToolAttribute? attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                if (toolType == typeof(TradingOrderTools))
                {
                    orderMethods.Add(method);
                    continue;
                }

                // record_observation writes this server's database. Every other non-order tool stays read-only.
                if (toolType == typeof(ObservationTools))
                {
                    continue;
                }

                attribute.ReadOnly.Should().BeTrue(
                    because: toolType.Name + "." + method.Name + " is not one of the five gated order tools");
                _orderTools.Should().NotContain(attribute.Name ?? string.Empty);
            }
        }

        foreach (MethodInfo method in typeof(TradingOrderTools).GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            {
                orderMethods.Add(method);
            }
        }

        typeof(TradingOrderTools).GetConstructors().Should().ContainSingle()
            .Which.GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Equal(typeof(TradingRiskGate));

        orderMethods.Should().HaveCount(_orderTools.Length);
        orderMethods.Select(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .Should()
            .BeEquivalentTo(_orderTools);
        orderMethods.Should().OnlyContain(method =>
            method.GetCustomAttribute<McpServerToolAttribute>()!.ReadOnly == false
            && method.GetCustomAttribute<McpServerToolAttribute>()!.Idempotent == false
            && method.GetCustomAttribute<McpServerToolAttribute>()!.Destructive == true
            && method.Name.StartsWith("Submit", StringComparison.Ordinal));
    }
}
