using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Registration;

/// <summary>
/// The consent page and its two form posts, for any signed-in account. The receipt rows are the only state: a receipt is
/// live while <see cref="ConsentReceipt.WithdrawnAtUtc"/> is null, and an account's messages count only while a live
/// receipt for the version in force exists. Both posts are refused by <see cref="RequestOriginGuard.Allows"/> before
/// anything is read or written, since a signed-in session is a cookie and these carry no antiforgery token.
/// </summary>
public static class ConsentEndpoints
{
    public const string Path = "/auth/bifrost/consent";

    public static void MapConsent(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, async (ICurrentBifrostActorAccessor actors, BifrostDbContext db, CancellationToken ct) =>
        {
            var actor = await actors.GetAsync(ct);
            if (!actor.IsAuthenticated || actor.UserAccount is null)
            {
                return Results.Redirect($"/auth/sign-in?returnUrl={Uri.EscapeDataString(LocalReturnPath.Parse(Path).Value)}");
            }

            return await PageAsync(db, actor.UserAccount.Id, null, StatusCodes.Status200OK, ct);
        }).AllowAnonymous();

        app.MapPost(Path + "/accept", async (
            HttpContext http,
            ICurrentBifrostActorAccessor actors,
            BifrostDbContext db,
            TimeProvider clock,
            IOptions<BifrostHostOptions> host,
            CancellationToken ct) =>
        {
            if (!RequestOriginGuard.Allows(http.Request, host.Value))
            {
                return Refused();
            }

            var actor = await actors.GetAsync(ct);
            if (!actor.IsAuthenticated || actor.UserAccount is null)
            {
                return Results.Unauthorized();
            }

            var form = await ReadFormAsync(http.Request, ct);
            if (form is null || form["version"].ToString() != TrainingNotice.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return await PageAsync(db, actor.UserAccount.Id, $"Send the version in force, {TrainingNotice.Version}, to agree.", StatusCodes.Status400BadRequest, ct);
            }

            var accountId = actor.UserAccount.Id;
            if (!await HasLiveReceiptAsync(db, accountId, ct))
            {
                db.ConsentReceipts.Add(new ConsentReceipt
                {
                    UserAccountId = accountId,
                    NoticeVersion = TrainingNotice.Version,
                    AcceptedAtUtc = clock.GetUtcNow()
                });
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException error) when (IsUniqueViolation(error))
                {
                    // The filtered unique index refused a second live receipt: another request holds it, so this accept has nothing to add.
                    // Only that violation is swallowed; any other store fault propagates. Not re-checked: a withdraw may already have stamped the winner, and the outcome for this request is the same.
                    db.ChangeTracker.Clear();
                }
            }

            return Back();
        }).AllowAnonymous();

        app.MapPost(Path + "/withdraw", async (
            HttpContext http,
            ICurrentBifrostActorAccessor actors,
            BifrostDbContext db,
            TimeProvider clock,
            IOptions<BifrostHostOptions> host,
            CancellationToken ct) =>
        {
            if (!RequestOriginGuard.Allows(http.Request, host.Value))
            {
                return Refused();
            }

            var actor = await actors.GetAsync(ct);
            if (!actor.IsAuthenticated || actor.UserAccount is null)
            {
                return Results.Unauthorized();
            }

            var live = await db.ConsentReceipts
                .Where(x => x.UserAccountId == actor.UserAccount.Id && x.WithdrawnAtUtc == null)
                .ToListAsync(ct);
            if (live.Count > 0)
            {
                var now = clock.GetUtcNow();
                foreach (var receipt in live)
                {
                    receipt.WithdrawnAtUtc = now;
                }

                await db.SaveChangesAsync(ct);
            }

            return Back();
        }).AllowAnonymous();
    }

    // Postgres SqlState 23505, on the exception or anywhere down its inner chain.
    private static bool IsUniqueViolation(Exception error)
    {
        for (var e = (Exception?)error; e is not null; e = e.InnerException)
        {
            if (e is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    private static IResult Refused() => Results.Text("Cross-site consent changes are refused.", statusCode: StatusCodes.Status403Forbidden);

    private static IResult Back() => new SeeOther(Path);

    private static Task<bool> HasLiveReceiptAsync(BifrostDbContext db, Guid accountId, CancellationToken ct) =>
        db.ConsentReceipts.AnyAsync(x => x.UserAccountId == accountId && x.NoticeVersion == TrainingNotice.Version && x.WithdrawnAtUtc == null, ct);

    private static async Task<IFormCollection?> ReadFormAsync(HttpRequest request, CancellationToken ct)
    {
        const int MaxFormBytes = 1024;
        if (!request.HasFormContentType || request.ContentLength > MaxFormBytes)
        {
            return null;
        }

        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxFormBytes;
        }

        try
        {
            return await request.ReadFormAsync(ct);
        }
        catch (Exception error) when (error is InvalidDataException or BadHttpRequestException)
        {
            return null;
        }
    }

    private sealed class SeeOther(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = location;
            httpContext.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }
    }

    private static async Task<IResult> PageAsync(BifrostDbContext db, Guid accountId, string? error, int status, CancellationToken ct)
    {
        var receipts = await db.ConsentReceipts.AsNoTracking().Where(x => x.UserAccountId == accountId).ToListAsync(ct);
        return new HtmlPage(ConsentPage.Render(ConsentPage.StateOf(receipts), error), status);
    }

    private sealed class HtmlPage(string html, int status) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = "text/html; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-store";
            await httpContext.Response.WriteAsync(html);
        }
    }
}
