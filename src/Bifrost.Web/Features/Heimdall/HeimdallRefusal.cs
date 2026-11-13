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
    public static async Task<string?> CodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
            if (body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { Length: > 0 and <= 64 } code
                && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return code;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }
}
