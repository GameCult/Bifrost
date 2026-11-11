using System.Net;
using System.Text;

namespace Bifrost.Web.Features.Registration;

/// <summary>
/// The training notice a person may agree to. Changing the wording in substance means a new <see cref="Version"/> (a
/// positive integer, no leading zeros), because a receipt records the version the person agreed to; a receipt for any
/// other version no longer counts, the chat sign-in is refused, and the person is asked again on the consent page.
/// The text is frozen once served: a test pins its hash per version, so an edit without a version bump fails.
/// </summary>
public static class TrainingNotice
{
    public const int Version = 1;

    public const string Text =
        "GameCult chat training notice, version 1\n\n" +
        "GameCult may use messages you post in GameCult chat to train and evaluate AI models that GameCult builds and runs.\n\n" +
        "What counts. The text of your messages, with their time and room, tied to your account name. Not your email address, your sign-in details or anything you did not post. Nothing that came from Discord is used, in any form.\n\n" +
        "It is your choice. Agreement is needed to speak in a recorded room. You can have a GameCult account, see the members page and vote without agreeing. If you do not agree, you cannot speak in a recorded room and none of your messages are used.\n\n" +
        "If you agree, we record your account, this notice's version number and the time. Nothing else is stored with it.\n\n" +
        "If you withdraw, you can no longer speak in a recorded room, and we leave all your messages out of every training set made after that moment. Lines you have already spoken stay: withdrawing deletes nothing. Your messages stay in chat and on our servers, anything already used to train a model stays used, and a dataset already built is not recalled. We keep the record that you agreed and when you withdrew.\n\n" +
        "You can agree or withdraw at any time on your consent page while signed in. If the notice changes, its version changes and we ask again; until you agree to the new version, you cannot speak in a recorded room and your messages are not used.";

    /// <summary>The text as HTML-encoded paragraphs inside a div with the given id.</summary>
    public static string Html(string id)
    {
        var html = new StringBuilder($"<div id=\"{id}\">");
        foreach (var paragraph in Text.Split("\n\n"))
        {
            html.Append("<p>").Append(WebUtility.HtmlEncode(paragraph)).Append("</p>");
        }

        return html.Append("</div>").ToString();
    }
}
