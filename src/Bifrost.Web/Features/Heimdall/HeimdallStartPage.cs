using System.Net;

namespace Bifrost.Web.Features.Heimdall;

/// <summary>
/// What a navigation to <c>GET /auth/heimdall/{provider}</c> gets when starting would do harm: the browser holds a
/// signed-in session (a sign-in would replace it), or both attempt slots hold a live attempt (a start would overwrite
/// one). Nothing is started. The visitor confirms with a form that posts to the same path with an antiforgery token,
/// which a page on another site can neither read nor send. Every field is encoded; the provider is one the route offers.
/// </summary>
public static class HeimdallStartPage
{
    public static string Render(string provider, string returnPath, bool memberAccess, string antiforgeryField, string antiforgeryToken, bool signedIn)
    {
        var action = "/auth/heimdall/" + Uri.EscapeDataString(provider);
        var why = signedIn
            ? "You are signed in. Continuing starts a new sign-in and replaces this session."
            : "A sign-in or account step is already in progress in this browser. Continuing starts another one and may cancel it.";
        return $$"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <meta name="referrer" content="no-referrer" />
                <title>Continue to sign in - Bifrost</title>
              </head>
              <body>
                <main>
                  <h1>Continue to {{WebUtility.HtmlEncode(provider)}}?</h1>
                  <p>{{why}}</p>
                  <form method="post" action="{{WebUtility.HtmlEncode(action)}}">
                    <input type="hidden" name="{{WebUtility.HtmlEncode(antiforgeryField)}}" value="{{WebUtility.HtmlEncode(antiforgeryToken)}}" />
                    <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnPath)}}" />
                    <input type="hidden" name="access" value="{{(memberAccess ? "member" : "")}}" />
                    <button type="submit">Continue</button>
                  </form>
                  <p><a href="/">Cancel</a></p>
                </main>
              </body>
            </html>
            """;
    }
}
