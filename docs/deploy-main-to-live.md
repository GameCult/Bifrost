# Deploying main over the live release

Status: **plan, not executed.** Written 2026-11-09 by the heimdall Self run; revised the same day by the eureka Self run
against main `55b74275`, from Libby's read of the first draft. Nothing here has been run. The deploy waits on a root
window, a verified database backup and a rehearsed rollback (Preconditions).

## What moves

| | Live | Target |
| --- | --- | --- |
| Release | `/srv/bifrost/app/releases/20261010043032-67072b2` | a new `<UTC>-<sha7>` directory |
| Revision | `67072b2` | `55b74275b0d86cdb0c5db01010380a90fbc2eb69` (main at revision; re-check with `git ls-remote`) |
| Distance | | 147 commits; `67072b2` is an ancestor of main |
| Schema | up to the last pre-`AddConceptVotes` migration | + `20261010043937_AddConceptVotes`, + `20261022163159_AddOpenIddict` |

Both migrations only create tables and indexes (`Up` has no `Drop*`, `AlterColumn` or `RenameColumn`). The previous
binary therefore runs against the new schema, which is what makes the rollback below a symlink move, not a restore.

The deploy mechanism is `gamecult-ops/scripts/deploy-bifrost-alpha.sh <sha40>` and
`runbooks/bifrost-alpha-deploy.md`. There is no Idunn recipe for Bifrost; this is a manual, root-window deploy.
**Bifrost #11** (`__Host-` cookies) and #17 (CI publish workflow deleted) are in main; this page (#15) is docs only and
changes no code. #11 renames every cookie Bifrost sets (`BifrostCookies.cs`: session, OAuth correlation, antiforgery,
voter, Heimdall attempt), so **the deploy signs every member out once**, drops sign-ins in flight at the restart, and
gives each returning anonymous voter a new device cookie. Say so to the members before the window. The data-protection
key ring is unchanged, so this is the only forced sign-out; the live check below tests it.

## Configuration

Read against the live `/srv/bifrost/env/bifrost.env` before the window, not during it, by someone who can read it (mode
600, owner `bifrost`; the eureka Self run's account cannot, so **no key below is marked present or absent here**: the
column is filled in by the reader). Key names only; never paste values into a chat, a PR or a log. Name-only check:
`sudo -n cut -d= -f1 /srv/bifrost/env/bifrost.env | sort`.

Outside Development and Testing, `StartupConfigurationValidator.Validate()` refuses to start (an `InvalidOperationException`
naming every failure) unless all of these hold. Environment-variable spelling uses `__` for `:`.

| Key (configuration name) | Rule | Present? |
| --- | --- | --- |
| `GitHubOAuth:ClientId`, `GitHubOAuth:ClientSecret` | both non-empty | |
| `Host:PublicBaseUrl`, `Host:ExpectedHost` | both non-empty | |
| `AllowedHosts` | non-empty and no `*` (while `Host:RequireStrictHostValidation` is true, its default) | |
| `ConnectionStrings:Bifrost` | non-empty | |
| `DataProtection:KeysPath` | non-empty | |
| **`ConceptVotes:NetworkTagSecret`** | non-empty and **at least 32 characters** (`ConceptVoteOptions.MinNetworkTagSecretLength`); a hard startup failure if missing or short (`StartupConfigurationValidator.cs:104`) | |
| `ConceptVotes:AllowedOrigins` | each entry exactly `https://host`, no path, wildcard or trailing slash | |

Conditional, only if the feature is switched on: `GitHubApp` settings when `GitHubApp:EnableWebhookSync` is true;
`Heimdall:PatronSupportIntakeSecret` while `Heimdall:EnablePatronSupportIntake` is true (its default); the Stripe keys
when `Stripe:EnableCheckout` is true. Two of these checks are satisfied by defaults once the one secret is present: Stripe
needs `SecretKey`, `SuccessUrl` and `CancelUrl`, and the last two default to `https://velvet.gamecult.org/...`
(`StripeOptions.cs:15,17`); Heimdall intake needs `BaseUrl`, `AppSlug` and the intake secret, and the first two default to
`https://heimdall.gamecult.org` and `bifrost` (`HeimdallOptions.cs:7,9`). Also checked at every start, in every environment: the `ConceptVotes` prefix lengths,
permits, windows and `MaxTrackedKeys`.

New since the live release, and not read by the validator:

- **`Heimdall__AppSecret`**: required for Discord and Patreon sign-in. Without it `HeimdallOptions.IsDiscordConfigured` and
  `IsPatreonConfigured` are false and those sign-ins stop working (GitHub sign-in is unaffected); startup does not fail,
  so a missing key shows only at the live check. The value is Heimdall's app secret for slug `bifrost`
  (`GC_ACCESS_APP_BIFROST_SHARED_SECRET` on the Heimdall side). Bifrost sends it as `x-heimdall-app-secret`, redacted
  from logs.
- **`Bifrost__Oidc__*`**: optional. Absent, the OIDC issuer is not mapped and discovery answers 404. Present, it must be
  complete and the key files must exist, or startup stops. Unless the window includes standing up chat sign-in, leave it
  unset. Keys and files: `docs/oidc-issuer.md`.
- `DataProtection:KeysPath` keeps its intent. Data Protection has a fixed application name (`Bifrost`, `Program.cs`), so a
  new release directory does not log members out; #11's cookie rename does (above). The live env points the keys path at
  `/srv/bifrost/.aspnet/DataProtection-Keys` (read 2026-11-09); confirm that again in the window, and that the new release
  reads it.
- `Bootstrap__ApplyMigrationsOnStartup=true` stays on, so **the first start applies both migrations**. That is the
  irreversible-looking step; the backup below is for it.

## Preconditions

All must hold before step 1. A missing one means the window is not used.

1. **Root window.** Someone with root on Yggdrasil is present for the whole deploy and the 30 minutes after (installing
   the unit, reading `journalctl`, restoring). The swarm brake is off for Bifrost, or the operator has said so.
2. **Verified database backup.** `pg_dump -Fc` as in the runbook section 8, taken inside the window, then verified:
   `pg_restore --list <dump> | head` succeeds, and the dump restores into a scratch database, where the member count
   equals the live count. The `bifrost` database role has no CREATEDB, so create and drop the scratch database as the
   superuser: `sudo -n -u postgres createdb -O bifrost bifrost_rehearsal`, then
   `sudo -n -u postgres pg_restore --no-owner --role=bifrost -d bifrost_rehearsal <dump>`, and at the end
   `sudo -n -u postgres dropdb bifrost_rehearsal`. An unrestored dump is not a backup.
3. **Env, secrets and key-ring backup.** The runbook's `bifrost-env-*.tar`, plus the live ring
   **`/srv/bifrost/.aspnet/DataProtection-Keys`** (read from the host 2026-11-09: `DataProtection__KeysPath` in
   `bifrost.env` names it; three key files at that reading, modes 600, owner `bifrost`, dated 2026-04-22, 2026-07-22 and
   2026-10-20). It lives outside `/srv/bifrost/env` and is not in that tar. `/srv/bifrost/data-protection-keys` holds one
   older key (2026-07-15) that no setting points at: it is not the ring, and is left alone. Losing the ring signs
   everyone out and breaks every protected cookie and token; `cp -a` it beside the env backup and record its file list.
4. **Rollback rehearsal.** Before the real deploy, on the same host. There is no `dotnet ef` on the host and this plan
   does not use it: the target applies its own migrations at start. Rehearse by starting the target once, then the
   previous release, on the scratch database, from a separate env file that touches nothing live:
   - a copy of `bifrost.env` at `/srv/bifrost/rehearsal/bifrost-rehearsal.env` (directory and file owned by `bifrost`,
     modes 700 and 600; nothing under `/root`, which `bifrost` cannot read), with `ConnectionStrings__Bifrost` pointing at
     `bifrost_rehearsal`, `DataProtection__KeysPath` at a throwaway empty directory in the same place (not the live key
     ring, which the rehearsal must not write), and `ASPNETCORE_URLS` set to a port that is not 5080 (e.g.
     `http://127.0.0.1:5081`). Delete the file and the directories afterwards.
   - Strip from that copy every key that could act on or sign in to something real, and every key the scratch run does
     not need: all `Bifrost__Oidc__*`, `Bridge__LocalBridgeToken`, `Bootstrap__AdminGitHubLogins__*`, the GitHub App,
     Stripe and Heimdall intake and app-secret keys. Stripping a key is not enough: startup validation refuses to start
     when a feature flag is on and its key is missing (`StartupConfigurationValidator.cs:59-62`, `:69-73`, `:75-80`), so
     set `Heimdall__EnablePatronSupportIntake=false` (it defaults to true and then needs the intake secret),
     `GitHubApp__EnableWebhookSync=false` and `Stripe__EnableCheckout=false` in the rehearsal env. With them off the
     process starts without the secrets, and the rehearsal cannot reach Stripe, GitHub or Heimdall. The rehearsal checks that the process starts and migrates; **there is
     no sign-in on 5081**, because the GitHub OAuth app and Heimdall redirect to the public host.
   - start the target release from that file as `bifrost`, with the file loaded by systemd, not by a shell:
     `sudo -n systemd-run --wait --pty -p User=bifrost -p EnvironmentFile=/srv/bifrost/rehearsal/bifrost-rehearsal.env
     -p WorkingDirectory=<target-release> /usr/bin/dotnet <target-release>/Bifrost.Web.dll` (detached with `--unit
     bifrost-rehearsal` and no `--wait`/`--pty` if the check runs from another shell; stop it with
     `systemctl stop bifrost-rehearsal`). The scratch database now has both migrations. Check it listens and that
     `curl -s -o /dev/null -w '%{http_code}' -H 'Host: <Host:ExpectedHost of the live env>' http://127.0.0.1:5081/readyz`
     answers 200 (the app validates the host, so a bare request to `127.0.0.1` is refused), then stop it.
   - start the **previous** release (`/srv/bifrost/app/releases/20261010043032-67072b2`) the same way on another spare
     port (e.g. 5082) against the same, now migrated, scratch database, with the same `Host:` header; `/readyz` must answer
     200. This is the claim that the
     rollback is a symlink move; it is checked, not assumed.
5. **Artifact.** Published per runbook section 3: `git merge-base --is-ancestor <sha> origin/main` exits 0, publish from a
   detached worktree, tar, upload. Record the tarball's SHA-256 on both ends. Build and test on Yggdrasil's verify path,
   not a workstation.
6. **Env diff done.** Every row of the Configuration table is marked present by the reader, `ConceptVotes__NetworkTagSecret`
   included and at least 32 characters (a count, not the value), and the `Heimdall__AppSecret` line is in `bifrost.env`
   (mode 600, owner `bifrost`) with Heimdall holding the same value. Nothing in "Configuration" is left unresolved.
7. **Quiet.** No motion closing and no known vote in flight inside the window. Check the motions page. Members told that
   everyone is signed out once (see "What moves").
8. **Watchdog.** `bifrost-watchdog-notice.timer` fires one minute after boot and one minute after each run
   (`OnBootSec=1min`, `OnUnitActiveSec=1min`; read 2026-11-09), and the oneshot `bifrost-watchdog-notice.service`, run as
   `bifrost-notice`, DMs the owner Idunn's operator incidents (`systemctl cat bifrost-watchdog-notice.service`). A deliberate restart or rollback that Idunn counts as an
   incident can page the owner during the window; tell them first. Leave the timer running: it is not part of the
   deploy and must not be stopped or edited.

## Steps, in order

Record the time and the output line of each in the deploy record. Stop at the first failure and go to Rollback.

1. Note the live state: `readlink -f /srv/bifrost/app/current` (expect `.../20261010043032-67072b2`),
   `systemctl is-active bifrost.service`, the member count, the open motion count, and the **log baseline**: the service
   writes to files, not the journal (`StandardOutput=append:`), so `journalctl -u bifrost.service` shows only systemd's
   own lines. Record `wc -l /srv/bifrost/log/bifrost.stdout.log /srv/bifrost/log/bifrost.stderr.log` (at 2026-11-09
   11:55Z: 4838 and 44925 lines; stderr last written 2026-10-18, stdout 2026-11-06) so the live check counts only the
   lines written after the restart. This is the "before" the live check compares to.
2. Take and verify the backups (Preconditions 2 and 3), then run the rollback rehearsal (Precondition 4).
3. Make the env complete: add `Heimdall__AppSecret` and, if the Configuration table found it absent or shorter than 32
   characters, **`ConceptVotes__NetworkTagSecret`** (the target does not start without it) to
   `/srv/bifrost/env/bifrost.env`, after the timestamped backup, printing nothing. The secret is random, 32 or more
   characters, and is never echoed, logged or pasted into a chat. For example (`openssl rand -hex 32` gives 64 hex
   characters and nothing that needs quoting in a systemd `EnvironmentFile`):
   ```
   sudo -n sh -c 'umask 077; cd /srv/bifrost/env || exit 1
   if grep -Eq "^[[:space:]]*ConceptVotes__NetworkTagSecret=[^[:space:]]" bifrost.env; then
     echo "ConceptVotes__NetworkTagSecret already set, left unchanged"
   elif grep -Eq "^[[:space:]]*ConceptVotes__NetworkTagSecret=" bifrost.env; then
     echo "ConceptVotes__NetworkTagSecret present with an empty value: fix by hand, nothing appended"; exit 1
   else
     v=$(openssl rand -hex 32) || { echo "openssl failed, nothing appended"; exit 1; }
     [ "${#v}" -ge 32 ] || { echo "generated value shorter than 32, nothing appended"; exit 1; }
     cp -a bifrost.env bifrost.env.pre-<UTC> && printf "\nConceptVotes__NetworkTagSecret=%s\n" "$v" >> bifrost.env
   fi && chown bifrost bifrost.env && chmod 600 bifrost.env'
   ```
   The generated value is checked before anything is written: a failed `openssl`, or a value under 32 characters, appends
   nothing. The guard makes a re-run safe: an existing `ConceptVotes__NetworkTagSecret=` line, with or without leading
   spaces, is never overwritten or duplicated, and the skip message prints no value. A line with an empty (or
   whitespace-only) value counts as not set and is reported, not repaired: the command stops with a message and the
   operator fixes the line by hand (an empty value would otherwise sit beside a new one; systemd's `EnvironmentFile` takes
   the last duplicate, so do not leave two). A present line shorter than 32 characters is likewise not replaced by this
   command: fix it deliberately, with the rotation warning below in mind. A commented-out line (`#`) is not a key and does
   not count.
   (the same, with the value from the operator's own source, for `Heimdall__AppSecret`, which is not generated here).
   The file ends mode 600, owner `bifrost`, and the backup `bifrost.env.pre-<UTC>` beside it keeps the old content.
   Re-run the name-only check after, and check the length by count only:
   `sudo -n awk -F= '$1=="ConceptVotes__NetworkTagSecret"{print length($0)-length($1)-1}' /srv/bifrost/env/bifrost.env` is 32 or more.
   **Generate it once; do not rotate it casually.** It is the key of the audit `NetworkTag` that `ConceptAppraisal` stores
   per vote (`src/Bifrost.Web/Data/BifrostDbContext.cs:546`). A different secret does not touch votes, the
   `(SubjectId, VoterKey)` uniqueness or sign-in, but tags already stored no longer match tags computed for the same
   network, so `PurgeAsync` (`src/Bifrost.Web/Features/ConceptVotes/ConceptAppraisalService.cs:154`, equality on
   `NetworkTag`) misses the old rows; only new casts and re-stanced rows carry the new tag. On this first deploy no vote row
   has a tag from the new code, so nothing is orphaned; after it, treat a change as a purge-affecting event.
4. Upload the tarball; compare its SHA-256 to the build's.
5. `sudo -n bash /home/gamecultadmin/deploy-bifrost-alpha.sh <sha40>`. It unpacks into `<UTC>-<sha7>`, writes `REVISION`,
   repoints `current`, restarts. The restart applies both migrations.
6. Watch the start in the real logs: `tail -n +<baseline+1> /srv/bifrost/log/bifrost.stdout.log` and the same for
   `bifrost.stderr.log`, until the host reports listening on `127.0.0.1:5080`; no `Failed executing DbCommand`, no
   `Bifrost startup configuration is incomplete`, no `Heimdall ... refused` at start. `journalctl -u bifrost.service -n 50
   --no-pager` shows only start, stop and restart events. A start that loops is a failure: roll back, do not wait.
7. Live check (next section).
8. Record the deploy in `gamecult-ops` `inventory.md` (Bifrost alpha live state): release directory, full sha, date.

## Live check

Done from outside nginx, with a real browser session, within the window. A 200 on `/` is not a check.

- `cat /srv/bifrost/app/current/REVISION` is the target sha; `systemctl is-active bifrost.service` is `active`.
- **Cookies (#11).** After the first sign-in, the browser's cookie list shows the `__Host-bifrost_*` names, each Secure with
  Path `/` and no Domain, and no old `bifrost_*` name is set again. The old sessions are gone by design.
- **Login.** Sign in with GitHub; then with Discord (exercises `Heimdall__AppSecret`: the start must reach Heimdall and
  return to `/auth/heimdall/callback`). A Discord or Patreon sign-in that comes back refused or loops is the new-secret
  failure. Data Protection having kept its keys shows as no `key ... was not found` line (name not verified) in the stderr log
  after the restart; the old sessions do not survive either way (see "What moves").
- **Vote read.** The public concept-vote read endpoint answers with the existing tallies, and the tallies equal those
  noted in step 1 (the schema moved; the data must not).
- **Persona picture.** Open a Persona's page and confirm its picture loads and comes from the host, not a stale cache. A broken
  image with a healthy `/` is the finding this check exists for. **Unknown: which app serves that picture.** `AvatarUrl` appears at
  `src/Bifrost.Web/Domain/BifrostModels.cs:261` and `src/Bifrost.Web/Program.cs:192`, so the picture is probably an
  external avatar URL; unverified. Name the serving app and the page before the window, or drop the check.
- **Checkout page.** Load one Stripe checkout page (start a checkout and stop at the Stripe-hosted page) and confirm it
  renders with the expected product and amount. Do not pay: no card is entered and no live payment is made.
- Member console, work board, motions and members pages load (runbook section 7).
- The error rate over five minutes: the number of lines added to `bifrost.stderr.log` and `bifrost.stdout.log` since the
  baseline in step 1 is small and stops growing; read what was added, not just the count.

## Rollback

Rollback signs members out again (the old release reads the old cookie names); say so if it happens. Trigger: the start loops or migrates with errors, any live check fails, or the root window closes with a doubt open.

1. `sudo -n ln -sfn /srv/bifrost/app/releases/20261010043032-67072b2 /srv/bifrost/app/current && sudo -n chown -h
   bifrost:bifrost /srv/bifrost/app/current && sudo -n systemctl restart bifrost.service`.
2. Restore the previous env file if step 3 changed it (`bifrost.env.pre-<UTC>`).
3. Re-run the "before" checks from step 1: revision, `active`, counts equal.
4. The two new migrations stay applied. They add tables the old release ignores, as rehearsed in Precondition 4.
5. Only if the schema or data is damaged (not merely new): the runbook section 8 restore, from the verified dump. This is
   the last resort and needs the same root window.
6. Leave the failed release directory for diagnosis; do not delete it in the window.

## What this plan does not do

It does not deploy, does not add an Idunn recipe, and does not touch `bifrost-watchdog-notice.timer`. The
Idunn recipe is the owner of making this repeatable; until then each deploy follows this page and the runbook.

**Host facts in this plan that no one re-verified in this revision:** the key-ring file count, modes and dates (lines
~64, ~82-85), the stderr/stdout line counts and last-write dates (~135-136), the `bifrost-watchdog-notice.timer`
cadence and unit names (~122-124), and that `bifrost` cannot read `/root` (~91) were read from the host on 2026-11-09 by
an earlier pass or taken from the host's reports; this revision made no host read. The body also states these as fact
without a host check in this revision, and they are unverified too: the `bifrost` database role has no CREATEDB (~77),
there is no `dotnet ef` on the host (~87), logs go to files via `StandardOutput=append:` and the journal shows only
systemd's own lines (~134-136, 148-149), the script path `/home/gamecultadmin/deploy-bifrost-alpha.sh` (~144), and that
no Idunn recipe exists for Bifrost (~20). Treat each as unverified until checked in the window.
