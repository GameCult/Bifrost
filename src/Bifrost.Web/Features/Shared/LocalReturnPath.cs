namespace Bifrost.Web.Features.Shared;

/// <summary>
/// A sign-in return target that stays on this site. The only way to make one is <see cref="Parse"/>, which
/// accepts a path of at most <see cref="MaxLength"/> characters that starts with a single slash and holds no
/// control character, and otherwise yields <see cref="Default"/>. Callers parse once at entry and carry the typed value from there.
/// </summary>
public sealed record LocalReturnPath
{
    private LocalReturnPath(string value) => Value = value;

    /// <summary>Bounds the path so the attempt cookie that carries it stays far below a browser's 4096-byte limit.</summary>
    public const int MaxLength = 256;

    public static LocalReturnPath Default { get; } = new("/App");

    public string Value { get; }

    public static LocalReturnPath Parse(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) ||
            candidate.Length > MaxLength ||
            candidate[0] != '/' ||
            candidate.Any(char.IsControl) ||
            (candidate.Length > 1 && (candidate[1] == '/' || candidate[1] == '\\')))
        {
            return Default;
        }

        return new LocalReturnPath(candidate);
    }

    public override string ToString() => Value;
}
