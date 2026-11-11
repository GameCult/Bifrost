-- Read-only. Which existing UserAccounts hold one of the 28 reserved localparts
-- (BifrostIdentityService.ReservedIdentities). Written for review; not run on live.
-- Run it in a read-only transaction by whoever holds the database:
--   psql -v ON_ERROR_STOP=1 -c 'BEGIN READ ONLY' -f docs/oidc-reserved-accounts.sql
-- Keep the list in step with ReservedIdentities; the issuer refuses these at authorize (#49).
SELECT a."Id", a."BifrostIdentity", a."NormalizedBifrostIdentity", a."CreatedAtUtc", a."LastSeenAtUtc"
FROM "UserAccounts" a
WHERE lower(a."NormalizedBifrostIdentity") IN (
  'admin', 'aqua', 'deru', 'eureka', 'heimdall', 'ivixey', 'kiko', 'libby', 'meta', 'nibu', 'unnie', 'void', 'weksa',
  'root', 'system', 'matrix', 'synapse', 'support', 'bot', 'mod', 'moderator', 'postmaster', 'gamecult',
  'bifrost', 'idunn', 'odin', 'huginn', 'ratatoskr')
ORDER BY a."NormalizedBifrostIdentity";
