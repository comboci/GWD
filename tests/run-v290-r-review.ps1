# OWNER ACTION ONLY. No command is run against an unspecified revision or by default.
# Windows smoke is opt-in; this script never publishes or deploys.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommit,
    [switch]$RunSource,
    [switch]$RunWindowsSmoke,
    [switch]$ConfirmDisposableVm
)
$ErrorActionPreference = 'Stop'
if (-not ($RunSource -or $RunWindowsSmoke)) {
    throw 'Specify -RunSource and/or -RunWindowsSmoke. No checks executed.'
}
if ($RunWindowsSmoke -and (-not $ConfirmDisposableVm -or $env:OS -ne 'Windows_NT')) {
    throw 'Windows smoke requires -ConfirmDisposableVm in an authorized disposable Windows test VM/account. It can touch HKCU and local test services.'
}
$root = Split-Path $PSScriptRoot -Parent
function Invoke-Checked([string]$Exe, [string[]]$CommandArgs) {
    Write-Host ('> ' + $Exe + ' ' + ($CommandArgs -join ' '))
    & $Exe @CommandArgs
    if ($LASTEXITCODE -ne 0) { throw "$Exe exited with code $LASTEXITCODE" }
}
Push-Location -LiteralPath $root
try {
    $head = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or "$head".Trim() -ne $ExpectedCommit) { throw 'HEAD differs from the owner-selected candidate.' }
    $status = & git status --porcelain=v1 --untracked-files=all
    if ($LASTEXITCODE -ne 0 -or $status) { throw 'Use a clean disposable checkout. Local sources/build inputs are modified or untracked.' }
    $manifest = Get-Content -Raw tests/v290-r-source-contract.json | ConvertFrom-Json
    $release = Get-Content -Raw deployment-manager.version.json | ConvertFrom-Json
    if ($manifest.release -ne ("V" + ($release.version -split '\.')[0] + "-R")) { throw 'Checkout version and source contract differ.' }
    if ($RunSource) {
        Get-Command node -ErrorAction Stop | Out-Null
        foreach ($path in @(
            'tests/verify-static.cjs',
            'tests/verify-iis-safety.cjs',
            'tests/verify-ui-performance.cjs',
            'tests/verify-ui-performance-phase-02.cjs',
            'tests/verify-ui-performance-phase-03.cjs',
            'tests/verify-ui-performance-phase-04.cjs',
            'tests/verify-ui-performance-phase-05.cjs',
            'tests/verify-ui-performance-phase-06.cjs',
            'tests/verify-gwd-final-architecture-07.cjs',
            'tests/verify-v290-r-source-contract.cjs',
            'tests/verify-gwd-final-remediation-08.cjs'
        )) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required source verification file missing: $path" }
            Invoke-Checked -Exe 'node' -CommandArgs @($path)
        }
        Write-Host 'Requested source contracts completed. This is not build/runtime evidence.'
    } else { Write-Host 'Source contract execution: NOT_RUN.' }
    if ($RunWindowsSmoke) {
        Get-Command dotnet -ErrorAction Stop | Out-Null
        Invoke-Checked -Exe 'dotnet' -CommandArgs @('--info')
        Invoke-Checked -Exe 'dotnet' -CommandArgs @('build','DeploymentManager.sln','-c','Release')
        $smoke = 'src/DeploymentManager.SmokeTests/DeploymentManager.SmokeTests.csproj'
        foreach ($arg in @('--ui-performance-phase-02','--ui-performance-phase-03','--ui-performance-phase-04',
            '--ui-performance-phase-05','--ui-performance-phase-06','--diagnostic-history','--sql-strategy','--workspace-sync')) {
            Invoke-Checked -Exe 'dotnet' -CommandArgs @('run','--project',$smoke,'-c','Release','--no-build','--',$arg)
        }
        Invoke-Checked -Exe 'dotnet' -CommandArgs @('run','--project',$smoke,'-c','Release','--no-build')
        foreach ($project in @('tests/IisSafety.Tests/IisSafety.Tests.csproj','tests/ReleaseVersionRegression/ReleaseVersionRegression.csproj',
            'tests/CodeQuality.Tests/CodeQuality.Tests.csproj','tests/CodeQuality.WindowsTests/CodeQuality.WindowsTests.csproj')) {
            Invoke-Checked -Exe 'dotnet' -CommandArgs @('run','--project',$project,'-c','Release')
        }
        Write-Host 'Requested Windows smoke commands completed. Manual visual/DPI and IIS/SQL acceptance remain separate.'
    } else { Write-Host 'Build and Windows smoke: NOT_RUN.' }
    Write-Host 'Deployment, rollback against real data and IIS/SQL acceptance: NOT_RUN.'
} finally {
    Pop-Location
}
