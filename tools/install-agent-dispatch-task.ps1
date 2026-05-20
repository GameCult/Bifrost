param(
  [string] $TaskName = "Bifrost Agent Dispatch",
  [string] $Repo = "*",
  [string] $Agent = "",
  [int] $IntervalMinutes = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$node = (Get-Command node.exe -ErrorAction Stop).Source
$script = Join-Path $repoRoot "tools\dispatch-agent-requests.mjs"

if (-not (Test-Path $script)) {
  throw "Missing dispatcher script at $script"
}

$argumentList = @(
  "`"$script`"",
  "dispatch",
  "--repo", "`"$Repo`"",
  "--max", "1"
)
if (-not [string]::IsNullOrWhiteSpace($Agent)) {
  $argumentList += @("--agent", "`"$Agent`"")
}

$action = New-ScheduledTaskAction `
  -Execute $node `
  -Argument ($argumentList -join " ") `
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
