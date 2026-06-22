using System.Diagnostics;
using System.Text.Json;

namespace Bifrost.Web.Tests;

public sealed class ProviderAdvertisementTests
{
    [Fact]
    public async Task Provider_advertisement_names_current_bridge_and_patron_contracts()
    {
        var result = await RunNodeAsync([
            "tools/provider-advertisement.mjs",
            "print",
            "--generated-at",
            "2026-06-22T00:00:00.000Z",
        ]);

        Assert.True(result.ExitCode == 0, $"stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
        using var payload = JsonDocument.Parse(result.Stdout);
        var root = payload.RootElement;

        AssertEndpoint(root, "bridge-action-ledger", "bifrost.bridge_action.v0", "bridge");
        AssertEndpoint(root, "patron-support-intake", "bifrost.patron_support_event.v0", "patronage");
        AssertEndpoint(root, "github-webhooks", "bifrost.work_item.v0", "github");
        AssertSchemaPurpose(root, "bifrost.bridge_action.v0", "current hosted governed crossing command witness");
        AssertSchemaPurpose(root, "bifrost.bridge_receipt.v0", "current hosted governed crossing result witness");
        AssertSchemaPurpose(root, "bifrost.patron_support_event.v0", "current hosted Heimdall-signed patron support fact consumed by Bifrost");
        AssertBoundaryCommand(root, "bridge", "open GitHub draft PRs and PR comments through Bifrost gate");
        AssertBoundaryCommand(root, "bridge", "post Discord messages and DMs through Bifrost gate with Heimdall-linked actor capability");
        AssertBoundaryCommand(root, "bridge", "record receipt-only future-surface requests with named-surface Heimdall capability matching before named actuators exist");
        AssertBoundaryCommand(root, "bridge", "post Persona-flaired Reddit organizing threads");
        AssertBoundaryCommand(root, "patron", "consume Heimdall-signed Patreon and PayPal support facts");
        AssertBoundaryForbiddenAuthority(root, "patron", "does not store Patreon or PayPal provider tokens");
    }

    [Fact]
    public async Task Interface_binding_exposes_current_agent_bridge_capabilities()
    {
        var result = await RunNodeAsync([
            "tools/provider-advertisement.mjs",
            "print-binding",
            "--generated-at",
            "2026-06-22T00:00:00.000Z",
        ]);

        Assert.True(result.ExitCode == 0, $"stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
        using var payload = JsonDocument.Parse(result.Stdout);
        var root = payload.RootElement;
        var provider = root.GetProperty("provider");

        AssertArrayContains(provider.GetProperty("capabilities"), "github-bridge");
        AssertArrayContains(provider.GetProperty("capabilities"), "github-work-sync");
        AssertArrayContains(provider.GetProperty("capabilities"), "discord-bridge");
        AssertArrayContains(provider.GetProperty("capabilities"), "reddit-bridge");
        AssertArrayContains(provider.GetProperty("capabilities"), "future-surface-bridge");
        AssertArrayContains(provider.GetProperty("capabilities"), "heimdall-patron-support-intake");

        var stats = root.GetProperty("surface").GetProperty("root").GetProperty("props");
        Assert.Equal("2026-06-22T00:00:00.000Z", stats.GetProperty("generatedAt").GetString());

        var bridgeMetric = root
            .GetProperty("surface")
            .GetProperty("root")
            .GetProperty("children")
            .EnumerateArray()
            .SelectMany(panel => panel.GetProperty("children").EnumerateArray())
            .Single(node => node.GetProperty("id").GetString() == "metric-bridge");

        var bridgeLine = bridgeMetric.GetProperty("props").GetProperty("value").GetString();
        Assert.Contains("GitHub live", bridgeLine, StringComparison.Ordinal);
        Assert.Contains("Discord prepared", bridgeLine, StringComparison.Ordinal);
        Assert.Contains("Reddit prepared", bridgeLine, StringComparison.Ordinal);
        Assert.Contains("Other live", bridgeLine, StringComparison.Ordinal);
        Assert.Contains("Patron live", bridgeLine, StringComparison.Ordinal);

        Assert.Equal("warn", bridgeMetric.GetProperty("props").GetProperty("tone").GetString());

        var readinessRows = root
            .GetProperty("surface")
            .GetProperty("root")
            .GetProperty("children")
            .EnumerateArray()
            .SelectMany(panel => panel.GetProperty("children").EnumerateArray())
            .Single(node => node.GetProperty("id").GetString() == "list-bridge-readiness")
            .GetProperty("children")
            .EnumerateArray()
            .Select(node => node.GetProperty("props").GetProperty("text").GetString())
            .ToArray();

        Assert.Contains(readinessRows, row => row is not null && row.Contains("reddit: prepared", StringComparison.Ordinal));
        Assert.Contains(readinessRows, row => row is not null && row.Contains("patron: live", StringComparison.Ordinal) && row.Contains("Bifrost stores no provider tokens", StringComparison.Ordinal));
    }

    private static void AssertEndpoint(JsonElement root, string id, string schemaId, string lowering)
    {
        var endpoint = root.GetProperty("endpoints")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == id);
        Assert.Equal(schemaId, endpoint.GetProperty("schemaId").GetString());
        AssertArrayContains(endpoint.GetProperty("lowerings"), lowering);
    }

    private static void AssertSchemaPurpose(JsonElement root, string id, string purpose)
    {
        var schema = root.GetProperty("schemas")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == id);
        Assert.Equal(purpose, schema.GetProperty("purpose").GetString());
    }

    private static void AssertBoundaryCommand(JsonElement root, string area, string command)
    {
        var boundary = Boundary(root, area);
        AssertArrayContains(boundary.GetProperty("commands"), command);
    }

    private static void AssertBoundaryForbiddenAuthority(JsonElement root, string area, string forbiddenAuthority)
    {
        var boundary = Boundary(root, area);
        AssertArrayContains(boundary.GetProperty("forbiddenAuthority"), forbiddenAuthority);
    }

    private static JsonElement Boundary(JsonElement root, string area)
        => root.GetProperty("commandBoundaries")
            .EnumerateArray()
            .Single(item => item.GetProperty("area").GetString() == area);

    private static void AssertArrayContains(JsonElement array, string expected)
    {
        Assert.Contains(array.EnumerateArray(), item => item.GetString() == expected);
    }

    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static async Task<ProcessResult> RunNodeAsync(string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.StartInfo.Environment["BIFROST_BRIDGE_BASE_URL"] = string.Empty;
        process.StartInfo.Environment["BIFROST_BRIDGE_TOKEN"] = string.Empty;
        process.StartInfo.Environment["BIFROST_SKIP_LOCAL_ENV"] = "true";
        process.StartInfo.Environment["BIFROST_REDDIT_CLIENT_ID"] = string.Empty;
        process.StartInfo.Environment["BIFROST_REDDIT_REFRESH_TOKEN"] = string.Empty;

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
