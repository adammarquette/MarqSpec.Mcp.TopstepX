using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using FluentAssertions;
using MarqSpec.Client.ProjectX.Authentication;
using MarqSpec.Client.ProjectX.Exceptions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The two signals that mean another TopstepX session has this login, and the switch that stops the
/// recorder on them (gh#757).
/// </summary>
public sealed class SessionConflictTests
{
    public static TheoryData<string> HubConflictPhrases() =>
    [
        "invalid session",
        "invalidsession",
        "session not found",
        "sessionnotfound",
        "The gateway closed: Invalid Session",
    ];

    public static TheoryData<string> UndocumentedHubPhrases() =>
    [
        "another session",
        "another device",
        "other device",
        "new device",
        "concurrent session",
        "multiple session",
        "logged in elsewhere",
        "logged in from another",
        "session conflict",
        "session has ended",
        "session was ended",
    ];

    [Theory]
    [MemberData(nameof(HubConflictPhrases))]
    public void SessionConflictSignals_HubMessageNamesADocumentedError_IsAConflict(string message)
    {
        SessionConflictSignals.IsHubConflict(message, exceptionMessage: null).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(UndocumentedHubPhrases))]
    public void SessionConflictSignals_UndocumentedHubPhrase_IsNotAConflict(string message)
    {
        SessionConflictSignals.IsHubConflict(message, exceptionMessage: null).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("session")]
    [InlineData("The session calendar rejected the window")]
    [InlineData("The connection was closed because the WebSocket handshake failed.")]
    [InlineData("The server did not respond in time.")]
    public void SessionConflictSignals_HubTransportClose_IsNotAConflict(string? message)
    {
        SessionConflictSignals.IsHubConflict(message, exceptionMessage: null).Should().BeFalse();
        SessionConflictSignals.IsHubConflict(errorMessage: null, message).Should().BeFalse();
    }

    [Fact]
    public void SessionConflictSignals_HubExceptionNamesInvalidSession_IsAConflict()
    {
        SessionConflictSignals.IsHubConflict(errorMessage: null, "Signed out: Invalid Session")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(false, SessionConflictSignals.InvalidSession, SessionProbeVerdict.Conflict)]
    [InlineData(false, SessionConflictSignals.SessionNotFound, SessionProbeVerdict.Conflict)]
    [InlineData(false, SessionConflictSignals.ExpiredToken, SessionProbeVerdict.Inconclusive)]
    [InlineData(false, SessionConflictSignals.UnknownError, SessionProbeVerdict.Inconclusive)]
    [InlineData(false, 0, SessionProbeVerdict.Inconclusive)]
    [InlineData(true, 0, SessionProbeVerdict.Valid)]
    [InlineData(true, SessionConflictSignals.InvalidSession, SessionProbeVerdict.Valid)]
    public void SessionConflictSignals_ValidateSuccessAndErrorCode_MapsToTheVerdict(
        bool success,
        int errorCode,
        SessionProbeVerdict verdict)
    {
        SessionConflictSignals.FromValidate(success, errorCode).Should().Be(verdict);
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_InvalidSessionBody_IsAConflict()
    {
        CapturingLogger logger = new();
        (ProjectXSessionConflictProbe probe, ScriptedHandler handler, _, _) = Probe(
            """{"success":false,"errorCode":1,"errorMessage":"vendor-sentence-must-not-be-logged"}""",
            HttpStatusCode.OK,
            logger);

        SessionProbeVerdict verdict = await probe.ProbeAsync(CancellationToken.None);

        verdict.Should().Be(SessionProbeVerdict.Conflict);
        handler.Calls.Should().Be(1);
        handler.LastName.Should().Be(ProjectXSessionConflictProbe.HttpClientName);
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/Auth/validate");
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("held-token");
        logger.Messages.Should().NotContain(message =>
            message.Contains("vendor-sentence-must-not-be-logged", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(message =>
            message.Contains("held-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_ExpiredTokenBody_IsNotAConflict()
    {
        (ProjectXSessionConflictProbe probe, _, _, _) = Probe(
            """{"success":false,"errorCode":3}""",
            HttpStatusCode.OK);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_SuccessBody_IsValid()
    {
        (ProjectXSessionConflictProbe probe, _, _, _) = Probe(
            """{"success":true,"errorCode":0}""",
            HttpStatusCode.OK);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Valid);
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_ValidateReturnsNewToken_RemembersItAndDoesNotLogIt()
    {
        CapturingLogger logger = new();
        (ProjectXSessionConflictProbe probe, ScriptedHandler handler, HeldSession held, FakeTimeProvider clock) = Probe(
            """{"success":true,"errorCode":0,"newToken":"rotated-token"}""",
            HttpStatusCode.OK,
            logger);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Valid);

        held.TryReadFresh(clock.GetUtcNow(), out string token).Should().BeTrue();
        token.Should().Be("rotated-token");
        logger.Messages.Should().NotContain(message =>
            message.Contains("rotated-token", StringComparison.Ordinal));

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Valid);
        handler.LastRequest!.Headers.Authorization!.Parameter.Should().Be("rotated-token");
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_MissingErrorCode_IsInconclusive()
    {
        // A missing number is missing. success:false with no code is not InvalidSession.
        (ProjectXSessionConflictProbe probe, _, _, _) = Probe(
            """{"success":false}""",
            HttpStatusCode.OK);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_ValidateHttpFailure_IsInconclusive()
    {
        CapturingLogger logger = new();
        (ProjectXSessionConflictProbe probe, ScriptedHandler handler, _, _) = Probe(
            "vendor-sentence-must-not-be-logged",
            HttpStatusCode.ServiceUnavailable,
            logger);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
        handler.Calls.Should().Be(1);
        logger.Messages.Should().NotContain(message =>
            message.Contains("vendor-sentence-must-not-be-logged", StringComparison.Ordinal));
        logger.Messages.Should().Contain(message => message.Contains("503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_UnauthorizedValidate_IsAConflict()
    {
        CapturingLogger logger = new();
        (ProjectXSessionConflictProbe probe, ScriptedHandler handler, _, _) = Probe(
            """{"errorMessage":"vendor-sentence-must-not-be-logged"}""",
            HttpStatusCode.Unauthorized,
            logger);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Conflict);
        handler.Calls.Should().Be(1);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/Auth/validate");
        logger.Messages.Should().NotContain(message =>
            message.Contains("vendor-sentence-must-not-be-logged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_NetworkFailure_IsInconclusive()
    {
        CapturingLogger logger = new();
        (ProjectXSessionConflictProbe probe, ScriptedHandler handler, _, _) = Probe(
            "{}",
            HttpStatusCode.OK,
            logger,
            throws: new HttpRequestException("vendor-sentence-must-not-be-logged"));

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
        handler.Calls.Should().Be(1);
        logger.Messages.Should().NotContain(message =>
            message.Contains("vendor-sentence-must-not-be-logged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_HttpTimeout_IsInconclusive()
    {
        CapturingLogger logger = new();
        (ProjectXSessionConflictProbe probe, ScriptedHandler handler, _, _) = Probe(
            "{}",
            HttpStatusCode.OK,
            logger,
            throws: new TaskCanceledException("vendor-sentence-must-not-be-logged"));

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
        handler.Calls.Should().Be(1);
        logger.Messages.Should().NotContain(message =>
            message.Contains("vendor-sentence-must-not-be-logged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_CallerCancellation_Propagates()
    {
        (ProjectXSessionConflictProbe probe, _, _, _) = Probe(
            """{"success":true,"errorCode":0}""",
            HttpStatusCode.OK);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Func<Task> act = () => probe.ProbeAsync(cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_AgedHeldToken_DoesNotCallLoginOrValidate()
    {
        (ProjectXSessionConflictProbe probe, ScriptedHandler handler, HeldSession held, FakeTimeProvider clock) = Probe(
            """{"success":false,"errorCode":1}""",
            HttpStatusCode.OK,
            remember: false);
        held.Remember("aged-token", clock.GetUtcNow());
        clock.Advance(HeldSession.CacheLifetime - HeldSession.RefreshBuffer);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
        handler.Calls.Should().Be(0);
        handler.Requests.Should().NotContain(request =>
            request.RequestUri!.AbsolutePath.Contains("loginKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_NoFreshHeldToken_IsInconclusiveAndDoesNotCallValidate()
    {
        (ProjectXSessionConflictProbe probe, ScriptedHandler handler, _, _) = Probe(
            """{"success":false,"errorCode":1}""",
            HttpStatusCode.OK,
            remember: false);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GatedAuthenticationService_LoginClosedAndTokenAgedOut_DoesNotCallTheInnerLogin()
    {
        ScriptedAuth inner = new();
        HeldSession held = new();
        SessionLoginGate gate = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        held.Remember("aged-token", clock.GetUtcNow());
        clock.Advance(HeldSession.CacheLifetime - HeldSession.RefreshBuffer);
        gate.BeginSuspecting();
        GatedAuthenticationService gated = new(inner, held, gate, clock);

        Func<Task> read = () => gated.GetAccessTokenAsync(CancellationToken.None);
        Func<Task> refresh = () => gated.RefreshTokenAsync(CancellationToken.None);

        await read.Should().ThrowAsync<AuthenticationException>();
        await refresh.Should().ThrowAsync<AuthenticationException>();
        (await gated.ValidateSessionAsync(CancellationToken.None)).Should().BeFalse();
        inner.GetCalls.Should().Be(0);
        inner.RefreshCalls.Should().Be(0);
        inner.ValidateCalls.Should().Be(0);
    }

    [Fact]
    public async Task GatedAuthenticationService_ProbeInFlightAndTokenAgedOut_DoesNotCallTheInnerLogin()
    {
        ScriptedAuth inner = new();
        HeldSession held = new();
        SessionLoginGate gate = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        gate.TryBeginProbe().Should().BeTrue();
        GatedAuthenticationService gated = new(inner, held, gate, clock);

        Func<Task> read = () => gated.GetAccessTokenAsync(CancellationToken.None);

        await read.Should().ThrowAsync<AuthenticationException>();
        inner.GetCalls.Should().Be(0);
    }

    [Fact]
    public async Task GatedAuthenticationService_StopLatchedAndTokenAgedOut_DoesNotCallTheInnerLogin()
    {
        ScriptedAuth inner = new();
        HeldSession held = new();
        SessionLoginGate gate = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        gate.LatchStop();
        GatedAuthenticationService gated = new(inner, held, gate, clock);

        Func<Task> read = () => gated.GetAccessTokenAsync(CancellationToken.None);

        await read.Should().ThrowAsync<AuthenticationException>();
        inner.GetCalls.Should().Be(0);
    }

    [Fact]
    public async Task GatedAuthenticationService_LoginClosedWithAFreshToken_ReturnsItWithoutLogin()
    {
        ScriptedAuth inner = new();
        HeldSession held = new();
        SessionLoginGate gate = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        held.Remember("still-fresh", clock.GetUtcNow());
        gate.LatchStop();
        GatedAuthenticationService gated = new(inner, held, gate, clock);

        string token = await gated.GetAccessTokenAsync(CancellationToken.None);

        token.Should().Be("still-fresh");
        inner.GetCalls.Should().Be(0);
    }

    [Fact]
    public async Task GatedAuthenticationService_LoginPermittedAndTokenAgedOut_CallsTheInnerLogin()
    {
        ScriptedAuth inner = new() { Token = "fresh-from-login" };
        HeldSession held = new();
        SessionLoginGate gate = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        GatedAuthenticationService gated = new(inner, held, gate, clock);

        string token = await gated.GetAccessTokenAsync(CancellationToken.None);

        token.Should().Be("fresh-from-login");
        inner.GetCalls.Should().Be(1);
        held.TryReadFresh(clock.GetUtcNow(), out string remembered).Should().BeTrue();
        remembered.Should().Be("fresh-from-login");
    }

    [Fact]
    public void SessionLoginGate_AutoReconnectFalse_FirstRetryDelayIsNull()
    {
        MarqSpec.Client.ProjectX.Configuration.WebSocketOptions options = new()
        {
            AutoReconnect = false,
            InitialReconnectDelaySeconds = 1,
            MaxReconnectDelaySeconds = 5,
        };

        SessionLoginGate.FirstAutomaticRetryDelay(options).Should().BeNull();
    }

    [Fact]
    public void SessionLoginGate_AutoReconnectTrue_FirstRetryDelayMatchesTheClient()
    {
        MarqSpec.Client.ProjectX.Configuration.WebSocketOptions options = new()
        {
            AutoReconnect = true,
            InitialReconnectDelaySeconds = 1,
            MaxReconnectDelaySeconds = 5,
        };

        SessionLoginGate.FirstAutomaticRetryDelay(options).Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void SessionLoginGate_SuppressThenRestore_PutsBackThePriorValue()
    {
        SessionLoginGate gate = new();
        MarqSpec.Client.ProjectX.Configuration.WebSocketOptions leftOff = new() { AutoReconnect = false };
        MarqSpec.Client.ProjectX.Configuration.WebSocketOptions leftOn = new() { AutoReconnect = true };

        gate.SuppressAutomaticRetry(leftOff);
        leftOff.AutoReconnect.Should().BeFalse();
        gate.RestorePriorAutoReconnect(leftOff);
        leftOff.AutoReconnect.Should().BeFalse();

        SessionLoginGate other = new();
        other.SuppressAutomaticRetry(leftOn);
        leftOn.AutoReconnect.Should().BeFalse();
        other.RestorePriorAutoReconnect(leftOn);
        leftOn.AutoReconnect.Should().BeTrue();
    }

    [Fact]
    public void SessionLoginGate_StopLatched_DoesNotRestoreAutoReconnect()
    {
        SessionLoginGate gate = new();
        MarqSpec.Client.ProjectX.Configuration.WebSocketOptions options = new() { AutoReconnect = true };
        gate.SuppressAutomaticRetry(options);
        gate.LatchStop();

        gate.RestorePriorAutoReconnect(options);

        options.AutoReconnect.Should().BeFalse();
    }

    [Fact]
    public void MarketDataOptions_StopOnSessionConflict_DefaultsToTrue()
    {
        MarketDataOptions options = new();

        options.StopOnSessionConflict.Should().BeTrue();
        options.SessionConflictBackoff.Should().Be(TimeSpan.FromSeconds(5));
        options.SessionConflictBackoff.Should().Be(MarketDataOptions.DefaultSessionConflictBackoff);
    }

    [Fact]
    public void MarketDataOptions_StopOnSessionConflictFromConfiguration_BindsTheSwitchAndTheBackoff()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MarketDataOptions.SectionName}:StopOnSessionConflict"] = "false",
                [$"{MarketDataOptions.SectionName}:SessionConflictBackoff"] = "00:00:30",
            })
            .Build();

        MarketDataOptions options = new();
        configuration.GetSection(MarketDataOptions.SectionName).Bind(options);

        options.StopOnSessionConflict.Should().BeFalse();
        options.SessionConflictBackoff.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void MarketDataOptions_SessionConflictBackoffPastTwoMinutes_IsRefused()
    {
        MarketDataOptions options = new()
        {
            SessionConflictBackoff = MarketDataOptions.MaxSessionConflictBackoff + TimeSpan.FromSeconds(1),
        };

        options.Validate(new ValidationContext(options)).Should().Contain(result =>
            result.ErrorMessage != null
            && result.ErrorMessage.Contains(
                "MarketData:SessionConflictBackoff",
                StringComparison.Ordinal)
            && result.MemberNames.Contains(nameof(MarketDataOptions.SessionConflictBackoff)));
    }

    [Fact]
    public void MarketDataOptions_SessionConflictBackoff_RefusesANegativeValue()
    {
        MarketDataOptions options = new() { SessionConflictBackoff = TimeSpan.FromSeconds(-1) };

        options.Validate(new ValidationContext(options)).Should().Contain(result =>
            result.MemberNames.Contains(nameof(MarketDataOptions.SessionConflictBackoff)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    public void MarketDataOptions_SessionConflictBackoffAtZeroAndTwoMinutes_IsAccepted(int seconds)
    {
        MarketDataOptions options = new() { SessionConflictBackoff = TimeSpan.FromSeconds(seconds) };

        options.Validate(new ValidationContext(options)).Should().NotContain(result =>
            result.MemberNames.Contains(nameof(MarketDataOptions.SessionConflictBackoff)));
    }

    [Fact]
    public void MarketDataOptions_EnvExampleAndCompose_StateTheSessionConflictDefaults()
    {
        string root = FindRepoRoot();
        string envExample = File.ReadAllText(Path.Combine(root, ".env.example"));
        string compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));

        envExample.Should().Contain("MarketData__StopOnSessionConflict=true");
        envExample.Should().Contain("MarketData__SessionConflictBackoff=00:00:05");
        compose.Should().Contain("${MarketData__StopOnSessionConflict:-true}");
        compose.Should().Contain("${MarketData__SessionConflictBackoff:-00:00:05}");
    }

    [Fact]
    public void TapeAvailabilityHolder_StoppedForSessionConflict_OutranksAPerInstrumentListen()
    {
        // A process-wide conflict is the tape. A stale per-instrument listening mark must not
        // make get_footprint look healthy after the recorder has stood down.
        TapeAvailabilityHolder holder = new();
        holder.Set(TapeAvailability.StoppedForSessionConflict());
        holder.Set("ES", TapeAvailability.Listening());

        TapeAvailability health = holder.For("ES");
        health.Reason.Should().Be(TapeUnavailableReason.StoppedForSessionConflict);
        health.Explanation.Should().Contain("trading platform");
        health.Explanation.Should().Contain("MarketData__StopOnSessionConflict=false");
        health.IsListening.Should().BeFalse();
    }

    [Fact]
    public void Program_VenueConfigured_RegistersTheSessionConflictProbe()
    {
        using ServiceProvider provider = BuildHost(new Dictionary<string, string?>
        {
            ["ProjectX:ApiKey"] = "a-username",
            ["ProjectX:ApiSecret"] = "an-api-key",
            ["ProjectX:DataTier"] = "Simulated",
            ["ProjectX:BaseUrl"] = "https://gateway.example/",
        });

        provider.GetRequiredService<ISessionConflictProbe>()
            .Should().BeOfType<ProjectXSessionConflictProbe>();
        provider.GetRequiredService<IAuthenticationService>()
            .Should().BeOfType<GatedAuthenticationService>();

        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ProjectXSessionConflictProbe.HttpClientName);
        client.BaseAddress.Should().Be(new Uri("https://gateway.example/"));
        client.Timeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Program_VenueNotConfigured_DoesNotRegisterTheSessionConflictProbe()
    {
        using ServiceProvider provider = BuildHost([]);

        provider.GetService<ISessionConflictProbe>().Should().BeNull();
    }

    private static (
        ProjectXSessionConflictProbe Probe,
        ScriptedHandler Handler,
        HeldSession Held,
        FakeTimeProvider Clock) Probe(
        string body,
        HttpStatusCode status,
        ILogger<ProjectXSessionConflictProbe>? logger = null,
        Exception? throws = null,
        bool remember = true)
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        HeldSession held = new();
        if (remember)
        {
            held.Remember("held-token", clock.GetUtcNow());
        }

        ScriptedHandler handler = new()
        {
            Status = status,
            Body = body,
            Throws = throws,
        };
        ProjectXSessionConflictProbe probe = new(
            held,
            new ScriptedFactory(handler),
            logger ?? NullLogger<ProjectXSessionConflictProbe>.Instance,
            clock);
        return (probe, handler, held, clock);
    }

    private static ServiceProvider BuildHost(Dictionary<string, string?> extra)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IConfigurationBuilder sources = builder.Configuration;
        foreach (EnvironmentVariablesConfigurationSource ambient in sources.Sources
            .OfType<EnvironmentVariablesConfigurationSource>()
            .Where(source => string.IsNullOrEmpty(source.Prefix))
            .ToList())
        {
            sources.Sources.Remove(ambient);
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["MarketData:Instruments"] = "ES",
            ["MarketData:SessionCloseCentral"] = "16:00",
        });
        builder.Configuration.AddInMemoryCollection(extra);

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MarqSpec.Mcp.TopstepX.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find MarqSpec.Mcp.TopstepX.slnx above " + AppContext.BaseDirectory);
    }

    private sealed class ScriptedAuth : IAuthenticationService
    {
        public string Token { get; set; } = "held-token";

        public Exception? GetThrows { get; set; }

        public int GetCalls { get; private set; }

        public int RefreshCalls { get; private set; }

        public int ValidateCalls { get; private set; }

        public int LogoutCalls { get; private set; }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            GetCalls++;
            if (GetThrows is not null)
            {
                throw GetThrows;
            }

            return Task.FromResult(Token);
        }

        public Task RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> ValidateSessionAsync(CancellationToken cancellationToken = default)
        {
            ValidateCalls++;
            return Task.FromResult(true);
        }

        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            LogoutCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = "{}";

        public Exception? Throws { get; set; }

        public int Calls { get; private set; }

        public string? LastName { get; set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            Requests.Add(request);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (Throws is not null)
            {
                throw Throws;
            }

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private sealed class ScriptedFactory(ScriptedHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            handler.LastName = name;
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://gateway.example/"),
            };
        }
    }

    private sealed class CapturingLogger : ILogger<ProjectXSessionConflictProbe>
    {
        public List<string> Warnings { get; } = [];

        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string rendered = formatter(state, exception);
            Messages.Add(rendered);
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }

            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(rendered);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
