using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Bridge;
using Bifrost.Web.Features.GitHub;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Features.Patronage;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    .BindConfiguration(HeimdallOptions.SectionName);

builder.Services
    .AddOptions<BridgeOptions>()
    .BindConfiguration(BridgeOptions.SectionName);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizePage("/Membership/Status");
    options.Conventions.AuthorizeFolder("/App", ActiveMemberRequirement.PolicyName);
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddDbContext<BifrostDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Bifrost")
        ?? "Host=127.0.0.1;Database=bifrost;Username=postgres;Password=postgres";

    options.UseNpgsql(connectionString);
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
builder.Services.AddScoped<DashboardSnapshotService>();
builder.Services.AddScoped<BridgeActionService>();
builder.Services.AddScoped<AgentDispatchRunService>();
builder.Services.AddScoped<AgentTransportReceiptService>();
builder.Services.AddScoped<GovernanceActivityReceiptService>();
builder.Services.AddScoped<MembershipSynchronizationService>();
builder.Services.AddScoped<GitHubWebhookService>();
builder.Services.AddScoped<MotionGovernanceService>();
builder.Services.AddScoped<MotionEveSurfaceService>();
builder.Services.AddScoped<PatronageService>();
builder.Services.AddScoped<HeimdallPatronSupportIntakeService>();
builder.Services.AddScoped<ReadinessService>();
builder.Services.AddScoped<ApplicationBootstrapper>();
builder.Services.AddSingleton<StartupConfigurationValidator>();
builder.Services.AddScoped<IAuthorizationHandler, ActiveMemberAuthorizationHandler>();
builder.Services.AddSingleton<HeimdallAuthAttemptStore>();
builder.Services.AddHttpClient<HeimdallAuthService>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<HeimdallOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));
});

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

var configuredGitHubOAuthOptions = builder.Configuration
    .GetSection(GitHubOAuthOptions.SectionName)
    .Get<GitHubOAuthOptions>() ?? new GitHubOAuthOptions();

var authenticationBuilder = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
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

var app = builder.Build();
var appJsonSerializerOptions = CreateAppJsonSerializerOptions();

app.Services.GetRequiredService<StartupConfigurationValidator>().Validate();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider
        .GetRequiredService<ApplicationBootstrapper>()
        .RunAsync(CancellationToken.None);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpLogging();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
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
    if (!gitHubOptions.Value.IsConfigured)
    {
        httpContext.Response.Redirect("/?auth=github-not-configured");
        return;
    }

    var redirectUri = httpContext.Request.Query["returnUrl"].ToString();
    if (string.IsNullOrWhiteSpace(redirectUri))
    {
        redirectUri = "/App";
    }

    await httpContext.ChallengeAsync(
        GitHubAuthenticationDefaults.AuthenticationScheme,
        new() { RedirectUri = redirectUri });
}).AllowAnonymous();

app.MapGet("/auth/heimdall/{provider}", async (
    string provider,
    HttpContext httpContext,
    HeimdallAuthService heimdallAuthService,
    IOptions<BifrostHostOptions> hostOptions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var returnUrl = httpContext.Request.Query["returnUrl"].ToString();
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            returnUrl = "/App";
        }

        var start = await heimdallAuthService.StartAsync(
            provider,
            hostOptions.Value.PublicBaseUrl,
            returnUrl,
            cancellationToken);

        return Results.Redirect(start.AuthorizationUrl.ToString());
    }
    catch (HeimdallAuthException error)
    {
        return Results.Redirect($"/?auth=heimdall-error&detail={Uri.EscapeDataString(error.Message)}");
    }
}).AllowAnonymous();

app.MapPost("/auth/heimdall/callback", (
    HeimdallBackendCallbackPayload payload,
    HeimdallAuthService heimdallAuthService) =>
{
    try
    {
        heimdallAuthService.StoreBackendCallback(payload);
        return Results.Accepted();
    }
    catch (HeimdallAuthException error)
    {
        return Results.BadRequest(error.Message);
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

var bridgeActions = app.MapGroup("/bridge/actions");

bridgeActions.MapPost("/request", async (
    HttpRequest request,
    BridgeActionRequest command,
    ICurrentBifrostActorAccessor actorAccessor,
    BridgeActionService bridgeActionService,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    var actor = await actorAccessor.GetAsync(cancellationToken);
    var caller = BuildBridgeCaller(actor, request, bridgeOptions.Value);
    if (!caller.IsAllowedTransport)
    {
        return Results.Unauthorized();
    }

    var result = await bridgeActionService.RequestAsync(command, caller, cancellationToken);
    return result.Status == BridgeActionStatus.Denied
        ? JsonResult(result, appJsonSerializerOptions, StatusCodes.Status403Forbidden)
        : JsonResult(result, appJsonSerializerOptions, StatusCodes.Status202Accepted);
});

bridgeActions.MapGet("/{id:guid}", async (
    Guid id,
    HttpRequest request,
    ICurrentBifrostActorAccessor actorAccessor,
    BifrostDbContext dbContext,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    var actor = await actorAccessor.GetAsync(cancellationToken);
    var caller = BuildBridgeCaller(actor, request, bridgeOptions.Value);
    if (!caller.IsAllowedTransport)
    {
        return Results.Unauthorized();
    }

    var action = await dbContext.BridgeActions
        .AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    if (action is null)
    {
        return Results.NotFound();
    }

    if (!caller.CanOperate(action))
    {
        return Results.Forbid();
    }

    return JsonResult(BridgeActionResult.From(action), appJsonSerializerOptions);
});

bridgeActions.MapPost("/{id:guid}/start", async (
    Guid id,
    HttpRequest request,
    ICurrentBifrostActorAccessor actorAccessor,
    BridgeActionService bridgeActionService,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    var actor = await actorAccessor.GetAsync(cancellationToken);
    var caller = BuildBridgeCaller(actor, request, bridgeOptions.Value);
    if (!caller.IsAllowedTransport)
    {
        return Results.Unauthorized();
    }

    try
    {
        var result = await bridgeActionService.StartAsync(id, caller, cancellationToken);
        return result is null
            ? Results.NotFound()
            : JsonResult(result, appJsonSerializerOptions, StatusCodes.Status202Accepted);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (BridgeActionException error)
    {
        return Results.BadRequest(error.Message);
    }
});

bridgeActions.MapPost("/{id:guid}/complete", async (
    Guid id,
    HttpRequest request,
    BridgeActionReceiptRequest command,
    ICurrentBifrostActorAccessor actorAccessor,
    BridgeActionService bridgeActionService,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    var actor = await actorAccessor.GetAsync(cancellationToken);
    var caller = BuildBridgeCaller(actor, request, bridgeOptions.Value);
    if (!caller.IsAllowedTransport)
    {
        return Results.Unauthorized();
    }

    try
    {
        var result = await bridgeActionService.CompleteAsync(id, command, caller, cancellationToken);
        return result is null
            ? Results.NotFound()
            : JsonResult(result, appJsonSerializerOptions);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (BridgeActionException error)
    {
        return Results.BadRequest(error.Message);
    }
});

bridgeActions.MapPost("/{id:guid}/fail", async (
    Guid id,
    HttpRequest request,
    BridgeActionFailureRequest command,
    ICurrentBifrostActorAccessor actorAccessor,
    BridgeActionService bridgeActionService,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    var actor = await actorAccessor.GetAsync(cancellationToken);
    var caller = BuildBridgeCaller(actor, request, bridgeOptions.Value);
    if (!caller.IsAllowedTransport)
    {
        return Results.Unauthorized();
    }

    try
    {
        var result = await bridgeActionService.FailAsync(id, command, caller, cancellationToken);
        return result is null
            ? Results.NotFound()
            : JsonResult(result, appJsonSerializerOptions);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
});

var dispatchRuns = app.MapGroup("/dispatch/runs");

dispatchRuns.MapPost("/start", async (
    HttpRequest request,
    AgentDispatchRunStartRequest command,
    AgentDispatchRunService agentDispatchRunService,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    if (!HasValidLocalBridgeToken(request, bridgeOptions.Value))
    {
        return Results.Unauthorized();
    }

    var result = await agentDispatchRunService.StartAsync(command, null, cancellationToken);
    return JsonResult(result, appJsonSerializerOptions, StatusCodes.Status202Accepted);
});

dispatchRuns.MapPost("/{id:guid}/complete", async (
    Guid id,
    HttpRequest request,
    AgentDispatchRunCompletionRequest command,
    AgentDispatchRunService agentDispatchRunService,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    if (!HasValidLocalBridgeToken(request, bridgeOptions.Value))
    {
        return Results.Unauthorized();
    }

    var result = await agentDispatchRunService.CompleteAsync(id, command, null, cancellationToken);
    return result is null
        ? Results.NotFound()
        : JsonResult(result, appJsonSerializerOptions);
});

dispatchRuns.MapPost("/{id:guid}/fail", async (
    Guid id,
    HttpRequest request,
    AgentDispatchRunFailureRequest command,
    AgentDispatchRunService agentDispatchRunService,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    if (!HasValidLocalBridgeToken(request, bridgeOptions.Value))
    {
        return Results.Unauthorized();
    }

    var result = await agentDispatchRunService.FailAsync(id, command, null, cancellationToken);
    return result is null
        ? Results.NotFound()
        : JsonResult(result, appJsonSerializerOptions);
});

var transportReceipts = app.MapGroup("/transport/receipts");

transportReceipts.MapPost("", async (
    HttpRequest request,
    AgentTransportReceiptRequest command,
    AgentTransportReceiptService agentTransportReceiptService,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    if (!HasValidLocalBridgeToken(request, bridgeOptions.Value))
    {
        return Results.Unauthorized();
    }

    var result = await agentTransportReceiptService.RecordAsync(command, null, cancellationToken);
    return JsonResult(result, appJsonSerializerOptions, StatusCodes.Status202Accepted);
});

var governanceReceipts = app.MapGroup("/governance/receipts");

governanceReceipts.MapPost("", async (
    HttpRequest request,
    GovernanceActivityReceiptRequest command,
    GovernanceActivityReceiptService governanceActivityReceiptService,
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken cancellationToken) =>
{
    if (!HasValidLocalBridgeToken(request, bridgeOptions.Value))
    {
        return Results.Unauthorized();
    }

    var result = await governanceActivityReceiptService.RecordAsync(command, null, cancellationToken);
    return JsonResult(result, appJsonSerializerOptions, StatusCodes.Status202Accepted);
});

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

eveGovernance.MapPost("/commands", async (
    MotionEveCommandRequest command,
    ICurrentBifrostActorAccessor actorAccessor,
    MotionGovernanceService motionGovernanceService,
    MotionEveSurfaceService motionEveSurfaceService,
    CancellationToken cancellationToken) =>
{
    var actor = await actorAccessor.GetAsync(cancellationToken);

    try
    {
        switch (command.Command)
        {
            case "motion.create":
                if (command.Scope is null ||
                    command.Category is null ||
                    command.ClosesAtUtc is null ||
                    string.IsNullOrWhiteSpace(command.Title) ||
                    string.IsNullOrWhiteSpace(command.Summary))
                {
                    return Results.BadRequest("motion.create requires scope, category, title, summary, and closesAtUtc.");
                }

                await motionGovernanceService.CreateMotionAsync(
                    actor,
                    new CreateMotionCommand(
                        command.Scope.Value,
                        command.ProjectId,
                        command.Category.Value,
                        command.Title,
                        command.Summary,
                        command.ClosesAtUtc.Value),
                    cancellationToken);
                break;
            case "motion.vote":
                if (command.MotionId is null || command.Choice is null)
                {
                    return Results.BadRequest("motion.vote requires motionId and choice.");
                }

                await motionGovernanceService.CastVoteAsync(
                    actor,
                    command.MotionId.Value,
                    command.Choice.Value,
                    command.Comment,
                    cancellationToken);
                break;
            case "motion.close":
                if (command.MotionId is null)
                {
                    return Results.BadRequest("motion.close requires motionId.");
                }

                await motionGovernanceService.CloseMotionAsync(actor, command.MotionId.Value, cancellationToken);
                break;
            default:
                return Results.BadRequest($"Unknown Eve governance command '{command.Command}'.");
        }
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }

    var state = await motionGovernanceService.GetStateAsync(actor, cancellationToken);
    var surface = motionEveSurfaceService.BuildSurface(state);
    return Results.Text(
        JsonSerializer.Serialize(surface, appJsonSerializerOptions),
        "application/json");
});

app.MapGet("/auth/heimdall/wait", async (
    HttpContext httpContext,
    HeimdallAuthService heimdallAuthService,
    CancellationToken cancellationToken) =>
{
    var attemptId = httpContext.Request.Query["attemptId"].ToString();
    if (string.IsNullOrWhiteSpace(attemptId))
    {
        return Results.Redirect("/?auth=heimdall-error&detail=missing-attempt-id");
    }

    try
    {
        var signIn = await heimdallAuthService.CompleteAttemptAsync(attemptId, cancellationToken);
        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, signIn.Principal);
        return Results.Redirect(signIn.RedirectUri);
    }
    catch (HeimdallAuthPendingException)
    {
        httpContext.Response.Headers.Append("Refresh", "2");
        return Results.Text("Waiting for Heimdall sign-in to complete.", "text/plain");
    }
    catch (HeimdallAuthException error)
    {
        return Results.Redirect($"/?auth=heimdall-error&detail={Uri.EscapeDataString(error.Message)}");
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

static JsonSerializerOptions CreateAppJsonSerializerOptions()
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    options.Converters.Add(new JsonStringEnumConverter());
    return options;
}

static BridgeCaller BuildBridgeCaller(
    CurrentBifrostActor actor,
    HttpRequest request,
    BridgeOptions bridgeOptions)
{
    var isLocalBridge = HasValidLocalBridgeToken(request, bridgeOptions);

    return new BridgeCaller(
        actor.IsActiveMember,
        isLocalBridge,
        actor.UserAccount?.Id,
        actor.DisplayName);
}

static bool HasValidLocalBridgeToken(HttpRequest request, BridgeOptions bridgeOptions)
{
    var bridgeToken = request.Headers["X-Bifrost-Bridge-Token"].ToString();
    return
        bridgeOptions.HasLocalBridgeToken &&
        string.Equals(bridgeToken, bridgeOptions.LocalBridgeToken, StringComparison.Ordinal);
}

public partial class Program;
