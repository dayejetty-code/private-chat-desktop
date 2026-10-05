param([Parameter(Mandatory)][string]$RunDirectory, [string]$WorkerDirectory = 'stable')
$ErrorActionPreference = 'Stop'
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
$broker = Join-Path $run 'isolation/Probe.exe'
$old = Join-Path $env:LOCALAPPDATA 'Programs/Private Chat/PrivateChat.exe'
$candidate = Join-Path $run ($WorkerDirectory + '/PrivateChat.exe')
$profile = Join-Path $run ('compat-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $profile | Out-Null
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$checks = [Collections.Generic.List[string]]::new()
$observations = [Collections.Generic.List[object]]::new()
$process = $null
function Start-Broker([string]$worker) {
    $info = [Diagnostics.ProcessStartInfo]::new($broker)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.ArgumentList.Add('--installer-broker'); $info.ArgumentList.Add($worker)
    $p = [Diagnostics.Process]::Start($info)
    $ready = $p.StandardOutput.ReadLineAsync()
    if (-not $ready.Wait(30000) -or -not ($ready.Result | ConvertFrom-Json).workerPid) { $p.Kill($true); throw 'Broker startup failed' }
    return $p
}
function Request($p, [hashtable]$value) {
    $value.id = [Guid]::NewGuid().ToString('N')
    $p.StandardInput.WriteLine(($value | ConvertTo-Json -Compress -Depth 8)); $p.StandardInput.Flush()
    $reply = $p.StandardOutput.ReadLineAsync()
    if (-not $reply.Wait(110000) -or -not $reply.Result) { throw 'Synthetic compatibility operation failed' }
    $response = $reply.Result | ConvertFrom-Json
    if ($response.id -ne $value.id) { throw 'Unexpected broker reply' }
    return $response.data
}
function Stop-Broker($p) {
    if (-not $p) { return }
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(15000)) { $p.Kill($true); throw 'Broker shutdown timeout' }
    $p.Dispose()
}
try {
    $process = Start-Broker $old
    $opened = Request $process @{op='init';path=(Join-Path $profile 'chat');password=$password}
    $observations.Add(@{stage='original-create';initialization=$opened})
    if ($opened.type -ne 'ok') { throw 'Original worker cannot initialize synthetic profile' }
    $observations.Add(@{stage='original-version';response=(Request $process @{op='cmd';command='/version'})})
    $null = Request $process @{op='cmd';command='/_create user {"profile":null,"pastTimestamp":false}'}
    $null = Request $process @{op='cmd';command='/sql chat CREATE TABLE compat_fixture (body TEXT)'}
    $null = Request $process @{op='cmd';command="/sql chat INSERT INTO compat_fixture VALUES ('SYNTHETIC-OLD-CORE')"}
    Stop-Broker $process; $process = $null
    $checks.Add('Installed 0.10.11 created the synthetic encrypted database')

    $process = Start-Broker $candidate
    $opened = Request $process @{op='init';path=(Join-Path $profile 'chat');password=$password}
    $observations.Add(@{stage='candidate-open';initialization=$opened})
    if ($opened.type -ne 'ok') { throw 'Candidate cannot open old database' }
    $observations.Add(@{stage='candidate-version';response=(Request $process @{op='cmd';command='/version'})})
    $result = Request $process @{op='cmd';command='/sql chat SELECT body FROM compat_fixture'}
    if (($result | ConvertTo-Json -Depth 12 -Compress) -notmatch 'SYNTHETIC-OLD-CORE') { throw 'Candidate cannot read old encrypted contents' }
    $checks.Add('Candidate opens and reads the old encrypted database')
    $null = Request $process @{op='cmd';command="/sql chat INSERT INTO compat_fixture VALUES ('SYNTHETIC-NEW-CORE')"}
    Stop-Broker $process; $process = $null
    $checks.Add('Candidate writes and closes its encrypted database')

    $process = Start-Broker $old
    $opened = Request $process @{op='init';path=(Join-Path $profile 'chat');password=$password}
    $observations.Add(@{stage='original-reopen';initialization=$opened})
    if ($opened.type -ne 'ok') { throw 'Old worker cannot reopen after candidate; rollback needs separate review' }
    $result = Request $process @{op='cmd';command='/sql chat SELECT body FROM compat_fixture'}
    $json = $result | ConvertTo-Json -Depth 12 -Compress
    if ($json -notmatch 'SYNTHETIC-OLD-CORE' -or $json -notmatch 'SYNTHETIC-NEW-CORE') { throw 'Round-trip contents differ' }
    $checks.Add('Original worker reopens and reads both old and candidate-written data')
    $status = 'passed'
} catch { $status = 'failed'; $reason = $_.Exception.Message }
finally {
    Stop-Broker $process
    $record = [pscustomobject]@{status=$status;reason=$reason;checks=$checks;observations=$observations;syntheticRoot=$profile;oldAssemblySha256=(Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($old,'.dll'))).Hash;candidateAssemblySha256=(Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($candidate,'.dll'))).Hash}
    $record | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $run ($WorkerDirectory + '-store-compatibility.json')) -Encoding utf8
    $record | Select-Object status,reason,@{Name='checks';Expression={$_.checks.Count}} | ConvertTo-Json -Compress
}
if ($status -ne 'passed') { exit 1 }
exit 0
