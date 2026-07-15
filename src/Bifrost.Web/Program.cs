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
    .AddOptions<StripeOptions>()
    .BindConfiguration(StripeOptions.SectionName);

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
builder.Services.AddScoped<BifrostIdentityService>();
builder.Services.AddScoped<GitHubWebhookService>();
builder.Services.AddScoped<MotionGovernanceService>();
builder.Services.AddScoped<MotionEveSurfaceService>();
builder.Services.AddScoped<PatronageService>();
builder.Services.AddScoped<StripeWebhookService>();
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
builder.Services.AddHttpClient<StripeCheckoutService>(client =>
{
    client.BaseAddress = new Uri("https://api.stripe.com/");
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
    var returnUrl = httpContext.Request.Query["returnUrl"].ToString();
    if (string.IsNullOrWhiteSpace(returnUrl))
    {
        returnUrl = "/App";
    }

    var provider = httpContext.Request.Query["provider"].ToString();
    if (string.IsNullOrWhiteSpace(provider))
    {
        await WriteSignInChooserAsync(httpContext, returnUrl);
        return;
    }

    if (!provider.Equals("github", StringComparison.OrdinalIgnoreCase))
    {
        httpContext.Response.Redirect($"/auth/heimdall/{Uri.EscapeDataString(provider)}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return;
    }

    if (!gitHubOptions.Value.IsConfigured)
    {
        httpContext.Response.Redirect("/?auth=github-not-configured");
        return;
    }

    await httpContext.ChallengeAsync(
        GitHubAuthenticationDefaults.AuthenticationScheme,
        new() { RedirectUri = returnUrl });
}).AllowAnonymous();

app.MapPost("/auth/bifrost/register", async (
    NativeBifrostRegistrationRequest request,
    HttpContext httpContext,
    BifrostIdentityService bifrostIdentityService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var userAccount = await bifrostIdentityService.RegisterNativeAsync(
            request.Identity,
            request.DisplayName,
            cancellationToken);
        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            bifrostIdentityService.BuildPrincipal(userAccount));

        var returnUrl = string.IsNullOrWhiteSpace(request.ReturnUrl) ? "/App" : request.ReturnUrl;
        return JsonResult(
            new NativeBifrostRegistrationResponse(
                userAccount.Id,
                userAccount.BifrostIdentity,
                userAccount.DisplayName,
                userAccount.Membership?.Status.ToString() ?? MembershipStatus.Authenticated.ToString(),
                returnUrl),
            appJsonSerializerOptions);
    }
    catch (BifrostIdentityException error)
    {
        return Results.Text(error.Message, statusCode: StatusCodes.Status400BadRequest);
    }
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

        var requireMemberAccess = string.Equals(
            httpContext.Request.Query["access"].ToString(),
            "member",
            StringComparison.OrdinalIgnoreCase);

        var start = await heimdallAuthService.StartAsync(
            provider,
            hostOptions.Value.PublicBaseUrl,
            returnUrl,
            requireMemberAccess,
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
    string? ReturnUrl);

public sealed record NativeBifrostRegistrationResponse(
    Guid UserAccountId,
    string BifrostIdentity,
    string DisplayName,
    string MembershipStatus,
    string ReturnUrl);
