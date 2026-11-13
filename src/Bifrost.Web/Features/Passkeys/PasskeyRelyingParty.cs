using Bifrost.Web.Configuration;
using Fido2NetLib;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Passkeys;

/// <summary>
/// The relying party every passkey ceremony runs as. RP ID and origin come from <see cref="BifrostHostOptions"/> and from
/// nothing the browser sends: the public base URL must be https and its host must be the expected host, otherwise passkeys
/// are unavailable (the page offers none and every ceremony endpoint refuses). A passkey is bound to the RP ID by the
/// authenticator, so a wrong or http origin would enrol keys that cannot be used or can be phished.
/// </summary>
public sealed class PasskeyRelyingParty
{
    public const string Name = "Bifrost";

    public PasskeyRelyingParty(IOptions<BifrostHostOptions> hostOptions, ILogger<PasskeyRelyingParty> logger)
    {
        var host = hostOptions.Value;
        if (host.ExpectedHost.Contains(':'))
        {
            logger.LogWarning("Passkeys are unavailable: Host:ExpectedHost must be a bare host name without a port (it is the RP ID's host); it was written with a port.");
            return;
        }

        if (!Uri.TryCreate(host.PublicBaseUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(host.ExpectedHost)
            || !string.Equals(url.Host, host.ExpectedHost, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Passkeys are unavailable: Host:PublicBaseUrl must be https and its host must equal Host:ExpectedHost.");
            return;
        }

        Origin = url.GetLeftPart(UriPartial.Authority);
        RpId = url.Host.ToLowerInvariant();
        Fido2 = new Fido2(new Fido2Configuration
        {
            RPID = RpId,
            RPName = Name,
            Origins = new HashSet<string>([Origin], StringComparer.Ordinal),
            ChallengeSize = 32,
            Timeout = 120_000
        });
    }

    public bool IsAvailable => Fido2 is not null;

    public string? Origin { get; }

    public string? RpId { get; }

    public IFido2? Fido2 { get; }
}
