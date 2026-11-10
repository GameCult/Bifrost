using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Bifrost.Web.Features.Desk;

namespace Bifrost.Web.Tests.Support;

// A header-driven stand-in for the bearer validation port. X-Test-Desk-Token carries {sub, aud, scopes}; no header
// means no valid token. It validates nothing: the desk's own judgments (audience, scope, membership) are the subject.
public sealed class FakeDeskCredential : IDeskCredential
{
    public const string Header = "X-Test-Desk-Token";

    public static string Encode(string? subject, string? audience, IEnumerable<string> scopes) =>
        JsonSerializer.Serialize(new { sub = subject, aud = audience, scopes });

    public ValueTask<DeskToken?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.Headers.TryGetValue(Header, out var raw))
        {
            return ValueTask.FromResult<DeskToken?>(null);
        }

        using var document = JsonDocument.Parse(raw.ToString());
        var root = document.RootElement;
        return ValueTask.FromResult<DeskToken?>(new DeskToken(
            root.GetProperty("sub").GetString(),
            root.GetProperty("aud").GetString(),
            root.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!).ToArray()));
    }
}
