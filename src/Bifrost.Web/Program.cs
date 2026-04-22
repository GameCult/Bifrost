using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Features.GitHub;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
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

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizePage("/Membership/Status");
    options.Conventions.AuthorizeFolder("/App", ActiveMemberRequirement.PolicyName);
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
builder.Services.AddScoped<MembershipSynchronizationService>();
builder.Services.AddScoped<GitHubWebhookService>();
builder.Services.AddScoped<ReadinessService>();
builder.Services.AddScoped<ApplicationBootstrapper>();
builder.Services.AddSingleton<StartupConfigurationValidator>();
builder.Services.AddScoped<IAuthorizationHandler, ActiveMemberAuthorizationHandler>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/auth/sign-in";
        options.AccessDeniedPath = "/Membership/Status";
        options.SlidingExpiration = true;
    })
    .AddOAuth(GitHubAuthenticationDefaults.AuthenticationScheme, options =>
    {
        var gitHubOptions = builder.Configuration
            .GetSection(GitHubOAuthOptions.SectionName)
            .Get<GitHubOAuthOptions>() ?? new GitHubOAuthOptions();

        options.ClientId = gitHubOptions.ClientId;
        options.ClientSecret = gitHubOptions.ClientSecret;
        options.CallbackPath = gitHubOptions.CallbackPath;
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

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ActiveMemberRequirement.PolicyName, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new ActiveMemberRequirement());
    });
});

var app = builder.Build();

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

app.MapPost("/auth/sign-out", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    httpContext.Response.Redirect("/");
}).AllowAnonymous();

app.MapRazorPages();

app.Run();

public partial class Program;
