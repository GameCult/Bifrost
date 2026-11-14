"""One-off mutation probe for the account merge saga (not a gate; survivors are triaged by name).
Usage, from any directory with BIFROST_TEST_PG and the dotnet environment set:
  python3 tools/merge-saga-mutants.py [mutant-name ...]     # no names: all
Each mutant is one exact text edit, applied, run against the merge tests (and again alone if it fails), then restored.
Logs go to $MUTANT_LOGS (default ./mutant-logs). Expected: every mutant KILLED.
"""
import subprocess, re, sys, os, json
WT=subprocess.run(['git','rev-parse','--show-toplevel'],capture_output=True,text=True,check=True,cwd=os.path.dirname(os.path.abspath(__file__))).stdout.strip()
LOGS=os.environ.get('MUTANT_LOGS','./mutant-logs'); os.makedirs(LOGS,exist_ok=True)
SAGA='src/Bifrost.Web/Features/Membership/AccountMergeSaga.cs'
LEDGER='src/Bifrost.Web/Features/Membership/AccountMergeLedger.cs'
FILTER='FullyQualifiedName~AccountMergeSaga|FullyQualifiedName~AccountMergePostgres'
M=[
('derive-move-id', SAGA, [("        var merge = new AccountMerge\n        {\n            State = AccountMergeState.Confirmed,","        var fixedId = Guid.NewGuid();\n        var merge = new AccountMerge\n        {\n            Id = fixedId,\n            State = AccountMergeState.Confirmed,"),('HeimdallMoveId = Guid.NewGuid().ToString("N"),','HeimdallMoveId = fixedId.ToString("N"),')], None),
('no-wipe-on-moved', SAGA, "                merge.SealedProofs = null;\n                if (moved.Receipt.AccountId != destination)", "                if (moved.Receipt.AccountId != destination)"),
('no-wipe-on-refused', SAGA, "                // Heimdall applied nothing before a 4xx. A forward refusal ends the merge; a refused undo leaves it as it was.\n                merge.SealedProofs = null;", "                // Heimdall applied nothing before a 4xx. A forward refusal ends the merge; a refused undo leaves it as it was."),
('refused-stays-confirmed', SAGA, "merge.State = AccountMergeState.Failed;", "merge.State = AccountMergeState.Confirmed;"),
('unknown-no-attempt', SAGA, "                merge.Attempts++;\n", ""),
('unknown-no-backoff', SAGA, "merge.NextAttemptUtc = now + Backoff[Math.Min(merge.Attempts, Backoff.Length) - 1];", "merge.NextAttemptUtc = null;"),
('no-digest-check', SAGA, "if (!string.Equals(plan.Digest(request.SurvivorUserAccountId, request.FoldedUserAccountId), request.PreviewDigest, StringComparison.Ordinal))", "if (false)"),
('no-proof-account-check', SAGA, "if (request.SurvivorProof.HeimdallAccountId != survivorHeimdall || request.FoldedProofs.Any(x => x.HeimdallAccountId != foldedHeimdall))", "if (false)"),
('no-notparty', SAGA, "if (request.ActorUserAccountId != request.SurvivorUserAccountId && request.ActorUserAccountId != request.FoldedUserAccountId)", "if (false)"),
('no-block-check', SAGA, "if (plan.Blocks.Count > 0)", "if (false)"),
('no-grace-check', SAGA, "if (merge.GraceEndsUtc is not { } graceEnds || now >= graceEnds)", "if (merge.GraceEndsUtc is not { } graceEnds)"),
('no-notsurvivor', SAGA, "if (actorUserAccountId != merge.SurvivorUserAccountId)", "if (false)"),
('no-undo-proof-check', SAGA, "if (survivorProofs.Any(x => x.HeimdallAccountId != survivorHeimdall))", "if (false)"),
('no-final-graceover', SAGA, "if (merge.State == AccountMergeState.Final || merge.FinalizedUtc is not null)", "if (false)"),
('no-receipt-check', SAGA, "if (moved.Receipt.AccountId != destination)", "if (false)"),
('no-concurrency-retry', SAGA, "catch (DbUpdateConcurrencyException) when (lost < 2)", "catch (DbUpdateConcurrencyException) when (false)"),
('no-live-violation-catch', SAGA, "catch (DbUpdateException error) when (IsLiveMergeViolation(error))", "catch (DbUpdateException error) when (false)"),
('undo-refused-fails', SAGA, "                    merge.State = AccountMergeState.Committed;\n                    merge.LastOutcome = \"undo_\" + code;", "                    merge.State = AccountMergeState.Failed;\n                    merge.LastOutcome = \"undo_\" + code;"),
('unreadable-no-delay', SAGA, "merge.NextAttemptUtc = now + UnreadableRetry;", "merge.NextAttemptUtc = null;"),
('due-ignores-next-attempt', SAGA, "&& (x.NextAttemptUtc == null || x.NextAttemptUtc <= now)", ""),
('mismatch-retries', SAGA, "        if (merge.LastOutcome == ReceiptMismatch)\n        {\n            return merge;", "        if (false)\n        {\n            return merge;"),
('reverse-id-forward', SAGA, "new HeimdallMoveRequest(merge.HeimdallReverseMoveId!, proofs.From, ReversesMoveId: merge.HeimdallMoveId)", "new HeimdallMoveRequest(merge.HeimdallMoveId!, proofs.From, ReversesMoveId: merge.HeimdallMoveId)"),
('log-token', SAGA, 'logger.LogInformation("Account merge {MergeId} {State}: {Outcome} {Code}", merge.Id, merge.State, outcome.GetType().Name, code);', 'logger.LogInformation("Account merge {MergeId} {State}: {Outcome} {Code} {Req}", merge.Id, merge.State, outcome.GetType().Name, code, proofs.From[0]);'),
('seal-plain', SAGA, "private string Seal(IEnumerable<string> from, string? to) => _seal.Protect(JsonSerializer.Serialize(new ProofSeal([.. from], to)));", "private string Seal(IEnumerable<string> from, string? to) => JsonSerializer.Serialize(new ProofSeal([.. from], to));"),
('sweeper-no-advance', LEDGER, "                await scope.ServiceProvider.GetRequiredService<AccountMergeSaga>().AdvanceDueAsync(now, stoppingToken);\n", ""),
('sweeper-no-finalize', LEDGER, "                await scope.ServiceProvider.GetRequiredService<AccountMergeLedger>().FinalizeDueAsync(now, stoppingToken);\n", ""),
('no-writegate-confirm', SAGA, "return await AccountMergeLedger.WriteAsAsync(dbContext, request.ActorUserAccountId, _ => InsertConfirmedAsync(request, cancellationToken), cancellationToken);", "return await InsertConfirmedAsync(request, cancellationToken);"),
('digest-no-entries', LEDGER, "        foreach (var entry in Entries)\n        {\n            Field(entry.Kind.ToString());", "        foreach (var entry in Entries.Take(0))\n        {\n            Field(entry.Kind.ToString());"),
('tier-swapped', SAGA, "merge.ProofFromTier = Tier(moved.Receipt.ProofFrom);", "merge.ProofFromTier = \"strong\";"),
('wipe-missing-on-reversed', SAGA, "                    merge.State = AccountMergeState.HeimdallReversed;\n                    merge.ReversedUtc = now;", "                    merge.State = AccountMergeState.HeimdallReversed;\n                    merge.ReversedUtc = now;\n                    merge.SealedProofs = \"kept\";"),
('grace-boundary', SAGA, "now >= graceEnds", "now > graceEnds"),
('move-id-not-unique', 'src/Bifrost.Web/Data/Migrations/20261114130000_AddAccountMergeSaga.cs', 'column: "HeimdallMoveId",\n                unique: true,', 'column: "HeimdallMoveId",\n                unique: false,'),
('reverse-id-not-unique', 'src/Bifrost.Web/Data/Migrations/20261114130000_AddAccountMergeSaga.cs', 'column: "HeimdallReverseMoveId",\n                unique: true,', 'column: "HeimdallReverseMoveId",\n                unique: false,'),
('digest-includes-created-id', LEDGER, 'entry.Action == AccountMergeEntryAction.Created ? null : entry.RecordId.ToString("N")', 'entry.RecordId.ToString("N")'),
('proofseal-prints-bearer', SAGA, 'builder.Append($"From = {From?.Count}, To = {(To is null ? "none" : "present")}");', 'builder.Append($"From = {string.Join(",", From)}, To = {To}");'),
]
only=sys.argv[1:]
def run(filt,log):
    env=dict(os.environ,DOTNET_USE_POLLING_FILE_WATCHER='1')
    r=subprocess.run([*os.environ.get('DOTNET','dotnet').split(),'test','tests/Bifrost.Web.Tests','--filter',filt],cwd=WT,env=env,capture_output=True,text=True)
    open(log,'w').write(r.stdout+r.stderr)
    out=r.stdout+r.stderr
    fails=sorted(set(re.findall(r'^\s*Failed (Bifrost\.\S+)',out,re.M)))
    built='Build FAILED' not in out and 'error CS' not in out
    return built,fails,out
res={}
for name,path,old,new in M:
    if only and name not in only: continue
    full=os.path.join(WT,path)
    src=open(full).read()
    pairs=old if isinstance(old,list) else [(old,new)]
    if any(src.count(o)!=1 for o,_ in pairs):
        print(name,'ANCHOR',flush=True); res[name]='anchor-miss'; continue
    mutated=src
    for o,n in pairs: mutated=mutated.replace(o,n)
    open(full,'w').write(mutated)
    try:
        built,fails,out=run(FILTER,f'{LOGS}/{name}.log')
        if not built: res[name]='BUILD-FAIL'
        elif not fails: res[name]='SURVIVED'
        else:
            # rerun the first failing test in isolation
            t=fails[0]; method=t.split('.')[-1].split('(')[0]
            built2,fails2,_=run('FullyQualifiedName~'+method,f'{LOGS}/{name}.iso.log')
            res[name]=('KILLED x%d; isolated %s: %s'%(len(fails),method,'still fails' if fails2 else 'PASSES ALONE (order-dependent)'))
    finally:
        open(full,'w').write(src)
    print(name,res[name],flush=True)
json.dump(res,open(LOGS+'/result.json','w'),indent=1)
print('MUT-DONE',flush=True)
