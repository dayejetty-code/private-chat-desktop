$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$profile=Join-Path $PSScriptRoot ('legacy-upgrade-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $profile | Out-Null
$password=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
function Start-CoreTest([string]$exe,[string[]]$parameters) {
 $info=[Diagnostics.ProcessStartInfo]::new($exe);$info.UseShellExecute=$false;$info.CreateNoWindow=$true
 $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardInputEncoding=[Text.UTF8Encoding]::new($false);$info.StandardOutputEncoding=[Text.Encoding]::UTF8
 foreach($parameter in $parameters){$info.ArgumentList.Add($parameter)}
 return [Diagnostics.Process]::Start($info)
}
function Invoke-CoreTest($process,$request) {
 $process.StandardInput.WriteLine(($request|ConvertTo-Json -Compress -Depth 10));$process.StandardInput.Flush()
 do {
  $line=$process.StandardOutput.ReadLineAsync()
  if(-not $line.Wait(15000)){throw 'Core timeout'}
  if($null -eq $line.Result){throw ('Core exited: '+$process.StandardError.ReadToEnd())}
  $reply=$line.Result|ConvertFrom-Json
 }while($reply.id -ne $request.id)
 if($reply.fault){throw ('Core rejected: '+$reply.fault)}
 return $reply.data
}
$old=Start-CoreTest (Join-Path $root 'dist-v0.10.3/PrivateChat.exe') @('--worker')
try {
 $created=Invoke-CoreTest $old @{id=1;op='init';path=(Join-Path $profile 'chat');password=$password}
 $user=Invoke-CoreTest $old @{id=2;op='cmd';command='/_create user {"profile":{"displayName":"SyntheticLegacy","fullName":""},"pastTimestamp":false}'}
 if($created.type -ne 'ok' -or $user.result.user.userId -ne 1){throw 'Legacy creation failed'}
 Write-Output 'Legacy ordinary-process profile created'
}finally{$old.StandardInput.Close();if(-not $old.WaitForExit(5000)){$old.Kill($true);$old.WaitForExit()};$old.Dispose()}
$new=Start-CoreTest (Join-Path $PSScriptRoot 'probe/Probe.exe') @('--installer-broker',(Join-Path $root 'dist-v0.10.4/PrivateChat.exe'))
try {
 $hello=$new.StandardOutput.ReadLineAsync();if(-not $hello.Wait(15000) -or $null -eq $hello.Result){throw 'Broker failed'}
 Write-Output 'Sandbox broker started'
 $opened=Invoke-CoreTest $new @{id=3;op='init';path=(Join-Path $profile 'chat');password=$password}
 $user=Invoke-CoreTest $new @{id=4;op='cmd';command='/u'}
 if($opened.type -ne 'ok' -or $user.result.user.localDisplayName -ne 'SyntheticLegacy'){throw 'Legacy reopen failed'}
 [ordered]@{status='passed';check='0.10.3 ordinary-process encrypted profile reopens in the 0.10.4 AppContainer with original user';workerAssemblySha256=(Get-FileHash (Join-Path $root 'dist-v0.10.4/PrivateChat.dll')).Hash;syntheticRoot=$profile}|ConvertTo-Json|Set-Content (Join-Path $PSScriptRoot 'results/legacy-upgrade.json') -Encoding utf8
 Write-Output 'PASS legacy profile reopens in the new sandbox'
}finally{$new.StandardInput.Close();if(-not $new.WaitForExit(5000)){$new.Kill($true);$new.WaitForExit()};$new.Dispose();$password=$null}
