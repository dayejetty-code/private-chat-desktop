param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
$version = '0.10.12'
$expected = 'A7B4CDCF468933A958E1D688ACBD468EA15B240AEF864DDFA8629F8A16FF9952'
$expectedCore = '7B8F7219039884E746ABD63377ACF02020B79A16D2B4F269CD162E953C1BF2B5'
$candidate = Join-Path $run 'candidate'
$summary = Get-Content -LiteralPath (Join-Path $run 'backport-summary.json') -Raw | ConvertFrom-Json
if (-not $summary.allAcceptanceGatesPassed -or $summary.passedAssertionCount -ne 3183 -or $summary.candidateAssemblySha256 -ne $expected -or $summary.candidateCoreSha256 -ne $expectedCore) { throw 'Missing exact backport acceptance' }
foreach ($record in @(Get-Content -LiteralPath (Join-Path $run 'evidence-sha256.json') -Raw | ConvertFrom-Json)) {
    $path = [IO.Path]::GetFullPath((Join-Path $run $record.path))
    if (-not $path.StartsWith($run + '\', [StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $path).Hash -ne $record.sha256) { throw 'Acceptance evidence changed' }
}
foreach ($suite in $summary.suites) {
    $report = Get-Content -LiteralPath (Join-Path $run $suite.report) -Raw | ConvertFrom-Json
    if ($report.status -ne 'passed') { throw ('Failed suite: ' + $suite.report) }
    if ($report.workerAssemblySha256 -and $report.workerAssemblySha256 -ne $expected) { throw ('Wrong worker: ' + $suite.report) }
}
foreach ($record in @(Get-Content -LiteralPath (Join-Path $run 'source-sha256.json') -Raw | ConvertFrom-Json)) {
    if ($record.path -match '^src[\\/]' -or $record.path -eq 'dependencies.lock.json') {
        if ((Get-FileHash -LiteralPath (Join-Path $root $record.path)).Hash -ne $record.sha256) { throw ('Tested source changed: ' + $record.path) }
    }
}
if ((Get-FileHash -LiteralPath (Join-Path $candidate 'PrivateChat.dll')).Hash -ne $expected -or (Get-FileHash -LiteralPath (Join-Path $candidate 'runtime/core/libsimplex.dll')).Hash -ne $expectedCore) { throw 'Candidate changed' }
$manifest = Get-Content -LiteralPath (Join-Path $candidate 'runtime-manifest.json') -Raw | ConvertFrom-Json
foreach ($category in @('core','tor')) { foreach ($entry in $manifest.$category.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath (Join-Path $candidate $entry.Name)).Hash -ne $entry.Value) { throw ('Runtime changed: ' + $entry.Name) }
} }
$flagChecks = foreach ($flag in @('--self-test','--file-test','--file-ui-test','--text-file-test','--cache-test','--backup-test','--backup-network-test','--conversation-test','--conversation-ui-test','--deletion-test','--key-test','--erase-test','--privacy-test','--privacy-ui-test','--privacy-network-test','--deletion-ui-test','--ui-security-test','--supervision-probe')) {
    $p = Start-Process -FilePath (Join-Path $candidate 'PrivateChat.exe') -ArgumentList $flag -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit(15000)) { $p.Kill($true); throw 'Production test-flag check timed out' }
    if ($p.ExitCode -ne 64) { throw ('Production exposes a QA entry point: ' + $flag) }
    [pscustomobject]@{flag=$flag;exitCode=$p.ExitCode}
    $p.Dispose()
}
$stage = Join-Path $root ('artifacts/staging-' + $version + '-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $root ('artifacts/release-' + $version)
New-Item -ItemType Directory -Path $stage,$output -Force | Out-Null
Get-ChildItem -LiteralPath $candidate | Copy-Item -Destination $stage -Recurse
if (Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -match '\.db($|-)|\.log$|\.pdb$|\.pcbackup|^profile\.key$|instance\.lock$|qa-result|security-qa' }) { throw 'Private or QA files in payload' }
Copy-Item -LiteralPath (Join-Path $root 'installer/notices') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $root 'installer/安装说明.txt'),(Join-Path $root '使用说明.txt'),(Join-Path $root 'SECURITY-PARSER-BACKPORT-0.10.12.md') -Destination $stage -Force
& (Join-Path $PSScriptRoot 'create-installer-icon.ps1') | Out-Null
$compiler = Join-Path $PSScriptRoot 'inno-setup/ISCC.exe'
& $compiler ('/DPayloadDir=' + $stage) ('/DReleaseVersion=' + $version) ('/DOutputPath=' + $output) (Join-Path $root 'installer/PrivateChat.iss') *> (Join-Path $output 'compiler-local.log')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$setup = Join-Path $output ('PrivateChat-' + $version + '-Windows-x64-Setup.exe')
if ((Get-Item -LiteralPath $setup).Length -lt 1000000) { throw 'Installer unexpectedly small' }
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($stage,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
$hash = (Get-FileHash -LiteralPath $setup).Hash
[ordered]@{version=$version;installer=[IO.Path]::GetFileName($setup);sha256=$hash;bytes=(Get-Item -LiteralPath $setup).Length;payload=$files;stage=$stage;sourceAssemblySha256=$expected;coreSha256=$expectedCore;acceptanceRun=$run;acceptanceSummarySha256=(Get-FileHash -LiteralPath (Join-Path $run 'backport-summary.json')).Hash;passedAssertions=3183;productionFlags=$flagChecks;scope='Public development preview with stable parser backport; no new ETW/soak/sleep or independent audit'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'package-local.json') -Encoding utf8
($hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($setup)) | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
[pscustomobject]@{installer=$setup;sha256=$hash;payloadFiles=$files.Count;productionFlags=$flagChecks.Count} | ConvertTo-Json
exit 0
