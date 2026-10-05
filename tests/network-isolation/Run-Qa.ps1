param([ValidateSet('ui','network','files','file-ui','text','cache')][string]$Suite)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$qa=Join-Path $PSScriptRoot 'qa'
$results=Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$definitions=@{
 ui=@('--ui-security-test','ui-security-qa.json')
 network=@('--self-test --network --public-relays','qa-result.json')
 files=@('--file-test --network','file-qa.json')
 'file-ui'=@('--file-ui-test','file-ui-qa.json')
 text=@('--text-file-test','text-file-qa.json')
 cache=@('--cache-test','cache-qa.json')
}
$definition=$definitions[$Suite]
$env:PRIVATECHAT_QA_WORKER=Join-Path $root 'dist-v0.10.4/PrivateChat.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$started=[DateTime]::UtcNow
$process=Start-Process -FilePath (Join-Path $root 'tools/dotnet/dotnet.exe') -ArgumentList ('"'+(Join-Path $qa 'PrivateChat.dll')+'" '+$definition[0]) -WorkingDirectory $qa -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $results ($Suite+'.stdout.log')) -RedirectStandardError (Join-Path $results ($Suite+'.stderr.log'))
try {
 Write-Output "Started $Suite test, PID $($process.Id)"
 if(-not $process.WaitForExit(1200000)) { $process.Kill($true); throw 'QA exceeded 20 minute limit' }
 $report=Join-Path $qa $definition[1]
 if(-not (Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTimeUtc -lt $started) { throw 'Missing fresh report' }
 Copy-Item -LiteralPath $report -Destination (Join-Path $results $definition[1]) -Force
 $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
 [ordered]@{suite=$Suite;started=$started;ended=[DateTime]::UtcNow;exitCode=$process.ExitCode;status=$result.status;checks=@($result.checks).Count;workerAssemblySha256=(Get-FileHash (Join-Path $root 'dist-v0.10.4/PrivateChat.dll')).Hash;qaAssemblySha256=(Get-FileHash (Join-Path $qa 'PrivateChat.dll')).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results ($Suite+'-run.json')) -Encoding utf8
 Write-Output "$Suite status=$($result.status), checks=$(@($result.checks).Count), exit=$($process.ExitCode)"
 if($process.ExitCode -ne 0 -or $result.status -ne 'passed') { throw 'QA did not pass; retain failure evidence and inspect' }
} finally { $process.Dispose() }

