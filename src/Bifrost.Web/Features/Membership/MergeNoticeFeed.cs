using Bifrost.Web.Data;
using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Membership;

/// <summary>What the chat host is told about one merge. Derived from the merge row on every read; never stored.</summary>
public sealed record MergeNotice(
    AccountMergeNoticeKind Kind,
    Guid MergeId,
    DateTimeOffset OccurredAt,
    DateTimeOffset? GraceEndsAt,
    Guid OldSubject,
    Guid NewSubject,
    string OldPreferredUsername,
    string NewPreferredUsername);

public sealed class MergeNoticeFeed(BifrostDbContext dbContext, TimeProvider timeProvider)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public Task<IReadOnlyList<MergeNotice>> ReadAsync(AccountMergeNoticeTarget target, DateTimeOffset? after, int? limit, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<bool> AckAsync(AccountMergeNoticeTarget target, Guid mergeId, AccountMergeNoticeKind kind, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
