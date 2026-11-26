# Run ONLY on a disposable Windows VM / test user. No release publish or deploy.
# Default runs source scripts; -RunWindowsSmoke also builds and runs the C# tests.
# A test Wizard can read/write HKCU and probe local IIS/SQL/HTTP; use an isolated VM.
param([switch]$RunWindowsSmoke, [switch]$ConfirmIsolatedVm)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
function Invoke-Checked([string]$Exe, [string[]]$CommandArgs) {
    Write-Host ('> ' + $Exe + ' ' + ($CommandArgs -join ' '))
    & $Exe @CommandArgs
    if ($LASTEXITCODE -ne 0) { throw "$Exe exited with $LASTEXITCODE" }
}
if ($RunWindowsSmoke -and (-not $ConfirmIsolatedVm)) {
    throw 'Windows smoke requires -ConfirmIsolatedVm on a disposable Windows VM/test account.'
}
if ($RunWindowsSmoke -and $env:OS -ne 'Windows_NT') {
    throw 'Windows smoke requires Windows.'
}
Get-Command node -ErrorAction Stop | Out-Null
foreach ($check in @(
    'tests/verify-static.cjs',
    'tests/verify-iis-safety.cjs',
    'tests/verify-ui-performance.cjs',
    'tests/verify-ui-performance-phase-02.cjs',
    'tests/verify-ui-performance-phase-03.cjs',
    'tests/verify-ui-performance-phase-04.cjs',
    'tests/verify-ui-performance-phase-05.cjs',
    'tests/verify-ui-performance-phase-06.cjs'
)) { Invoke-Checked -Exe 'node' -CommandArgs @($check) }
if (-not $RunWindowsSmoke) {
    Write-Host 'Source scripts completed. Build, C# tests and visual review NOT RUN.'
    exit 0
}
Get-Command dotnet -ErrorAction Stop | Out-Null
Invoke-Checked -Exe 'dotnet' -CommandArgs @('--info')
Invoke-Checked -Exe 'dotnet' -CommandArgs @('build','DeploymentManager.sln','-c','Release')
$smoke = 'src/DeploymentManager.SmokeTests/DeploymentManager.SmokeTests.csproj'
foreach ($arg in @('--ui-performance-phase-02','--ui-performance-phase-03','--ui-performance-phase-04',
    '--ui-performance-phase-05','--ui-performance-phase-06','--diagnostic-history','--sql-strategy','--workspace-sync')) {
    Invoke-Checked -Exe 'dotnet' -CommandArgs @('run','--project',$smoke,'-c','Release','--no-build','--',$arg)
}
Invoke-Checked -Exe 'dotnet' -CommandArgs @('run','--project',$smoke,'-c','Release','--no-build')
foreach ($p in @('tests/IisSafety.Tests/IisSafety.Tests.csproj','tests/ReleaseVersionRegression/ReleaseVersionRegression.csproj',
    'tests/CodeQuality.Tests/CodeQuality.Tests.csproj','tests/CodeQuality.WindowsTests/CodeQuality.WindowsTests.csproj')) {
    Invoke-Checked -Exe 'dotnet' -CommandArgs @('run','--project',$p,'-c','Release')
}
Write-Host 'Requested test commands completed. Manual visual/DPI and actual isolated IIS/SQL acceptance remain separate.'
