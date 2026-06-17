using System.Diagnostics;
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

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
