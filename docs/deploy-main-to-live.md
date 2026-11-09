# Deploying main over the live release

Status: **plan, not executed.** Written 2026-11-09 by the heimdall Self run. Nothing here has been run. The deploy
waits on a root window, a verified database backup and a rehearsed rollback (Preconditions).

## What moves

| | Live | Target |
| --- | --- | --- |
| Release | `/srv/bifrost/app/releases/20261010043032-67072b2` | a new `<UTC>-<sha7>` directory |
| Revision | `67072b2` | `244811cb49bf65a2325792882e2d97e11115ca17` (main at writing; re-check) |
| Distance | | 141 commits; `67072b2` is an ancestor of main |
| Schema | up to the last pre-`AddConceptVotes` migration | + `20261010043937_AddConceptVotes`, + `20261022163159_AddOpenIddict` |

Both migrations only create tables and indexes (`Up` has no `Drop*`, `AlterColumn` or `RenameColumn`). The previous
binary therefore runs against the new schema, which is what makes the rollback below a symlink move, not a restore.

The deploy mechanism is `gamecult-ops/scripts/deploy-bifrost-alpha.sh <sha40>` and
`runbooks/bifrost-alpha-deploy.md`. There is no Idunn recipe for Bifrost; this is a manual, root-window deploy.
**Bifrost #11** (`__Host-` cookies) is not in main at writing and rides this deploy: merge it first, or the deploy is
repeated.

## Configuration that changed

Read against the live `/srv/bifrost/env/bifrost.env` before the window, not during it. Key names only; never paste values
into a chat, a PR or a log.

- **`Heimdall__AppSecret`: new, required for sign-in.** Without it, `HeimdallOptions.IsDiscordConfigured` and
  `IsPatreonConfigured` are false and Discord and Patreon sign-in stop working (GitHub sign-in is unaffected). The value is
  Heimdall's app secret for slug `bifrost` (`GC_ACCESS_APP_BIFROST_SHARED_SECRET` on the Heimdall side). Bifrost sends it
  as `x-heimdall-app-secret`, redacted from logs.
- **`ConceptVotes__NetworkTagSecret`**: at least 32 characters; keys the anonymous vote network tag. Check
  `ConceptVoteOptions` for the other `ConceptVotes__*` keys and whether the vote surface refuses to start without it.
- **`Bifrost__Oidc__*`**: optional. Absent, the OIDC issuer is not mapped and discovery answers 404. Present, it must be
  complete and the key files must exist, or startup stops. Unless the window includes standing up chat sign-in, leave it
  unset. Keys and files: `docs/oidc-issuer.md`.
- `DataProtection__KeysPath`: unchanged in intent. Data Protection already has a fixed application name (`Bifrost`), so a
  new release directory does not log members out. Confirm the live env still sets the keys path to a directory the new
  release can read.
- `Bootstrap__ApplyMigrationsOnStartup=true` stays on, so **the first start applies both migrations**. That is the
  irreversible-looking step; the backup below is for it.

## Preconditions

All must hold before step 1. A missing one means the window is not used.

1. **Root window.** Someone with root on Yggdrasil is present for the whole deploy and the 30 minutes after (installing
   the unit, reading `journalctl`, restoring). The swarm brake is off for Bifrost, or the operator has said so.
2. **Verified database backup.** `pg_dump -Fc` as in the runbook section 8, taken inside the window, then verified:
   `pg_restore --list <dump> | head` succeeds, and the dump restores into a scratch database
   (`createdb bifrost_rehearsal; pg_restore --no-owner -d bifrost_rehearsal <dump>`), where the member count equals the
   live count. Drop the scratch database afterwards. An unrestored dump is not a backup.
3. **Env and secrets backup.** The runbook's `bifrost-env-*.tar`, plus the Data Protection key ring directory if it lives
   outside `/srv/bifrost/env`.
4. **Rollback rehearsal.** Before the real deploy, on the same host: unpack the old publish (or keep the live release
   directory untouched, which is the plan) and prove the previous release starts against a database that already has the
   two migrations: start it on a spare port against the scratch database from step 2 after applying the new migrations
   to it (`dotnet ef database update` from the target revision, or one start of the target with the flag on). The
   previous release must answer `/` with 200. This is the claim that the rollback is a symlink move; it is checked, not
   assumed.
5. **Artifact.** Published per runbook section 3: `git merge-base --is-ancestor <sha> origin/main` exits 0, publish from a
   detached worktree, tar, upload. Record the tarball's SHA-256 on both ends. Build and test on Yggdrasil's verify path,
   not a workstation.
6. **Env diff done.** The `Heimdall__AppSecret` line is in `bifrost.env` (mode 600, owner `bifrost`) and Heimdall holds the
   same value. Nothing from the diff in "Configuration that changed" is left unresolved.
7. **Quiet.** No motion closing and no known vote in flight inside the window. Check the motions page.

## Steps, in order

Record the time and the output line of each in the deploy record. Stop at the first failure and go to Rollback.

1. Note the live state: `readlink -f /srv/bifrost/app/current` (expect `.../20261010043032-67072b2`),
   `systemctl is-active bifrost.service`, the member count, the open motion count. This is the "before" the live check
   compares to.
2. Take and verify the backups (Preconditions 2 and 3).
3. Add `Heimdall__AppSecret` (and any other new key) to `/srv/bifrost/env/bifrost.env`: `sudo -n install` the new file at
   mode 600 owner `bifrost`; keep the old file as `bifrost.env.pre-<UTC>` beside it.
4. Upload the tarball; compare its SHA-256 to the build's.
5. `sudo -n bash /home/gamecultadmin/deploy-bifrost-alpha.sh <sha40>`. It unpacks into `<UTC>-<sha7>`, writes `REVISION`,
   repoints `current`, restarts. The restart applies both migrations.
6. Watch the start: `journalctl -u bifrost.service -n 100 --no-pager` until the host reports listening on
   `127.0.0.1:5080`; no `Failed executing DbCommand`, no `Heimdall ... refused` at start. A start that loops is a
   failure: roll back, do not wait.
7. Live check (next section).
8. Record the deploy in `gamecult-ops` `inventory.md` (Bifrost alpha live state): release directory, full sha, date.

## Live check

Done from outside nginx, with a real browser session, within the window. A 200 on `/` is not a check.

- `cat /srv/bifrost/app/current/REVISION` is the target sha; `systemctl is-active bifrost.service` is `active`.
- **Login.** Sign in with GitHub; then with Discord (exercises `Heimdall__AppSecret`: the start must reach Heimdall and
  return to `/auth/heimdall/callback`). A Discord or Patreon sign-in that comes back refused or loops is the new-secret
  failure; an existing session surviving the restart shows Data Protection kept its keys.
- **Vote read.** The public concept-vote read endpoint answers with the existing tallies, and the tallies equal those
  noted in step 1 (the schema moved; the data must not).
- **Persona picture.** Open a Persona's page and confirm its picture loads (the case that regressed when the media path
  was wrong). A broken image here with a healthy `/` is the finding the check exists for.
- Member console, work board, motions and members pages load (runbook section 7).
- The error rate in `journalctl` over five minutes is flat against the lines noted before.

## Rollback

Trigger: the start loops or migrates with errors, any live check fails, or the root window closes with a doubt open.

1. `sudo -n ln -sfn /srv/bifrost/app/releases/20261010043032-67072b2 /srv/bifrost/app/current && sudo -n chown -h
   bifrost:bifrost /srv/bifrost/app/current && sudo -n systemctl restart bifrost.service`.
2. Restore the previous env file if step 3 changed it (`bifrost.env.pre-<UTC>`).
3. Re-run the "before" checks from step 1: revision, `active`, counts equal.
4. The two new migrations stay applied. They add tables the old release ignores, as rehearsed in Precondition 4.
5. Only if the schema or data is damaged (not merely new): the runbook section 8 restore, from the verified dump. This is
   the last resort and needs the same root window.
6. Leave the failed release directory for diagnosis; do not delete it in the window.

## What this plan does not do

It does not deploy, does not add an Idunn recipe, and does not decide whether Bifrost #11 merges first (it should). The
Idunn recipe is the owner of making this repeatable; until then each deploy follows this page and the runbook.
