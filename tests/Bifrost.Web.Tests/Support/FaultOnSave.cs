using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bifrost.Web.Tests.Support;

/// <summary>
/// Armed by a test, it throws on the save its predicate picks and lets every other save through: a crash between two writes (the
/// SaveChangesInterceptor seam of AccountMergeLedgerTests). The host's contexts all carry it, so a crash in the saga's own scope is real.
/// </summary>
public sealed class FaultOnSave : SaveChangesInterceptor
{
    public Func<DbContext, bool>? When { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        When is { } when && when(eventData.Context!) ? throw new InvalidOperationException("injected save failure") : ValueTask.FromResult(result);
}
