using System.Text;
using Bifrost.Web.Configuration;

namespace Bifrost.Web.Features.Desk;

// The {mind} segment of a desk route (fork-card.md M1). The raw segment is percent-decoded once and compared,
// ordinally, with the configured ids: no case folding, trimming or normalisation, and a decoding failure is a miss.
// The segment is never joined into a path, a socket name or a query, so a value off the list opens nothing.
public sealed class DeskMindRoutes
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Dictionary<string, DeskMindOptions> byId;

    public DeskMindRoutes(IEnumerable<DeskMindOptions> minds)
    {
        byId = new Dictionary<string, DeskMindOptions>(StringComparer.Ordinal);
        foreach (var mind in minds)
        {
            byId.Add(mind.Id, mind);
        }
    }

    public DeskMindOptions? Resolve(string rawSegment) =>
        TryDecode(rawSegment, out var id) && byId.TryGetValue(id, out var mind) ? mind : null;

    // One pass of percent-decoding. Uri.UnescapeDataString is not used: it leaves a bad escape in place and turns
    // bytes that are not UTF-8 into U+FFFD instead of failing.
    private static bool TryDecode(string raw, out string decoded)
    {
        decoded = string.Empty;
        var bytes = new List<byte>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '%')
            {
                if (i + 2 >= raw.Length)
                {
                    return false;
                }

                if (!TryHex(raw[i + 1], out var high) || !TryHex(raw[i + 2], out var low))
                {
                    return false;
                }

                bytes.Add((byte)((high << 4) | low));
                i += 2;
            }
            else if (c > 0x7f)
            {
                // A raw request target is ASCII; anything else is not a segment this desk issues.
                return false;
            }
            else
            {
                bytes.Add((byte)c);
            }
        }

        try
        {
            decoded = StrictUtf8.GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryHex(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return value >= 0;
    }
}
