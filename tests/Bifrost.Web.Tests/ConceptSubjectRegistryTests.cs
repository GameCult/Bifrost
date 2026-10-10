using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

public sealed class ConceptSubjectRegistryTests
{
    private const string Collection = "aetheria-concept-mill";
    private static readonly string[] None = [];

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "mill-three-ships.json");

    private static IReadOnlyList<ManifestShip> Parse(string json) =>
        ConceptSubjectRegistry.ParseManifest(Encoding.UTF8.GetBytes(json));

    private static IReadOnlyList<ManifestShip> Fixture() =>
        ConceptSubjectRegistry.ParseManifest(File.ReadAllBytes(FixturePath));

    [Fact]
    public async Task Rerun_of_the_same_manifest_is_a_no_op()
    {
        using var world = new World();

        var first = await world.Apply(Fixture());
        Assert.All(first.Entries, e => Assert.Equal(SubjectChange.Add, e.Change));
        Assert.Equal(3, await world.Fresh(db => db.AuditEvents.CountAsync(x => x.EntityType == nameof(ConceptSubject))));

        var before = await world.Fresh(db => db.ConceptSubjects.AsNoTracking().OrderBy(x => x.Key).ToListAsync());
        world.Clock.Advance(TimeSpan.FromHours(1));
        var second = await world.Apply(Fixture());

        Assert.All(second.Entries, e => Assert.Equal(SubjectChange.Unchanged, e.Change));
        Assert.Equal(3, await world.Fresh(db => db.AuditEvents.CountAsync()));
        var after = await world.Fresh(db => db.ConceptSubjects.AsNoTracking().OrderBy(x => x.Key).ToListAsync());
        Assert.Equal(before.Select(x => (x.Id, x.Title, x.RegisteredAtUtc, x.RetiredAtUtc)),
            after.Select(x => (x.Id, x.Title, x.RegisteredAtUtc, x.RetiredAtUtc)));
    }

    [Fact]
    public async Task Apply_writes_one_host_command_audit_event_per_change_and_none_for_a_plan()
    {
        using var world = new World();

        var plan = await world.Plan(Fixture());
        Assert.All(plan.Entries, e => Assert.Equal(SubjectChange.Add, e.Change));
        Assert.Empty(await world.Fresh(db => db.ConceptSubjects.ToListAsync()));
        Assert.Empty(await world.Fresh(db => db.AuditEvents.ToListAsync()));

        await world.Apply(Fixture());

        var events = await world.Fresh(db => db.AuditEvents.ToListAsync());
        var subjectIds = (await world.Fresh(db => db.ConceptSubjects.ToListAsync())).Select(x => x.Id.ToString()).ToHashSet();
        Assert.Equal(3, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(nameof(ConceptSubject), e.EntityType);
            Assert.Equal("Add", e.Action);
            Assert.Null(e.ActorUserAccountId);
            Assert.Contains("actor=host-command", e.Detail);
            Assert.Contains(e.EntityId, subjectIds);
        });
    }

    [Fact]
    public async Task Rename_changes_title_and_keeps_votes()
    {
        using var world = new World();
        await world.Apply(Fixture());
        await world.Cast("bison", ConceptStance.Up);
        await world.Cast("kestrel", ConceptStance.Down);

        var renamed = Fixture().Select(s => s.Key == "bison" ? s with { Title = "Bison Prime" } : s).ToList();
        var plan = await world.Apply(renamed);

        Assert.Equal(SubjectChange.Rename, plan.Entries.Single(e => e.Key == "bison").Change);
        Assert.Equal(2, plan.Entries.Count(e => e.Change == SubjectChange.Unchanged));
        var subject = await world.Fresh(db => db.ConceptSubjects.AsNoTracking().SingleAsync(x => x.Key == "bison"));
        Assert.Equal("Bison Prime", subject.Title);
        Assert.Null(subject.RetiredAtUtc);
        Assert.Equal(2, await world.Fresh(db => db.ConceptAppraisals.CountAsync()));
        var view = await world.Fresh(db => world.Appraisals(db).GetSubjectAsync(Collection, "bison", null, default));
        Assert.Equal("Bison Prime", view!.Title);
        Assert.Equal(new ConceptTally(1, 0, new StanceCounts(0, 0)), view.Tally);
        Assert.Equal(1, await world.Fresh(db => db.AuditEvents.CountAsync(x => x.Action == "Rename")));
    }

    [Fact]
    public async Task Absent_key_is_retired_only_when_named()
    {
        using var world = new World();
        await world.Apply(Fixture());
        await world.Cast("kestrel", ConceptStance.Up);
        var smaller = Fixture().Where(s => s.Key != "kestrel").ToList();

        var unnamed = await world.Apply(smaller);
        Assert.Equal(SubjectChange.Absent, unnamed.Entries.Single(e => e.Key == "kestrel").Change);
        Assert.Null((await world.Fresh(db => db.ConceptSubjects.AsNoTracking().SingleAsync(x => x.Key == "kestrel"))).RetiredAtUtc);
        Assert.Contains("kestrel", (await world.Fresh(db => world.Appraisals(db).GetTalliesAsync(Collection, default))).Keys);

        var named = await world.Apply(smaller, ["kestrel"]);
        Assert.Equal(SubjectChange.Retire, named.Entries.Single(e => e.Key == "kestrel").Change);
        var retired = await world.Fresh(db => db.ConceptSubjects.AsNoTracking().SingleAsync(x => x.Key == "kestrel"));
        Assert.NotNull(retired.RetiredAtUtc);
        Assert.Equal(1, await world.Fresh(db => db.ConceptAppraisals.CountAsync()));
        Assert.DoesNotContain("kestrel", (await world.Fresh(db => world.Appraisals(db).GetTalliesAsync(Collection, default))).Keys);
        Assert.Equal(CastResult.RetiredSubject, await world.Cast("kestrel", ConceptStance.Down));
        Assert.Equal(1, await world.Fresh(db => db.AuditEvents.CountAsync(x => x.Action == "Retire")));
    }

    [Theory]
    [InlineData("bison")]
    [InlineData("never-registered")]
    public async Task Retire_naming_a_key_that_is_not_registered_live_and_absent_is_refused_whole(string named)
    {
        using var world = new World();
        await world.Apply(Fixture());
        var renamed = Fixture().Select(s => s.Key == "kestrel" ? s with { Title = "Kestrel II" } : s).ToList();

        var refusal = await Assert.ThrowsAsync<ManifestRefusedException>(() => world.Apply(renamed, [named]));

        Assert.Equal(("retire", "NotAbsent"), (refusal.Field, refusal.Code));
        Assert.DoesNotContain(named, refusal.Message);
        Assert.Equal("Kestrel", (await world.Fresh(db => db.ConceptSubjects.AsNoTracking().SingleAsync(x => x.Key == "kestrel"))).Title);
        Assert.Equal(3, await world.Fresh(db => db.AuditEvents.CountAsync()));
    }

    [Fact]
    public async Task Rerunning_the_same_retire_list_converges_and_writes_nothing()
    {
        using var world = new World();
        await world.Apply(Fixture());
        var smaller = Fixture().Where(s => s.Key == "bison").ToList();

        var first = await world.Apply(smaller, ["kestrel", "ibis-mk2"]);
        Assert.Equal([SubjectChange.Unchanged, SubjectChange.Retire, SubjectChange.Retire], first.Entries.Select(e => e.Change));
        var before = await world.Fresh(db => db.ConceptSubjects.AsNoTracking().OrderBy(x => x.Key).ToListAsync());
        var audits = await world.Fresh(db => db.AuditEvents.CountAsync());
        world.Clock.Advance(TimeSpan.FromHours(1));

        var second = await world.Apply(smaller, ["kestrel", "ibis-mk2"]);

        Assert.Equal(
            [("bison", SubjectChange.Unchanged), ("ibis-mk2", SubjectChange.Retired), ("kestrel", SubjectChange.Retired)],
            second.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Change)));
        Assert.Equal(audits, await world.Fresh(db => db.AuditEvents.CountAsync()));
        var after = await world.Fresh(db => db.ConceptSubjects.AsNoTracking().OrderBy(x => x.Key).ToListAsync());
        Assert.Equal(before.Select(x => (x.Id, x.Title, x.RetiredAtUtc)), after.Select(x => (x.Id, x.Title, x.RetiredAtUtc)));
    }

    [Fact]
    public async Task Rerunning_a_retire_list_after_a_partial_apply_retires_the_rest()
    {
        using var world = new World();
        await world.Apply(Fixture());
        var smaller = Fixture().Where(s => s.Key == "bison").ToList();
        await world.Apply(smaller, ["kestrel"]);

        var plan = await world.Apply(smaller, ["kestrel", "ibis-mk2"]);

        Assert.Equal(SubjectChange.Retired, plan.Entries.Single(e => e.Key == "kestrel").Change);
        Assert.Equal(SubjectChange.Retire, plan.Entries.Single(e => e.Key == "ibis-mk2").Change);
        var rows = await world.Fresh(db => db.ConceptSubjects.AsNoTracking().ToListAsync());
        Assert.Equal(["ibis-mk2", "kestrel"], rows.Where(r => r.RetiredAtUtc is not null).Select(r => r.Key).Order());
        Assert.Equal(2, await world.Fresh(db => db.AuditEvents.CountAsync(x => x.Action == "Retire")));
    }

    [Fact]
    public async Task Naming_a_retired_key_that_the_manifest_lists_again_is_still_refused()
    {
        using var world = new World();
        await world.Apply(Fixture());
        await world.Apply(Fixture().Where(s => s.Key != "kestrel").ToList(), ["kestrel"]);

        var refusal = await Assert.ThrowsAsync<ManifestRefusedException>(() => world.Apply(Fixture(), ["kestrel"]));

        Assert.Equal(("retire", "NotAbsent"), (refusal.Field, refusal.Code));
        Assert.NotNull((await world.Fresh(db => db.ConceptSubjects.AsNoTracking().SingleAsync(x => x.Key == "kestrel"))).RetiredAtUtc);
    }

    [Fact]
    public async Task Re_registering_a_retired_key_revives_it()
    {
        using var world = new World();
        await world.Apply(Fixture());
        await world.Cast("kestrel", ConceptStance.Up);
        await world.Apply(Fixture().Where(s => s.Key != "kestrel").ToList(), ["kestrel"]);

        var plan = await world.Apply(Fixture());

        Assert.Equal(SubjectChange.Revive, plan.Entries.Single(e => e.Key == "kestrel").Change);
        var subject = await world.Fresh(db => db.ConceptSubjects.AsNoTracking().SingleAsync(x => x.Key == "kestrel"));
        Assert.Null(subject.RetiredAtUtc);
        var tallies = await world.Fresh(db => world.Appraisals(db).GetTalliesAsync(Collection, default));
        Assert.Equal(new ConceptTally(1, 0, new StanceCounts(0, 0)), tallies["kestrel"]);
        Assert.Equal(1, await world.Fresh(db => db.AuditEvents.CountAsync(x => x.Action == "Revive")));
    }

    [Theory]
    [InlineData("""{"ships":[{"id":"a","name":"A"},{"id":"a","name":"B"}]}""", "ships[1].id", "Duplicate")]
    [InlineData("""{"ships":[{"id":"Upper","name":"A"}]}""", "ships[0].id", "InvalidKey")]
    [InlineData("""{"ships":[{"id":"a/b","name":"A"}]}""", "ships[0].id", "InvalidKey")]
    [InlineData("""{"ships":[{"id":"ship-a\n","name":"A"}]}""", "ships[0].id", "InvalidKey")]
    [InlineData("""{"ships":[{"id":"","name":"A"}]}""", "ships[0].id", "InvalidKey")]
    [InlineData("""{"ships":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"A"}]}""", "ships[0].id", "InvalidKey")]
    [InlineData("""{"ships":[{"id":"a"}]}""", "ships[0].name", "Missing")]
    [InlineData("""{"ships":[{"id":"a","name":7}]}""", "ships[0].name", "NotString")]
    [InlineData("""{"ships":[{"id":"a","name":"  "}]}""", "ships[0].name", "InvalidTitle")]
    [InlineData("""{"ships":[{"id":"a","name":"bad\ntitle"}]}""", "ships[0].name", "InvalidTitle")]
    [InlineData("""{"ships":["a"]}""", "ships[0].id", "Missing")]
    [InlineData("""{"fleet":[]}""", "ships", "Missing")]
    [InlineData("[]", "ships", "Missing")]
    [InlineData("""{"ships":5}""", "ships", "Missing")]
    [InlineData("not json", "manifest", "NotJson")]
    public async Task Malformed_manifest_is_refused_whole(string json, string field, string code)
    {
        using var world = new World();
        var refusal = Assert.Throws<ManifestRefusedException>(() => Parse(json));

        Assert.Equal((field, code), (refusal.Field, refusal.Code));
        Assert.Equal($"{field}: {code}", refusal.Message);
        Assert.Empty(await world.Fresh(db => db.ConceptSubjects.ToListAsync()));
    }

    [Fact]
    public void A_title_of_exactly_the_column_width_is_accepted_and_one_more_is_refused()
    {
        var fits = new string('t', ConceptSubjectRegistry.MaxTitleLength);

        Assert.Equal(fits, Parse($$"""{"ships":[{"id":"a","name":"{{fits}}"}]}""").Single().Title);
        var refusal = Assert.Throws<ManifestRefusedException>(() =>
            Parse($$"""{"ships":[{"id":"a","name":"{{fits}}x"}]}"""));
        Assert.Equal(("ships[0].name", "TooLong"), (refusal.Field, refusal.Code));
    }

    private static readonly JsonSerializerOptions Raw = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Theory]
    [InlineData("‮evil")]
    [InlineData("a‪b")]
    [InlineData("a⁦b")]
    [InlineData("a⁩b")]
    [InlineData("a؜b")]
    [InlineData("a‎b")]
    [InlineData("a‏b")]
    [InlineData("a b")]
    [InlineData("a b")]
    [InlineData("a​b")]
    [InlineData("a‌b")]
    [InlineData("a‍b")]
    [InlineData("a⁠b")]
    [InlineData("a﻿b")]
    [InlineData("a\u0085b")]
    [InlineData("a\tb")]
    [InlineData(" Ibis")]
    [InlineData("Ibis ")]
    [InlineData(" Ibis")]
    [InlineData("Ibis ")]
    [InlineData("　Ibis")]
    [InlineData("!!!")]
    public void A_title_with_a_format_character_edge_whitespace_or_no_letter_is_refused(string title)
    {
        var json = """{"ships":[{"id":"a","name":""" + JsonSerializer.Serialize(title, Raw) + "}]}";

        var refusal = Assert.Throws<ManifestRefusedException>(() => Parse(json));

        Assert.Equal(("ships[0].name", "InvalidTitle"), (refusal.Field, refusal.Code));
    }

    [Theory]
    [InlineData("Ibis Mk II")]
    [InlineData("Ünïcode Ship")]
    [InlineData("Ship ★ 7")]
    [InlineData("7")]
    [InlineData("Kestrel-2: the \"Bold\"")]
    public void An_ordinary_title_is_accepted(string title)
    {
        var json = """{"ships":[{"id":"a","name":""" + JsonSerializer.Serialize(title, Raw) + "}]}";

        Assert.Equal(title, Parse(json).Single().Title);
    }

    [Theory]
    [InlineData("""{"ships":[{"id":"a","name":"x\ud800y"}]}""", "ships[0].name", "BadText")]
    [InlineData("""{"ships":[{"id":"a\udc00","name":"x"}]}""", "ships[0].id", "BadText")]
    [InlineData("""{"ships":[{"id":"a","name":"x","na\ud800me":"y"}]}""", "manifest", "BadText")]
    public void A_lone_surrogate_is_refused_not_thrown(string json, string field, string code)
    {
        var refusal = Assert.Throws<ManifestRefusedException>(() => Parse(json));

        Assert.Equal((field, code), (refusal.Field, refusal.Code));
    }

    [Fact]
    public void Malformed_utf8_anywhere_in_the_file_is_refused()
    {
        byte[] inName = [.. "{\"ships\":[{\"id\":\"a\",\"name\":\"X"u8, 0xC3, 0x28, .. "\"}]}"u8];
        byte[] inUnreadValue = [.. "{\"ships\":[],\"x\":\"X"u8, 0xFF, .. "\"}"u8];

        foreach (var bytes in new[] { inName, inUnreadValue })
        {
            var refusal = Assert.Throws<ManifestRefusedException>(() => ConceptSubjectRegistry.ParseManifest(bytes));

            Assert.Equal(("manifest", "NotUtf8"), (refusal.Field, refusal.Code));
        }
    }

    [Theory]
    [InlineData("""{"ships":[{"id":"a","id":"b","name":"A"}]}""")]
    [InlineData("""{"ships":[{"id":"a","name":"A","name":"B"}]}""")]
    [InlineData("""{"ships":[],"ships":[]}""")]
    [InlineData("""{"ships":[{"id":"a","name":"A"}],"votes":{"x":1,"x":2}}""")]
    [InlineData("""{"ships":[{"id":"a","name":"A","meta":{"k":[{"z":1,"z":1}]}}]}""")]
    [InlineData("""{"ships":[{"id":"a","name":"A","id":"b"}]}""")]
    public void A_duplicate_json_property_is_refused_wherever_it_sits(string json)
    {
        var refusal = Assert.Throws<ManifestRefusedException>(() => Parse(json));

        Assert.Equal(("manifest", "DuplicateProperty"), (refusal.Field, refusal.Code));
    }

    [Fact]
    public void The_same_property_name_in_sibling_and_nested_objects_is_not_a_duplicate()
    {
        var ships = Parse("""{"id":"top","ships":[{"id":"a","name":"A","meta":{"id":"x","name":"y"}},{"id":"b","name":"B"}]}""");

        Assert.Equal(["a", "b"], ships.Select(s => s.Key));
    }

    [Fact]
    public void A_manifest_of_exactly_the_byte_bound_is_accepted_and_one_more_byte_is_refused()
    {
        const string head = """{"ships":[{"id":"a","name":"A"}]}""";
        var fits = Encoding.UTF8.GetBytes(head + new string(' ', ConceptSubjectRegistry.MaxManifestBytes - head.Length));

        Assert.Equal(ConceptSubjectRegistry.MaxManifestBytes, fits.Length);
        Assert.Single(ConceptSubjectRegistry.ParseManifest(fits));
        var refusal = Assert.Throws<ManifestRefusedException>(() => ConceptSubjectRegistry.ParseManifest([.. fits, (byte)' ']));
        Assert.Equal(("manifest", "TooLarge"), (refusal.Field, refusal.Code));
    }

    [Fact]
    public void A_manifest_of_exactly_the_ship_bound_is_accepted_and_one_more_ship_is_refused()
    {
        static string Fleet(int count) =>
            """{"ships":[""" + string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{"id":"s{{i}}","name":"S{{i}}"}""")) + "]}";

        Assert.Equal(ConceptSubjectRegistry.MaxShips, Parse(Fleet(ConceptSubjectRegistry.MaxShips)).Count);
        var refusal = Assert.Throws<ManifestRefusedException>(() => Parse(Fleet(ConceptSubjectRegistry.MaxShips + 1)));
        Assert.Equal(("ships", "TooMany"), (refusal.Field, refusal.Code));
    }

    [Fact]
    public async Task Absent_keys_are_planned_in_key_order()
    {
        using var world = new World();
        await world.Apply(Fixture());

        var plan = await world.Plan([new ManifestShip("zzz-new", "New")]);

        Assert.Equal(["zzz-new", "bison", "ibis-mk2", "kestrel"], plan.Entries.Select(e => e.Key));
    }

    [Fact]
    public async Task A_bad_entry_after_good_ones_refuses_the_file_and_writes_no_row()
    {
        using var world = new World();
        var json = """{"ships":[{"id":"good","name":"Good"},{"id":"ship-a\n","name":"Bad"}]}""";

        Assert.Throws<ManifestRefusedException>(() => Parse(json));
        Assert.Empty(await world.Fresh(db => db.ConceptSubjects.ToListAsync()));
    }

    [Fact]
    public async Task Refusal_never_echoes_the_offending_value()
    {
        const string canary = "CANARY-secret/value";
        var json = $$"""{"ships":[{"id":"{{canary}}","name":"A"}]}""";

        var refusal = Assert.Throws<ManifestRefusedException>(() => Parse(json));
        Assert.DoesNotContain("CANARY", refusal.Message);
        using var world = new World();
        var collection = await Assert.ThrowsAsync<ManifestRefusedException>(() => world.Registry(world.NewDb()).PlanAsync(canary, [], None, default));
        Assert.DoesNotContain("CANARY", collection.Message);
        Assert.Equal(("collection", "InvalidKey"), (collection.Field, collection.Code));
        var retire = await Assert.ThrowsAsync<ManifestRefusedException>(() => world.Registry(world.NewDb()).PlanAsync(Collection, [], [canary], default));
        Assert.DoesNotContain("CANARY", retire.Message);
        Assert.Equal(("retire", "InvalidKey"), (retire.Field, retire.Code));
    }

    [Theory]
    [InlineData("ship-a\n")]
    [InlineData("ship-a\r\n")]
    [InlineData("ship-a ")]
    public async Task The_key_grammar_rejects_a_trailing_line_feed_wherever_it_is_reused(string forged)
    {
        using var world = new World();
        await world.Apply(Fixture());
        await world.Registry(world.NewDb()).ApplyAsync(Collection, [new ManifestShip("ship-a", "A")], None, default);

        Assert.False(ConceptKey.IsValid(forged));
        Assert.False(ConceptKey.IsValid("key\n"));
        Assert.True(ConceptKey.IsValid("ship-a"));
        Assert.Equal(CastResult.UnknownSubject, await world.Cast(forged, ConceptStance.Up));
        Assert.Null(await world.Fresh(db => world.Appraisals(db).GetSubjectAsync(Collection, forged, null, default)));
        await Assert.ThrowsAsync<ManifestRefusedException>(() =>
            world.Registry(world.NewDb()).PlanAsync(Collection + "\n", [], None, default));
        await Assert.ThrowsAsync<ManifestRefusedException>(() =>
            world.Registry(world.NewDb()).PlanAsync(Collection, [], ["ship-a\n"], default));
        Assert.Empty(await world.Fresh(db => db.ConceptAppraisals.ToListAsync()));
    }

    [Fact]
    public async Task Host_command_plans_and_applies_with_exit_codes()
    {
        using var world = new World();
        var services = world.Services();

        var plan = new StringWriter();
        Assert.Equal(ConceptCommand.Ok, await ConceptCommand.RunAsync(["plan", Collection, FixturePath], services, plan));
        Assert.Contains("add bison", plan.ToString());
        Assert.Contains("add ibis-mk2", plan.ToString());
        Assert.Empty(await world.Fresh(db => db.ConceptSubjects.ToListAsync()));
        Assert.Empty(await world.Fresh(db => db.AuditEvents.ToListAsync()));

        var apply = new StringWriter();
        Assert.Equal(ConceptCommand.Ok, await ConceptCommand.RunAsync(["apply", Collection, FixturePath], services, apply));
        Assert.Equal(3, await world.Fresh(db => db.ConceptSubjects.CountAsync()));

        var again = new StringWriter();
        Assert.Equal(ConceptCommand.Ok, await ConceptCommand.RunAsync(["apply", Collection, FixturePath], services, again));
        Assert.Contains("unchanged bison", again.ToString());
        Assert.Equal(3, await world.Fresh(db => db.AuditEvents.CountAsync()));
    }

    [Fact]
    public async Task Host_command_retire_flag_retires_the_named_absent_key()
    {
        using var world = new World();
        var services = world.Services();
        await ConceptCommand.RunAsync(["apply", Collection, FixturePath], services, new StringWriter());
        var smaller = Path.Combine(world.Dir, "two.json");
        await File.WriteAllTextAsync(smaller, """{"ships":[{"id":"bison","name":"Bison"},{"id":"ibis-mk2","name":"Ibis Mk II"}]}""");

        var output = new StringWriter();
        Assert.Equal(ConceptCommand.Ok, await ConceptCommand.RunAsync(["apply", Collection, smaller, "--retire", "kestrel"], services, output));

        Assert.Contains("retire kestrel", output.ToString());
        Assert.NotNull((await world.Fresh(db => db.ConceptSubjects.AsNoTracking().SingleAsync(x => x.Key == "kestrel"))).RetiredAtUtc);
    }

    [Fact]
    public async Task Host_command_exits_non_zero_on_refusal_and_never_echoes_input()
    {
        using var world = new World();
        var services = world.Services();
        var bad = Path.Combine(world.Dir, "bad.json");
        await File.WriteAllTextAsync(bad, """{"ships":[{"id":"CANARY/Id","name":"A"}]}""");

        var refused = new StringWriter();
        Assert.Equal(ConceptCommand.Refused, await ConceptCommand.RunAsync(["apply", Collection, bad], services, refused));
        Assert.Contains("ships[0].id", refused.ToString());
        Assert.DoesNotContain("CANARY", refused.ToString());

        var missing = new StringWriter();
        Assert.Equal(ConceptCommand.Refused, await ConceptCommand.RunAsync(["apply", Collection, Path.Combine(world.Dir, "CANARY-missing.json")], services, missing));
        Assert.DoesNotContain("CANARY", missing.ToString());
        Assert.Equal("refused manifest Unreadable", missing.ToString().Trim());
        Assert.Empty(await world.Fresh(db => db.ConceptSubjects.ToListAsync()));
    }

    [Theory]
    [InlineData("frobnicate", "c", "m")]
    [InlineData("plan", "c")]
    [InlineData("apply", "c", "m", "--bogus", "x")]
    [InlineData("apply", "c", "m", "--retire")]
    public async Task Host_command_unknown_subcommand_or_shape_exits_2(params string[] args)
    {
        using var world = new World();

        var output = new StringWriter();
        Assert.Equal(ConceptCommand.Usage, await ConceptCommand.RunAsync(args, world.Services(), output));
        Assert.StartsWith("usage:", output.ToString());
        Assert.Empty(await world.Fresh(db => db.ConceptSubjects.ToListAsync()));
        Assert.Equal(ConceptCommand.Usage, await ConceptCommand.RunAsync([], world.Services(), new StringWriter()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("CANARY\0path.json")]
    public async Task Host_command_refuses_an_empty_or_nul_path(string path)
    {
        using var world = new World();

        var output = new StringWriter();
        Assert.Equal(ConceptCommand.Refused, await ConceptCommand.RunAsync(["plan", Collection, path], world.Services(), output));

        Assert.Equal("refused manifest Unreadable", output.ToString().Trim());
    }

    [Fact]
    public async Task Host_command_refuses_a_directory_and_an_oversized_file()
    {
        using var world = new World();
        var big = Path.Combine(world.Dir, "big.json");
        await File.WriteAllBytesAsync(big, new byte[ConceptSubjectRegistry.MaxManifestBytes + 1]);

        var dir = new StringWriter();
        Assert.Equal(ConceptCommand.Refused, await ConceptCommand.RunAsync(["plan", Collection, world.Dir], world.Services(), dir));
        Assert.Equal("refused manifest Unreadable", dir.ToString().Trim());
        var large = new StringWriter();
        Assert.Equal(ConceptCommand.Refused, await ConceptCommand.RunAsync(["plan", Collection, big], world.Services(), large));
        Assert.Equal("refused manifest TooLarge", large.ToString().Trim());
    }

    [Fact]
    public async Task Host_command_refuses_a_manifest_that_is_not_utf8_text()
    {
        using var world = new World();
        var path = Path.Combine(world.Dir, "latin1.json");
        await File.WriteAllBytesAsync(path, [.. "{\"ships\":[{\"id\":\"a\",\"name\":\"CANARY-"u8, 0xE9, .. "\"}]}"u8]);

        var output = new StringWriter();
        Assert.Equal(ConceptCommand.Refused, await ConceptCommand.RunAsync(["plan", Collection, path], world.Services(), output));

        Assert.Equal("refused manifest NotUtf8", output.ToString().Trim());
    }

    [Fact]
    public async Task Built_command_exits_with_its_documented_codes_and_echoes_no_input()
    {
        using var world = new World();
        var big = Path.Combine(world.Dir, "CANARYBIG.json");
        await File.WriteAllBytesAsync(big, new byte[ConceptSubjectRegistry.MaxManifestBytes + 1]);
        var bidi = Path.Combine(world.Dir, "CANARYBIDI.json");
        await File.WriteAllTextAsync(bidi, """{"ships":[{"id":"a","name":"CANARYTITLE‮"}]}""");
        var dup = Path.Combine(world.Dir, "CANARYDUP.json");
        await File.WriteAllTextAsync(dup, """{"ships":[{"id":"CANARYID","id":"b","name":"A"}]}""");
        var surrogate = Path.Combine(world.Dir, "CANARYSURR.json");
        await File.WriteAllTextAsync(surrogate, """{"ships":[{"id":"a","name":"CANARYNAME\ud800"}]}""");
        var latin1 = Path.Combine(world.Dir, "CANARYLATIN.json");
        await File.WriteAllBytesAsync(latin1, [.. "{\"ships\":[{\"id\":\"a\",\"name\":\"CANARYBYTES"u8, 0xE9, .. "\"}]}"u8]);

        var cases = new (string[] Args, int Code)[]
        {
            (["plan", Collection, ""], ConceptCommand.Refused),
            (["plan", Collection, Path.Combine(world.Dir, "CANARYMISSING.json")], ConceptCommand.Refused),
            (["plan", Collection, world.Dir], ConceptCommand.Refused),
            (["plan", Collection, big], ConceptCommand.Refused),
            (["plan", Collection, bidi], ConceptCommand.Refused),
            (["apply", Collection, dup], ConceptCommand.Refused),
            (["apply", Collection, surrogate], ConceptCommand.Refused),
            (["plan", Collection, latin1], ConceptCommand.Refused),
            (["plan", "CANARYCOLLECTION", bidi], ConceptCommand.Refused),
            (["frob", "CANARYARG", "x"], ConceptCommand.Usage),
            (["plan"], ConceptCommand.Usage),
        };
        foreach (var (args, code) in cases)
        {
            var result = await RunBuiltCommandAsync(args);

            Assert.True(code == result.ExitCode, $"case {args[0]}/{args.Length}: exit {result.ExitCode}, stdout {result.StdOut}, stderr {result.StdErr}");
            Assert.DoesNotContain("CANARY", result.StdOut + result.StdErr);
            Assert.DoesNotContain("Unhandled exception", result.StdOut + result.StdErr);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunBuiltCommandAsync(string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Bifrost.Web.dll");
        Assert.True(File.Exists(Path.ChangeExtension(dll, ".runtimeconfig.json")), "Bifrost.Web.runtimeconfig.json is not beside the test assembly");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var part in new[] { dll, "concepts" }.Concat(args))
        {
            start.ArgumentList.Add(part);
        }

        // Every case refuses before the database is touched; the password is a canary for leaks.
        start.Environment["ConnectionStrings__Bifrost"] = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=CANARYPW";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    private sealed class World : IDisposable
    {
        private readonly string _dbName = $"registry-{Guid.NewGuid():N}";

        public World()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"registry-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Dir);
        }

        public string Dir { get; }

        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

        public BifrostDbContext NewDb() =>
            new(new DbContextOptionsBuilder<BifrostDbContext>().UseInMemoryDatabase(_dbName).Options);

        public ConceptSubjectRegistry Registry(BifrostDbContext db) =>
            new(db, new AuditTrailService(db, Clock), Clock);

        public ConceptAppraisalService Appraisals(BifrostDbContext db) =>
            new(db, new AuditTrailService(db, Clock), Clock,
                Options.Create(new ConceptVoteOptions { NetworkTagSecret = "test-tag-secret" }));

        public async Task<SubjectPlan> Apply(IReadOnlyList<ManifestShip> ships, string[]? retire = null)
        {
            await using var db = NewDb();
            return await Registry(db).ApplyAsync(Collection, ships, retire ?? None, default);
        }

        public async Task<SubjectPlan> Plan(IReadOnlyList<ManifestShip> ships)
        {
            await using var db = NewDb();
            return await Registry(db).PlanAsync(Collection, ships, None, default);
        }

        public async Task<CastResult> Cast(string key, ConceptStance stance)
        {
            await using var db = NewDb();
            var voter = VoterIdentity.For(CurrentBifrostActor.Anonymous, "device-" + stance)!;
            return await Appraisals(db).CastAsync(Collection, key, voter, stance, IPAddress.Parse("203.0.113.7"), default);
        }

        public async Task<T> Fresh<T>(Func<BifrostDbContext, Task<T>> read)
        {
            await using var db = NewDb();
            return await read(db);
        }

        public IServiceProvider Services()
        {
            var services = new ServiceCollection();
            services.AddDbContext<BifrostDbContext>(o => o.UseInMemoryDatabase(_dbName));
            services.AddSingleton<TimeProvider>(Clock);
            services.AddScoped<AuditTrailService>();
            services.AddScoped<ConceptSubjectRegistry>();
            return services.BuildServiceProvider();
        }

        public void Dispose() => Directory.Delete(Dir, true);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
