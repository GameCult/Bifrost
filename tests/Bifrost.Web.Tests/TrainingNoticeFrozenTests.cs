using System.Security.Cryptography;
using System.Text;
using Bifrost.Web.Features.Registration;

namespace Bifrost.Web.Tests;

/// <summary>
/// A receipt records agreement to specific words, so served words never change under a version: text change requires a
/// version bump. When the notice is reworded, bump <see cref="TrainingNotice.Version"/> and ADD a line here; never edit one.
/// </summary>
public sealed class TrainingNoticeFrozenTests
{
    private static readonly Dictionary<int, string> Frozen = new()
    {
        [1] = "9ba9be747a7387d70f2f3ee75fd7460a2e6cbe40a8c7e80aae7d7efd9b33a314"
    };

    [Fact]
    public void The_text_of_the_version_in_force_is_the_text_that_was_frozen_for_it()
    {
        Assert.True(Frozen.TryGetValue(TrainingNotice.Version, out var pinned), $"Version {TrainingNotice.Version} has no frozen hash: add one when you bump the version.");
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TrainingNotice.Text))).ToLowerInvariant();
        Assert.True(pinned == actual, $"The notice text changed but the version is still {TrainingNotice.Version}. Bump TrainingNotice.Version and add its hash ({actual}) here; do not edit the old line.");
    }

    [Fact]
    public void The_notice_names_what_agreement_is_for_and_what_withdrawal_leaves_standing()
    {
        Assert.Contains("to speak in a recorded room", TrainingNotice.Text);
        Assert.Contains("Lines you have already spoken stay", TrainingNotice.Text);
        Assert.StartsWith($"GameCult chat training notice, version {TrainingNotice.Version}\n", TrainingNotice.Text);
    }

    [Fact]
    public void The_consent_page_status_lines_keep_their_exact_words()
    {
        var at = new DateTimeOffset(2026, 11, 1, 12, 30, 5, TimeSpan.Zero);
        var v = TrainingNotice.Version;
        Assert.Contains($"You agreed to version {v} on 2026-11-01T12:30:05Z. You may speak in a recorded room, and your messages may be used as the notice says.", ConsentPage.Render(new ConsentState.Current(at), null));
        Assert.Contains($"You agreed to version 0 on 2026-11-01T12:30:05Z; version {v} is in force; you cannot speak in a recorded room and your messages are not used until you agree to it.", ConsentPage.Render(new ConsentState.Older(0, at), null));
        Assert.Contains("You withdrew on 2026-11-01T12:30:05Z. You cannot speak in a recorded room, and your messages are not used. Lines you already spoke stay.", ConsentPage.Render(new ConsentState.Withdrawn(at), null));
        Assert.Contains("You have not agreed. You cannot speak in a recorded room, and your messages are not used.", ConsentPage.Render(new ConsentState.None(), null));
    }
}
