using System.Text.RegularExpressions;

namespace Bifrost.Web.Features.ConceptVotes;

// The one grammar for a concept collection or key. \z, not $: $ admits a trailing line feed.
public static class ConceptKey
{
    public const int MaxLength = 80;

    private static readonly Regex Shape = new(@"^[a-z0-9-]{1,80}\z", RegexOptions.CultureInvariant);

    public static bool IsValid(string? value) => value is not null && Shape.IsMatch(value);
}
