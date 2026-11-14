using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bifrost.Web.Tests;

/// <summary>
/// Runs the page's first-party passkey script (src/Bifrost.Web/wwwroot/js/passkeys.js) under Node against a simulated browser
/// (Fixtures/passkey-script-harness.cjs) and pins what it posts: the antiforgery header, base64url strings only, and no navigation
/// text that did not come from a closed code. Node is required: a missing Node fails these tests.
/// </summary>
public sealed partial class PasskeyScriptTests
{
    private sealed record Fetch(string Url, string Method, Dictionary<string, string> Headers, string Body, string Credentials);

    private sealed record Call(string Name, Fetch[] Fetches, JsonElement CredentialCall, string[] Navigations, Dictionary<string, string>? Submitted, string Status);

    [GeneratedRegex("^[A-Za-z0-9_-]*$")]
    private static partial Regex Base64Url();

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bifrost.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "Bifrost.Web", "wwwroot", "js", "passkeys.js");
    }

    private static async Task<Dictionary<string, Call>> RunAsync()
    {
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures", "passkey-script-harness.cjs"));
        start.ArgumentList.Add(ScriptPath());
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await errors);
        return JsonSerializer.Deserialize<Call[]>(await output, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.ToDictionary(x => x.Name);
    }

    [Fact]
    public async Task Enrolment_posts_the_antiforgery_header_to_this_page_and_only_base64url_fields_after_the_ceremony()
    {
        var run = (await RunAsync())["enrol"];

        var fetch = Assert.Single(run.Fetches);
        Assert.Equal("/Membership/LinkedAccounts?handler=PasskeyEnrolOptions", fetch.Url);
        Assert.Equal("POST", fetch.Method);
        Assert.Equal("same-origin", fetch.Credentials);
        Assert.Equal("TOKEN", fetch.Headers["RequestVerificationToken"]);
        Assert.Equal("", fetch.Body);
        Assert.True(run.CredentialCall.GetProperty("challengeIsBuffer").GetBoolean());
        Assert.True(run.CredentialCall.GetProperty("listIdsAreBuffers").GetBoolean());
        Assert.Equal([4, 5, 6], run.CredentialCall.GetProperty("userIdBytes").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal("CRED-ID", run.Submitted!["id"]);
        Assert.Equal("-__-", run.Submitted["rawId"]); // 251,255,254 in base64url without padding
        Assert.Matches(Base64Url(), run.Submitted["attestationObject"]);
        Assert.Matches(Base64Url(), run.Submitted["clientDataJson"]);
        Assert.Equal("internal,hybrid", run.Submitted["transports"]);
        Assert.Empty(run.Navigations);
    }

    [Fact]
    public async Task Unlinking_asks_for_a_challenge_for_the_chosen_identity_only_and_posts_the_assertion_as_base64url()
    {
        var run = (await RunAsync())["unlink"];

        var fetch = Assert.Single(run.Fetches);
        Assert.Equal("/Membership/LinkedAccounts?handler=PasskeyChallenge", fetch.Url);
        Assert.Equal("identityId=IDENTITY", fetch.Body);
        Assert.Equal("TOKEN", fetch.Headers["RequestVerificationToken"]);
        Assert.True(run.CredentialCall.GetProperty("listIdsAreBuffers").GetBoolean());
        foreach (var name in new[] { "rawId", "authenticatorData", "clientDataJson", "signature", "userHandle" })
        {
            Assert.Matches(Base64Url(), run.Submitted![name]);
            Assert.NotEmpty(run.Submitted[name]);
        }

        Assert.Equal("IDENTITY", run.Submitted!["identityId"]);
        Assert.Empty(run.Navigations);
    }

    [Theory]
    [InlineData("refused", "?result=enrol_needs_sign_in")]
    [InlineData("hostile-error", "?result=passkey_failed")]
    [InlineData("cancelled", "?result=passkey_failed")]
    public async Task A_refusal_or_a_cancelled_ceremony_submits_nothing_and_navigates_only_to_a_closed_result_code(string scenario, string expected)
    {
        var run = (await RunAsync())[scenario];

        Assert.Null(run.Submitted);
        Assert.Equal(["/Membership/LinkedAccounts" + expected], run.Navigations);
    }
}
