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

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The two signals that mean another TopstepX session has this login, and the switch that stops the
/// recorder on them (gh#757).
/// </summary>
public sealed class SessionConflictTests
{
    public static TheoryData<string> HubConflictPhrases() =>
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
        "invalid session",
        "invalidsession",
        "session not found",
        "sessionnotfound",
        "session has ended",
        "session was ended",
        "The gateway closed: Another Session took this login",
    ];

    [Theory]
    [MemberData(nameof(HubConflictPhrases))]
    public void SessionConflictSignals_HubMessageNamesAnotherSession_IsAConflict(string message)
    {
        SessionConflictSignals.IsHubConflict(message, exceptionMessage: null).Should().BeTrue();
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
    public void SessionConflictSignals_HubExceptionNamesAnotherDevice_IsAConflict()
    {
        SessionConflictSignals.IsHubConflict(errorMessage: null, "Signed in on another device")
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
    public void SessionConflictSignals_ValidateResult_MapsToTheVerdict(
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
        (ProjectXSessionConflictProbe probe, ScriptedAuth auth, ScriptedHandler handler) = Probe(
            """{"success":false,"errorCode":1,"detail":"vendor-sentence-must-not-be-logged"}""",
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
        auth.RefreshCalls.Should().Be(0);
        auth.ValidateCalls.Should().Be(0);
        auth.LogoutCalls.Should().Be(0);
        logger.Warnings.Should().NotContain(message =>
            message.Contains("vendor-sentence-must-not-be-logged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_ExpiredTokenBody_IsNotAConflict()
    {
        (ProjectXSessionConflictProbe probe, _, _) = Probe(
            """{"success":false,"errorCode":3}""",
            HttpStatusCode.OK);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_SuccessBody_IsValid()
    {
        (ProjectXSessionConflictProbe probe, _, _) = Probe(
            """{"success":true,"errorCode":0}""",
            HttpStatusCode.OK);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Valid);
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_MissingErrorCode_IsInconclusive()
    {
        // A missing number is missing. success:false with no code is not InvalidSession.
        (ProjectXSessionConflictProbe probe, _, _) = Probe(
            """{"success":false}""",
            HttpStatusCode.OK);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_ValidateHttpFailure_IsInconclusive()
    {
        (ProjectXSessionConflictProbe probe, ScriptedAuth auth, ScriptedHandler handler) = Probe(
            "nope",
            HttpStatusCode.ServiceUnavailable);

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
        handler.Calls.Should().Be(1);
        auth.RefreshCalls.Should().Be(0);
        auth.ValidateCalls.Should().Be(0);
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_NetworkFailure_IsInconclusive()
    {
        (ProjectXSessionConflictProbe probe, _, ScriptedHandler handler) = Probe(
            "{}",
            HttpStatusCode.OK,
            throws: new HttpRequestException("the gateway could not be reached"));

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ProjectXSessionConflictProbe_TokenLookupFails_IsInconclusive()
    {
        (ProjectXSessionConflictProbe probe, ScriptedAuth auth, ScriptedHandler handler) = Probe(
            """{"success":false,"errorCode":1}""",
            HttpStatusCode.OK);
        auth.GetThrows = new AuthenticationException("the cached token could not be read");

        (await probe.ProbeAsync(CancellationToken.None)).Should().Be(SessionProbeVerdict.Inconclusive);
        handler.Calls.Should().Be(0);
        auth.RefreshCalls.Should().Be(0);
        auth.ValidateCalls.Should().Be(0);
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
                "MarketData__SessionConflictBackoff",
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
    public void MarketDataOptions_SessionConflictBackoff_AcceptsZeroThroughTwoMinutes(int seconds)
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
        ScriptedAuth Auth,
        ScriptedHandler Handler) Probe(
        string body,
        HttpStatusCode status,
        ILogger<ProjectXSessionConflictProbe>? logger = null,
        Exception? throws = null)
    {
        ScriptedAuth auth = new();
        ScriptedHandler handler = new()
        {
            Response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
            Throws = throws,
        };
        ProjectXSessionConflictProbe probe = new(
            auth,
            new ScriptedFactory(handler),
            logger ?? NullLogger<ProjectXSessionConflictProbe>.Instance);
        return (probe, auth, handler);
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
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);

        public Exception? Throws { get; set; }

        public int Calls { get; private set; }

        public string? LastName { get; set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (Throws is not null)
            {
                throw Throws;
            }

            return Task.FromResult(Response);
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
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
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
