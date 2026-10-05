param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
foreach ($mode in @('direct', 'obfs4', 'snowflake')) {
    $started = [DateTime]::UtcNow
    $process = Start-Process -FilePath (Join-Path $run 'isolation/Probe.exe') -ArgumentList @((Join-Path $run 'candidate/PrivateChat.exe'), $mode) -WorkingDirectory (Join-Path $run 'isolation') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run "isolation-$mode.stdout.log") -RedirectStandardError (Join-Path $run "isolation-$mode.stderr.log")
    if (-not $process.WaitForExit(300000)) { Stop-Process -Id $process.Id -Force; throw "Isolation timeout: $mode" }
    $path = Join-Path $run 'isolation/isolation-qa.json'
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).LastWriteTimeUtc -lt $started) { throw "Missing fresh isolation report: $mode" }
    $data = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    Copy-Item -LiteralPath $path -Destination (Join-Path $run "candidate-isolation-$mode.json")
    [pscustomobject]@{mode=$mode;status=$data.status;checks=@($data.checks).Count;exitCode=$process.ExitCode} | ConvertTo-Json -Compress
    if ($process.ExitCode -ne 0 -or $data.status -ne 'passed') { throw "Isolation failed: $mode - $($data.reason)" }
}
exit 0
