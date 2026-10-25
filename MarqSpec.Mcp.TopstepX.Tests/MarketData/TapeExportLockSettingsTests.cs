using System.Net.Sockets;
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
        // A bare NpgsqlException is not transient. Protocol, auth, and a command already in progress
        // propagate; they are not a lost lock.
        TapeExportPass.IsIdentityConnectionFailure(new NpgsqlException("broken stream")).Should().BeFalse();
        TapeExportPass.IsIdentityConnectionFailure(
            new NpgsqlOperationInProgressException(new NpgsqlCommand("SELECT 1"))).Should().BeFalse();
        TapeExportPass.IsIdentityConnectionFailure(
            new PostgresException("syntax", "ERROR", "ERROR", PostgresErrorCodes.SyntaxError)).Should().BeFalse();
        // Transient on Postgres, and still not a dropped session. 55P03 is IdentityBusy.
        TapeExportPass.IsIdentityConnectionFailure(
            new PostgresException("serialize", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure)).Should().BeFalse();
        TapeExportPass.IsIdentityConnectionFailure(
            new PostgresException("locked", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable)).Should().BeFalse();
    }

    [Fact]
    public void IsIdentityConnectionFailure_WhenTheSessionDied_IsTrue()
    {
        TapeExportPass.IsIdentityConnectionFailure(new IOException("reset")).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(new TimeoutException("timed out")).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(
            new SocketException((int)SocketError.ConnectionReset)).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(
            new NpgsqlException("broken stream", new IOException("reset"))).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(
            new NpgsqlException("broken stream", new SocketException((int)SocketError.ConnectionReset))).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(
            new NpgsqlException("outer", new NpgsqlException("inner", new TimeoutException("timed out")))).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(
            new PostgresException("shutdown", "FATAL", "FATAL", PostgresErrorCodes.AdminShutdown)).Should().BeTrue();
        TapeExportPass.IsIdentityConnectionFailure(
            new PostgresException("down", "FATAL", "FATAL", PostgresErrorCodes.ConnectionFailure)).Should().BeTrue();
    }
}
