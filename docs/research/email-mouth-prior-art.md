# Bifrost email mouth: prior art

Served: eureka-body:ruling:payment-mints-invite-bifrost-ledger-email-mouth and the fork on outbound mail (self-hosted SMTP vs transactional provider). Eyes pass, facts only; [U] marks unverified. Gaps not researched: provider suspension behaviour for new senders, DPAs, .NET SDKs, IP warm-up and blocklists, invite/recovery token lifetimes across platforms.

# Eyes: email mouth prior art (2026-10-30)
[U] = unverified / secondary source only. Web facts came via WebFetch summaries (small-model extraction), not raw page reads.

## Q4 The body (read first, local)
- Yggdrasil provider: Netcup KVM, 16 vCPU, 62 GiB, Debian 13, ufw active, public ports 22/80/443 only. F:\Projects\gamecult-ops\inventory.md:605,619,629; docs\research\2026-10-22-matrix-stand-up-facts.md:73. Netcup SCP is the console fallback (inventory.md:626).
- DNS: gamecult.org zone on Hetzner DNS (NS hydrogen/oxygen/helium.ns.hetzner.com); agents hold no Hetzner DNS credential, DNS edits are operator-done; no wildcard, no CAA. gamecult-ops\docs\gamecult-chat-map.md:23; inventory.md:1075,1090; runbooks\forgejo-eureka-yggdrasil.md:24.
- Other domains: erycina.org exists (runbooks\erycina-org-site-deploy.md, forgejo-erycina-yggdrasil.md); its DNS host not checked [U]. gamecult.com/.net/.dev: no hit in inventory.md.
- Existing mail setup / MX / SPF / DKIM / DMARC records: grep of gamecult-ops inventory.md, runbooks, docs for MX|SPF|DKIM|DMARC|smtp|postfix|mailgun|postmark|"port 25" found none documented. Only mail mention: runbooks\forgejo-erycina-yggdrasil.md:104 "Mailer - no notifications configured. Enabling it means owning deliverability and an SMTP credential." Live DNS not queried (no dig/DoH run) [U].
- Port 25 on Yggdrasil: not documented in gamecult-ops; ufw lists inbound ports only. Not tested (no ssh by constraint).
- Bifrost (origin/main): no Smtp, MailKit, MimeKit, IEmailSender, SendGrid hit in src/*.cs, *.csproj, *.cshtml. Only "email" hit in code: src/Bifrost.Web/Program.cs:187 (GitHub OAuth scope "user:email"). Checkout is Stripe: src/Bifrost.Web/Features/Patronage/StripeCheckoutService.cs, StripeWebhookService.cs, Configuration/StripeOptions.cs; HttpClient to https://api.stripe.com/ (Program.cs:135); route /patronage/{project}/checkout (Program.cs:431).
- Bifrost UserAccount (src/Bifrost.Web/Domain/BifrostModels.cs:243): fields Id, BifrostIdentity, NormalizedBifrostIdentity, GitHubUserId, HeimdallAccountId, GitHubLogin, NormalizedGitHubLogin, DisplayName, AvatarUrl, CreatedAtUtc, LastSeenAtUtc + nav props (read to ~line 275). No Email field seen. Bifrost currently stores no email per account.
- docs/research/customer-onboarding-body-facts.md (Bifrost origin/main) :29 "no email invite (invitations are GitHub-login keyed)"; :59 no email/password/magic-link sign-in in Heimdall's provider list; sign-in is Discord role, Patreon tier, GitHub, or native register.
- Heimdall (F:\Projects\Heimdall, HEAD): carries optional `primaryEmail` from OAuth identity into account create/update: src/app.ts:381,1048,1052,1078; src/backend-handoff.ts:8; src/oauth.ts:27; src/store/in-memory.ts:81. No SMTP/mailer found in the grep (head -8 only, not exhaustive) [U].
- Forgejo on Yggdrasil: mailer disabled (see above).

## Q1 Self-hosted outbound
- Gmail sender guidelines (https://support.google.com/a/answer/81126), all senders: SPF or DKIM; valid forward+reverse DNS (PTR) on sending IPs; TLS; spam rate <0.3% (recommend <0.10%); RFC 5322. Bulk (>5,000/day): both SPF and DKIM, DMARC published (p=none ok), From aligned with SPF or DKIM domain, one-click unsubscribe (List-Unsubscribe + List-Unsubscribe-Post) for marketing/subscribed mail only. DKIM key >=1024, 2048 recommended. Volume here (tens-hundreds/month) is far below the 5,000/day bulk line, so only the all-senders rules apply by the page's wording.
- Gmail rejection codes 4.7.29-4.7.31 for non-compliance [U] (secondary summary of the Google page).
- Outlook.com high-volume sender requirements (5,000/day, SPF/DKIM/DMARC, reject action): fetch failed to return body; only title/keywords seen [U]. https://techcommunity.microsoft.com/blog/microsoftdefenderforoffice365blog/strengthening-email-ecosystem-outlook%E2%80%99s-new-requirements-for-high%E2%80%90volume-senders/4399730
- Yahoo: not fetched; secondary sources say same 2024 requirements as Google [U] (https://help.close.com/docs/requirements-for-bulk-email-senders).
- Netcup port 25: conflicting sources, no official Netcup page retrieved. serverkueche.de tutorial says new servers ship with SCP firewall policy "netcup Mail block" blocking outbound 25, 465, 587 (silent timeout), removable in SCP by deactivating the template (https://serverkueche.de/en/tutorials/netcup-firewall-setup/); netcup community forum thread (Aug 2026 per search summary) agrees default firewall blocks mail ports (https://forum.netcup.de/thread/22247-netcup-firewall-blockiert-smtp-ports-warum/); netcup.discount (third party) claims port 25 open by default (https://www.netcup.discount/en/guides/port-25-unblock). All [U]. Note this SCP firewall is separate from Yggdrasil's ufw.
- PTR/rDNS on Netcup is set in SCP (per netcup.discount guide) [U].
- IP reputation/warm-up/blocklists for VPS ranges: not researched beyond Mox README: "Large or cheap cloud providers often don't maintain good reputations or may block all outgoing SMTP"; new machines may have SMTP blocked initially, opened after cool-down or support contact (https://github.com/mjl-/mox).
- Stacks:
  - Mox (Go, MIT, Mechiel Lukkien, NLnet/EU funded; SMTP in/out, IMAP, webmail, SPF/DKIM/DMARC, DANE, MTA-STS, TLSRPT, ACME; `mox quickstart` prints DNS records; README recommends dedicated machine + DNSSEC resolver; smarthost via Transport in mox.conf + Routes in domains.conf; if smarthost add its IPs to SPF) https://github.com/mjl-/mox . 1,105 commits, 196 open issues at fetch.
  - Stalwart (Rust, AGPL-3.0 + Enterprise SELv2, ~85% features AGPL; SMTP/IMAP/JMAP/CalDAV; DMARC/DKIM/SPF/ARC/DANE/MTA-STS; queues, routing rules; weekly community releases; only vouched contributors, bugfix/translation PRs) https://github.com/stalwartlabs/stalwart
  - Maddy (Go, GPL-3.0; replaces Postfix+Dovecot+OpenDKIM etc.; IMAP "beta"; commit/release recency not shown in fetch; smarthost support not stated on page) https://github.com/foxcpp/maddy
  - Postfix+OpenDKIM, Chasquid: not researched.
- Unattended-operation failure modes: not found in sourced material beyond Mox README note on IP reputation. Stalwart fetch at stalw.art/docs redirected without content.

## Q2 Transactional providers (pricing fetched from vendor pages 2026-10-30)
- Postmark: Free 100 emails/mo, no overages, no expiry; no tier between 100 and 10,000/mo; Basic $15/mo for 10,000; inbound on Pro $16.50 and Platform $18 only; data residency not stated on pricing page; new accounts start on free plan, no approval text on page. https://postmarkapp.com/pricing
- Amazon SES: $0.10/1,000 outbound + $0.12/GB attachments; no permanent free email quota on page (new-AWS-customer credits up to $200, 6 months); inbound $0.10/1,000 + $0.09/1,000 chunks; dedicated IP $24.95/mo. Sandbox/production-access text not on page [U]. https://aws.amazon.com/ses/pricing/ . EU regions exist (eu-west-1 etc.) [U not fetched].
- Mailgun: Free $0, 100 emails/day, 1 domain, 1 inbound route, 1-day log retention; Basic $15/mo for 10,000, 5 inbound routes; "send through the US or the EU from a single account". https://www.mailgun.com/pricing/
- Resend: Free 3,000/mo with 100/day cap, 3 domains; Pro $20/mo for 50,000; multi-region sending on all plans (EU not named on page); inbound on all plans via webhooks, counts toward quota. https://resend.com/pricing
- Mailjet: Free 6,000/mo, 200/day; Starter $9/mo 8,000; EU hosting not stated on page; GDPR/DPA link present; inbound = "Parse API", limits not stated. https://www.mailjet.com/pricing/
- Brevo: pricing page fetch gave no numbers. Search summaries: free 300/day, SMTP relay smtp-relay.brevo.com:587, Brevo branding on free plan mails, French company with EU data centres, paid tiers conflicting ($9/$25) [U]. https://www.brevo.com/en/bulk-email-service/ ; inbound parsing mentioned on page, details not seen.
- Scaleway TEM (search summary of scaleway.com docs/pricing; direct fetch 403): EUR 0.25/1,000 pay-as-you-go, free 300/mo, billed per recipient; Scale plan 100K emails + dedicated IP ~EUR 80/mo (French site) [U]. Pages: https://scaleway.com/en/pricing/managed-services , https://www.scaleway.com/en/docs/transactional-email/reference-content/tem-capabilities-and-limits/ . Inbound: not found [U]. Scaleway is French (EU residency) [U].
- Account suspension/approval behaviour for new senders, .NET SDK vs SMTP relay per provider, DPAs: not retrieved for any provider (all of these offer SMTP relay and REST API per general knowledge [U]).

## Q3 Hybrid (self-hosted inbound + provider smarthost)
- Mox documents the pattern directly (Transport/Routes smarthost, SPF include of relay IPs, caveat: relay may rewrite Message-ID or reject multiple DKIM signatures). https://github.com/mjl-/mox
- Frequency of the pattern in practice: only generic search hits on "Smart host" (https://en.wikipedia.org/wiki/Smart_host); no prevalence data found.

## Q5 Account-security prior art
- OWASP Forgot Password Cheat Sheet (https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html): tokens from CSPRNG; single use and expire after an appropriate period (no number given); invalidate outstanding links after success; tie token to user; rate-limit requests per account; identical response and timing whether or not account exists; do not build URLs from Host header; HTTPS; Referrer-Policy no-referrer on the reset page; do not auto-login after reset; do not lock account on forgot attempts; page refers to Password Storage cheat sheet for storage (hashing not mandated on page).
- Not researched: GitHub/Slack/Discord/Notion invite and recovery lifetimes, email as recovery factor for OAuth-only accounts, OWASP magic-link-specific guidance, NIST 800-63B out-of-band authenticator (10-minute lifetime is my recollection) [U].
- Related local prior art: Loomio single-use email invite link, no expiry found [U] (Bifrost docs/research/customer-onboarding-prior-art.md:37).

## Gaps
Live DNS (MX/SPF/DMARC) not queried; Yggdrasil port 25 untested; Outlook/Yahoo primary pages not read; provider suspension policies, DPAs, .NET SDKs, EU-hosted extras (e.g. Mailjet/Brevo/Scaleway residency confirmation), account-security prior art mostly unresearched (tool budget).
