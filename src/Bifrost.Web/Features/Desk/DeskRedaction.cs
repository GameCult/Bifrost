namespace Bifrost.Web.Features.Desk;

// What a caller who is not a decider of the Mind does not read, taken out at the door and nowhere else. For them
// every note is nil and every answerer is the sentinel; every key stays written (nil where absent, a string where a
// string is required), so the redacted body is still the contract's shape. Cards, outcomes, codes, counts and the
// number of answers are untouched; Huginn returns the full record and Bifrost drops and adds nothing.
//
// The sentinel is one fixed string for every answer: nothing derived from an account id (a hash or a per-reader
// alias would still let a reader link two answers to one person, or confirm a guess), and it is not a D-format
// UUID, so it cannot be mistaken for an account. The contract's `answerer` is a non-empty string and nothing more.
public static class DeskRedaction
{
    public const string MaskedAnswerer = "masked";

    public static ForkEntry ForNonDecider(ForkEntry entry) =>
        entry with
        {
            Record = entry.Record with
            {
                Answers = [.. entry.Record.Answers.Select(a => a with { Note = null, Answerer = MaskedAnswerer })],
            },
        };

    public static ForkList ForNonDecider(ForkList list) => list with { Forks = [.. list.Forks.Select(ForNonDecider)] };
}
