using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Motions;

public sealed class MotionEveSurfaceService
{
    private const string MotionCommandTarget = "cultmesh://asgard.starfire.bifrost/commands/motion";

    public EveSurfaceDocument BuildSurface(MotionGovernanceState state)
    {
        var motionNodes = state.Motions.Count > 0
            ? state.Motions.Select(BuildMotionCard).ToArray()
            : [TextNode("motion-empty", "No motions yet.")];

        return new EveSurfaceDocument(
            "gamecult.eve.surface.v1",
            "bifrost-motion-verse",
            "Bifrost Motion Verse",
            new EveNode(
                "bifrost-motion-root",
                "workspace",
                new Dictionary<string, object?>
                {
                    ["title"] = "Bifrost Governance",
                    ["subtitle"] = "Motions, weighted votes, thresholds, and receipts",
                    ["actor"] = state.ActorName,
                    ["effectiveVotingWeight"] = state.EffectiveVotingWeight,
                    ["canonicalCommandTarget"] = MotionCommandTarget,
                    ["commandTransport"] = "cultmesh-command-document",
                    ["commandSchema"] = "bifrost.motion_command.v0"
                },
                [
                    new EveNode(
                        "motion-create",
                        "form",
                        new Dictionary<string, object?>
                        {
                            ["title"] = "Open Motion",
                            ["command"] = "motion.create",
                            ["target"] = MotionCommandTarget,
                            ["transport"] = "cultmesh-command-document"
                        },
                        [
                            SelectNode("scope", "Scope", EnumNames<MotionScope>()),
                            SelectNode("projectId", "Project", state.Projects.Select(x => new EveOption(x.Id.ToString(), x.Name)).Prepend(new EveOption("", "No project scope"))),
                            SelectNode("category", "Category", EnumNames<MotionCategory>()),
                            InputNode("title", "Title", "text"),
                            InputNode("summary", "Summary", "multiline"),
                            InputNode("closesAtUtc", "Close at UTC", "datetime")
                        ]),
                    new EveNode(
                        "vote-ledger",
                        "panel",
                        new Dictionary<string, object?>
                        {
                            ["title"] = "Vote Ledger",
                            ["effectiveVotingWeight"] = state.EffectiveVotingWeight
                        },
                        state.CategoryPolicies
                            .Select(x => TextNode($"threshold-{Slug(x.Label)}", $"{x.Label}: {x.Threshold:P0} threshold"))
                            .ToArray()),
                    new EveNode(
                        "motions",
                        "collection",
                        new Dictionary<string, object?> { ["title"] = "Motions" },
                        motionNodes)
                ]));
    }

    private static EveNode BuildMotionCard(MotionListItem motion)
    {
        var commandNodes = new List<EveNode>();
        foreach (var choice in Enum.GetValues<VoteChoice>())
        {
            commandNodes.Add(CommandNode(
                $"vote-{motion.Id}-{choice}",
                $"Vote {choice}",
                "motion.vote",
                new Dictionary<string, object?>
                {
                    ["motionId"] = motion.Id,
                    ["choice"] = choice.ToString(),
                    ["comment"] = string.Empty
                },
                motion.CurrentUserVote == choice ? "selected" : "default"));
        }

        if (motion.StatusLabel == "Expired")
        {
            commandNodes.Add(CommandNode(
                $"close-{motion.Id}",
                "Close motion",
                "motion.close",
                new Dictionary<string, object?> { ["motionId"] = motion.Id },
                "secondary"));
        }

        return new EveNode(
            $"motion-{motion.Id}",
            "card",
            new Dictionary<string, object?>
            {
                ["title"] = motion.Title,
                ["summary"] = motion.Summary,
                ["scope"] = motion.Scope,
                ["category"] = motion.Category,
                ["status"] = motion.StatusLabel,
                ["tone"] = motion.StatusTone,
                ["project"] = motion.ProjectName,
                ["threshold"] = motion.Threshold,
                ["closesAtUtc"] = motion.ClosesAtUtc,
                ["resolutionNote"] = motion.ResolutionNote
            },
            [
                new EveNode(
                    $"motion-votes-{motion.Id}",
                    "metrics",
                    new Dictionary<string, object?>
                    {
                        ["for"] = motion.VotesFor,
                        ["against"] = motion.VotesAgainst,
                        ["abstain"] = motion.VotesAbstain,
                        ["currentUserVote"] = motion.CurrentUserVote?.ToString() ?? string.Empty
                    }),
                new EveNode(
                    $"motion-commands-{motion.Id}",
                    "commands",
                    new Dictionary<string, object?>
                    {
                        ["target"] = MotionCommandTarget,
                        ["transport"] = "cultmesh-command-document"
                    },
                    commandNodes)
            ]);
    }

    private static EveNode CommandNode(
        string id,
        string label,
        string command,
        IReadOnlyDictionary<string, object?> payload,
        string tone) =>
        new(
            id,
            "command",
            new Dictionary<string, object?>
            {
                ["label"] = label,
                ["command"] = command,
                ["target"] = MotionCommandTarget,
                ["transport"] = "cultmesh-command-document",
                ["payload"] = payload,
                ["tone"] = tone
            });

    private static EveNode InputNode(string id, string label, string inputKind) =>
        new(
            $"input-{id}",
            "input",
            new Dictionary<string, object?>
            {
                ["name"] = id,
                ["label"] = label,
                ["inputKind"] = inputKind
            });

    private static EveNode SelectNode(string id, string label, IEnumerable<string> options) =>
        SelectNode(id, label, options.Select(x => new EveOption(x, x)));

    private static EveNode SelectNode(string id, string label, IEnumerable<EveOption> options) =>
        new(
            $"input-{id}",
            "select",
            new Dictionary<string, object?>
            {
                ["name"] = id,
                ["label"] = label,
                ["options"] = options.ToArray()
            });

    private static EveNode TextNode(string id, string text) =>
        new(id, "text", new Dictionary<string, object?> { ["text"] = text });

    private static IReadOnlyList<string> EnumNames<TEnum>() where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>();

    private static string Slug(string value) =>
        value.ToLowerInvariant().Replace(" ", "-", StringComparison.Ordinal);
}

public sealed record EveSurfaceDocument(
    string Schema,
    string Id,
    string Title,
    EveNode Root);

public sealed record EveNode(
    string Id,
    string Kind,
    IReadOnlyDictionary<string, object?> Props,
    IReadOnlyList<EveNode>? Children = null);

public sealed record EveOption(string Value, string Label);
