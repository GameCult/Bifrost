using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bifrost.Web.Configuration;

namespace Bifrost.Web.Features.Heimdall;

/// <summary>One login a Heimdall account holds, as the Linked accounts page shows it. No provider user id, scope or email.</summary>
public sealed record LinkedIdentitySummary(string Id, string Provider, string? DisplayName, DateTimeOffset? LinkedAt);

public enum HeimdallMemberCall
{
    Ok,
    SignInRequired,
    StepUpRequired,
    IdentityNotFound,
    LastIdentity,
    Error
}

public sealed record HeimdallMemberResult(HeimdallMemberCall Call, IReadOnlyList<LinkedIdentitySummary>? Identities);

/// <summary>
/// Heimdall's member routes (docs/service-contract.md: GET /v1/me/identities, POST /v1/me/identities/{id}/unlink). Each
/// takes the member's bearer as an argument, uses it for that one request and keeps nothing. Its HttpClient carries no
/// app secret: these routes act only for a member who is present, so a Bifrost fault cannot reach them without one.
/// </summary>
public sealed class HeimdallMemberClient(HttpClient httpClient)
{
    private const int MaxIdentities = 32;
    private const int MaxDisplayName = 80;

    public static void ConfigureClient(HttpClient client, HeimdallOptions options) =>
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));

    public Task<HeimdallMemberResult> ListAsync(string bearer, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/v1/me/identities", bearer, cancellationToken);

    public Task<HeimdallMemberResult> UnlinkAsync(string bearer, string identityId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"/v1/me/identities/{Uri.EscapeDataString(identityId)}/unlink", bearer, cancellationToken);

    private async Task<HeimdallMemberResult> SendAsync(HttpMethod method, string path, string bearer, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(method, path);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return new HeimdallMemberResult(HeimdallMemberCall.Ok, await ReadIdentitiesAsync(response, cancellationToken));
            }

            var code = await HeimdallRefusal.CodeAsync(response, cancellationToken);
            var call = (response.StatusCode, code) switch
            {
                (HttpStatusCode.Unauthorized, _) => HeimdallMemberCall.SignInRequired,
                (HttpStatusCode.Forbidden, "step_up_required") => HeimdallMemberCall.StepUpRequired,
                (HttpStatusCode.NotFound, "identity_not_found") => HeimdallMemberCall.IdentityNotFound,
                (HttpStatusCode.Conflict, "last_identity") => HeimdallMemberCall.LastIdentity,
                _ => HeimdallMemberCall.Error
            };
            return new HeimdallMemberResult(call, null);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException
                                      && !cancellationToken.IsCancellationRequested)
        {
            return new HeimdallMemberResult(HeimdallMemberCall.Error, null);
        }
    }

    /// <summary>The identities array of a 200 answer, keeping only the fields the page shows; a malformed entry is dropped.</summary>
    private static async Task<IReadOnlyList<LinkedIdentitySummary>> ReadIdentitiesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var list = new List<LinkedIdentitySummary>();
        if (body.RootElement.ValueKind != JsonValueKind.Object
            || !body.RootElement.TryGetProperty("identities", out var identities)
            || identities.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in identities.EnumerateArray().Take(MaxIdentities))
        {
            var id = Text(item, "id");
            var provider = Text(item, "provider");
            if (!Guid.TryParse(id, out _)
                || provider is not { Length: > 0 and <= 32 }
                || !provider.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                continue;
            }

            var name = Text(item, "displayName") ?? Text(item, "username");
            if (name is { Length: > MaxDisplayName })
            {
                name = name[..MaxDisplayName];
            }

            DateTimeOffset? linkedAt = DateTimeOffset.TryParse(Text(item, "linkedAt"), out var parsed) ? parsed : null;
            list.Add(new LinkedIdentitySummary(id!, provider, name, linkedAt));
        }

        return list;
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
