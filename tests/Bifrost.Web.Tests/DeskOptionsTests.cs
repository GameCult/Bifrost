using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Features.Shared;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

public sealed class DeskOptionsTests
{
    private const string Alice = "e0d1c2b3-a495-4687-8879-6a5b4c3d2e1f";
    private const string Bob = "00000000-0000-4000-8000-000000000002";

    private static Dictionary<string, string?> Good() => new()
    {
        ["Desk:AllowedOrigin"] = "https://chat.gamecult.org",
        ["Desk:Minds:0:Id"] = "gamecult-chat",
        ["Desk:Minds:0:Socket"] = "/run/huginn/huginn-gamecult-chat.sock",
        ["Desk:Minds:0:Deciders:0"] = Alice,
        ["Desk:Minds:1:Id"] = "nibu",
        ["Desk:Minds:1:Socket"] = "/run/huginn/huginn-nibu.sock",
        ["Desk:Minds:1:Deciders:0"] = Bob,
        ["Desk:Attribution:Operator"] = Alice,
        ["Desk:Attribution:Stewards:0"] = Bob,
    };

    private static DeskOptions Bind(Dictionary<string, string?> values)
    {
        var options = new DeskOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(DeskOptions.SectionName).Bind(options);
        return options;
    }

    private static void Validate(Dictionary<string, string?> values, string environment = "Testing")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        new StartupConfigurationValidator(
            configuration,
            new StubEnvironment(environment),
            Options.Create(new GitHubOAuthOptions()),
            Options.Create(new GitHubAppOptions()),
            Options.Create(new HeimdallOptions()),
            Options.Create(new StripeOptions()),
            Options.Create(new BifrostHostOptions()),
            Options.Create(new ConceptVoteOptions()),
            Options.Create(Bind(values))).Validate();
    }

    private static void Refused(Dictionary<string, string?> values, string expectedFragment)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Validate(values));
        Assert.Contains(expectedFragment, failure.Message);
    }

    [Fact]
    public void A_good_desk_configuration_binds_by_position_and_passes()
    {
        var options = Bind(Good());

        Assert.Equal(["gamecult-chat", "nibu"], options.Minds.Select(m => m.Id));
        Assert.Equal([Alice], options.Minds[0].Deciders);
        Assert.Equal([Bob], options.Minds[1].Deciders);
        Validate(Good());
    }

    [Fact]
    public void No_desk_section_is_not_configured_and_passes()
    {
        Assert.False(Bind([]).IsConfigured);
        Validate([]);
    }

    [Fact]
    public void Mind_ids_keep_the_case_the_configuration_gave_them()
    {
        var values = Good();
        values["Desk:Minds:0:Id"] = "GameCult-Chat";

        Assert.Equal("GameCult-Chat", Bind(values).Minds[0].Id);
    }

    [Fact]
    public void Startup_refuses_duplicate_ids() => Refused(Dup(), "Desk:Minds:1:Id repeats");

    private static Dictionary<string, string?> Dup()
    {
        var values = Good();
        values["Desk:Minds:1:Id"] = "gamecult-chat";
        return values;
    }

    [Fact]
    public void Startup_refuses_a_blank_id()
    {
        var values = Good();
        values["Desk:Minds:1:Id"] = "  ";

        Refused(values, "Desk:Minds:1:Id must not be blank");
    }

    [Theory]
    [InlineData("huginn.sock")]
    [InlineData("run/huginn.sock")]
    [InlineData("")]
    public void Startup_refuses_relative_socket(string socket)
    {
        var values = Good();
        values["Desk:Minds:1:Socket"] = socket;

        Refused(values, "Desk:Minds:1:Socket must be an absolute path");
    }

    [Theory]
    [InlineData("E0D1C2B3-A495-4687-8879-6A5B4C3D2E1F")]
    [InlineData("e0d1c2b3a495468788796a5b4c3d2e1f")]
    [InlineData("{e0d1c2b3-a495-4687-8879-6a5b4c3d2e1f}")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void Startup_refuses_bad_decider_id(string decider)
    {
        var values = Good();
        values["Desk:Minds:0:Deciders:1"] = decider;

        Refused(values, "Desk:Minds:0:Deciders entries must be lowercase D-format");
    }

    [Fact]
    public void Startup_refuses_a_repeated_decider()
    {
        var values = Good();
        values["Desk:Minds:0:Deciders:1"] = Alice;

        Refused(values, "Desk:Minds:0:Deciders repeats");
    }

    [Fact]
    public void Startup_refuses_operator_in_stewards()
    {
        var values = Good();
        values["Desk:Attribution:Stewards:1"] = Alice;

        Refused(values, "must not also be a steward");
    }

    [Fact]
    public void Startup_refuses_bad_or_repeated_attribution_ids()
    {
        var bad = Good();
        bad["Desk:Attribution:Operator"] = "ALICE";
        Refused(bad, "Desk:Attribution:Operator must be a lowercase");

        var badSteward = Good();
        badSteward["Desk:Attribution:Stewards:1"] = "E0D1C2B3-A495-4687-8879-6A5B4C3D2E1F";
        Refused(badSteward, "Desk:Attribution:Stewards entries must be lowercase");

        var repeated = Good();
        repeated["Desk:Attribution:Stewards:1"] = Bob;
        Refused(repeated, "Desk:Attribution:Stewards repeats");
    }

    private static string Id(int n) => new Guid(n, 0, 0, new byte[8]).ToString("D");

    private static Dictionary<string, string?> WithDeciders(int count)
    {
        var values = Good();
        for (var i = 0; i < count; i++)
        {
            values[$"Desk:Minds:0:Deciders:{i}"] = Id(i + 1);
        }

        if (count == 0)
        {
            values.Remove("Desk:Minds:0:Deciders:0");
        }

        return values;
    }

    [Fact]
    public void Startup_takes_one_to_64_deciders_per_mind()
    {
        Validate(WithDeciders(1));
        Validate(WithDeciders(64));
        Refused(WithDeciders(65), "Desk:Minds:0:Deciders must hold 1 to 64");
        Refused(WithDeciders(0), "Desk:Minds:0:Deciders must hold 1 to 64");

        // The bound is per Mind: the second Mind is judged on its own list.
        var second = Good();
        second.Remove("Desk:Minds:1:Deciders:0");
        Refused(second, "Desk:Minds:1:Deciders must hold 1 to 64");
    }

    [Fact]
    public void Startup_refuses_two_minds_sharing_one_socket()
    {
        var values = Good();
        values["Desk:Minds:1:Socket"] = values["Desk:Minds:0:Socket"];

        Refused(values, "Desk:Minds:1:Socket repeats an earlier Mind's socket");
    }

    [Fact]
    public void Desk_failures_are_reported_in_production_too_and_alongside_each_other()
    {
        var values = Good();
        values["Desk:Minds:1:Socket"] = "relative";
        values["Desk:Attribution:Stewards:1"] = Alice;

        var failure = Assert.Throws<InvalidOperationException>(() => Validate(values, "Production"));
        Assert.Contains("Desk:Minds:1:Socket", failure.Message);
        Assert.Contains("must not also be a steward", failure.Message);
    }

    [Fact]
    public void Attribution_is_operator_steward_member()
    {
        var attribution = Bind(Good()).Attribution;

        Assert.Equal("Operator", attribution.AuthorityOf(Alice));
        Assert.Equal("Steward", attribution.AuthorityOf(Bob));
        Assert.Equal("Member", attribution.AuthorityOf("00000000-0000-4000-8000-000000000003"));
        Assert.Equal("Member", attribution.AuthorityOf(Alice.ToUpperInvariant()));
        Assert.Equal("Member", new DeskAttributionOptions().AuthorityOf(string.Empty));
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Bifrost.Web";
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
