using System.Security.Claims;
using System.Text.Encodings.Web;
using Bifrost.Web.Features.Membership;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Bifrost.Web.Tests.Support;

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey("X-Test-Anonymous"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // A test may sign in as another account by naming its GitHub id and login.
        var gitHubId = Request.Headers["X-Test-GitHub-Id"].FirstOrDefault() ?? "12345";
        var login = Request.Headers["X-Test-GitHub-Login"].FirstOrDefault() ?? "test-admin";
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, gitHubId),
            new Claim(ClaimTypes.Name, login),
            new Claim(BifrostClaimTypes.GitHubLogin, login),
            new Claim(BifrostClaimTypes.DisplayName, "Test Admin")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
