namespace Bifrost.Web.Features.Heimdall;

/// <summary>
/// The page Heimdall's "Return to the app" lands on. The completion code arrives in the URL fragment, which
/// the server never sees, so the page's script reads it, drops it from the address bar, and posts it with
/// the attempt to the same path. The page is static: it never contains a code.
/// </summary>
public static class HeimdallCompletePage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <meta name="referrer" content="no-referrer" />
            <title>Signing in - Bifrost</title>
          </head>
          <body>
            <p>Finishing sign-in...</p>
            <noscript><p>JavaScript is needed to finish sign-in.</p></noscript>
            <form id="complete" method="post" action="/auth/heimdall/complete">
              <input type="hidden" name="attempt" />
              <input type="hidden" name="code" />
            </form>
            <script>
              (function () {
                var fragment = new URLSearchParams(location.hash.slice(1));
                var attempt = new URLSearchParams(location.search).get("attempt");
                var code = fragment.get("heimdall_completion_code");
                var failed = fragment.get("heimdall_status") === "error";
                history.replaceState(null, "", location.pathname + location.search);
                if (failed || !code || !attempt) {
                  location.replace("/?auth=heimdall-error");
                  return;
                }
                var form = document.getElementById("complete");
                form.elements.attempt.value = attempt;
                form.elements.code.value = code;
                form.submit();
              })();
            </script>
          </body>
        </html>
        """;
}
