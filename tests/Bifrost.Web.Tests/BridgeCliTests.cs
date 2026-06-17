using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Bifrost.Web.Tests;

public sealed class BridgeCliTests
{
    [Fact]
    public async Task GitHub_bridge_command_fails_closed_without_bifrost_gate()
    {
        var result = await RunNodeAsync([
            "tools/bifrost-bridge.mjs",
            "github-pr-comment",
            "--repo-root", RepoRoot,
            "--identity", "nibu",
            "--pr", "1",
            "--content", "test comment",
            "--target-repository-full-name", "GameCult/Bifrost",
        ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("require Bifrost authorization", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitHub_bridge_command_allows_explicit_operator_recovery_dry_run()
    {
        var result = await RunNodeAsync([
            "tools/bifrost-bridge.mjs",
            "github-pr-comment",
            "--repo-root", RepoRoot,
            "--identity", "nibu",
            "--pr", "1",
            "--content", "test comment",
            "--target-repository-full-name", "GameCult/Bifrost",
            "--allow-ungated-github", "true",
            "--dry-run", "true",
        ]);

        Assert.Equal(0, result.ExitCode);
        using var payload = JsonDocument.Parse(result.Stdout);
        Assert.Equal("github-pr-comment", payload.RootElement.GetProperty("action").GetString());
        Assert.True(payload.RootElement.GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task Agent_transport_mutation_fails_closed_without_bifrost_receipt_gate()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"bifrost-agent-transport-{Guid.NewGuid():N}.cc");
        var result = await RunNodeAsync([
            "tools/agent-transport.mjs",
            "enqueue",
            "--repo", "Bifrost",
            "--agent", "nibu",
            "--title", "Receipt gate test",
            "--request", "Do the thing.",
            "--store", storePath,
            "--allow-unmirrored", "true",
        ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("BIFROST_BRIDGE_BASE_URL and BIFROST_BRIDGE_TOKEN", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_transport_mutation_allows_explicit_operator_recovery()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"bifrost-agent-transport-{Guid.NewGuid():N}.cc");
        var result = await RunNodeAsync([
            "tools/agent-transport.mjs",
            "enqueue",
            "--repo", "Bifrost",
            "--agent", "nibu",
            "--title", "Receipt gate recovery",
            "--request", "Do the thing.",
            "--store", storePath,
            "--allow-unmirrored", "true",
            "--allow-unreceipted-activity", "true",
        ]);

        Assert.Equal(0, result.ExitCode);
        using var payload = JsonDocument.Parse(result.Stdout);
        Assert.Equal("queued", payload.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Governance_mutation_fails_closed_without_bifrost_receipt_gate()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"bifrost-governance-{Guid.NewGuid():N}.cc");
        var result = await RunNodeAsync([
            "tools/governance-threads.mjs",
            "open",
            "--repo", "Bifrost",
            "--agent", "nibu",
            "--title", "Governance receipt gate test",
            "--summary", "A topic body.",
            "--store", storePath,
            "--allow-unmirrored", "true",
        ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("BIFROST_BRIDGE_BASE_URL and BIFROST_BRIDGE_TOKEN", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Governance_mutation_allows_explicit_operator_recovery()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"bifrost-governance-{Guid.NewGuid():N}.cc");
        var result = await RunNodeAsync([
            "tools/governance-threads.mjs",
            "open",
            "--repo", "Bifrost",
            "--agent", "nibu",
            "--title", "Governance receipt recovery",
            "--summary", "A topic body.",
            "--store", storePath,
            "--allow-unmirrored", "true",
            "--allow-unreceipted-activity", "true",
        ]);

        Assert.Equal(0, result.ExitCode);
        using var payload = JsonDocument.Parse(result.Stdout);
        Assert.Equal("open", payload.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Dispatch_worker_fails_closed_without_bifrost_receipt_gate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bifrost-dispatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var requestPath = Path.Combine(tempDir, "request.json");
        var promptPath = Path.Combine(tempDir, "prompt.md");
        var logPath = Path.Combine(tempDir, "codex.log");

        await File.WriteAllTextAsync(requestPath, """
{
  "id": "req_dispatch_gate_123",
  "targetRepoName": "Bifrost",
  "targetRepositoryFullName": "GameCult/Bifrost",
  "targetAgentIdentity": "nibu",
  "title": "Dispatch gate test",
  "requestMarkdown": "## Request\n\nDo the thing.",
  "priority": 50,
  "status": "claimed",
  "sourceKind": "manual",
  "sourceChannelId": "",
  "sourceMessageIds": [],
  "sourcePacketPath": "",
  "sourcePromptPath": "",
  "createdByAgent": "tester",
  "claimedByAgent": "tester",
  "closeNote": "",
  "createdAt": "2026-06-17T00:00:00Z",
  "updatedAt": "2026-06-17T00:00:00Z",
  "claimedAt": "2026-06-17T00:00:00Z",
  "closedAt": ""
}
""", Encoding.UTF8);
        await File.WriteAllTextAsync(promptPath, "Hello", Encoding.UTF8);

        var result = await RunNodeAsync([
            "tools/dispatch-agent-requests.mjs",
            "run-claimed",
            "--request-file", requestPath,
            "--repo-root", RepoRoot,
            "--prompt-file", promptPath,
            "--log", logPath,
            "--launch-mode", "codex-exec",
        ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("requires BIFROST_BRIDGE_BASE_URL and BIFROST_BRIDGE_TOKEN", result.Stderr, StringComparison.OrdinalIgnoreCase);
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
        process.StartInfo.Environment["BIFROST_ALLOW_UNGATED_GITHUB"] = string.Empty;
        process.StartInfo.Environment["BIFROST_ALLOW_UNRECEIPTED_ACTIVITY"] = string.Empty;

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
