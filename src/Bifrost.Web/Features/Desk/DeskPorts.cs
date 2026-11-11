namespace Bifrost.Web.Features.Desk;

// The two ports the desk stands on. Bifrost owns the edge's order and judgments; these two own what is not
// Bifrost's yet: validating a bearer token (the OIDC validation scheme) and speaking to a Mind's Huginn on the
// peer-checked door. No production implementation is registered here: with the Desk section configured and a port
// missing, startup fails (DeskEndpoints.MapDesk) instead of serving.

// What a validated bearer token claims. The credential port verifies the token; the desk judges audience, scope and
// membership. A null Subject or Audience means the token did not carry one.
public sealed record DeskToken(string? Subject, string? Audience, IReadOnlyCollection<string> Scopes);

public interface IDeskCredential
{
    // Null when the request carries no valid bearer token.
    ValueTask<DeskToken?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken);
}

// The envelope sent with every answer, read and list call (fork-card.md M5), under the contract's names, built by
// Bifrost alone: the route's configured Mind id, the verified account's authority (Operator, Steward or Member),
// the account id, and the admin-set decider list of that Mind (1 to 64 unique ids). Nothing in the request sets any
// of it, and Huginn keeps no copy of the list.
public sealed record DeskDoorContext(string Mind, string Authority, string Subject, IReadOnlyList<string> Deciders);

public abstract record DeskAnswerOutcome
{
    // Huginn stored an outcome (Counted, Revision, Retraction or Refused) and returned its document.
    public sealed record Answered(AnswerResult Result) : DeskAnswerOutcome;

    // Nothing stored: Huginn refused with one of the contract's refusal codes.
    public sealed record NotStored(string Code) : DeskAnswerOutcome;
}

public abstract record DeskReadOutcome
{
    public sealed record Found(ForkEntry Entry) : DeskReadOutcome;

    public sealed record NotFound(string Code) : DeskReadOutcome;
}

// The list request the desk's query string stands for. Mind is the route's, resolved against the configured list.
public sealed record DeskListRequest(string Mind, string? Status, string? Cursor, int Limit);

public abstract record DeskListOutcome
{
    public sealed record Listed(ForkList List) : DeskListOutcome;

    public sealed record NotListed(string Code) : DeskListOutcome;

    // The next fork is too large for any page: the page holds nothing past the cursor. The client continues with
    // cursor c<Order>; at Order 0 it has reached the end. List only: no other call can be refused this way.
    public sealed record ForkTooLarge(string ForkId, int Order) : DeskListOutcome;
}

public interface IDeskDoor
{
    // The body is exactly the bytes the client sent. Any exception means the outcome is not confirmed.
    Task<DeskAnswerOutcome> AnswerAsync(
        string socket, DeskDoorContext context, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    // The entry for one fork, its card built and hashed by Huginn.
    Task<DeskReadOutcome> ReadAsync(
        string socket, DeskDoorContext context, string forkId, CancellationToken cancellationToken);

    // A page of entries, the counts and the stamp, as Huginn projects them for this viewer.
    Task<DeskListOutcome> ListAsync(
        string socket, DeskDoorContext context, DeskListRequest request, CancellationToken cancellationToken);
}
