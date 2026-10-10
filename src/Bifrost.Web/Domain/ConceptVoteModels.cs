namespace Bifrost.Web.Domain;

public enum ConceptStance
{
    Up,
    Down
}

public enum VoterClass
{
    Anonymous,
    Account,
    Member
}

public sealed class ConceptSubject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Collection { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset RegisteredAtUtc { get; set; }

    public DateTimeOffset? RetiredAtUtc { get; set; }
}

public sealed class ConceptAppraisal
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SubjectId { get; set; }

    public string VoterKey { get; set; } = string.Empty;

    public ConceptStance Stance { get; set; }

    public VoterClass VoterClass { get; set; }

    public Guid? UserAccountId { get; set; }

    public string NetworkTag { get; set; } = string.Empty;

    public DateTimeOffset CastAtUtc { get; set; }

    public ConceptSubject? Subject { get; set; }
}
