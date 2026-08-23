using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>
/// Asking the host to stop before it has finished starting is a shutdown, not a failure.
/// </summary>
/// <remarks>
/// <para>
/// This exists because it was a crash (gh#76). <c>docker run --rm &lt;image&gt;</c> without <c>-i</c> hands the
/// container an already-closed stdin; the stdio transport reads EOF immediately, completes, and asks the host
/// to shut down — while the host is still inside <c>StartAsync</c>. The generic host runs the remaining hosted
/// services against a token linked to <c>ApplicationStopping</c>, so Kestrel's <c>BindAsync</c> is cancelled
/// and throws, nothing catches it, and the runtime aborts the process. Measured on Docker Engine 29.6.2:
/// unhandled <c>TaskCanceledException</c>, exit 139, three times out of three.
/// </para>
/// <para>
/// The scenario is reproduced here by the mechanism rather than by the symptom: a hosted service registered
/// <b>before</b> <c>builder.Build()</c> adds <c>GenericWebHostService</c> — which is where the stdio transport
/// sits — that calls <see cref="IHostApplicationLifetime.StopApplication"/> from its own
/// <c>StartAsync</c>. No stdin, no container, same cancelled Kestrel bind.
/// </para>
/// <para>
/// The three tests around it are the ones that stop the fix being "make it go away by never starting", or
/// "make it go away by never failing": a host that starts fully must still bind, serve and stop at 0; a
/// cancellation that is <i>not</i> this shutdown must still fail the process; and a background service that
/// <b>faults</b> — which asks the host to stop by the very same call a clean EOF does — must still fail it
/// too, rather than exiting 0 under a <c>crit</c> log line.
/// </para>
/// <para>
/// <b>It has already earned its keep.</b> The first fix read the lifetime from the host inside the catch
/// filter — which throws <c>ObjectDisposedException</c>, because <c>RunAsync</c> disposes the host in its own
/// <c>finally</c> and that runs before the exception resurfaces at the await. An exception inside a filter is
/// swallowed and the filter reads as "does not match", so the crash came back unchanged while the code looked
/// correct. Nothing but this test said so.
/// </para>
/// </remarks>
public sealed class HostShutdownTests
{
    /// <summary>Stands in for the stdio transport meeting EOF while the host is still starting.</summary>
    private sealed class StopsWhileStarting(IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A cancellation that is nobody's shutdown request, and so is a real startup failure.</summary>
    private sealed class ThrowsCancellationWhileStarting : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            throw new OperationCanceledException("a cancellation that is not this host being asked to stop");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Stands in for the stdio transport whose read loop <b>fails</b> rather than reaching EOF.
    /// </summary>
    /// <remarks>
    /// The throw is after a yield, deliberately. A <see cref="BackgroundService"/> that throws synchronously
    /// completes its <c>ExecuteTask</c> inside <c>StartAsync</c>, and the exception surfaces straight out of
    /// startup — never reaching <see cref="BackgroundServiceExceptionBehavior.StopHost"/>, which is the path
    /// under test. Faulting after the first await is what makes the host log <c>crit</c> and then call
    /// <c>StopApplication()</c>: the same state a clean EOF leaves behind.
    /// </remarks>
    private sealed class FaultsAfterStarting : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("the transport's read loop failed");
        }
    }

    /// <summary>
    /// Holds startup open until the host has actually been asked to stop, so the ordering is explicit.
    /// </summary>
    /// <remarks>
    /// Registered <b>after</b> <see cref="FaultsAfterStarting"/> and before Kestrel, which the builder appends
    /// last. Hosted services start in registration order, so this returns only once the fault has been
    /// observed and <c>StopApplication</c> called — no sleep, no polling, and Kestrel meets a token that is
    /// already cancelled every time.
    /// </remarks>
    private sealed class WaitsForStopping(IHostApplicationLifetime lifetime) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration =
                lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

            await stopping.Task.ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Port 0 so the OS picks one. Kestrel really does bind here -- that is the point of the second test, and
    // a fixed port would make these tests unable to run beside anything else.
    private static WebApplicationBuilder NewBuilder()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder;
    }

    private static WebApplication Build()
        => NewBuilder().Build();

    private static WebApplication Build<TService>()
        where TService : class, IHostedService
    {
        WebApplicationBuilder builder = NewBuilder();
        builder.Services.AddHostedService<TService>();
        return builder.Build();
    }

    private static WebApplication Build<TFirst, TSecond>()
        where TFirst : class, IHostedService
        where TSecond : class, IHostedService
    {
        WebApplicationBuilder builder = NewBuilder();
        builder.Services.AddHostedService<TFirst>();
        builder.Services.AddHostedService<TSecond>();
        return builder.Build();
    }

    [Fact]
    public async Task TheRunExitsZero_WhenShutdownIsRequestedWhileTheHostIsStillStarting()
    {
        await using WebApplication app = Build<StopsWhileStarting>();

        int exit = await Program.RunHostAsync(app);

        exit.Should().Be(0);
    }

    [Fact]
    public async Task TheRunExitsZero_AndKestrelHasBound_WhenTheHostStartsFullyAndIsStoppedAfterwards()
    {
        // The normal path, and the one a fix can silently destroy: a client that holds stdin open means the
        // host really does finish starting -- Kestrel binds -- and only then is asked to stop.
        //
        // The bound address is captured BEFORE the stop is requested, and asserted, because exit 0 on its own
        // proves nothing about listening: a "fix" that started the host without ever binding would exit 0 too.
        // The empty-address assertion is what catches that.
        //
        // Note what this test does NOT do: a fix that short-circuited startup ENTIRELY -- one where
        // ApplicationStarted never fires -- would HANG here rather than fail, because nothing else asks this
        // host to stop. The hang is the signal in that case; the assertions below cover the case where the
        // host does start but has no listener.
        await using WebApplication app = Build();

        List<string> bound = [];
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            bound.AddRange(app.Urls);
            app.Lifetime.StopApplication();
        });

        int exit = await Program.RunHostAsync(app);

        exit.Should().Be(0);
        bound.Should().NotBeEmpty("Kestrel must actually have bound a port before the stop was requested");
        bound.Should().AllSatisfy(url =>
            url.Should().NotEndWith(":0", "port 0 means the OS assigned one, and it should be reported back"));
    }

    [Fact]
    public async Task TheRunStillFails_WhenAStartupCancellationIsNotThisHostBeingAskedToStop()
    {
        await using WebApplication app = Build<ThrowsCancellationWhileStarting>();

        Func<Task> run = () => Program.RunHostAsync(app);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TheRunStillFails_WhenABackgroundServiceFaultedAndThatIsWhatStoppedTheHost()
    {
        // The case a filter that reads only `IsCancellationRequested` gets wrong, and gets wrong SILENTLY.
        //
        // Nothing here is stdio-specific: the SDK's transport is a BackgroundService, so a read loop that
        // fails after its first await puts the host in exactly this state -- `crit: BackgroundService failed`,
        // then `StopApplication()`, then a Kestrel bind against a cancelled token. That is byte-for-byte the
        // state a clean EOF leaves, so "a stop was requested" cannot tell them apart and the fault has to be
        // observed instead. A server that faulted and never served must not report success.
        await using WebApplication app = Build<FaultsAfterStarting, WaitsForStopping>();

        Func<Task> run = () => Program.RunHostAsync(app);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }
}
