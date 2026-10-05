param([Parameter(Mandatory)][string]$RunDirectory, [switch]$SkipNetwork)
$ErrorActionPreference = 'Stop'
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
$qa = Join-Path $run 'qa'
$env:PRIVATECHAT_QA_WORKER = Join-Path $run 'candidate/PrivateChat.exe'
$sha = (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($env:PRIVATECHAT_QA_WORKER, '.dll'))).Hash
$results = @()
# These reports are written by different suites. Run them sequentially because
# self-test briefly mutates its own runtime manifest to exercise integrity checks.
$suites = [ordered]@{
    '--self-test'='qa-result.json'
    '--privacy-ui-test'='privacy-ui-qa.json'
    '--file-ui-test'='file-ui-qa.json'
    '--conversation-ui-test'='conversation-ui-qa.json'
    '--deletion-ui-test'='deletion-ui-qa.json'
    '--ui-security-test'='ui-security-qa.json'
}
foreach ($entry in $suites.GetEnumerator()) {
    $started = [DateTime]::UtcNow
    $process = Start-Process -FilePath (Join-Path $qa 'PrivateChat.exe') -ArgumentList $entry.Key -WorkingDirectory $qa -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(240000)) { Stop-Process -Id $process.Id -Force; throw ('Suite timeout: ' + $entry.Key) }
    $path = Join-Path $qa $entry.Value
    if ((Get-Item -LiteralPath $path).LastWriteTimeUtc -lt $started) { throw ('Stale suite report: ' + $entry.Key) }
    $data = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    $copy = Join-Path $run ('candidate-acceptance-' + $entry.Value)
    Copy-Item -LiteralPath $path -Destination $copy
    $result = [pscustomobject]@{flag=$entry.Key;status=$data.status;exitCode=$process.ExitCode;checks=@($data.checks).Count;report=$copy;workerAssemblySha256=$sha}
    $results += $result
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'candidate-acceptance-checks.json') -Encoding utf8
    $result | ConvertTo-Json -Compress
    if ($process.ExitCode -ne 0 -or $data.status -ne 'passed') { throw ('Suite failed: ' + $entry.Key + ' ' + $data.reason) }
}
if (-not $SkipNetwork) {
    & (Join-Path $PSScriptRoot 'Run-Checks.ps1') -RunDirectory $run -WorkerDirectory candidate -Network
    if ($LASTEXITCODE -ne 0) { throw 'Network suites failed' }
}
exit 0
