using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;

namespace Bifrost.Web.Tests;

/// <summary>The wording of a merge plan: closed vocabulary, counts from the plan's entries, the statements a member must always see.</summary>
public sealed class MergePreviewTests
{
    private static readonly string[] BlockCodes =
    [
        "same_account", "account_missing", "mind_account", "native_account", "merge_in_flight",
        "survivor_is_folded", "already_folded", "folded_is_survivor", "sealed_ballot_pending"
    ];

    private static MergePlanEntry Entry(AccountMergeEntryKind kind, AccountMergeEntryAction action) => new(kind, action, Guid.NewGuid());

    private static MergePlan Plan(IEnumerable<MergePlanEntry> entries, IReadOnlyDictionary<string, int>? left = null) =>
        new([], [.. entries], left ?? new Dictionary<string, int>());

    [Fact]
    public void Every_block_code_has_its_own_words_and_none_names_an_account()
    {
        var words = BlockCodes.Select(MergePreview.Block).ToList();

        Assert.All(words, x => Assert.NotEmpty(x));
        Assert.Equal(BlockCodes.Length, words.Distinct().Count());
        Assert.All(words, x => Assert.DoesNotContain("@", x));
        Assert.NotEmpty(MergePreview.Block("a code nobody wrote"));
        Assert.DoesNotContain("a code nobody wrote", MergePreview.Block("a code nobody wrote"));
    }

    [Fact]
    public void The_counts_come_from_the_plans_entries_by_kind_and_action()
    {
        var plan = Plan(
        [
            .. Enumerable.Range(0, 3).Select(_ => Entry(AccountMergeEntryKind.Vote, AccountMergeEntryAction.Voided)),
            Entry(AccountMergeEntryKind.Vote, AccountMergeEntryAction.Moved),
            .. Enumerable.Range(0, 2).Select(_ => Entry(AccountMergeEntryKind.ConceptAppraisal, AccountMergeEntryAction.Voided)),
            Entry(AccountMergeEntryKind.Membership, AccountMergeEntryAction.Closed),
            Entry(AccountMergeEntryKind.Standing, AccountMergeEntryAction.Suspended),
            .. Enumerable.Range(0, 4).Select(_ => Entry(AccountMergeEntryKind.PatronSupportEvent, AccountMergeEntryAction.Moved))
        ],
            new Dictionary<string, int> { ["ConsentReceipts"] = 5, ["PasskeyCredentials"] = 1, ["LedgerEntries"] = 0 });

        var text = string.Join('\n', MergePreview.Lines(plan));

        Assert.Contains("3 open votes", text);
        Assert.Contains("1 open vote ", text);
        Assert.Contains("2 appraisals", text);
        Assert.Contains("membership", text);
        Assert.Contains("suspended", text);
        Assert.Contains("4 patron events", text);
        Assert.Contains("5 consent records", text);
        Assert.Contains("1 passkey ", text);
        Assert.DoesNotContain("0 ledger", text);

        Assert.Contains("2 open votes", string.Join('\n', MergePreview.Lines(Plan(
            Enumerable.Range(0, 2).Select(_ => Entry(AccountMergeEntryKind.Vote, AccountMergeEntryAction.Voided))))));
    }

    [Theory]
    [InlineData("chat id")]
    [InlineData("consent again")]
    [InlineData("StreamPixels")]
    [InlineData("Roles and admin")]
    [InlineData("one vote per motion")]
    [InlineData("7 days")]
    [InlineData("ordinary sign-ins")]
    [InlineData("Merge status")]
    public void The_statements_always_carry_what_a_merge_cannot_carry_over(string statement)
    {
        Assert.Contains(MergePreview.Statements, x => x.Contains(statement, StringComparison.Ordinal));
    }

    [Fact]
    public void The_statements_promise_no_passkey_because_no_page_offers_one()
    {
        Assert.DoesNotContain(MergePreview.Statements, x => x.Contains("passkey", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("preview_stale")]
    [InlineData("not_party")]
    [InlineData("proof_account_mismatch")]
    [InlineData("proofs_needed")]
    [InlineData("consent_needed")]
    [InlineData("bad_choice")]
    public void Every_refusal_the_page_can_answer_has_closed_words(string code)
    {
        Assert.NotEmpty(MergeWording.Confirm(code));
        Assert.Empty(MergeWording.Confirm("not a code"));
    }
}
