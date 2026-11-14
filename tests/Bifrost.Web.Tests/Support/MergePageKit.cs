using System.Text.RegularExpressions;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests.Support;

/// <summary>The merge status page as a browser uses it: a GET with the session, a POST with the antiforgery pair the page itself handed out.</summary>
internal static class MergePage
{
    public const string Url = "/Membership/MergeStatus";

    public sealed record Antiforgery(string Token, string Cookie);

    public static async Task<(HttpResponseMessage Response, string Html)> GetAsync(HeimdallHost host, string session, string query = "")
    {
        using var client = host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Url + query);
        request.Headers.Add("Cookie", $"{BifrostCookies.Session}={session}");
        var response = await client.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The antiforgery pair from a page of this member that carries a form.</summary>
    public static async Task<Antiforgery> TokenAsync(HeimdallHost host, string session)
    {
        var (response, html) = await GetAsync(host, session);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return new Antiforgery(token, HeimdallHost.CookieValue(response, BifrostCookies.Antiforgery)!);
    }

    public static async Task<HttpResponseMessage> PostAsync(
        HeimdallHost host, string session, Antiforgery antiforgery, string handler, IDictionary<string, string> form)
    {
        using var client = host.NewClient();
        var fields = new Dictionary<string, string>(form) { ["__RequestVerificationToken"] = antiforgery.Token };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}?handler={handler}") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("Cookie", $"{BifrostCookies.Session}={session}; {BifrostCookies.Antiforgery}={antiforgery.Cookie}");
        return await client.SendAsync(request);
    }

    public static string? Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString;
}
