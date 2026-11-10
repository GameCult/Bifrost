using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

public sealed class DeskMindRoutesTests
{
    private static DeskMindRoutes Routes()
    {
        var minds = DeskVectors.Root["transport"]!["mindRoutes"]!["minds"]!.AsObject();
        return new DeskMindRoutes(minds.Select(m => new DeskMindOptions { Id = m.Key, Socket = "/run/huginn/" + (string)m.Value! }));
    }

    [Fact]
    public void Every_vector_segment_resolves_or_misses()
    {
        var routes = Routes();
        var cases = DeskVectors.Rows("transport", "mindRoutes", "cases").ToList();
        Assert.Equal(57, cases.Count);

        foreach (var row in cases)
        {
            var mind = (string)row["mind"]!;
            var opened = (string?)row["opened"];
            var resolved = routes.Resolve(mind);

            // A route that opens a socket resolves to the Mind whose socket it is; every other route is a miss.
            // Auth-refused cases never reach the lookup, so only the cases that reach it are judged here.
            var expect = (string)row["expect"]!;
            if (expect is not ("ok" or "fork_unknown" or "unavailable"))
            {
                continue;
            }

            if (opened is null)
            {
                Assert.True(resolved is null, $"{row["name"]}: '{Short(mind)}' resolved to '{resolved?.Id}'");
            }
            else
            {
                Assert.True(resolved is not null, $"{row["name"]}: '{Short(mind)}' missed");
                Assert.EndsWith(opened, resolved!.Socket, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Segments_that_do_not_name_a_configured_mind_miss()
    {
        var routes = Routes();
        foreach (var mind in new[]
                 {
                     "ghost", ".", "..", "../x", "a/b", "a%2fb", "%2e%2e%2fx", "eureka%00", "nibu%20", "nibu ", " nibu",
                     "%ff", "%c3%28", "%", "%6", "%zz", "nibu%", "%256eibu", "", new string('a', 4096), "nïbu",
                 })
        {
            Assert.Null(routes.Resolve(mind));
        }
    }

    [Fact]
    public void Percent_encoded_configured_ids_are_decoded_once()
    {
        var routes = Routes();

        Assert.Equal("nibu", routes.Resolve("%6eibu")!.Id);
        Assert.Equal("nibu", routes.Resolve("%6Eibu")!.Id);
        Assert.Equal("gamecult-chat", routes.Resolve("gamecult%2Dchat")!.Id);
    }

    [Fact]
    public void Other_case_misses_despite_config_case_insensitivity()
    {
        var routes = Routes();

        Assert.Null(routes.Resolve("GAMECULT-CHAT"));
        Assert.Null(routes.Resolve("Nibu"));
        Assert.Null(routes.Resolve("gamecult-chat%20"));
        Assert.NotNull(routes.Resolve("gamecult-chat"));
    }

    [Fact]
    public void Double_encoded_misses()
    {
        var routes = Routes();

        Assert.Null(routes.Resolve("%256eibu"));
        Assert.Null(routes.Resolve("gamecult%252Dchat"));
    }

    [Fact]
    public void Configured_ids_that_differ_only_by_case_stay_distinct()
    {
        var routes = new DeskMindRoutes([
            new DeskMindOptions { Id = "a", Socket = "/s/a" },
            new DeskMindOptions { Id = "A", Socket = "/s/A" },
        ]);

        Assert.Equal("/s/a", routes.Resolve("a")!.Socket);
        Assert.Equal("/s/A", routes.Resolve("A")!.Socket);
    }

    [Fact]
    public void A_decoding_failure_is_a_miss_even_when_the_failure_spells_a_configured_id()
    {
        var replacement = char.ConvertFromUtf32(0xFFFD);
        var e = char.ConvertFromUtf32(0xE9);
        var routes = new DeskMindRoutes([
            new DeskMindOptions { Id = replacement, Socket = "/s/replacement" },
            new DeskMindOptions { Id = e, Socket = "/s/e-acute" },
            new DeskMindOptions { Id = "a%zz", Socket = "/s/bad-escape" },
            new DeskMindOptions { Id = "100%", Socket = "/s/trailing" },
        ]);

        Assert.Null(routes.Resolve("%ff"));                                          // not UTF-8, not U+FFFD
        Assert.Null(routes.Resolve(char.ConvertFromUtf32(0xC3) + char.ConvertFromUtf32(0xA9))); // UTF-8 bytes as raw chars
        Assert.Null(routes.Resolve("a%zz"));
        Assert.Null(routes.Resolve("100%"));
        Assert.Equal("/s/replacement", routes.Resolve("%ef%bf%bd")!.Socket);          // the real encoding still resolves
        Assert.Equal("/s/e-acute", routes.Resolve("%c3%a9")!.Socket);
    }

    private static string Short(string value) => value.Length > 40 ? value[..40] + "..." : value;
}
