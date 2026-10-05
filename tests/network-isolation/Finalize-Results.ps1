$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$distribution=Join-Path $root 'dist-v0.10.4'
$results=Join-Path $PSScriptRoot 'results'
$hash=(Get-FileHash (Join-Path $distribution 'PrivateChat.dll')).Hash
$reports=[ordered]@{}
$count=0
$mapping=[ordered]@{network=@('nativeAndNetwork','qa-result.json');ui=@('ui','ui-security-qa.json');files=@('files','file-qa.json');'file-ui'=@('fileUi','file-ui-qa.json');text=@('textFiles','text-file-qa.json');cache=@('cache','cache-qa.json')}
foreach($suite in $mapping.Keys) {
 $run=Get-Content (Join-Path $results ($suite+'-run.json')) -Raw|ConvertFrom-Json
 $body=Get-Content (Join-Path $results $mapping[$suite][1]) -Raw|ConvertFrom-Json
 if($run.status -ne 'passed' -or $body.status -ne 'passed' -or $run.workerAssemblySha256 -ne $hash){throw ('Current regression is incomplete: '+$suite)}
 $reports[$mapping[$suite][0]]=$body;$count+=$run.checks
}
$isolation=[ordered]@{}
foreach($mode in @('direct','obfs4','snowflake')) {
 $body=Get-Content (Join-Path $results ('isolation-'+$mode+'.json')) -Raw|ConvertFrom-Json
 if($body.status -ne 'passed' -or $body.workerAssemblySha256 -ne $hash){throw ('Current OS isolation is incomplete: '+$mode)}
 $isolation[$mode]=$body
}
$history=@()
foreach($folder in @('Bridge Space','Final Bridge Space')) {
 foreach($file in Get-ChildItem (Join-Path $PSScriptRoot ($folder+'/runs')) -Recurse -Filter result.json) {
  $body=Get-Content $file.FullName -Raw|ConvertFrom-Json
  $history+=@{report=$file.FullName;data=$body}
 }
}
$bridges=[ordered]@{}
foreach($mode in @('obfs4','snowflake')) {
 $selected=$history|Where-Object {$_.data.mode -eq $mode -and $_.data.status -eq 'passed' -and $_.data.networkRequested -and $_.data.workerAssemblySha256 -eq $hash}|Sort-Object {$_.data.ended} -Descending|Select-Object -First 1
 if(-not $selected){throw ('No successful current production bridge delivery: '+$mode)}
 $bridges[$mode]=$selected.data
}
$legacy=Get-Content (Join-Path $results 'legacy-upgrade.json') -Raw|ConvertFrom-Json
$arguments=Get-Content (Join-Path $results 'production-args.json') -Raw|ConvertFrom-Json
if($legacy.status -ne 'passed' -or $legacy.workerAssemblySha256 -ne $hash -or $arguments.status -ne 'passed' -or $arguments.assemblySha256 -ne $hash){throw 'Legacy profile or production-argument check incomplete'}
$report=[ordered]@{version='0.10.4';status='local-tested-not-published';assembledAt=[DateTime]::UtcNow.ToString('O');assemblySha256=$hash;exeSha256=(Get-FileHash (Join-Path $distribution 'PrivateChat.exe')).Hash;workerUnderTest=(Join-Path $distribution 'PrivateChat.exe');regressionAssertionCount=$count}
foreach($key in $reports.Keys){$report[$key]=$reports[$key]}
$report.networkIsolation=$isolation;$report.legacyUpgrade=$legacy;$report.bridges=$bridges;$report.productionArgs=$arguments
$report.bridgeRunHistory=@($history|ForEach-Object {[ordered]@{report=$_.report;mode=$_.data.mode;status=$_.data.status;stage=$_.data.stage;reason=$_.data.reason;ended=$_.data.ended}})
$report.limitations=@('Synthetic profiles on one Windows machine; new physical cross-device acceptance remains pending','AppContainer restricts the core to its application package, not one TCP port; strict SOCKS policy and transport lifecycle select Tor','Tor and bridge helper retain Internet access; parent UI is not in AppContainer','No new comprehensive administrator ETW capture, independent audit or publisher code signature','Bridge cold starts and preset SMP relays had timeouts; retained attempts and bounded invitation retries must not be represented as universal availability','Windows filtering services/profiles must remain enabled; administrator or compromised OS can defeat the boundary')
$report|ConvertTo-Json -Depth 30|Set-Content (Join-Path $distribution 'security-qa.json') -Encoding utf8
Write-Output ('Validated current reports for '+$hash)
