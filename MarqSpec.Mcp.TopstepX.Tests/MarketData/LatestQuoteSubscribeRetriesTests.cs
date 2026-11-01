using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Refused-subscribe state is locked, and a recovery starts the wait over.
/// </summary>
public sealed class LatestQuoteSubscribeRetriesTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _initial = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan _maximum = TimeSpan.FromMinutes(5);

    [Fact]
    public void ASecondRefusal_WaitsLonger_AndRecoveryRestoresTheInitialWait()
    {
        LatestQuoteSubscribeRetries retries = new();

        retries.NoteRefused("CON.F.US.EP.Z26", "ES", _now, _initial, _maximum).Should().BeTrue();
        retries.Backoff("CON.F.US.EP.Z26").Should().Be(_initial);

        retries.NoteRefused("CON.F.US.EP.Z26", "ES", _now.AddSeconds(1), _initial, _maximum)
            .Should().BeFalse();
        retries.Backoff("CON.F.US.EP.Z26").Should().Be(TimeSpan.FromSeconds(2));

        retries.NoteRecovered("CON.F.US.EP.Z26").Should().Be("ES");
        retries.Backoff("CON.F.US.EP.Z26").Should().BeNull();

        retries.NoteRefused("CON.F.US.EP.Z26", "ES", _now.AddSeconds(4), _initial, _maximum)
            .Should().BeTrue();
        retries.Backoff("CON.F.US.EP.Z26").Should().Be(_initial);
    }

    [Fact]
    public async Task Clear_WhileAnotherThreadRetries_DoesNotThrow()
    {
        LatestQuoteSubscribeRetries retries = new();
        int stop = 0;
        Task hammer = Task.Run(() =>
        {
            int n = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                string contract = "c" + (n % 4);
                retries.NoteRefused(contract, "ES", DateTimeOffset.UtcNow, _initial, _maximum);
                _ = retries.Due(DateTimeOffset.UtcNow);
                if (n % 3 == 0)
                {
                    retries.NoteRecovered(contract);
                }

                n++;
            }
        });

        for (int i = 0; i < 1_000; i++)
        {
            retries.Clear();
        }

        Volatile.Write(ref stop, 1);
        Func<Task> wait = () => hammer;
        await wait.Should().NotThrowAsync();
    }
}
