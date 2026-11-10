using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Bifrost.Web.Features.ConceptVotes;

// The opaque cursor of the ranked read: the last row's sort value and key, plus the order and filter it was issued
// for, so a cursor cannot be replayed under another order or filter. Text "1|order|u|value|key", base64url without
// padding. Not signed: it names a position in public data and grants nothing. Decoding is strict and canonical (a
// cursor that does not re-encode to the same text is refused), and every field is re-validated, so no cursor text
// reaches a query unparsed.
public sealed record RankCursor(RankOrder Order, bool Unvoted, long Value, string Key)
{
    public const int MaxLength = 200;

    private static readonly Regex Alphabet = new(@"^[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant);

    public string Encode() =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"1|{(int)Order}|{(Unvoted ? 1 : 0)}|{Value}|{Key}")));

    public static bool TryDecode(string? text, out RankCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || !Alphabet.IsMatch(text))
        {
            return false;
        }

        string plain;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(text);
            plain = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }

        var parts = plain.Split('|');
        if (parts.Length != 5 || parts[0] != "1" ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var order) ||
            !Enum.IsDefined((RankOrder)order) ||
            parts[2] is not ("0" or "1") ||
            !long.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
            !ConceptKey.IsValid(parts[4]))
        {
            return false;
        }

        var candidate = new RankCursor((RankOrder)order, parts[2] == "1", value, parts[4]);
        if (candidate.Encode() != text)
        {
            return false;
        }

        cursor = candidate;
        return true;
    }
}
