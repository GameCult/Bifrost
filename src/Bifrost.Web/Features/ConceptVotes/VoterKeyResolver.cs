using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Bifrost.Web.Features.ConceptVotes;

// The only reader and writer of the bifrost_voter cookie. It supplies a device id and nothing else: VoterIdentity.For
// decides the voter key and class. The cookie holds a protected random id, never a count, class or account.
public sealed class VoterKeyResolver(IDataProtectionProvider dataProtection)
{
    public const string CookieName = "bifrost_voter";
    public const string CookiePath = "/mill";
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);

    private const string Purpose = "Bifrost.ConceptVotes.DeviceToken.v1";

    private readonly IDataProtector protector = dataProtection.CreateProtector(Purpose);

    // The device id, or null when there is no cookie or it does not unprotect under this key ring and purpose
    // (tampered, truncated, a bare id, or minted elsewhere). Nothing about a refused value is kept or reported.
    public string? TryRead(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // Sets a fresh device cookie on this response and returns its device id. Called only from a cast.
    public string Mint(HttpContext context)
    {
        var deviceId = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append(
            CookieName,
            protector.Protect(deviceId),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = CookiePath,
                MaxAge = CookieLifetime,
                IsEssential = true
            });
        return deviceId;
    }
}
