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
/// The two tests either side of it are the ones that stop the fix being "make it go away by never starting":
/// a host that starts fully must still start, serve and stop at 0, and a cancellation that is <i>not</i> this
/// shutdown must still fail the process.
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

    /// <summary>Stands in for a real client: the host starts, then something later asks it to stop.</summary>
    private sealed class StopsOnceStarted(IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.ApplicationStarted.Register(lifetime.StopApplication);
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

    // Port 0 so the OS picks one. Kestrel really does bind here -- that is the point of the second test, and
    // a fixed port would make these two tests unable to run beside anything else.
    private static WebApplication Build<TService>()
        where TService : class, IHostedService
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHostedService<TService>();
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
    public async Task TheRunExitsZero_WhenTheHostStartsFullyAndIsStoppedAfterwards()
    {
        // The normal path, and the one a fix can silently destroy: a client that holds stdin open means the
        // host really does finish starting -- Kestrel binds -- and only then is asked to stop. A "fix" that
        // short-circuits startup would still pass the test above and fail this one.
        await using WebApplication app = Build<StopsOnceStarted>();

        int exit = await Program.RunHostAsync(app);

        exit.Should().Be(0);
    }

    [Fact]
    public async Task TheRunStillFails_WhenAStartupCancellationIsNotThisHostBeingAskedToStop()
    {
        await using WebApplication app = Build<ThrowsCancellationWhileStarting>();

        Func<Task> run = () => Program.RunHostAsync(app);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }
}
