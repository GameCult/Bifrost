using System.Net;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

// The site serves every script and stylesheet itself: a page that pulls code or styling from another origin hands
// that origin the visitor's session. Fails on any cross-origin <script src> / <link href> and on htmx remnants.
public sealed partial class NoThirdPartyAssetTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    // Known standing exception, not a ruling: the layout loads Google Fonts (fonts.googleapis.com, fonts.gstatic.com).
    // Remove the lookahead when the fonts are self-hosted so the guard becomes absolute.
    [GeneratedRegex(@"<(?:script|link)\b[^>]*\b(?:src|href)\s*=\s*[""']?\s*(?:https?:)?//(?!fonts\.(?:googleapis|gstatic)\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex CrossOriginAsset();

    [Fact]
    public async Task Real_pages_load_no_third_party_asset()
    {
        var collection = "c" + Guid.NewGuid().ToString("N");
        var identity = $"assets-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            db.ConceptSubjects.Add(new ConceptSubject { Collection = collection, Key = "alpha", Title = "alpha", RegisteredAtUtc = DateTimeOffset.UtcNow });
            db.UserAccounts.Add(new UserAccount { BifrostIdentity = identity, NormalizedBifrostIdentity = identity, DisplayName = identity });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var pages = new (string Path, string? Identity, bool Anonymous)[]
        {
            ("/", null, true),
            ($"/mill/{collection}/alpha", null, true),
            ($"/mill/{collection}/alpha", null, false),
            ("/App", null, false),
            ("/auth/sign-in", null, true),
            ("/auth/bifrost/register", null, true),
            ("/auth/bifrost/consent", identity, false),
        };

        foreach (var (path, who, anonymous) in pages)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (anonymous)
            {
                request.Headers.Add("X-Test-Anonymous", "true");
            }
            else if (who is not null)
            {
                request.Headers.Add("X-Test-Bifrost-Identity", who);
            }

            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(html), path);
            Assert.DoesNotContain("unpkg.com", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("htmx", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hx-", html, StringComparison.OrdinalIgnoreCase);
            Assert.False(CrossOriginAsset().IsMatch(html), $"{path} loads a cross-origin asset");
        }
    }
}
