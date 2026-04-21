using System.Text;
using System.Text.RegularExpressions;

namespace Bifrost.Web.Features.Shared;

public static partial class SlugGenerator
{
    public static string Create(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Guid.NewGuid().ToString("N")[..12];
        }

        var sanitized = rawValue.Trim().ToLowerInvariant();
        sanitized = NonAlphaNumeric().Replace(sanitized, "-");
        sanitized = MultipleHyphens().Replace(sanitized, "-").Trim('-');

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = Convert.ToHexString(Encoding.UTF8.GetBytes(rawValue)).ToLowerInvariant()[..12];
        }

        return sanitized;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumeric();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultipleHyphens();
}
