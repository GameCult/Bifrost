# Heimdall app secret from a file

`Heimdall:AppSecret` (`Heimdall__AppSecret`) is Bifrost's app secret for Heimdall slug `bifrost`; it is sent as the
`x-heimdall-app-secret` header. Set through the environment it rides in the process environment. `Heimdall:AppSecretFile`
(`Heimdall__AppSecretFile`) names a file holding it instead.

- File set: Bifrost reads it once at startup and one trailing newline is trimmed. The file wins over `Heimdall__AppSecret`.
- File set but missing, unreadable, a directory, or empty: startup fails in every environment. The message names
  `Heimdall:AppSecretFile` and the path, never the secret.
- Only `Heimdall__AppSecret` set: still works; startup logs a deprecation warning (without the value).
- Neither set: Discord and Patreon sign-in stay off, as before.

Under systemd, pass the file with `LoadCredential=heimdall-app-secret:<root-owned file>` and
`Environment=Heimdall__AppSecretFile=%d/heimdall-app-secret`.

Code: `HeimdallOptions.ResolveAppSecret` (`src/Bifrost.Web/Configuration/HeimdallOptions.cs`), wired in `Program.cs`, forced at
startup by `StartupConfigurationValidator.Validate`. Tests: `tests/Bifrost.Web.Tests/HeimdallAppSecretFileTests.cs`.
