namespace Bifrost.Web.Features.Desk;

// What a caller who is not a decider of the Mind does not read, taken out at the door and nowhere else: every note is
// nil, with every key still written, so the body is still the contract's shape. Account ids are not Bifrost's to hide:
// Huginn's door withholds the decider ids and answerer ids from a non-decider and returns counts
// (eureka-substrate:ruling:desk-read-visibility-withheld-ids), and Bifrost cannot mask what it cannot read (the card is
// opaque to it). Cards, outcomes, codes, counts and the number of answers are untouched.
public static class DeskRedaction
{
    public static ForkEntry ForNonDecider(ForkEntry entry) =>
        entry with { Record = entry.Record with { Answers = [.. entry.Record.Answers.Select(a => a with { Note = null })] } };

    public static ForkList ForNonDecider(ForkList list) => list with { Forks = [.. list.Forks.Select(ForNonDecider)] };
}
