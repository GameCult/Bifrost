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

// Sent with every door call, built by Bifrost alone: the verified account, its attribution, and the decider list
// configured for the route's Mind. Nothing in the request sets any of it.
public sealed record DeskDoorContext(string Subject, string Attribution, IReadOnlyList<string> Deciders);

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

public interface IDeskDoor
{
    // The body is exactly the bytes the client sent. Any exception means the outcome is not confirmed.
    Task<DeskAnswerOutcome> AnswerAsync(
        string socket, DeskDoorContext context, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    Task<DeskReadOutcome> ReadAsync(
        string socket, DeskDoorContext context, string forkId, CancellationToken cancellationToken);
}
