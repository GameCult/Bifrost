using System.Text.RegularExpressions;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests.Support;

/// <summary>The merge status page as a browser uses it: a GET with the session, a POST with the antiforgery pair the page itself handed out.</summary>
internal static class MergePage
{
    public const string Url = "/Membership/MergeStatus";

    public const string AccountsUrl = "/Membership/MergeAccounts";

    public sealed record Antiforgery(string Token, string Cookie);

    public static async Task<(HttpResponseMessage Response, string Html)> GetAsync(HeimdallHost host, string session, string query = "", string url = Url)
    {
        using var client = host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url + query);
        request.Headers.Add("Cookie", $"{BifrostCookies.Session}={session}");
        var response = await client.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The antiforgery pair from a page of this member that carries a form.</summary>
    public static async Task<Antiforgery> TokenAsync(HeimdallHost host, string session, string url = Url)
    {
        var (response, html) = await GetAsync(host, session, "", url);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return new Antiforgery(token, HeimdallHost.CookieValue(response, BifrostCookies.Antiforgery)!);
    }

    public static async Task<HttpResponseMessage> PostAsync(
        HeimdallHost host, string session, Antiforgery antiforgery, string handler, IDictionary<string, string> form, string url = Url)
    {
        using var client = host.NewClient();
        var fields = new Dictionary<string, string>(form) { ["__RequestVerificationToken"] = antiforgery.Token };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}?handler={handler}") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("Cookie", $"{BifrostCookies.Session}={session}; {BifrostCookies.Antiforgery}={antiforgery.Cookie}");
        return await client.SendAsync(request);
    }

    public static string? Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString;
}
