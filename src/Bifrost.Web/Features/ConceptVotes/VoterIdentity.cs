using System.Text.RegularExpressions;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;

namespace Bifrost.Web.Features.ConceptVotes;

public sealed record VoterIdentity(string Key, VoterClass Class, Guid? UserAccountId)
{
    public const int MaxKeyLength = 120;

    private static readonly Regex DeviceIdShape = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);

    // The one place a voter key and class are decided. Never reads vote weight.
    public static VoterIdentity? For(CurrentBifrostActor actor, string? deviceId)
    {
        if (actor.IsAuthenticated && actor.UserAccount is { } account)
        {
            return new VoterIdentity(
                "account:" + account.Id.ToString("N"),
                actor.IsActiveMember ? VoterClass.Member : VoterClass.Account,
                account.Id);
        }

        return deviceId is not null && DeviceIdShape.IsMatch(deviceId)
            ? new VoterIdentity("device:" + deviceId, VoterClass.Anonymous, null)
            : null;
    }
}
