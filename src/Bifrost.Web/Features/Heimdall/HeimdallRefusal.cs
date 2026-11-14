using System.Text.Json;

namespace Bifrost.Web.Features.Heimdall;

/// <summary>Reads the one thing Bifrost takes from a Heimdall refusal body: its error code, and only if it is a safe token.</summary>
public static class HeimdallRefusal
{
    private const int BodyLimit = 4096;

    /// <summary>
    /// The error code in the first 4 KiB of a body when it is a short snake_case token, else null. A body that cannot be
    /// read or parsed yields null: a refusal never becomes a 500, and free text or tokens never reach a log or a page.
    /// </summary>
    public static async Task<string?> CodeAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        (await ReadAsync(response, cancellationToken))?.Code;

    /// <summary>
    /// The error code plus the two qualifiers a move refusal carries (the contract's `proof`, only "from" or "to", and `provider`, a
    /// short snake_case token), each kept only when it is that safe shape. Nothing else of the body is read. Null when no code reads.
    /// </summary>
    public static async Task<HeimdallRefusalBody?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var head = new byte[BodyLimit];
            var length = 0;
            while (length < head.Length && await stream.ReadAsync(head.AsMemory(length), cancellationToken) is > 0 and var read)
            {
                length += read;
            }

            using var body = JsonDocument.Parse(head.AsMemory(0, length));
            if (body.RootElement.ValueKind == JsonValueKind.Object && Token(body.RootElement, "error", 64) is { } code)
            {
                var proof = body.RootElement.TryGetProperty("proof", out var side) && side.ValueKind == JsonValueKind.String ? side.GetString() : null;
                return new HeimdallRefusalBody(code, proof is "from" or "to" ? proof : null, Token(body.RootElement, "provider", 32));
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }

    private static string? Token(JsonElement root, string name, int maxLength) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
        && text.Length <= maxLength
        && text.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            ? text
            : null;
}

/// <summary>A Heimdall refusal's safe fields: the code, the proof side ("from"/"to") and the provider, when present and well formed.</summary>
public sealed record HeimdallRefusalBody(string Code, string? Proof, string? Provider);
