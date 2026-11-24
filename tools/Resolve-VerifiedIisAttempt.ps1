#requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param(
 [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,80}$')][string]$AttemptId,
 [Parameter(Mandatory)][string]$Site,
 [Parameter(Mandatory)][string]$Pool,
 [Parameter(Mandatory)][string]$ContentRoot,
 [Parameter(Mandatory)][string]$JournalRoot,
 [Parameter(Mandatory)][uri]$HealthUri,
 [Parameter(Mandatory)][string]$ExpectedVersion,
 [switch]$VerifiedBackupAndDatabase
)
$ErrorActionPreference='Stop'
if (!$VerifiedBackupAndDatabase) { throw 'Verify database, backup and deployed release before recovery.' }
if (!$HealthUri.IsLoopback -or $HealthUri.Scheme -notin @('http','https')) { throw 'Local health endpoint required.' }
$target=[IO.Path]::GetFullPath($ContentRoot).TrimEnd('\','/')
$volume=[IO.Path]::GetPathRoot($target)
if ($target -eq $volume.TrimEnd('\','/')) { throw 'Volume root cannot be a deployment target.' }
$lockRoot=Join-Path $env:ProgramData 'GenericWeb.DeploymentManager\TargetLocks'
function Assert-Plain([string]$path) {
 $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
 if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point: $path" }
}
function Assert-PlainParents([string]$path) {
 $parent=Split-Path -Parent $path
 while ($parent) { Assert-Plain $parent; $next=Split-Path -Parent $parent; if ($next -eq $parent) { break }; $parent=$next }
}
$sha=[Security.Cryptography.SHA256]::Create()
try {
 $keys=@("site:$Site","pool:$Pool","path:$target") | ForEach-Object {
  [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($_.ToUpperInvariant()))).Replace('-','')
 } | Sort-Object -Unique
} finally { $sha.Dispose() }
$locks=@($keys | ForEach-Object { Join-Path $lockRoot "$_.lock" }) + @("$target.gwd.lock",(Join-Path $volume '.gwd-content.lock'))
$markers=@($keys | ForEach-Object { Join-Path $lockRoot "$_.unresolved" }) + @("$target.gwd.unresolved",(Join-Path $volume '.gwd-content.unresolved'))
$leases=@()
try {
 foreach ($path in $locks) {
  Assert-PlainParents $path
  if (Test-Path -LiteralPath $path) { Assert-Plain $path }
  $leases += [IO.File]::Open($path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 }
 $journal=Join-Path $JournalRoot "$AttemptId.json"
 Assert-PlainParents $journal
 Assert-Plain $journal
 $j=Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
 if ($j.AttemptId -cne $AttemptId -or $j.Resolved -ne $true) { throw 'Journal is not explicitly resolved; manual recovery must be completed first.' }
 $appcmd=Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
 function Read-Iis([string[]]$arguments) {
  $result=& $appcmd @arguments
  if ($LASTEXITCODE -ne 0) { throw 'IIS inspection failed.' }
  return ($result -join '').Trim()
 }
 if ((Read-Iis @('list','site',"/name:$Site",'/text:state')) -ne 'Started' -or
     (Read-Iis @('list','apppool',"/name:$Pool",'/text:state')) -ne 'Started' -or
     (Read-Iis @('list','app',"/app.name:$Site/",'/text:applicationPool')) -ne $Pool -or
     [IO.Path]::GetFullPath((Read-Iis @('list','vdir',"/vdir.name:$Site/",'/text:physicalPath'))).TrimEnd('\','/') -ne $target) { throw 'IIS identity/state mismatch.' }
 $health=Invoke-WebRequest -Uri $HealthUri -UseBasicParsing -TimeoutSec 30 -MaximumRedirection 0
 if ($health.StatusCode -ne 200 -or $health.Content -notmatch [regex]::Escape($ExpectedVersion)) { throw 'Health or release identity mismatch.' }
 $owned=@()
 foreach ($path in $markers) {
  try { $attr=[IO.File]::GetAttributes($path) } catch [IO.FileNotFoundException] { continue }
  if ($attr -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) { throw "Unsafe marker: $path" }
  if ([IO.File]::ReadAllText($path).Trim() -cne $AttemptId) { throw "Different marker owner: $path" }
  $owned += $path
 }
 if (!$owned.Count) { Write-Output 'No matching recovery markers remain.'; return }
 if ($PSCmdlet.ShouldProcess(($owned -join '; '),'Archive resolved recovery markers')) {
  foreach ($path in $owned) {
   if ([IO.File]::ReadAllText($path).Trim() -cne $AttemptId) { throw "Marker changed: $path" }
   [IO.File]::Move($path,"$path.resolved-$AttemptId-$([guid]::NewGuid().ToString('N'))")
  }
  Write-Output 'Resolved markers archived. Journal and backup preserved. Retry deployment.'
 }
} finally { foreach ($lease in $leases) { $lease.Dispose() } }
