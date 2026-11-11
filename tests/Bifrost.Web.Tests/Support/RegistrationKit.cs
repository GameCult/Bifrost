using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Features.Registration;

namespace Bifrost.Web.Tests.Support;

/// <summary>Registers through the real gate: fetch a challenge, solve it, post with the notice version.</summary>
public static class RegistrationKit
{
    public static async Task<(string Challenge, string Nonce)> SolveAsync(HttpClient client)
    {
        var issued = await client.GetFromJsonAsync<JsonElement>("/auth/bifrost/challenge");
        var challenge = issued.GetProperty("challenge").GetString()!;
        return (challenge, Solve(challenge, issued.GetProperty("difficulty").GetInt32()));
    }

    public static string Solve(string challenge, int difficulty)
    {
        for (long nonce = 0; ; nonce++)
        {
            var text = nonce.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (RegistrationChallengeService.LeadingZeroBits(SHA256.HashData(Encoding.UTF8.GetBytes(challenge + ":" + text))) >= difficulty)
            {
                return text;
            }
        }
    }

    public static async Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string identity,
        string? displayName = null,
        string? returnUrl = null,
        bool anonymous = false,
        bool agree = true)
    {
        var (challenge, nonce) = await SolveAsync(client);
        return await PostAsync(client, new
        {
            identity,
            displayName = displayName ?? identity,
            returnUrl,
            challenge,
            nonce,
            acceptedNoticeVersion = agree ? TrainingNotice.Version : (int?)null
        }, anonymous);
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body, bool anonymous = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register")
        {
            Content = JsonContent.Create(body)
        };
        if (anonymous)
        {
            request.Headers.Add("X-Test-Anonymous", "true");
        }

        return await client.SendAsync(request);
    }
}
