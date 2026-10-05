param([Parameter(Mandatory)][string]$RunDirectory, [string]$WorkerDirectory = 'stable', [switch]$Network, [string[]]$OnlyFlags = @())
$ErrorActionPreference = 'Stop'
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
$qa = Join-Path $run 'qa'
$env:PRIVATECHAT_QA_WORKER = Join-Path $run ($WorkerDirectory + '/PrivateChat.exe')
$sha = (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($env:PRIVATECHAT_QA_WORKER, '.dll'))).Hash
$results = @()
$suites = if ($Network) {
    [ordered]@{'--privacy-network-test'='privacy-network-qa.json'; '--file-test --network'='file-qa.json'; '--conversation-test --network'='conversation-network-qa.json'}
} else {
    [ordered]@{'--privacy-test'='privacy-qa.json'; '--key-test'='key-qa.json'; '--text-file-test'='text-file-qa.json'; '--file-test'='file-qa.json'; '--cache-test'='cache-qa.json'; '--conversation-test'='conversation-qa.json'; '--deletion-test'='deletion-qa.json'; '--erase-test'='erase-qa.json'}
}
$suffix = if ($Network) { 'network' } else { 'local' }
foreach ($flag in $OnlyFlags) { if (-not $suites.Contains($flag)) { throw ('Unknown suite: ' + $flag) } }
foreach ($entry in $suites.GetEnumerator()) {
    if ($OnlyFlags.Count -gt 0 -and $entry.Key -notin $OnlyFlags) { continue }
    $started = [DateTime]::UtcNow
    $logBase = Join-Path $run ($WorkerDirectory + '-' + $suffix + '-' + [IO.Path]::GetFileNameWithoutExtension($entry.Value) + '-' + $started.ToString('yyyyMMdd-HHmmss'))
    $process = Start-Process -FilePath (Join-Path $qa 'PrivateChat.exe') -ArgumentList $entry.Key -WorkingDirectory $qa -WindowStyle Hidden -RedirectStandardOutput ($logBase + '.stdout.log') -RedirectStandardError ($logBase + '.stderr.log') -PassThru
    if (-not $process.WaitForExit(1500000)) { Stop-Process -Id $process.Id -Force; throw ('Suite timeout: ' + $entry.Key) }
    $path = Join-Path $qa $entry.Value
    if ((Get-Item -LiteralPath $path).LastWriteTimeUtc -lt $started) { throw ('Stale suite report: ' + $entry.Key) }
    $data = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    $copy = Join-Path $run ($WorkerDirectory + '-' + $suffix + '-' + $entry.Value)
    Copy-Item -LiteralPath $path -Destination $copy
    $result = [pscustomobject]@{flag=$entry.Key;status=$data.status;exitCode=$process.ExitCode;checks=@($data.checks).Count;report=$copy;stdout=($logBase + '.stdout.log');stderr=($logBase + '.stderr.log');workerAssemblySha256=$sha}
    $results += $result
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run ($WorkerDirectory + '-' + $suffix + '-checks.json')) -Encoding utf8
    $result | ConvertTo-Json -Compress
    if ($process.ExitCode -ne 0 -or $data.status -ne 'passed') { throw ('Suite failed: ' + $entry.Key + ' ' + $data.reason) }
}
exit 0
