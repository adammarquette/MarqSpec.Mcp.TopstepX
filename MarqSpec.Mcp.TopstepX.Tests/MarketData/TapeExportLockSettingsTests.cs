using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>The identity-lock session string and the exceptions that count as a dropped lock.</summary>
public sealed class TapeExportLockSettingsTests
{
    [Fact]
    public void IdentityLockConnectionString_WhenBuilt_UsesTcpKeepaliveAndRefusesPooling()
    {
        string built = TapeExportPass.IdentityLockConnectionString(
            "Host=localhost;Username=tape;Password=secret;Pooling=true;Keepalive=15");

        NpgsqlConnectionStringBuilder parsed = new(built);
        parsed.Pooling.Should().BeFalse();
        parsed.ApplicationName.Should().Be("tape-export-lock");
        parsed.TcpKeepAlive.Should().BeTrue();
        parsed.TcpKeepAliveTime.Should().Be(30);
        parsed.TcpKeepAliveInterval.Should().Be(30);
        parsed.KeepAlive.Should().Be(0);
    }

    [Fact]
    public void IsIdentityConnectionFailure_WhenTheErrorIsNotADroppedSession_IsFalse()
    {
        TapeExportPass.IsIdentityConnectionFailure(new InvalidOperationException("closed")).Should().BeFalse();
        TapeExportPass.IsIdentityConnectionFailure(
            new PostgresException("syntax", "ERROR", "ERROR", PostgresErrorCodes.SyntaxError)).Should().BeFalse();
    }

    [Fact]
    public void IsIdentityConnectionFailure_WhenTheSessionDied_IsTrue()
    {
        TapeExportPass.IsIdentityConnectionFailure(new IOException("reset")).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(new TimeoutException("timed out")).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(new NpgsqlException("broken stream")).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(
            new PostgresException("shutdown", "FATAL", "FATAL", PostgresErrorCodes.AdminShutdown)).Should().BeTrue();
    }
}
