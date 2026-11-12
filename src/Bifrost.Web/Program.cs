using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Bridge;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Features.GitHub;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Features.Oidc;
using Bifrost.Web.Features.Patronage;
using Bifrost.Web.Features.Registration;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;

// Host command: registers Mill concept subjects with configuration and the database only; no web app, no migrations.
if (args.Length > 0 && args[0] == "concepts")
{
    var host = Host.CreateApplicationBuilder([]);
    host.Services.AddDbContext<BifrostDbContext>(options => options.UseNpgsql(BifrostConnectionString(host.Configuration)));
    host.Services.AddSingleton(TimeProvider.System);
    host.Services.AddScoped<AuditTrailService>();
    host.Services.AddScoped<ConceptSubjectRegistry>();
    using var commandHost = host.Build();
    Environment.ExitCode = await ConceptCommand.RunAsync(args[1..], commandHost.Services, Console.Out);
    return;
}

static string BifrostConnectionString(IConfiguration configuration) =>
    configuration.GetConnectionString("Bifrost")
        ?? "Host=127.0.0.1;Database=bifrost;Username=postgres;Password=postgres";

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<GitHubOAuthOptions>()
    .BindConfiguration(GitHubOAuthOptions.SectionName);

builder.Services
    .AddOptions<GitHubAppOptions>()
    .BindConfiguration(GitHubAppOptions.SectionName);

builder.Services
    .AddOptions<BootstrapOptions>()
    .BindConfiguration(BootstrapOptions.SectionName);

builder.Services
    .AddOptions<BifrostHostOptions>()
    .BindConfiguration(BifrostHostOptions.SectionName);

builder.Services
    .AddOptions<HeimdallOptions>()
    .BindConfiguration(HeimdallOptions.SectionName)
    .PostConfigure<ILogger<HeimdallOptions>>(HeimdallOptions.ResolveAppSecret);

builder.Services
    .AddOptions<StripeOptions>()
    .BindConfiguration(StripeOptions.SectionName);

builder.Services
    .AddOptions<BridgeOptions>()
    .BindConfiguration(BridgeOptions.SectionName);

builder.Services
    .AddOptions<ConceptVoteOptions>()
    .BindConfiguration(ConceptVoteOptions.SectionName);

builder.Services
    .AddOptions<DeskOptions>()
    .BindConfiguration(DeskOptions.SectionName);

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = BifrostCookies.Antiforgery;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.Path = BifrostCookies.Path;
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizePage("/Membership/Status");
    options.Conventions.AuthorizeFolder("/App", ActiveMemberRequirement.PolicyName);
    options.Conventions.AllowAnonymousToFolder("/Mill");
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddDbContext<BifrostDbContext>(options =>
{
    options.UseNpgsql(BifrostConnectionString(builder.Configuration));
});

builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields =
        HttpLoggingFields.RequestMethod |
        HttpLoggingFields.RequestPath |
        HttpLoggingFields.ResponseStatusCode |
        HttpLoggingFields.Duration;
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICurrentBifrostActorAccessor, CurrentBifrostActorAccessor>();
builder.Services.AddScoped<AuditTrailService>();
builder.Services.AddScoped<Bifrost.Web.Features.Membership.MembershipSuspensionService>();
builder.Services.AddScoped<DashboardSnapshotService>();
builder.Services.AddScoped<BridgeActionService>();
builder.Services.AddScoped<AgentDispatchRunService>();
builder.Services.AddScoped<AgentTransportReceiptService>();
builder.Services.AddScoped<GovernanceActivityReceiptService>();
builder.Services.AddScoped<MembershipSynchronizationService>();
builder.Services.AddScoped<BifrostIdentityService>();
builder.Services.AddScoped<GitHubWebhookService>();
builder.Services.AddScoped<MotionGovernanceService>();
builder.Services.AddScoped<SealedBallotService>();
builder.Services.AddScoped<MindIntakeService>();
builder.Services.AddOptions<MindIntakeOptions>().BindConfiguration(MindIntakeOptions.SectionName);
builder.Services.AddSingleton<MindIntakeRateLimit>(); // TimeProvider and options resolve from the container
builder.Services.AddOptions<RegistrationOptions>().BindConfiguration(RegistrationOptions.SectionName);
builder.Services.AddSingleton<RegistrationChallengeService>();
builder.Services.AddSingleton<RegistrationRateLimit>();
builder.Services.AddScoped<NativeRegistration>();
builder.Services.AddHostedService<SealedBallotCloser>();
builder.Services.AddScoped<MotionEveSurfaceService>();
builder.Services.AddScoped<PatronageService>();
builder.Services.AddScoped<StripeWebhookService>();
builder.Services.AddScoped<HeimdallPatronSupportIntakeService>();
builder.Services.AddScoped<ConceptAppraisalService>();
builder.Services.AddScoped<VoterKeyResolver>();
builder.Services.AddSingleton<ConceptVoteRateLimits>();
builder.Services.AddScoped<ConceptSubjectRegistry>();
builder.Services.AddScoped<ReadinessService>();
builder.Services.AddScoped<ApplicationBootstrapper>();
builder.Services.AddSingleton<StartupConfigurationValidator>();
builder.Services.AddScoped<IAuthorizationHandler, ActiveMemberAuthorizationHandler>();
builder.Services.AddHttpClient<HeimdallAuthService>((serviceProvider, client) =>
    HeimdallAuthService.ConfigureClient(client, serviceProvider.GetRequiredService<IOptions<HeimdallOptions>>().Value))
    .RedactLoggedHeaders(header => string.Equals(header, HeimdallAuthService.AppSecretHeader, StringComparison.OrdinalIgnoreCase));
builder.Services.AddSingleton<HeimdallTokenVerifier>();
builder.Services.AddHttpClient(HeimdallTokenVerifier.ClientName, (serviceProvider, client) =>
    HeimdallTokenVerifier.ConfigureClient(client, serviceProvider.GetRequiredService<IOptions<HeimdallOptions>>().Value));
builder.Services.AddHttpClient<StripeCheckoutService>(client =>
{
    client.BaseAddress = new Uri("https://api.stripe.com/");
});

var dataProtectionBuilder = builder.Services
    .AddDataProtection()
    .SetApplicationName("Bifrost");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// nginx on this host is the only proxy, and it connects from loopback. Trusting exactly
// those addresses keeps a direct client from choosing its own address with X-Forwarded-For.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    options.KnownProxies.Add(IPAddress.Loopback.MapToIPv6());
    options.ForwardLimit = 1;
});

var configuredGitHubOAuthOptions = builder.Configuration
    .GetSection(GitHubOAuthOptions.SectionName)
    .Get<GitHubOAuthOptions>() ?? new GitHubOAuthOptions();

var authenticationBuilder = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = BifrostCookies.Session;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.Path = BifrostCookies.Path;
        options.Cookie.Domain = null;
        options.LoginPath = "/auth/sign-in";
        options.AccessDeniedPath = "/Membership/Status";
        options.SlidingExpiration = true;
    });

if (configuredGitHubOAuthOptions.IsConfigured)
{
    authenticationBuilder.AddOAuth(GitHubAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.ClientId = configuredGitHubOAuthOptions.ClientId;
        options.ClientSecret = configuredGitHubOAuthOptions.ClientSecret;
        options.CallbackPath = configuredGitHubOAuthOptions.CallbackPath;
        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
        options.UserInformationEndpoint = "https://api.github.com/user";
        options.SaveTokens = true;
        options.CorrelationCookie.Name = BifrostCookies.OAuthCorrelation;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.CorrelationCookie.Path = BifrostCookies.Path;
        options.Scope.Add("read:user");
        options.Scope.Add("user:email");
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
        options.ClaimActions.MapJsonKey(BifrostClaimTypes.GitHubLogin, "login");
        options.ClaimActions.MapJsonKey(BifrostClaimTypes.DisplayName, "name");
        options.ClaimActions.MapJsonKey(BifrostClaimTypes.AvatarUrl, "avatar_url");

        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                request.Headers.UserAgent.ParseAdd("Bifrost");

                using var response = await context.Backchannel.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.HttpContext.RequestAborted);

                response.EnsureSuccessStatusCode();

                await using var payloadStream = await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);
                using var payload = await JsonDocument.ParseAsync(payloadStream, cancellationToken: context.HttpContext.RequestAborted);
                context.RunClaimActions(payload.RootElement);
            }
        };
    });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ActiveMemberRequirement.PolicyName, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new ActiveMemberRequirement());
    });
});

builder.Services.AddConceptTallyCors();

// The OIDC provider is mapped only when Bifrost:Oidc is configured; a configured provider that cannot load its keys stops startup.
var oidcOptions = OidcIssuerOptions.FromConfiguration(builder.Configuration);
if (oidcOptions is not null)
{
    builder.Services.AddBifrostOidc(oidcOptions);
}

// The desk is mapped only when the Desk section exists, and a configured desk needs its credential and door ports.
var deskConfigured = builder.Configuration.GetSection(DeskOptions.SectionName).Exists();
if (deskConfigured)
{
    builder.Services.AddBifrostDesk(oidcConfigured: oidcOptions is not null);
}

var app = builder.Build();
var appJsonSerializerOptions = CreateAppJsonSerializerOptions();

app.Services.GetRequiredService<StartupConfigurationValidator>().Validate();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider
        .GetRequiredService<ApplicationBootstrapper>()
        .RunAsync(CancellationToken.None);
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpLogging();
app.UseHttpsRedirection();
if (oidcOptions is not null)
{
    app.UseMiddleware<OidcGuardMiddleware>(); // before authentication: OpenIddict answers /connect/* inside it
}

app.UseStaticFiles();
app.UseRouting();
app.UseConceptTallyCors();
app.UseAuthentication();
app.UseMiddleware<GitHubMembershipSynchronizationMiddleware>();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

app.MapGet("/readyz", async (ReadinessService readinessService, CancellationToken cancellationToken) =>
{
    var report = await readinessService.GetReportAsync(cancellationToken);
    return report.IsReady
        ? Results.Text("ready")
        : Results.Text(
            string.Join(Environment.NewLine, report.Failures),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.MapConceptTallyExport();
app.MapConceptRankedExport();

if (oidcOptions is not null)
{
    app.MapBifrostOidc();
}

if (deskConfigured)
{
    app.MapDesk();
}

app.MapPost("/github/webhooks", async (
    HttpRequest request,
    GitHubWebhookService gitHubWebhookService,
    CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(request.Body);
    var payload = await reader.ReadToEndAsync(cancellationToken);
    var eventName = request.Headers["X-GitHub-Event"].ToString();
    var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();
    var signature = request.Headers["X-Hub-Signature-256"].ToString();

    var result = await gitHubWebhookService.ProcessAsync(
        eventName,
        deliveryId,
        signature,
        payload,
        cancellationToken);

    return result.Status switch
    {
        GitHubWebhookProcessStatus.Processed => Results.Text(result.Message, statusCode: StatusCodes.Status202Accepted),
        GitHubWebhookProcessStatus.Ignored => Results.Text(result.Message),
        GitHubWebhookProcessStatus.NotConfigured => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status401Unauthorized)
    };
}).AllowAnonymous();

app.MapGet("/auth/sign-in", async (HttpContext httpContext, IOptions<GitHubOAuthOptions> gitHubOptions) =>
{
    var returnPath = LocalReturnPath.Parse(httpContext.Request.Query["returnUrl"].ToString());

    var provider = httpContext.Request.Query["provider"].ToString();
    if (string.IsNullOrWhiteSpace(provider))
    {
        await WriteSignInChooserAsync(httpContext, returnPath.Value);
        return;
    }

    if (!provider.Equals("github", StringComparison.OrdinalIgnoreCase))
    {
        httpContext.Response.Redirect($"/auth/heimdall/{Uri.EscapeDataString(provider)}?returnUrl={Uri.EscapeDataString(returnPath.Value)}");
        return;
    }

    if (!gitHubOptions.Value.IsConfigured)
    {
        httpContext.Response.Redirect("/?auth=github-not-configured");
        return;
    }

    await httpContext.ChallengeAsync(
        GitHubAuthenticationDefaults.AuthenticationScheme,
        new() { RedirectUri = returnPath.Value });
}).AllowAnonymous();

app.MapPost("/auth/bifrost/register", async (
    NativeBifrostRegistrationRequest request,
    HttpContext httpContext,
    NativeRegistration registration,
    RegistrationRateLimit rateLimit,
    CancellationToken cancellationToken) =>
{
    if (rateLimit.Refuse(httpContext) is { } refused)
    {
        return refused;
    }

    var outcome = await registration.RunAsync(
        httpContext,
        new RegistrationAttempt(request.Identity, request.DisplayName, request.Challenge, request.Nonce, request.Waited == true, request.AcceptedNoticeVersion),
        cancellationToken);
    if (outcome.Refusal is not null)
    {
        return outcome.Refusal;
    }

    if (outcome.Account is not { } userAccount)
    {
        return Results.Text(outcome.Message!, statusCode: outcome.Status);
    }

    var returnUrl = string.IsNullOrWhiteSpace(request.ReturnUrl) ? "/App" : request.ReturnUrl;
    return JsonResult(
        new NativeBifrostRegistrationResponse(
            userAccount.Id,
            userAccount.BifrostIdentity,
            userAccount.DisplayName,
            userAccount.Membership?.Status.ToString() ?? MembershipStatus.Authenticated.ToString(),
            returnUrl),
        appJsonSerializerOptions);
}).AllowAnonymous();

// GET issues a challenge, so it spends the same per-address budget as /auth/bifrost/challenge.
app.MapGet("/auth/bifrost/register", (
    HttpContext httpContext,
    RegistrationChallengeService challenges,
    RegistrationRateLimit rateLimit,
    IOptions<RegistrationOptions> registrationOptions) =>
{
    if (rateLimit.Refuse(httpContext) is { } refused)
    {
        return refused;
    }

    var returnPath = LocalReturnPath.Parse(httpContext.Request.Query["returnUrl"].ToString());
    return RegistrationFormResult(httpContext, challenges, registrationOptions.Value, new RegistrationPageModel(returnPath.Value, "", 0, registrationOptions.Value.Captcha.FallbackWaitSeconds), null, StatusCodes.Status200OK);
}).AllowAnonymous();

// No antiforgery token or cookie exists for an anonymous visitor, so a cross-site post is refused by origin instead
// (RequestOriginGuard, the same Fetch Metadata check as the vote page, except that a request with no Origin and no
// Sec-Fetch-Site and no session cookie passes: text browsers send neither, and there is no session to replace). Without it a foreign page could post here with
// a challenge of its own and replace the visitor's session cookie with an empty account holding a forged consent.
// Order: origin, then the general address budget, then the body cap, then the form; a refusal sets no cookie.
app.MapPost("/auth/bifrost/register/form", async (
    HttpContext httpContext,
    NativeRegistration registration,
    RegistrationChallengeService challenges,
    RegistrationRateLimit rateLimit,
    IOptions<BifrostHostOptions> hostOptions,
    IOptions<RegistrationOptions> registrationOptions,
    CancellationToken cancellationToken) =>
{
    if (!RequestOriginGuard.AllowsFormWithoutFetchMetadata(httpContext.Request, hostOptions.Value))
    {
        return Results.Text("Cross-site registration is refused.", statusCode: StatusCodes.Status403Forbidden);
    }

    if (rateLimit.Refuse(httpContext) is { } refused)
    {
        return refused;
    }

    if (!(httpContext.Request.ContentType ?? "").StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Text("Send the form as application/x-www-form-urlencoded.", statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    // The form is a handful of short fields: refuse a big body before reading any of it.
    const int MaxFormBytes = 16 * 1024;
    if (httpContext.Request.ContentLength > MaxFormBytes)
    {
        return Results.Text("The form is too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    if (httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
    {
        limit.MaxRequestBodySize = MaxFormBytes;
    }

    IFormCollection form;
    try
    {
        form = await httpContext.Request.ReadFormAsync(cancellationToken);
    }
    catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
        return Results.Text("The form is too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (Exception error) when (error is InvalidDataException or BadHttpRequestException)
    {
        return Results.Text("The form could not be read.", statusCode: StatusCodes.Status400BadRequest);
    }

    string Field(string name) => form[name].FirstOrDefault() ?? "";
    var returnPath = LocalReturnPath.Parse(Field("returnUrl"));
    // The checkbox is the consent act and is optional: unticked, the posted notice version is ignored and no receipt is made.
    // Anything but the canonical decimal of the version in force ("02" included) with the box ticked is sent as -1 and refused, never read as "did not agree".
    int? accepted = Field("consent") != "yes"
        ? null
        : Field("acceptedNoticeVersion") == TrainingNotice.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) ? TrainingNotice.Version : -1;
    var identity = Field("identity");
    var displayName = Field("displayName");
    var challenge = Field("challenge");

    var outcome = await registration.RunAsync(
        httpContext,
        new RegistrationAttempt(identity, displayName, challenge, Field("nonce"), Field("waited") == "true", accepted),
        cancellationToken);
    if (outcome.Refusal is not null)
    {
        return outcome.Refusal;
    }

    if (outcome.Account is not null)
    {
        // A Location header takes ASCII only; anything else is not a path worth keeping.
        var target = returnPath.Value.All(c => c > ' ' && c < '\u007f') ? returnPath.Value : LocalReturnPath.Default.Value;
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Location = target;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    // A refusal that spent nothing and needs only a correction (a taken identity, a too-early time-lock) keeps its
    // challenge, whose clock must not restart. Any other failure gets a fresh challenge. The consent box always comes back unchecked.
    var options = registrationOptions.Value;
    return RegistrationFormResult(
        httpContext,
        challenges,
        options,
        new RegistrationPageModel(returnPath.Value, "", 0, options.Captcha.FallbackWaitSeconds, Truncate(identity), Truncate(displayName), outcome.Message, outcome.Field),
        outcome.KeepChallenge && challenges.IsLive(challenge) ? challenge : null,
        outcome.Status);

    static string Truncate(string text) => text.Length <= 256 ? text : text[..256];
}).AllowAnonymous();

app.MapGet("/auth/bifrost/challenge", (HttpContext httpContext, RegistrationChallengeService challenges, RegistrationRateLimit rateLimit) =>
{
    if (rateLimit.Refuse(httpContext) is { } refused)
    {
        return refused;
    }

    httpContext.Response.Headers.CacheControl = "no-store";
    var challenge = challenges.Issue();
    return challenge is null
        ? Results.Text("Registration is busy; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable)
        : JsonResult(challenge, appJsonSerializerOptions);
}).AllowAnonymous();

app.MapGet("/auth/bifrost/notice", () =>
    JsonResult(new { version = TrainingNotice.Version, text = TrainingNotice.Text }, appJsonSerializerOptions)).AllowAnonymous();

app.MapConsent();

app.MapGet("/auth/heimdall/{provider}", async (
    string provider,
    HttpContext httpContext,
    HeimdallAuthService heimdallAuthService,
    IOptions<BifrostHostOptions> hostOptions,
    ILogger<HeimdallAuthService> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        var returnPath = LocalReturnPath.Parse(httpContext.Request.Query["returnUrl"].ToString());
        var requireMemberAccess = string.Equals(
            httpContext.Request.Query["access"].ToString(),
            "member",
            StringComparison.OrdinalIgnoreCase);

        var start = await heimdallAuthService.StartAsync(
            provider,
            hostOptions.Value.PublicBaseUrl,
            returnPath,
            requireMemberAccess,
            httpContext.Request.Cookies,
            cancellationToken);

        httpContext.Response.Cookies.Append(start.CookieName, start.CookieValue, HeimdallAttemptCookie.Options());
        return Results.Redirect(start.AuthorizationUrl.ToString());
    }
    catch (HeimdallAuthException error)
    {
        logger.LogWarning("Heimdall start refused: {Reason}", error.Message);
        return Results.Redirect(HeimdallAuthService.ErrorRedirect);
    }
}).AllowAnonymous();

app.MapPost("/heimdall/patron-support/events", async (
    HttpRequest request,
    HeimdallPatronSupportIntakeService intakeService,
    CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(request.Body);
    var payload = await reader.ReadToEndAsync(cancellationToken);
    var signature = request.Headers["X-Heimdall-Signature-256"].ToString();

    var result = await intakeService.ProcessAsync(signature, payload, cancellationToken);
    return result.Status switch
    {
        HeimdallPatronSupportIntakeStatus.Processed => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status202Accepted),
        HeimdallPatronSupportIntakeStatus.BadRequest => Results.BadRequest(result.Message),
        HeimdallPatronSupportIntakeStatus.NotConfigured => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(result.Message, statusCode: StatusCodes.Status401Unauthorized)
    };
}).AllowAnonymous();

app.MapGet("/patronage/{project}/checkout", async (
    string project,
    HttpRequest request,
    ICurrentBifrostActorAccessor actorAccessor,
    StripeCheckoutService stripeCheckoutService,
    CancellationToken cancellationToken) =>
{
    var amountCentsText = request.Query["amountCents"].ToString();
    if (!int.TryParse(amountCentsText, out var amountCents))
    {
        return Results.Text(
            "Missing or invalid required amountCents query parameter.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var item = request.Query["item"].ToString();
    if (string.IsNullOrWhiteSpace(item))
    {
        return Results.Text(
            "Missing required item query parameter.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var actor = await actorAccessor.GetAsync(cancellationToken);
    if (!actor.IsAuthenticated || actor.UserAccount is null)
    {
        var returnUrl = Uri.EscapeDataString($"{request.Path}{request.QueryString}");
        return Results.Redirect($"/auth/sign-in?returnUrl={returnUrl}");
    }

    var result = await stripeCheckoutService.CreateProjectDonationCheckoutAsync(
        new StripeDonationCheckoutRequest(
            project,
            request.Query["project"].ToString(),
            item,
            amountCents,
            request.Query["currency"].ToString(),
            request.Query["source"].ToString()),
        actor.UserAccount,
        cancellationToken);
    return result.Status switch
    {
        StripeCheckoutStatus.Created => Results.Redirect(result.CheckoutUrl!.ToString()),
        StripeCheckoutStatus.InvalidRequest => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status400BadRequest),
        StripeCheckoutStatus.UnknownProject => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status404NotFound),
        StripeCheckoutStatus.NotConfigured => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status502BadGateway)
    };
}).AllowAnonymous();

app.MapPost("/patronage/stripe/webhook", async (
    HttpRequest request,
    StripeWebhookService webhookService,
    CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(request.Body);
    var payload = await reader.ReadToEndAsync(cancellationToken);
    var signature = request.Headers["Stripe-Signature"].ToString();

    var result = await webhookService.ProcessAsync(signature, payload, cancellationToken);
    return result.Status switch
    {
        StripeWebhookStatus.Processed => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status202Accepted),
        StripeWebhookStatus.Ignored => Results.Text(result.Message),
        StripeWebhookStatus.BadRequest => Results.BadRequest(result.Message),
        StripeWebhookStatus.NotConfigured => Results.Text(
            result.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Text(result.Message, statusCode: StatusCodes.Status401Unauthorized)
    };
}).AllowAnonymous();

var eveGovernance = app.MapGroup("/eve/governance")
    .RequireAuthorization(ActiveMemberRequirement.PolicyName);

eveGovernance.MapGet("/surface", async (
    ICurrentBifrostActorAccessor actorAccessor,
    MotionGovernanceService motionGovernanceService,
    MotionEveSurfaceService motionEveSurfaceService,
    CancellationToken cancellationToken) =>
{
    var actor = await actorAccessor.GetAsync(cancellationToken);
    var state = await motionGovernanceService.GetStateAsync(actor, cancellationToken);
    var surface = motionEveSurfaceService.BuildSurface(state);
    return Results.Text(
        JsonSerializer.Serialize(surface, appJsonSerializerOptions),
        "application/json");
});

// Sealed-ballot intake. Typed JSON documents posted directly by the voter; no ballot content ever rides in a room message,
// and no response before close carries another member's ballot.
var sealedMotions = app.MapGroup("/api/sealed-motions")
    .RequireAuthorization(ActiveMemberRequirement.PolicyName);

async Task<IResult> SealedCall(Func<Task<object>> call)
{
    try
    {
        return Results.Text(JsonSerializer.Serialize(await call(), appJsonSerializerOptions), "application/json");
    }
    catch (SealedBallotException error)
    {
        return Results.Text(
            JsonSerializer.Serialize(new { error = error.Message }, appJsonSerializerOptions),
            "application/json",
            statusCode: error.Failure switch
            {
                SealedBallotFailure.Forbidden => StatusCodes.Status403Forbidden,
                SealedBallotFailure.Unauthorized => StatusCodes.Status401Unauthorized,
                SealedBallotFailure.NotFound => StatusCodes.Status404NotFound,
                SealedBallotFailure.WrongPhase => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest
            });
    }
}

sealedMotions.MapPost("", (ICurrentBifrostActorAccessor actors, SealedBallotService service, OpenSealedMotionCommand command, CancellationToken ct) =>
    SealedCall(async () => await service.OpenAsync(await actors.GetAsync(ct), command, ct)));
sealedMotions.MapGet("/{id:guid}", (ICurrentBifrostActorAccessor actors, SealedBallotService service, Guid id, CancellationToken ct) =>
    SealedCall(async () => await service.GetStateAsync(await actors.GetAsync(ct), id, ct)));
sealedMotions.MapPost("/{id:guid}/commit", (ICurrentBifrostActorAccessor actors, SealedBallotService service, Guid id, SealedCommitRequest body, CancellationToken ct) =>
    SealedCall(async () => await service.CommitAsync(await actors.GetAsync(ct), id, body.Commitment, ct)));
sealedMotions.MapPost("/{id:guid}/reveal", (ICurrentBifrostActorAccessor actors, SealedBallotService service, Guid id, SealedBallotDocument body, CancellationToken ct) =>
    SealedCall(async () => await service.RevealAsync(await actors.GetAsync(ct), id, body, ct)));
sealedMotions.MapGet("/{id:guid}/result", (ICurrentBifrostActorAccessor actors, SealedBallotService service, Guid id, CancellationToken ct) =>
    SealedCall(async () => await service.GetResultAsync(await actors.GetAsync(ct), id, ct)));

sealedMotions.MapPost("/mind-keys", (ICurrentBifrostActorAccessor actors, MindIntakeService service, RegisterMindKeyRequest body, CancellationToken ct) =>
    SealedCall(async () => await service.RegisterKeyAsync(await actors.GetAsync(ct), body.Persona, body.UserAccountId, body.PublicKey, ct)));
sealedMotions.MapGet("/mind-keys", (ICurrentBifrostActorAccessor actors, MindIntakeService service, CancellationToken ct) =>
    SealedCall(async () => await service.ListKeysAsync(await actors.GetAsync(ct), ct)));
sealedMotions.MapDelete("/mind-keys/{keyId:guid}", (ICurrentBifrostActorAccessor actors, MindIntakeService service, Guid keyId, CancellationToken ct) =>
    SealedCall(async () =>
    {
        await service.RevokeKeyAsync(await actors.GetAsync(ct), keyId, ct);
        return new { revoked = keyId };
    }));

// Signed Mind intake: the Ed25519 signature is the credential. No session, no cookie; the verified envelope runs the same
// SealedBallotService verbs as a member cast.
app.MapPost("/api/sealed-motions/{id:guid}/mind", async (
    HttpContext http, MindIntakeService service, MindIntakeRateLimit limiter, IOptions<MindIntakeOptions> limits, Guid id, CancellationToken ct) =>
{
    // Per-source permit and a hard body cap, both before any parsing or database work.
    if (limiter.Spend(http.Connection.RemoteIpAddress?.ToString() ?? "unknown") is { } denied)
    {
        http.Response.Headers.RetryAfter = Math.Ceiling(denied.RetryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Text("{\"error\":\"Too many requests.\"}", "application/json", statusCode: StatusCodes.Status429TooManyRequests);
    }

    var max = limits.Value.MaxBodyBytes;
    if (http.Request.ContentLength > max)
    {
        return Results.Text("{\"error\":\"Envelope too large.\"}", "application/json", statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    var buffer = new byte[max + 1];
    var read = 0;
    int n;
    while (read < buffer.Length && (n = await http.Request.Body.ReadAsync(buffer.AsMemory(read), ct)) > 0)
    {
        read += n;
    }

    if (read > max)
    {
        return Results.Text("{\"error\":\"Envelope too large.\"}", "application/json", statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    MindEnvelope? envelope;
    try
    {
        envelope = JsonSerializer.Deserialize<MindEnvelope>(buffer.AsSpan(0, read), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (JsonException)
    {
        envelope = null;
    }

    return envelope is null
        ? Results.Text("{\"error\":\"The envelope is malformed.\"}", "application/json", statusCode: StatusCodes.Status400BadRequest)
        : await SealedCall(async () => await service.HandleAsync(id, envelope, ct));
}).AllowAnonymous();

app.MapGet("/auth/heimdall/complete", (HttpContext httpContext) =>
{
    httpContext.Response.Headers.CacheControl = "no-store";
    httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
    return Results.Content(HeimdallCompletePage.Html, "text/html", System.Text.Encoding.UTF8);
}).AllowAnonymous();

app.MapPost("/auth/heimdall/complete", async (
    HttpContext httpContext,
    HeimdallAuthService heimdallAuthService,
    IOptions<BifrostHostOptions> hostOptions,
    ILogger<HeimdallAuthService> logger,
    CancellationToken cancellationToken) =>
{
    var form = await ReadFormOrNullAsync(httpContext.Request, cancellationToken);
    var attempt = form?["attempt"].ToString();
    var held = heimdallAuthService.FindAttemptCookie(httpContext.Request.Cookies, attempt);
    try
    {
        var signIn = await heimdallAuthService.CompleteAsync(
            hostOptions.Value.PublicBaseUrl,
            attempt,
            held?.Value,
            form?["code"].ToString(),
            cancellationToken);
        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, signIn.Principal);
        httpContext.Response.Cookies.Delete(held!.Value.Name, HeimdallAttemptCookie.DeleteOptions());
        return Results.LocalRedirect(signIn.ReturnPath.Value);
    }
    catch (HeimdallAuthException error)
    {
        logger.LogWarning("Heimdall completion refused: {Reason}", error.Message);
        if (held is not null)
        {
            httpContext.Response.Cookies.Delete(held.Value.Name, HeimdallAttemptCookie.DeleteOptions());
        }

        return Results.Redirect(HeimdallAuthService.ErrorRedirect);
    }
}).AllowAnonymous();

app.MapPost("/auth/sign-out", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    httpContext.Response.Redirect("/");
}).AllowAnonymous();

app.MapRazorPages();

app.Run();

static IResult JsonResult<T>(T value, JsonSerializerOptions options, int statusCode = StatusCodes.Status200OK) =>
    Results.Text(
        JsonSerializer.Serialize(value, options),
        "application/json",
        statusCode: statusCode);

static async Task<IFormCollection?> ReadFormOrNullAsync(HttpRequest request, CancellationToken cancellationToken)
{
    if (!request.HasFormContentType)
    {
        return null;
    }

    try
    {
        return await request.ReadFormAsync(cancellationToken);
    }
    catch (Exception error) when (error is InvalidDataException or BadHttpRequestException)
    {
        return null;
    }
}

// Renders the sign-up page with a challenge: the kept one when given, else a fresh one (null when the issuing set is full).
static IResult RegistrationFormResult(
    HttpContext httpContext,
    RegistrationChallengeService challenges,
    RegistrationOptions options,
    RegistrationPageModel model,
    string? keepChallenge,
    int statusCode)
{
    var token = keepChallenge;
    var difficulty = options.Captcha.Difficulty;
    if (token is null)
    {
        var issued = challenges.Issue();
        if (issued is null)
        {
            return Results.Text("Registration is busy; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        token = issued.Challenge;
        difficulty = issued.Difficulty;
    }

    httpContext.Response.Headers.CacheControl = "no-store";
    return Results.Content(
        RegistrationPage.Render(model with { Challenge = token, Difficulty = difficulty }),
        "text/html; charset=utf-8",
        statusCode: statusCode);
}

static async Task WriteSignInChooserAsync(HttpContext httpContext, string returnUrl)
{
    httpContext.Response.ContentType = "text/html; charset=utf-8";
    var encodedReturnUrl = Uri.EscapeDataString(returnUrl);
    var escapedReturnUrl = System.Net.WebUtility.HtmlEncode(returnUrl);
    await httpContext.Response.WriteAsync($$"""
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>Sign in - Bifrost</title>
            <style>
              body { margin: 0; min-height: 100svh; display: grid; place-items: center; background: #090908; color: #fff8ef; font-family: system-ui, sans-serif; }
              main { width: min(92vw, 520px); padding: 28px; border: 1px solid rgba(255,248,239,.16); border-radius: 8px; background: #141110; }
              h1 { margin: 0 0 10px; font-size: 2rem; line-height: 1; }
              p { color: #c7b8ad; }
              .providers { display: grid; gap: 10px; margin-top: 22px; }
              a { display: flex; justify-content: space-between; align-items: center; min-height: 46px; padding: 0 14px; border-radius: 6px; background: #c43e4f; color: white; text-decoration: none; font-weight: 750; }
              a.secondary { background: rgba(255,248,239,.08); border: 1px solid rgba(255,248,239,.22); }
              small { color: #d9ad66; }
            </style>
          </head>
          <body>
            <main>
              <small>Transport identity</small>
              <h1>Create or use a Bifrost patron account</h1>
              <p>Choose a provider to attach this checkout to a Bifrost account. Patron accounts do not automatically grant active member access.</p>
              <div class="providers">
                <a href="/auth/sign-in?provider=github&returnUrl={{encodedReturnUrl}}">GitHub <span>OAuth</span></a>
                <a class="secondary" href="/auth/heimdall/discord?returnUrl={{encodedReturnUrl}}">Discord <span>Heimdall</span></a>
                <a class="secondary" href="/auth/heimdall/patreon?returnUrl={{encodedReturnUrl}}">Patreon <span>Heimdall</span></a>
                <a class="secondary" href="/auth/heimdall/twitch?returnUrl={{encodedReturnUrl}}">Twitch <span>Heimdall</span></a>
              </div>
              <p><small>After sign-in, Bifrost returns you to {{escapedReturnUrl}}.</small></p>
            </main>
          </body>
        </html>
        """);
}

static JsonSerializerOptions CreateAppJsonSerializerOptions()
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    options.Converters.Add(new JsonStringEnumConverter());
    return options;
}

public partial class Program;

public sealed record NativeBifrostRegistrationRequest(
    string Identity,
    string? DisplayName,
    string? ReturnUrl,
    string? Challenge,
    string? Nonce,
    int? AcceptedNoticeVersion,
    bool? Waited = null);

public sealed record NativeBifrostRegistrationResponse(
    Guid UserAccountId,
    string BifrostIdentity,
    string DisplayName,
    string MembershipStatus,
    string ReturnUrl);
