using System.Net;
using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Registration;

/// <summary>Where an account stands against the notice in force. Exactly one holds.</summary>
public abstract record ConsentState
{
    public sealed record None : ConsentState;

    public sealed record Current(DateTimeOffset AgreedAtUtc) : ConsentState;

    public sealed record Older(int Version, DateTimeOffset AgreedAtUtc) : ConsentState;

    public sealed record Withdrawn(DateTimeOffset WithdrawnAtUtc) : ConsentState;
}

/// <summary>
/// The server-rendered consent page: the notice in force, the account's state and the buttons that apply. No script,
/// no external asset, no cookie of its own.
/// </summary>
public static class ConsentPage
{
    /// <summary>A live receipt for the version in force wins, then a live older one, then the latest withdrawal, else none.</summary>
    public static ConsentState StateOf(IReadOnlyCollection<ConsentReceipt> receipts)
    {
        var live = receipts.Where(x => x.WithdrawnAtUtc is null).ToList();
        if (live.FirstOrDefault(x => x.NoticeVersion == TrainingNotice.Version) is { } current)
        {
            return new ConsentState.Current(current.AcceptedAtUtc);
        }

        if (live.OrderByDescending(x => x.NoticeVersion).FirstOrDefault() is { } older)
        {
            return new ConsentState.Older(older.NoticeVersion, older.AcceptedAtUtc);
        }

        return receipts.Count > 0
            ? new ConsentState.Withdrawn(receipts.Max(x => x.WithdrawnAtUtc!.Value))
            : new ConsentState.None();
    }

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static string Render(ConsentState state, string? error)
    {
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var version = TrainingNotice.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (message, anyLive) = state switch
        {
            ConsentState.Current current => ($"You agreed to version {version} on {Iso(current.AgreedAtUtc)}. Your messages may be used as the notice says.", true),
            ConsentState.Older older => ($"You agreed to version {older.Version} on {Iso(older.AgreedAtUtc)}; version {version} is in force and your messages are not used until you agree to it.", true),
            ConsentState.Withdrawn withdrawn => ($"You withdrew on {Iso(withdrawn.WithdrawnAtUtc)}. Your messages are not used.", false),
            _ => ("You have not agreed. Your messages are not used.", false)
        };
        var summary = error is null ? "" : $"""<div id="errors" role="alert" tabindex="-1"><strong>That did not go through.</strong><p>{E(error)}</p></div>""";
        // Withdraw whenever any live receipt exists (an older one included); Agree whenever none is live for the version in force.
        var withdrawButton = anyLive
            ? """<form method="post" action="/auth/bifrost/consent/withdraw"><button type="submit">Withdraw</button></form>"""
            : "";
        var agreeButton = state is ConsentState.Current
            ? ""
            : $"""<form method="post" action="/auth/bifrost/consent/accept"><input type="hidden" name="version" value="{version}" /><button type="submit">Agree to version {version}</button></form>""";
        var button = agreeButton + withdrawButton;

        return $$"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <title>Training consent - Bifrost</title>
                <style>
                  body { margin: 0; min-height: 100svh; display: grid; place-items: center; background: #090908; color: #fff8ef; font-family: system-ui, sans-serif; line-height: 1.5; }
                  main { width: min(92vw, 600px); margin: 24px 0; padding: 28px; border: 1px solid rgba(255,248,239,.16); border-radius: 8px; background: #141110; }
                  h1 { margin: 0 0 10px; font-size: 2rem; line-height: 1.1; }
                  p { color: #c7b8ad; }
                  fieldset { margin: 20px 0 0; padding: 14px 16px; border: 1px solid #8a7d73; border-radius: 6px; }
                  legend { padding: 0 6px; color: #d9ad66; font-weight: 750; }
                  #state { color: #fff8ef; font-weight: 650; }
                  button { margin-top: 20px; min-height: 46px; padding: 0 18px; border: 0; border-radius: 6px; background: #c43e4f; color: #fff; font: inherit; font-weight: 750; cursor: pointer; }
                  :focus-visible { outline: 3px solid #ffd27a; outline-offset: 2px; }
                  #errors { margin-top: 18px; padding: 10px 14px; border: 2px solid #ff9aa6; border-radius: 6px; color: #ffc2c9; }
                  #errors p { margin: 4px 0 0; color: #ffc2c9; }
                  small { color: #d9ad66; }
                </style>
              </head>
              <body>
                <main>
                  <small>Your account</small>
                  <h1>Training consent</h1>
                  {{summary}}
                  <p id="state" aria-live="polite">{{E(message)}}</p>
                  {{button}}
                  <fieldset>
                    <legend>Training notice, version {{version}}</legend>
                    {{TrainingNotice.Html("notice")}}
                  </fieldset>
                </main>
              </body>
            </html>
            """;
    }
}
