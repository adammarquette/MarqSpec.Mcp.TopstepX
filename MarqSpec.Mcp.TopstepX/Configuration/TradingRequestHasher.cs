using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Normalises trading commands into stable SHA-256 digests for the audit log.</summary>
public static class TradingRequestHasher
{
    /// <summary>Hashes a place-order command into a stable SHA-256 hex digest for the audit log.</summary>
    public static string HashPlace(ExecutionPlaceOrderCommand command) =>
        Hash(
            command.AccountId,
            command.ContractId,
            command.OrderType,
            command.Side,
            command.Size,
            command.LimitPrice,
            command.StopPrice,
            command.TrailPrice);

    /// <summary>Hashes a modify-order command.</summary>
    public static string HashModify(ExecutionModifyOrderCommand command) =>
        Hash(command.AccountId, command.OrderId, command.Size, command.LimitPrice, command.StopPrice);

    /// <summary>Hashes a cancel-order command.</summary>
    public static string HashCancel(ExecutionCancelOrderCommand command) =>
        Hash(command.AccountId, command.OrderId);

    /// <summary>Hashes a close-position command.</summary>
    public static string HashClose(ExecutionClosePositionCommand command) =>
        Hash(command.AccountId, command.ContractId);

    /// <summary>Hashes a partial-close command.</summary>
    public static string HashPartialClose(ExecutionPartialClosePositionCommand command) =>
        Hash(command.AccountId, command.ContractId, command.Size);

    private static string Hash(params object?[] parts)
    {
        StringBuilder builder = new();
        foreach (object? part in parts)
        {
            if (builder.Length > 0)
            {
                builder.Append('|');
            }

            builder.Append(part switch
            {
                null => "null",
                decimal d => d.ToString(CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => part.ToString() ?? string.Empty,
            });
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
