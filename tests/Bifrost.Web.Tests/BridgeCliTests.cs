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
    public async Task GitHub_pr_comment_returns_concrete_github_receipt()
    {
        var fakeToolsDir = Path.Combine(Path.GetTempPath(), $"bifrost-gh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fakeToolsDir);
        var fakeGhPath = Path.Combine(fakeToolsDir, "fake-gh.js");
        await File.WriteAllTextAsync(fakeGhPath, """
console.log(JSON.stringify({
  id: 123456,
  node_id: "IC_kwDOTest",
  html_url: "https://github.com/GameCult/Bifrost/pull/1#issuecomment-123456"
}));
""", Encoding.UTF8);

        var result = await RunNodeAsync([
            "tools/bifrost-bridge.mjs",
            "github-pr-comment",
            "--repo-root", RepoRoot,
            "--identity", "nibu",
            "--pr", "1",
            "--content", "test comment",
            "--target-repository-full-name", "GameCult/Bifrost",
            "--gh-executable", "node",
            "--gh-exec-args", fakeGhPath,
            "--allow-ungated-github", "true",
        ]);

        Assert.Equal(0, result.ExitCode);
        using var payload = JsonDocument.Parse(result.Stdout);
        Assert.Equal("github-pr-comment", payload.RootElement.GetProperty("action").GetString());
        Assert.Equal("https://github.com/GameCult/Bifrost/pull/1#issuecomment-123456", payload.RootElement.GetProperty("receiptUrl").GetString());
        Assert.Equal("123456", payload.RootElement.GetProperty("externalReceiptId").GetString());
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
    public async Task Agent_transport_apply_snapshot_fails_closed_without_bifrost_receipt_gate()
    {
        var sourceStorePath = Path.Combine(Path.GetTempPath(), $"bifrost-agent-transport-source-{Guid.NewGuid():N}.cc");
        var targetStorePath = Path.Combine(Path.GetTempPath(), $"bifrost-agent-transport-target-{Guid.NewGuid():N}.cc");
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"bifrost-agent-transport-{Guid.NewGuid():N}.msgpack");

        await RunNodeAsync([
            "tools/agent-transport.mjs",
            "enqueue",
            "--repo", "Bifrost",
            "--agent", "nibu",
            "--title", "Snapshot gate seed",
            "--request", "Do the thing.",
            "--store", sourceStorePath,
            "--allow-unmirrored", "true",
            "--allow-unreceipted-activity", "true",
        ]);

        await RunNodeAsync([
            "tools/agent-transport.mjs",
            "snapshot",
            "--store", sourceStorePath,
            "--out", snapshotPath,
        ]);

        var result = await RunNodeAsync([
            "tools/agent-transport.mjs",
            "apply-snapshot",
            "--store", targetStorePath,
            "--in", snapshotPath,
        ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("BIFROST_BRIDGE_BASE_URL and BIFROST_BRIDGE_TOKEN", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_transport_apply_snapshot_allows_explicit_operator_recovery()
    {
        var sourceStorePath = Path.Combine(Path.GetTempPath(), $"bifrost-agent-transport-source-{Guid.NewGuid():N}.cc");
        var targetStorePath = Path.Combine(Path.GetTempPath(), $"bifrost-agent-transport-target-{Guid.NewGuid():N}.cc");
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"bifrost-agent-transport-{Guid.NewGuid():N}.msgpack");

        await RunNodeAsync([
            "tools/agent-transport.mjs",
            "enqueue",
            "--repo", "Bifrost",
            "--agent", "nibu",
            "--title", "Snapshot recovery seed",
            "--request", "Do the thing.",
            "--store", sourceStorePath,
            "--allow-unmirrored", "true",
            "--allow-unreceipted-activity", "true",
        ]);

        await RunNodeAsync([
            "tools/agent-transport.mjs",
            "snapshot",
            "--store", sourceStorePath,
            "--out", snapshotPath,
        ]);

        var result = await RunNodeAsync([
            "tools/agent-transport.mjs",
            "apply-snapshot",
            "--store", targetStorePath,
            "--in", snapshotPath,
            "--allow-unreceipted-activity", "true",
        ]);

        Assert.Equal(0, result.ExitCode);

        var listed = await RunNodeAsync([
            "tools/agent-transport.mjs",
            "list",
            "--store", targetStorePath,
        ]);

        Assert.Equal(0, listed.ExitCode);
        using var payload = JsonDocument.Parse(listed.Stdout);
        Assert.Equal(1, payload.RootElement.GetArrayLength());
        Assert.Equal("Snapshot recovery seed", payload.RootElement[0].GetProperty("title").GetString());
        Assert.Equal("queued", payload.RootElement[0].GetProperty("status").GetString());
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

    [Fact]
    public async Task Dispatched_turn_git_hook_blocks_raw_git_push()
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), $"bifrost-remote-{Guid.NewGuid():N}.git");
        var workDir = Path.Combine(Path.GetTempPath(), $"bifrost-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            await RunGitAsync(["init", "--bare", remoteDir], RepoRoot);
            await RunGitAsync(["init", "-b", "main"], workDir);
            await RunGitAsync(["config", "user.name", "Bifrost Tests"], workDir);
            await RunGitAsync(["config", "user.email", "bifrost-tests@example.invalid"], workDir);
            await File.WriteAllTextAsync(Path.Combine(workDir, "README.md"), "# Gate Test\n", Encoding.UTF8);
            await RunGitAsync(["add", "README.md"], workDir);
            await RunGitAsync(["commit", "-m", "Initial commit"], workDir);
            await RunGitAsync(["remote", "add", "origin", remoteDir], workDir);

            var blocked = await RunCommandAsync(
                "powershell",
                ["-NoProfile", "-Command", "git push -u origin main"],
                workDir,
                BuildDispatchedGitHookEnv());

            Assert.NotEqual(0, blocked.ExitCode);
            Assert.Contains("Bifrost blocked git push", blocked.Stderr, StringComparison.OrdinalIgnoreCase);

            var blockedNoVerify = await RunCommandAsync(
                "powershell",
                ["-NoProfile", "-Command", "git push --no-verify -u origin main"],
                workDir,
                BuildDispatchedGitGateEnv());

            Assert.NotEqual(0, blockedNoVerify.ExitCode);
            Assert.Contains("Bifrost blocked git push", blockedNoVerify.Stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfPresent(workDir);
            DeleteDirectoryIfPresent(remoteDir);
        }
    }

    [Fact]
    public async Task Bridge_owned_github_draft_pr_authorizes_push_under_dispatch_gate()
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), $"bifrost-remote-{Guid.NewGuid():N}.git");
        var workDir = Path.Combine(Path.GetTempPath(), $"bifrost-work-{Guid.NewGuid():N}");
        var fakeToolsDir = Path.Combine(Path.GetTempPath(), $"bifrost-gh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(fakeToolsDir);

        try
        {
            await RunGitAsync(["init", "--bare", remoteDir], RepoRoot);
            await RunGitAsync(["init", "-b", "main"], workDir);
            await RunGitAsync(["config", "user.name", "Bifrost Tests"], workDir);
            await RunGitAsync(["config", "user.email", "bifrost-tests@example.invalid"], workDir);
            await File.WriteAllTextAsync(Path.Combine(workDir, "README.md"), "# Bridge Gate Test\n", Encoding.UTF8);
            await RunGitAsync(["add", "README.md"], workDir);
            await RunGitAsync(["commit", "-m", "Initial commit"], workDir);
            await RunGitAsync(["remote", "add", "origin", remoteDir], workDir);
            await RunGitAsync(["push", "-u", "origin", "main"], workDir);

            var fakeGhPath = Path.Combine(fakeToolsDir, "fake-gh.js");
            await File.WriteAllTextAsync(fakeGhPath, """
console.log("https://github.com/GameCult/Bifrost/pull/999");
""", Encoding.UTF8);

            var environment = BuildDispatchedGitGateEnv();
            environment["BIFROST_ALLOW_UNGATED_GITHUB"] = "true";
            environment["BIFROST_REAL_GH"] = ResolveExecutable("node");
            environment["BIFROST_REAL_GH_ARGS"] = fakeGhPath;

            var result = await RunNodeAsync([
                "tools/bifrost-bridge.mjs",
                "github-draft-pr",
                "--repo-root", workDir,
                "--identity", "nibu",
                "--title", "Bridge gate test",
                "--path", "docs/receipt.md",
                "--content", "Bridge-owned push.",
                "--body", "Bifrost bridge test PR.",
                "--base", "main",
            ], environment);

            Assert.Equal(0, result.ExitCode);
            using var payload = JsonDocument.Parse(result.Stdout);
            Assert.Equal("github-draft-pr", payload.RootElement.GetProperty("action").GetString());
            Assert.True(payload.RootElement.GetProperty("pushed").GetBoolean());
            Assert.Equal("https://github.com/GameCult/Bifrost/pull/999", payload.RootElement.GetProperty("prUrl").GetString());

            var heads = await RunGitAsync(["ls-remote", "--heads", "origin"], workDir);
            Assert.Contains("refs/heads/bifrost/nibu/bridge-gate-test", heads.Stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfPresent(workDir);
            DeleteDirectoryIfPresent(remoteDir);
            DeleteDirectoryIfPresent(fakeToolsDir);
        }
    }

    [Fact]
    public async Task Dispatched_turn_github_cli_mutation_is_blocked_without_bridge_authorization()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bifrost-gh-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var fakeGhPath = Path.Combine(tempDir, "fake-gh.cmd");
            await File.WriteAllTextAsync(fakeGhPath, """
@echo off
echo should-not-run
exit /b 0
""", Encoding.UTF8);

            var environment = BuildDispatchedGitGateEnv();
            environment["BIFROST_REAL_GH"] = fakeGhPath;

            var blocked = await RunCommandAsync(
                "powershell",
                ["-NoProfile", "-Command", "gh pr comment 42 --body blocked"],
                RepoRoot,
                environment);

            Assert.NotEqual(0, blocked.ExitCode);
            Assert.Contains("Bifrost blocked GitHub CLI mutation", blocked.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("should-not-run", blocked.Stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempDir);
        }
    }

    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static async Task<ProcessResult> RunNodeAsync(string[] args, IReadOnlyDictionary<string, string?>? environmentOverrides = null)
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
        if (environmentOverrides is not null)
        {
            foreach (var pair in environmentOverrides)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<ProcessResult> RunGitAsync(string[] args, string workingDirectory, IReadOnlyDictionary<string, string?>? environmentOverrides = null)
        => await RunCommandAsync("git", args, workingDirectory, environmentOverrides);

    private static async Task<ProcessResult> RunCommandAsync(
        string fileName,
        string[] args,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (environmentOverrides is not null)
        {
            foreach (var pair in environmentOverrides)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static Dictionary<string, string?> BuildDispatchedGitHookEnv()
    {
        var hooksPath = Path.Combine(RepoRoot, "tools", "git-hooks");
        var gatePath = Path.Combine(RepoRoot, "tools", "git-gate");
        var path = Environment.GetEnvironmentVariable("Path")
            ?? Environment.GetEnvironmentVariable("PATH")
            ?? string.Empty;
        var nextPath = string.IsNullOrWhiteSpace(path)
            ? gatePath
            : $"{gatePath};{path}";
        return new Dictionary<string, string?>
        {
            ["BIFROST_ENFORCE_GITHUB_GATE"] = "true",
            ["BIFROST_GIT_EXECUTABLE"] = Path.Combine(gatePath, "git.cmd"),
            ["BIFROST_GH_EXECUTABLE"] = Path.Combine(gatePath, "gh.cmd"),
            ["BIFROST_NODE_EXECUTABLE"] = ResolveExecutable("node"),
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.hooksPath",
            ["GIT_CONFIG_VALUE_0"] = hooksPath,
            ["PATH"] = nextPath,
            ["Path"] = nextPath,
        };
    }

    private static Dictionary<string, string?> BuildDispatchedGitGateEnv() => BuildDispatchedGitHookEnv();

    private static string ResolveExecutable(string command)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                WorkingDirectory = RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        process.StartInfo.ArgumentList.Add(command);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not resolve {command} for tests: {stderr}{stdout}");
        }

        var path = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Could not resolve {command} for tests.");
        }

        return path;
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
