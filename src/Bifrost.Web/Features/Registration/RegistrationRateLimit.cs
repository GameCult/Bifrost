using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Shared;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Registration;

/// <summary>
/// The per-address permits on the registration endpoints (challenge and register share one budget), on the shared
/// fixed-window budget. It exists so one address cannot fill the consumed-challenge set alone. It fails closed:
/// anything it cannot decide is a 503, never a pass.
/// </summary>
public sealed class RegistrationRateLimit(IOptions<RegistrationOptions> options, TimeProvider clock, ILogger<RegistrationRateLimit> logger)
{
    private readonly FixedWindowBudget addresses = new(
        options.Value.Limits.PerAddressPermits,
        options.Value.Limits.Window,
        options.Value.Limits.MaxTrackedKeys,
        clock);

    /// <summary>Null when the request may proceed; otherwise the refusal to return.</summary>
    public IResult? Refuse(HttpContext http)
    {
        try
        {
            if (addresses.Spend(ClientAddress.BudgetKey(http.Connection.RemoteIpAddress)) is not { } denial)
            {
                return null;
            }

            http.Response.Headers.RetryAfter = RetryAfter(denial.RetryAfter);
            return Results.Text("Too many registration requests from this address.", statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Registration rate limit could not decide ({ExceptionType}); the request is refused.", exception.GetType().Name);
            http.Response.Headers.RetryAfter = "60";
            return Results.Text("Registration is unavailable; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string RetryAfter(TimeSpan wait) =>
        Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
