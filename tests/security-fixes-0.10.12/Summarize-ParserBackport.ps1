param([Parameter(Mandatory)][string]$RunDirectory, [switch]$FinalizeEvidence)
$ErrorActionPreference = 'Stop'
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$reports = @(
    'candidate-local-privacy-qa.json','candidate-local-key-qa.json','candidate-local-text-file-qa.json',
    'candidate-local-file-qa.json','candidate-local-cache-qa.json','candidate-local-conversation-qa.json',
    'candidate-local-deletion-qa.json','candidate-local-erase-qa.json',
    'candidate-acceptance-qa-result.json','candidate-acceptance-privacy-ui-qa.json',
    'candidate-acceptance-file-ui-qa.json','candidate-acceptance-conversation-ui-qa.json',
    'candidate-acceptance-deletion-ui-qa.json','candidate-acceptance-ui-security-qa.json',
    'candidate-isolation-direct.json','candidate-isolation-obfs4.json','candidate-isolation-snowflake.json',
    'candidate-boundary.json','candidate-store-compatibility.json',
    'candidate-network-privacy-network-qa.json','candidate-network-file-qa.json',
    'candidate-network-conversation-network-qa.json','candidate-interop.json','patched-parser.json'
)
$suites = foreach ($name in $reports) {
    $path = Join-Path $run $name
    if (-not (Test-Path -LiteralPath $path)) { [pscustomobject]@{report=$name;status='missing';checks=0}; continue }
    $data = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    if ($name -eq 'candidate-network-file-qa.json' -and $data.status -eq 'passed' -and $data.networkRequested -ne $true) { throw 'Network acceptance requires an actual network run' }
    if ($name -eq 'patched-parser.json' -and ($data.status -ne 'passed' -or @($data.checks | Where-Object { -not $_.passed }).Count -gt 0)) { throw 'Patched native parser regression failed' }
    $count = if ($null -ne $data.totalChecks) { $data.totalChecks } else { @($data.checks).Count }
    [pscustomobject]@{report=$name;status=$data.status;checks=$count}
}
$baseline = Get-Content -Raw -LiteralPath (Join-Path $run 'baseline-summary.json') | ConvertFrom-Json
$patched = Get-Content -Raw -LiteralPath (Join-Path $run 'patched-summary.json') | ConvertFrom-Json
$integrity = Get-Content -Raw -LiteralPath (Join-Path $run 'runtime-integrity.json') | ConvertFrom-Json
$candidateSha = (Get-FileHash -LiteralPath (Join-Path $run 'candidate/PrivateChat.dll')).Hash
$coreSha = (Get-FileHash -LiteralPath (Join-Path $run 'candidate/runtime/core/libsimplex.dll')).Hash
$default = Get-Content -Raw -LiteralPath (Join-Path $repo 'dependencies.lock.json') | ConvertFrom-Json
$gatesPassed = @($suites | Where-Object status -ne 'passed').Count -eq 0 -and $baseline.expectedOutcomeObserved -and $patched.expectedOutcomeObserved -and @($integrity | Where-Object { $_.mismatches.Count -ne 0 }).Count -eq 0
$summary = [ordered]@{
    checkedUtc = [DateTime]::UtcNow.ToString('o')
    status = if ($gatesPassed) { 'tested-stable-backport-not-installed' } else { 'backport-acceptance-incomplete' }
    allAcceptanceGatesPassed = $gatesPassed
    passedAssertionCount = ($suites | Where-Object status -eq 'passed' | Measure-Object checks -Sum).Sum
    suites = $suites
    parserBaselineReproduced = $baseline.expectedOutcomeObserved
    parserPatchedPassed = $patched.expectedOutcomeObserved
    stableSourceCommit = 'b3907c9de7a2c596075762bf05ba3e1d2532aff1'
    upstreamPatchCommit = '33e3d94328dcd7f57611acedf463e1a27efc3e57'
    candidateAssemblySha256 = $candidateSha
    candidateCoreSha256 = $coreSha
    installedAppUnchanged = ((Get-FileHash -LiteralPath 'C:/Users/Lenovo/AppData/Local/Programs/Private Chat/PrivateChat.dll').Hash -eq '306E18546DABD7A6090EF76CD0B537B4DDA178148111EB60E8AD9456E56A2B8E')
    defaultCoreVersion = $default.simplex.version
    defaultBuildUsesBackport = ($default.simplex.nativeFiles.'libsimplex.dll' -eq $coreSha)
    installed = $false
    published = $false
    realProfileUsed = $false
    limitations = @('Native parser regression uses generated inputs, not a remote exploit demonstration','No new ETW, soak, sleep, second-device, code-signing or independent audit')
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'backport-summary.json')
$source = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src') -File | Where-Object Extension -in '.cs','.xaml','.csproj','.manifest')
$source += @(Get-ChildItem -LiteralPath (Join-Path $repo 'tests') -File -Filter '*.cs')
$source += @(Get-Item -LiteralPath (Join-Path $repo 'tools/build.ps1'),(Join-Path $repo 'dependencies.lock.json'))
$source += @(Get-Item -LiteralPath (Join-Path $repo 'README.md'),(Join-Path $repo 'BUILD.md'),(Join-Path $repo 'RELEASE-NOTES.md'),(Join-Path $repo 'SECURITY-PARSER-BACKPORT-0.10.12.md'),(Join-Path $repo 'installer/notices/COMPONENTS.txt'))
$source += @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -in '.ps1','.json')
$source += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'native-backport') -Recurse -File | Where-Object Extension -in '.ps1','.sh','.hs','.cabal','.patch','.json','.local','.config')
$source += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'interop') -File)
$source | ForEach-Object { [pscustomobject]@{path=[IO.Path]::GetRelativePath($repo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'source-sha256.json')
if ($FinalizeEvidence) {
    # Call only after the test processes have exited and released their logs.
    Get-ChildItem -LiteralPath $run -File | Where-Object Name -ne 'evidence-sha256.json' | ForEach-Object { [pscustomobject]@{path=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'evidence-sha256.json')
}
[pscustomobject]$summary | Select-Object status,allAcceptanceGatesPassed,passedAssertionCount,installedAppUnchanged,defaultBuildUsesBackport | ConvertTo-Json
