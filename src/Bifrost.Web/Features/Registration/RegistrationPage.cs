using System.Net;
using System.Text;

namespace Bifrost.Web.Features.Registration;

/// <summary>What the sign-up page shows. Everything user-supplied is HTML-encoded on the way out.</summary>
public sealed record RegistrationPageModel(
    string ReturnPath,
    string Challenge,
    int Difficulty,
    int WaitSeconds,
    string Identity = "",
    string DisplayName = "",
    string? Error = null,
    RegistrationField ErrorField = RegistrationField.None);

/// <summary>
/// The server-rendered sign-up form. It works with no script (time-lock path); the inline script, when it can, replaces the
/// wait by solving the proof of work. No external asset, no cookie, no third party.
/// </summary>
public static class RegistrationPage
{
    // Hash rule is RegistrationChallengeService's: SHA-256 of challenge + ":" + nonce, leading zero bits >= difficulty.
    // Hashes run in batches that yield to the UI between them. If the browser has no crypto.subtle, nothing here runs
    // and the form submits on the time-lock path.
    public const string Script = """
        (function () {
          var form = document.getElementById("register");
          var status = document.getElementById("status");
          if (!form || !status || !window.crypto || !window.crypto.subtle || !window.TextEncoder) { return; }
          var BATCH = 256;
          var busy = false;
          var solved = false;
          var skipped = false;

          function zeroBits(bytes) {
            var bits = 0;
            for (var i = 0; i < bytes.length; i++) {
              if (bytes[i] === 0) { bits += 8; continue; }
              return bits + Math.clz32(bytes[i]) - 24;
            }
            return bits;
          }

          async function solve(challenge, difficulty) {
            var encoder = new TextEncoder();
            for (var start = 0; !skipped; start += BATCH) {
              var tries = [];
              for (var n = start; n < start + BATCH; n++) {
                tries.push(crypto.subtle.digest("SHA-256", encoder.encode(challenge + ":" + n)));
              }
              var hashes = await Promise.all(tries);
              for (var i = 0; i < hashes.length; i++) {
                if (zeroBits(new Uint8Array(hashes[i])) >= difficulty) { return String(start + i); }
              }
              await new Promise(function (resolve) { setTimeout(resolve, 0); });
            }
            return null;
          }

          form.addEventListener("submit", function (event) {
            if (event.submitter && event.submitter.id === "skip") {
              // The way out of a slow solve: stop it and let this submit go on the time-lock path.
              skipped = true;
              solved = true;
              form.elements.nonce.value = "";
              form.elements.waited.disabled = false;
              status.textContent = "Skipping the automatic check.";
              return;
            }
            if (solved) { return; }
            event.preventDefault();
            if (busy) { return; }
            busy = true;
            status.textContent = "Checking. This can take longer on a slow device; press Skip the automatic check to wait " + form.getAttribute("data-wait") + " seconds instead.";
            solve(form.elements.challenge.value, parseInt(form.getAttribute("data-difficulty"), 10)).then(function (nonce) {
              if (skipped || nonce === null) { return; }
              form.elements.nonce.value = nonce;
              form.elements.waited.disabled = true;
              solved = true;
              status.textContent = "Creating your account";
              form.submit();
            }, function () {
              solved = true;
              busy = false;
              status.textContent = "The automatic check did not run in this browser. Press Skip the automatic check and wait as described below.";
            });
          });
        })();
        """;

    public static string Render(RegistrationPageModel model)
    {
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var notice = TrainingNotice.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var summary = new StringBuilder();
        if (model.Error is not null)
        {
            var link = model.ErrorField switch
            {
                RegistrationField.Identity => """<ul><li><a href="#identity">Identity name</a></li></ul>""",
                RegistrationField.Consent => """<ul><li><a href="#consent">I agree to this notice (optional)</a></li></ul>""",
                _ => ""
            };
            summary.Append($"""<div id="errors" role="alert" tabindex="-1"><strong>Registration did not go through.</strong><p>{E(model.Error)}</p>{link}</div>""");
        }

        string Invalid(RegistrationField field) => model.Error is not null && model.ErrorField == field ? " aria-invalid=\"true\" aria-describedby=\"errors\"" : "";
        var wait = model.WaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return $$"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <title>Create an account - Bifrost</title>
                <style>
                  body { margin: 0; min-height: 100svh; display: grid; place-items: center; background: #090908; color: #fff8ef; font-family: system-ui, sans-serif; line-height: 1.5; }
                  main { width: min(92vw, 600px); margin: 24px 0; padding: 28px; border: 1px solid rgba(255,248,239,.16); border-radius: 8px; background: #141110; }
                  h1 { margin: 0 0 10px; font-size: 2rem; line-height: 1.1; }
                  p { color: #c7b8ad; }
                  fieldset { margin: 20px 0 0; padding: 14px 16px; border: 1px solid #8a7d73; border-radius: 6px; }
                  legend { padding: 0 6px; color: #d9ad66; font-weight: 750; }
                  label { display: block; margin-top: 12px; font-weight: 650; }
                  .hint { margin: 2px 0 0; font-size: .9rem; }
                  input[type=text] { box-sizing: border-box; width: 100%; min-height: 44px; margin-top: 4px; padding: 0 10px; border: 1px solid #8a7d73; border-radius: 6px; background: #0d0b0a; color: #fff8ef; font: inherit; }
                  input[type=checkbox] { width: 22px; height: 22px; margin: 0 8px 0 0; vertical-align: middle; accent-color: #c43e4f; }
                  label.check { display: flex; align-items: center; margin-top: 14px; }
                  button.secondary { margin-left: 10px; background: #2a2522; border: 1px solid #8a7d73; }
                  button { margin-top: 20px; min-height: 46px; padding: 0 18px; border: 0; border-radius: 6px; background: #c43e4f; color: #fff; font: inherit; font-weight: 750; cursor: pointer; }
                  :focus-visible { outline: 3px solid #ffd27a; outline-offset: 2px; }
                  #errors { margin-top: 18px; padding: 10px 14px; border: 2px solid #ff9aa6; border-radius: 6px; color: #ffc2c9; }
                  #errors p { margin: 4px 0 0; color: #ffc2c9; }
                  [aria-invalid=true] { border-color: #ff9aa6; }
                  #status { min-height: 1.5em; color: #fff8ef; }
                  small { color: #d9ad66; }
                </style>
              </head>
              <body>
                <main>
                  <small>Public registration</small>
                  <h1>Create a Bifrost account</h1>
                  <p>A new account can chat. It has no vote and opens no member or governance surface.</p>
                  {{summary}}
                  <form id="register" method="post" action="/auth/bifrost/register/form" data-difficulty="{{model.Difficulty}}" data-wait="{{wait}}">
                    <fieldset>
                      <legend>Your account</legend>
                      <label for="identity">Identity name</label>
                      <input type="text" id="identity" name="identity" value="{{E(model.Identity)}}" required autocomplete="username" autocapitalize="none" spellcheck="false"{{Invalid(RegistrationField.Identity)}} />
                      <label for="displayName">Display name (optional)</label>
                      <input type="text" id="displayName" name="displayName" value="{{E(model.DisplayName)}}" autocomplete="nickname" />
                    </fieldset>
                    <fieldset>
                      <legend>Training notice, version {{notice}}</legend>
                      {{TrainingNotice.Html("notice")}}
                      <label class="check" for="consent"><input type="checkbox" id="consent" name="consent" value="yes" aria-describedby="notice"{{Invalid(RegistrationField.Consent)}} />I agree to this notice (optional)</label>
                      <p class="hint">Optional. You can create an account without agreeing; then none of your messages are used. You can agree later on your consent page.</p>
                      <input type="hidden" name="acceptedNoticeVersion" value="{{notice}}" />
                    </fieldset>
                    <input type="hidden" name="challenge" value="{{E(model.Challenge)}}" />
                    <input type="hidden" name="returnUrl" value="{{E(model.ReturnPath)}}" />
                    <input type="hidden" name="nonce" value="" />
                    <input type="hidden" name="waited" value="true" />
                    <p id="status" aria-live="polite"></p>
                    <button type="submit" id="create">Create account</button>
                    <button type="submit" id="skip" class="secondary">Skip the automatic check and wait {{wait}} seconds instead</button>
                  </form>
                  <p class="hint" id="wait-note">Without a script, or if the automatic check is slow, wait {{wait}} seconds after the page opened and press the button to skip it (or Create account when there is no script). Pressed sooner, the page comes back saying how many seconds remain and keeps your names; the consent box comes back unchecked, so tick it again if you want to agree. Without the automatic check only a few registrations per hour are accepted from one address.</p>
                  <p><small>After registering, Bifrost returns you to {{E(model.ReturnPath)}}.</small></p>
                </main>
                <script>{{Script}}</script>
              </body>
            </html>
            """;
    }
}
