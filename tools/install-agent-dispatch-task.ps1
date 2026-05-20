param(
  [string] $TaskName = "Bifrost Agent Dispatch",
  [string] $Repo = "*",
  [string] $Agent = "",
  [int] $IntervalMinutes = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$script = Join-Path $repoRoot "tools\dispatch-agent-requests.mjs"
$hiddenLauncher = Join-Path $repoRoot "tools\run-agent-dispatch-hidden.vbs"

if (-not (Test-Path $script)) {
  throw "Missing dispatcher script at $script"
}
if (-not (Test-Path $hiddenLauncher)) {
  throw "Missing hidden dispatcher launcher at $hiddenLauncher"
}

$argumentList = @(
  "dispatch",
  "--repo", "`"$Repo`"",
  "--max", "1"
)
if (-not [string]::IsNullOrWhiteSpace($Agent)) {
  $argumentList += @("--agent", "`"$Agent`"")
}

$action = New-ScheduledTaskAction `
  -Execute "wscript.exe" `
  -Argument ("//B //Nologo `"$hiddenLauncher`" " + ($argumentList -join " ")) `
  -WorkingDirectory $repoRoot

$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
  -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
  -RepetitionDuration (New-TimeSpan -Days 3650)

$settings = New-ScheduledTaskSettingsSet `
  -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries `
  -MultipleInstances IgnoreNew `
  -ExecutionTimeLimit (New-TimeSpan -Minutes 5)

Register-ScheduledTask `
  -TaskName $TaskName `
  -Action $action `
  -Trigger $trigger `
  -Settings $settings `
  -Description "Claims Bifrost agent transport requests, starts target Codex turns, and posts dispatch receipts." `
  -Force | Out-Null

Write-Host "Installed scheduled task '$TaskName' every $IntervalMinutes minute(s)."
