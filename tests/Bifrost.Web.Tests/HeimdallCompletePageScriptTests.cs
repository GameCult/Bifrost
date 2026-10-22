using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

/// <summary>
/// Runs the complete page's inline script, as the server serves it, under Node against a simulated browser
/// (Fixtures/complete-page-harness.cjs). Node is required: a missing Node fails these tests, it does not skip them.
/// </summary>
public sealed class HeimdallCompletePageScriptTests
{
    private const string ErrorTarget = "replace:/?auth=heimdall-error";

    private sealed record Run(string Name, string[] Events, Submit? Submit);

    private sealed record Submit(string Attempt, string Code, string Href, string Hash);

    private static async Task<Dictionary<string, Run>> RunScenariosAsync()
    {
        using var host = new HeimdallHost();
        using var client = host.NewClient();
        var page = await client.GetStringAsync("/auth/heimdall/complete");
        var pagePath = Path.Combine(Path.GetTempPath(), $"bifrost-complete-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(pagePath, page);
        try
        {
            var harness = Path.Combine(AppContext.BaseDirectory, "Fixtures", "complete-page-harness.cjs");
            var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(harness);
            start.ArgumentList.Add(pagePath);
            Process process;
            try
            {
                process = Process.Start(start)!;
            }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException("Node must be on PATH to run the complete page's script: " + error.Message, error);
            }

            using (process)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await errors);
                var runs = JsonSerializer.Deserialize<Run[]>(await output, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return runs.ToDictionary(x => x.Name);
            }
        }
        finally
        {
            File.Delete(pagePath);
        }
    }

    [Fact]
    public async Task The_fragment_is_dropped_from_the_address_before_the_code_is_posted()
    {
        var runs = await RunScenariosAsync();

        foreach (var name in new[] { "success", "success-with-decoy-query-code" })
        {
            var run = runs[name];
            Assert.Equal(2, run.Events.Length);
            Assert.StartsWith("replaceState:/auth/heimdall/complete?attempt=NONCE", run.Events[0]);
            Assert.DoesNotContain("#", run.Events[0]);
            Assert.DoesNotContain("CODE123", run.Events[0]);
            Assert.Equal("submit", run.Events[1]);
            Assert.Equal("", run.Submit!.Hash);
            Assert.DoesNotContain("CODE123", run.Submit.Href);
            Assert.DoesNotContain("#", run.Submit.Href);
        }
    }

    [Fact]
    public async Task The_code_is_read_only_from_the_fragment()
    {
        var runs = await RunScenariosAsync();

        Assert.Equal(new Submit("NONCE", "CODE123", "/auth/heimdall/complete?attempt=NONCE", ""), runs["success"].Submit);
        Assert.Equal("CODE123", runs["success-with-decoy-query-code"].Submit!.Code);

        var queryOnly = runs["code-in-query-only"];
        Assert.Null(queryOnly.Submit);
        Assert.DoesNotContain("submit", queryOnly.Events);
        Assert.Equal(ErrorTarget, queryOnly.Events[^1]);
    }

    [Theory]
    [InlineData("error-status-with-code")]
    [InlineData("no-code")]
    [InlineData("no-attempt")]
    public async Task A_failed_or_incomplete_handoff_never_submits_and_leaves_through_the_error_redirect(string scenario)
    {
        var run = (await RunScenariosAsync())[scenario];

        Assert.Null(run.Submit);
        Assert.DoesNotContain("submit", run.Events);
        Assert.Equal(ErrorTarget, run.Events[^1]);
        Assert.Contains(run.Events, x => x.StartsWith("replaceState:", StringComparison.Ordinal) && !x.Contains('#'));
        Assert.DoesNotContain("CODE123", string.Join("\n", run.Events));
    }
}
