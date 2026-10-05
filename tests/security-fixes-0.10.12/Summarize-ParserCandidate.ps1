param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$run=(Resolve-Path -LiteralPath $RunDirectory).Path
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$suites=@()
$reports=@(
 'candidate-local-privacy-qa.json','candidate-local-key-qa.json','candidate-local-text-file-qa.json',
 'candidate-local-file-qa.json','candidate-local-cache-qa.json','candidate-local-conversation-qa.json',
 'candidate-local-deletion-qa.json','candidate-local-erase-qa.json',
 'candidate-acceptance-qa-result.json','candidate-acceptance-privacy-ui-qa.json',
 'candidate-acceptance-file-ui-qa.json','candidate-acceptance-conversation-ui-qa.json',
 'candidate-acceptance-deletion-ui-qa.json','candidate-acceptance-ui-security-qa.json',
 'candidate-isolation-direct.json','candidate-isolation-obfs4.json','candidate-isolation-snowflake.json',
 'candidate-boundary.json','candidate-network-privacy-network-retry.json',
 'candidate-network-file-qa.json','candidate-network-conversation-network-qa.json','candidate-interop.json'
)
foreach ($name in $reports) {
 $path=Join-Path $run $name
 if (-not (Test-Path -LiteralPath $path)) { $suites+=@{report=$name;status='missing';checks=0}; continue }
 $r=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
 if ($name -eq 'candidate-network-file-qa.json' -and $r.status -eq 'passed' -and $r.networkRequested -ne $true) { throw 'Offline file report cannot stand in for network acceptance' }
 $count=if ($null -ne $r.totalChecks) { $r.totalChecks } else { @($r.checks).Count }
 $suites+=@{report=$name;status=$r.status;checks=$count}
}
$compat=Get-Content (Join-Path $run 'candidate-store-compatibility.json') -Raw | ConvertFrom-Json
$summary=[ordered]@{
 at=[DateTime]::UtcNow.ToString('o')
 status='evaluation-only-not-adopted'
 functionalSuitesPassed=(@($suites | Where-Object status -ne 'passed').Count -eq 0)
 passedAssertionCount=($suites | Where-Object status -eq 'passed' | Measure-Object -Property checks -Sum).Sum
 suites=$suites
 databaseRoundTripStatus=$compat.status
 databaseRoundTripReason=$compat.reason
 parserEvidence='Official release archive verified; matching tag source includes PR7614; native diagnostic string found. No direct malformed-forward injection or local upstream Haskell test run.'
 installedAppUnchanged=((Get-FileHash -LiteralPath 'C:/Users/Lenovo/AppData/Local/Programs/Private Chat/PrivateChat.dll').Hash -eq '306E18546DABD7A6090EF76CD0B537B4DDA178148111EB60E8AD9456E56A2B8E')
 defaultCoreVersion=(Get-Content (Join-Path $repo 'dependencies.lock.json') -Raw | ConvertFrom-Json).simplex.version
 candidateAssemblySha256=(Get-FileHash (Join-Path $run 'candidate/PrivateChat.dll')).Hash
 retryEvidence=@('sandbox-rejected-privacy-qa.json','candidate-network-privacy-timeout-first.json','candidate-network-file-timeout-first.json','candidate-interop-default-relay-timeout.json')
 limitations=@('Prerelease core','Downgrade refused by installed core','No new ETW/soak/sleep/second-device/independent audit','Not installed or packaged')
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'parser-summary.json') -Encoding utf8
$source=Get-ChildItem (Join-Path $repo 'src') -File | Where-Object Extension -in '.cs','.xaml','.csproj','.manifest'
$source+=Get-ChildItem (Join-Path $repo 'tests') -File -Filter '*.cs'
$source+=Get-Item (Join-Path $repo 'tools/build.ps1'),(Join-Path $repo 'dependencies.lock.json')
$source+=Get-ChildItem $PSScriptRoot -File | Where-Object Extension -in '.ps1','.json'
$source+=Get-ChildItem (Join-Path $PSScriptRoot 'interop') -File
$source | ForEach-Object { @{path=[IO.Path]::GetRelativePath($repo,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} } | ConvertTo-Json | Set-Content (Join-Path $run 'source-sha256.json') -Encoding utf8
Get-ChildItem $run -File | Where-Object Name -ne 'evidence-sha256.json' | ForEach-Object { @{path=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} } | ConvertTo-Json | Set-Content (Join-Path $run 'evidence-sha256.json') -Encoding utf8
[pscustomobject]$summary | Select-Object status,functionalSuitesPassed,passedAssertionCount,databaseRoundTripStatus,installedAppUnchanged,defaultCoreVersion | ConvertTo-Json
