# Anonymous vote stuffing: prior art

Serves the fork: concept-mill-votes Cut 4, anti-stuffing defence; campaign concept-mill-votes; 2026-10-10.
Subject text: "Anti-stuffing defence" in `F:\Projects\Bifrost\docs\concept-mill-votes-target.md` (lines ~106-130).
Gathered by Eyes. Facts with pointers; no recommendation. Items marked (secondary) were not read at the primary source. Search-engine summaries were not verified against the pages unless stated.

Gaps: (a) a query for residential-proxy and CAPTCHA-farm pricing was refused by the search tool, so no measured cost of a headless/proxy farm is here; (b) StrawPoll's own duplicate-check docs were not found; (c) Friendly Captcha's own licence/docs pages were not read; (d) ALTCHA's server-verification and PoW doc pages returned 404/empty; (e) national ePrivacy guidance beyond CNIL and ICO was not searched.

## 1. Anonymous and low-friction voting in established systems

- Poll software generally: duplicate checks are IP locking, cookie, or sign-in. Vendor docs say IP checks block shared networks (offices, schools), cookie checks are beaten by clearing cookies or switching browsers, and sign-in is strongest but still needs multi-account safeguards. Kentico: https://docs.kentico.com/k8/securing-websites/designing-secure-websites/securing-and-protecting-the-system/preventing-duplicate-poll-voting-and-content-rating . Academic restatement ("IP locking, cookie-based methods"): https://www.scitepress.org/PublishedPapers/2004/14026/14026.pdf (2004). WordPress poll-plugin thread reporting repeat votes despite "cookie and ip" logging: https://wordpress.org/support/?p=1278907
- StrawPoll: its own option documentation was not found; the design doc's StrawPoll statement is not confirmed by this pass.
- Reddit: "vote fuzzing" described by staff only via press (Daily Dot, https://dailydot.com/news/reddit-vote-count-percent-like/ , undated): displayed totals altered as an anti-spam measure. Mechanics (offsetting detected bot votes, paired fake votes) come from user/moderator posts only; no official algorithm page found (secondary).
- Hacker News: a "voting ring detector" demotes rather than kills (dang, 2014, https://news.ycombinator.com/item?id=7542985 ; community list https://github.com/minimaxir/hacker-news-undocumented ). Moderators have publicly rolled back a ring penalty as a false positive (https://hn.svelte.dev/item/47457689 ). Internals unpublished.
- Stack Exchange: help page "serial voting reversed" (https://stackoverflowteams.com/help/serial-voting-reversed): one user voting on many of another user's posts in a short window is judged invalid and removed; some removal by a daily automated process, some by staff after investigation. Thresholds not public. The page says a "voting corrected" event does not imply wrongdoing.
- Product Hunt: staff reply in https://www.producthunt.com/p/producthunt/vote-selling-on-product-hunt says they will not reveal vote weights or all signals; a zero-point rule for voters with no history was under consideration. Weighting by account age/activity is asserted only by third-party guides (secondary).
- Academic, Sybil resistance:
  - Douceur, "The Sybil Attack" (IPTPS 2002): absent a logically centralized authority, Sybil attacks can always be mounted barring unrealistic resource parity. https://courses.csail.mit.edu/6.895/fall02/papers/Douceur/iptps02-douceur.pdf . Identifying nodes by hash of IP address (CFS) is noted as implicitly relying on an authority.
  - SybilGuard/SybilLimit (Yu, Gibbons, Kaminsky, Xiao, 2008): bound accepted Sybil nodes using social-network structure (fast mixing). https://www.comp.nus.edu.sg/~yuhf/sybillimit-tr.pdf
  - SumUp (Tran, Min, Li, Subramanian, NSDI 2009): content-vote aggregation capping adversary votes at the number of attack edges in a trust graph, plus feedback-based reduction of misbehaving voters; applied to a Digg vote trace, found strong evidence of attack on many "popular" articles. https://static.usenix.org/events/nsdi09/tech/full_papers/tran/tran.pdf
  - All three need a social/trust graph or authority; none targets graph-less anonymous visitors.

## 2. Proof-of-work challenges

| Project | Licence | Self-host | .NET | Pointers |
|---|---|---|---|---|
| ALTCHA | MIT (widget README) | Yes | Official server list: TS, Dart, PHP, Go, Python, Java, Ruby, Elixir, Rust, C++ (no C#). Community: Ixnas.AltchaNet (0.4.x; SHA-256; replay store interface; bundled in-memory store "for testing only") and unofficial Altcha.Net 1.2.0 (replay store interface; in-memory not advised multi-server) | https://raw.githubusercontent.com/altcha-org/altcha/main/README.md ; https://www.nuget.org/packages/Altcha.Net ; https://www.nuget.org/packages/Ixnas.AltchaNet (via search, not opened) |
| mCaptcha | AGPL-3.0 | Yes, Docker Compose; separate Rust service | None found | https://github.com/mCaptcha/mCaptcha ; README: "active development", no releases listed, SHA-256 PoW, difficulty scales with load (about 2 s under attack), proof configs expire after 30 s and are single-use, cookie-free, IP-independent |
| Cap | Apache-2.0 | Yes, Docker/workers | None stated | https://github.com/tiagozip/cap ; README claims "gpu-resistant" PoW plus instrumentation challenges; no algorithm parameters; size claims are marketing |
| Friendly Captcha | Third-party listings: front end MPL-2.0 (unconfirmed); back end hosted commercial, a "lite" self-host server mentioned (unconfirmed) | Hosted service | Not checked | https://friendlycaptcha.com/insights/open-source-captcha/ ; https://openalternative.co/friendly-captcha (secondary) |
| Anubis | MIT, Go | Yes, reverse-proxy sidecar | n/a | https://github.com/TecharoHQ/anubis ; aimed at scrapers; README calls itself "a bit of a nuclear response" and notes it can block good bots |

- ALTCHA widget: PBKDF2/SHA variants bundled; Argon2id and Scrypt (memory-bound, "harder to accelerate on ASICs or GPUs") are separate imports; `minDuration` default 500 ms; `timeout` default 90 s; `humanInteractionSignature` on by default (mechanism not documented in the README). Ixnas.AltchaNet defaults: complexity 50000-100000, expiry 120 s (search summary).
- Argued or measured attacker cost:
  - Laurie and Clayton, "Proof-of-Work Proves Not to Work" (WEIS 2004): difficulty high enough to stop spam would stop significant numbers of legitimate senders, since attackers use hijacked machines and run continuously. https://www.cl.cam.ac.uk/~rnc1/proofwork2.pdf . Rebuttal, Liu and Camp, "Proof of Work can Work" (2006): works if work is weighted by a reputation function. https://sites.cs.ucsb.edu/~rich/class/cs293b-cloud/papers/lui-pow.pdf
  - Anubis: its author acknowledges PoW works best against crawlers that cannot run JavaScript; an operator with a solver keeps the authorization cookie and continues (LWN, https://lwn.net/Articles/1028558 ). A July 2026 CLI client (anubis-fetch) automates the challenge (secondary: https://runtimewire.com/article/anubis-bypass-proof-of-work-bot-wall-limits ).
  - No numbers (seconds per solve on commodity vs GPU hardware, attacker cost per 1000 votes) found for ALTCHA, mCaptcha or Cap.
- Reuse shape: mCaptcha tokens are single-use and short-lived; Anubis issues a cookie that covers later requests.

## 3. ASP.NET Core rate limiting and forwarded headers

Docs read: https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit (ms.date 2026-09-03; covers 7.0-12.0) and https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer (ms.date 2026-04-23).

- Rate limiter: `AddRateLimiter`, `GlobalLimiter` or named policies, `PartitionedRateLimiter.Create<HttpContext,string>`, `RateLimitPartition.GetFixedWindowLimiter` etc. The doc's "By IP address" sample keys on `Connection.RemoteIpAddress?.ToString() ?? "unknown"`. The page contains no IPv6 prefix guidance.
- Doc warnings: partitioning on user-controlled input can exhaust memory (each distinct key creates and caches a limiter); "if you create partitions by using client IP addresses, your app becomes vulnerable to Denial of Service attacks that use IP Source Address Spoofing" (cites BCP 38).
- `UseRateLimiter` must come after `UseRouting` when endpoint-specific policies/attributes are used; global-only may precede it.
- Time-based limiters do not refund a permit already taken when a later chained limiter rejects.
- `OnRejected`/`RejectionStatusCode` are needed for 429; the doc shows setting Retry-After manually or from lease metadata.
- Forwarded headers:
  - Middleware "should run before other middleware" (before `UseHsts`; may follow diagnostics/error handling). Anything reading `RemoteIpAddress`, including the rate limiter, must come after it to see the forwarded address.
  - Default `KnownNetworks` is a single loopback entry (`IPNetwork(IPAddress.Loopback, 8)`), and `KnownProxies` likewise loopback-only. Forwarded values are accepted only if the immediate peer matches one of the lists.
  - Default `ForwardLimit` is 1: only the rightmost `X-Forwarded-For` entry is processed (right to left). Null is advised only with known proxies/networks configured.
  - Doc: "Only allow trusted proxies and networks to forward headers. Otherwise, IP spoofing attacks are possible."
  - Dual-mode sockets: IPv4 entries must be given in IPv4-mapped IPv6 form; check `RemoteIpAddress` to see which applies.
  - `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` uses cloud-style settings and "doesn't enable features such as the KnownProxies option".
  - Unverified: whether newer .NET renames `KnownNetworks`; the page read lists `KnownNetworks` with `IPNetwork`.
- IPv6 prefix practice (practitioner sources only; no standard or vendor guidance found):
  - RFC 8981 privacy extensions rotate the interface id inside one /64 (preferred lifetime 1 day, valid 2 days, up to 3 concurrent). https://www.rfc-editor.org/rfc/rfc8981.html
  - A residential customer can obtain /56 or /48 via DHCPv6-PD, so per-/64 buckets can be spread across by one household. v6ops https://psg.com/lists/v6ops/v6ops.2010/msg00152.html ; NANOG https://mailman.nanog.org/pipermail/nanog/2015-October/079735.html
  - One ISP policy quoted: /56 broadband, /48 business, /64 mobile; aggregating too high can block tens of thousands of mobile users. Suggested escalating aggregation /64 to /48 to /40 to /36 to /32 on observed abuse. Nextcloud thread weighing /56 default: https://help.nextcloud.com/t/is-bruteforce-protection-ipv6-ready/215084
  - No source found for a /24 IPv4 partition choice.

## 4. Device/cookie identifiers

- Data Protection (https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings ; `.../configuration/overview`):
  - Default key storage: Azure Apps `%HOME%`, else user profile (`%LOCALAPPDATA%`, DPAPI on Windows), else IIS registry; "If none of these conditions match, keys aren't persisted outside of the current process. When the process shuts down, all generated keys are lost."
  - Docker: persist keys to a volume or external store. `SetApplicationName` must match across deployments (default discriminator is content-root path).
  - Default key lifetime 90 days with auto-roll; retired keys still decrypt while kept. "Deleting a key makes its protected data permanently inaccessible"; docs recommend not deleting.
  - A custom key repository disables automatic encryption of keys at rest.
  - Payload algorithms: AES-256-CBC + HMACSHA256 (encrypted and authenticated, not only signed).
- SameSite: Chrome treats cookies without a SameSite attribute as Lax from v80 (https://web.dev/articles/samesite-cookies-explained ). Chrome kept third-party cookies; Privacy Sandbox APIs retired Oct 2025 (secondary: https://engadget.com/cybersecurity/google-has-killed-privacy-sandbox-130029899.html ). Safari blocks third-party cookies by default, Firefox blocks known trackers (secondary, 2019). A same-origin vote page is first-party throughout.
- Safari ITP: 7-day cap applies to cookies written via `document.cookie`; WebKit's Wilander stated Secure HttpOnly server-set cookies are not affected (https://simoahava.com/privacy/first-party-cookies-webkit-revisited/ ; WebKit bugs 237350, 237474). Sources disagree on later script-cookie handling; none found capping server-set cookies.
- Private/incognito windows discard the cookie jar on close (browser behaviour; no primary source fetched).
- Regulators:
  - ICO, "What are the exceptions" (https://ico.org.uk/for-organisations/direct-marketing-and-privacy-and-electronic-communications/guidance-on-the-use-of-storage-and-access-technologies/what-are-the-exceptions/ ): PECR examples include "ensuring the security of terminal equipment" and "preventing or detecting fraud"; necessity is assessed "from the point of view of the subscriber or user, not your own"; a second purpose voids the exception; advertising never qualifies, including click-fraud detection.
  - CNIL cookies FAQ (https://www.cnil.fr/fr/cookies-et-autres-traceurs/regles/cookies/FAQ), Q16: fraud-prevention trackers are in general not exempt, but may be in particular cases such as securing a user authentication mechanism. Q17 (CAPTCHA): no consent if the sole purpose is securing the site or an authentication mechanism for the user's benefit; consent if data is reused for the provider's own ends (reCAPTCHA named) or purposes unspecified; where consent is required, refusal bypasses the protection.
  - EDPB Guidelines 2/2023 on Art. 5(3) ePrivacy (v2.0, 7 Oct 2024): applies to any storage/access on terminal equipment, technology-neutral, including identifiers other than cookies; lists "tracking based on IP only" as a use case. https://www.edpb.europa.eu/documents/guideline/guidelines-22023-on-technical-scope-of-art-53-of-eprivacy-directive_en . Its exemption text on identifiers was not read.
  - GDPR status of an HMAC'd network tag stored per row was not searched.

## 5. Known failure modes per layer

- Device token: cleared cookies, private windows and fresh profiles reset it (poll vendors, Kentico); a scripted client omits or discards it. Unpersisted Data Protection keys invalidate every issued token on each restart; deleted keys invalidate tokens signed with them.
- IP/prefix limits:
  - Shared NAT: poll vendors report schools and offices tripping IP checks. CGN measurement: Livadariu et al. infer 4.1K ASes deploy CGN (2014-2016 data); Mandalari et al. found CGN in 10% of 60+ tested ISPs; Richter et al. https://arxiv.org/abs/1605.05606 ; CAIDA https://www-old.caida.org/publications/papers/2018/inferring_carrier_grade_nat/ . Blocklists and address reuse: https://steel.isi.edu/members/sivaram/papers/blocklisting_impact.pdf . No CGN false-positive rate for voting found.
  - Mobile IPv6: carriers commonly assign a /64 per device (forum, secondary); whether a named carrier changes the prefix per session was not found. RFC 8981 rotates addresses within the /64.
  - Delegated prefixes larger than /64 span many buckets (v6ops/NANOG above).
  - Header trust: forged `X-Forwarded-For` is accepted only when the immediate peer is in KnownProxies/Networks; without `UseForwardedHeaders`, `RemoteIpAddress` is the proxy for every client (one bucket). IP-keyed partitions carry the doc's DoS/memory warning.
- Audit-and-purge: HN and Stack Exchange both reverse or demote by pattern and both document false positives (above); neither publishes thresholds.
- PoW: Laurie/Clayton and Anubis (above): attackers with solvers or botnets pay CPU; legitimate low-end devices pay too; a reusable cookie spreads one solve over many requests. .NET libraries require a replay store; in-memory stores are test-only/single-instance. Headless-farm cost: not gathered.
- Third-party challenge: CNIL Q17 names reCAPTCHA as requiring consent where the provider reuses data.

## Where the current design's assumptions are contradicted by evidence

1. The design lists proof-of-work as slowing scripts yet barely touching determined attackers; the cited literature agrees, but the design also says ALTCHA "needs no third party" and is "ALTCHA-style library served from Bifrost": ALTCHA has no official C# server library, only community packages that need a custom replay store for production.
2. Data Protection default key persistence is process-lifetime unless a profile/registry/volume exists; default key lifetime is 90 days, not one year. The one-year device cookie depends on retired keys being kept and `DataProtection__KeysPath` set (the doc's fact 22 flags the path; the doc does not state the key-loss consequence for tokens).
3. The design's /64 partition for IPv6 is described by practitioner sources as bypassable by one household holding a /56 or /48 and by privacy-extension rotation inside a /64; the opposite risk (aggregating above /64 on mobile) is documented as collateral damage. No evidence was found for the /24 IPv4 choice.
4. The design treats the device cookie as needing no consent notice; ICO and CNIL exempt security cookies only where security is the sole purpose and from the user's side, CNIL calls fraud-prevention trackers generally non-exempt (exception: securing an authentication mechanism), and ICO says users should still be informed. A vote-integrity cookie is not an authentication mechanism and was not addressed by either text found.
5. The design says large sites "rely on after-the-fact detection ... then discount or purge": confirmed for HN, Stack Exchange and (secondary) Reddit, but those systems also publish no thresholds, keep false-positive reversals, and Reddit/Product Hunt add vote fuzzing or voter-history weighting, which the design does not include.
