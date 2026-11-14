using System.Text.RegularExpressions;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;

namespace Bifrost.Web.Tests;

/// <summary>The merge pages' words are a closed set: every state, refusal code and outcome the saga can store has one, and nothing the store holds is echoed.</summary>
public sealed class MergeWordingTests
{
    private static string Snake(HeimdallMoveRefusal code) =>
        code == HeimdallMoveRefusal.RequestInvalid ? "invalid_request" : Regex.Replace(code.ToString(), "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    [Fact]
    public void Every_state_has_words_and_they_differ()
    {
        var words = Enum.GetValues<AccountMergeState>().Select(MergeWording.State).ToArray();
        Assert.All(words, x => Assert.NotEmpty(x));
        Assert.Equal(words.Length, words.Distinct().Count());
    }

    [Fact]
    public void Every_refusal_Heimdall_can_give_has_words_and_the_code_itself_is_not_one_of_them()
    {
        foreach (var code in Enum.GetValues<HeimdallMoveRefusal>().Select(Snake))
        {
            var words = MergeWording.Failure(code);
            Assert.False(string.IsNullOrWhiteSpace(words), code);
            Assert.DoesNotContain(code, words!);
            Assert.False(string.IsNullOrWhiteSpace(MergeWording.Outcome("undo_" + code)), code);
        }
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("moved")]
    [InlineData("refused")]
    [InlineData("undo_requested")]
    [InlineData("receipt_mismatch")]
    [InlineData("proofs_unreadable")]
    [InlineData("transport")]
    [InlineData("move_id_conflict")]
    [InlineData("malformed or mismatched receipt")]
    public void Every_outcome_the_saga_stores_has_words(string outcome) => Assert.False(string.IsNullOrWhiteSpace(MergeWording.Outcome(outcome)), outcome);

    [Theory]
    [InlineData("HTTP 502")]
    [InlineData("HEIMDALL FREE TEXT")]
    [InlineData("undo_made_up_code")]
    public void An_outcome_the_wording_does_not_know_is_a_generic_line_that_does_not_echo_it(string outcome)
    {
        var words = MergeWording.Outcome(outcome);
        Assert.False(string.IsNullOrWhiteSpace(words));
        Assert.DoesNotContain(outcome, words!);
        Assert.Null(MergeWording.Failure(outcome));
    }

    [Fact]
    public void Every_undo_answer_has_distinct_words_and_a_closed_code()
    {
        UndoOutcome[] all = [new UndoOutcome.Requested(), new UndoOutcome.NotCommitted(), new UndoOutcome.GraceOver(), new UndoOutcome.NotSurvivor(), new UndoOutcome.ProofAccountMismatch()];
        var codes = all.Select(MergeWording.UndoCode).Append("logins_unproved").ToArray();
        Assert.Equal(6, codes.Distinct().Count());
        Assert.All(codes, x => Assert.False(string.IsNullOrWhiteSpace(MergeWording.Undo(x)), x));
        Assert.True(string.IsNullOrEmpty(MergeWording.Undo("anything else")));
    }
}
