namespace Bifrost.Web.Features.Shared;

// Every cookie Bifrost sets carries the __Host- prefix: the browser then accepts it only with Secure, Path=/ and no
// Domain, so no sibling subdomain of gamecult.org (the preview host is same-site) can plant or shadow one by setting
// a Domain=gamecult.org cookie of the same name. The prefix forbids a narrower Path, so these all live at Path=/.
public static class BifrostCookies
{
    public const string HostPrefix = "__Host-";

    public const string Session = HostPrefix + "bifrost_session";
    public const string OAuthCorrelation = HostPrefix + "bifrost_oauth_correlation";
    public const string Antiforgery = HostPrefix + "bifrost_antiforgery";
    public const string Voter = HostPrefix + "bifrost_voter";
    public const string HeimdallAttemptPrefix = HostPrefix + "bifrost_heimdall_";
    public const string PasskeyTicket = HostPrefix + "bifrost_passkey_ticket";

    public const string Path = "/";
}
