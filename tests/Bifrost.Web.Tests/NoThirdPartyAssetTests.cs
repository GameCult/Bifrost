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

    // An htmx remnant is an attribute (` hx-get="..."`); a bare "hx-" can occur inside a random token.
    [GeneratedRegex(@"\shx-[a-z-]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex HtmxAttribute();

    // Source-level: any element that fetches a resource (script/link/img/iframe/...) from an absolute or protocol-relative
    // URL outside the Google Fonts allowance, in markup that may sit in C# strings (escaped or doubled quotes), plus CSS @import / url().
    [GeneratedRegex(@"<(?:script|link|img|iframe|frame|source|embed|object|video|audio|input)\b[^>]*?\b(?:src|href|data|srcset)\s*=\s*(?:\\|"")?[""']?\s*(?:https?:)?//(?!fonts\.(?:googleapis|gstatic)\.com)|@import\s+(?:url\()?\s*(?:\\|"")?[""']?\s*(?:https?:)?//(?!fonts\.(?:googleapis|gstatic)\.com)|\burl\(\s*[""']?\s*(?:https?:)?//(?!fonts\.(?:googleapis|gstatic)\.com)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CrossOriginSource();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bifrost.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Bifrost.slnx not found above the test binary.");
    }

    // Every file that can emit HTML or CSS: all .cshtml, all .cs under src (the C# page builders), and the site's own css/js.
    private static IEnumerable<string> MarkupSources(string root)
    {
        var src = Path.Combine(root, "src", "Bifrost.Web");
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}wwwroot{sep}lib{sep}"))
            .Where(f => f.EndsWith(".cshtml") || f.EndsWith(".cs") || f.EndsWith(".css") || f.EndsWith(".js") || f.EndsWith(".html"));
    }

    [Fact]
    public void No_source_that_emits_markup_loads_a_cross_origin_asset()
    {
        var files = MarkupSources(RepoRoot()).ToList();
        Assert.True(files.Count(f => f.EndsWith(".cshtml")) >= 10, "the scan found too few views; the root is wrong");
        Assert.Contains(files, f => f.EndsWith("HeimdallCompletePage.cs"));
        Assert.Contains(files, f => f.EndsWith(Path.Combine("Membership", "Status.cshtml")));

        var offenders = files.Where(f => CrossOriginSource().IsMatch(File.ReadAllText(f))).ToList();
        Assert.True(offenders.Count == 0, "cross-origin asset in: " + string.Join(", ", offenders));
        Assert.DoesNotContain(files, f => HtmxAttribute().IsMatch(File.ReadAllText(f)));
    }

    [Theory]
    [InlineData("<script src=\"https://evil.example/x.js\"></script>", true)]
    [InlineData("<script src='//evil.example/x.js'></script>", true)]
    [InlineData("html.Append(\"<script src=\\\"https://evil.example/x.js\\\">\")", true)]
    [InlineData("<script src=\"\"https://evil.example/x.js\"\">", true)]
    [InlineData("<img src=\"http://evil.example/p.png\">", true)]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", true)]
    [InlineData("@import url('https://evil.example/a.css');", true)]
    [InlineData("background: url(https://evil.example/a.png)", true)]
    [InlineData("<link rel=\"stylesheet\" href=\"https://fonts.googleapis.com/css2?family=X\">", false)]
    [InlineData("<script src=\"/js/site.js\"></script>", false)]
    [InlineData("<a href=\"https://github.com/GameCult\">repo</a>", false)]
    public void The_source_pattern_catches_cross_origin_assets_and_allows_same_origin_and_fonts(string markup, bool flagged) =>
        Assert.Equal(flagged, CrossOriginSource().IsMatch(markup));

    [Theory]
    [InlineData("<div hx-get=\"/x\">", true)]
    [InlineData("<div class=a hx-post = \"/x\">", true)]
    [InlineData("token=ab-hx-9f3", false)]
    public void Htmx_is_matched_only_as_an_attribute(string markup, bool flagged) =>
        Assert.Equal(flagged, HtmxAttribute().IsMatch(markup));

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
            Assert.False(HtmxAttribute().IsMatch(html), $"{path} carries an hx- attribute");
            Assert.False(CrossOriginAsset().IsMatch(html), $"{path} loads a cross-origin asset");
        }
    }
}
