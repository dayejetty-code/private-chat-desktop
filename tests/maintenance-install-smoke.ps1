param([Parameter(Mandatory)][string]$InstallPath,[Parameter(Mandatory)][string]$ReportPath,[string]$Version='0.10.11',[string]$BrokerPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$InstallPath=[IO.Path]::GetFullPath($InstallPath)
$ReportPath=[IO.Path]::GetFullPath($ReportPath)
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version' }
if (-not $BrokerPath) { $BrokerPath=Join-Path $root 'tests/network-stability-0.10.11/isolation/bin/Probe.exe' }
$testRoot=Join-Path $root ('artifacts/local-upgrade-'+$Version+'/native-smoke-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force|Out-Null
$package=Get-Content (Join-Path $root ('artifacts/release-'+$Version+'/package-local.json')) -Raw|ConvertFrom-Json
$checks=[Collections.Generic.List[string]]::new()
function Assert($condition,[string]$message){if(-not $condition){throw $message};$checks.Add($message)}
foreach($file in $package.payload){Assert ((Get-FileHash -LiteralPath (Join-Path $InstallPath $file.path)).Hash -eq $file.sha256) ('Payload matches: '+$file.path)}
Assert ((Get-FileHash -LiteralPath (Join-Path $InstallPath 'PrivateChat.dll')).Hash -eq $package.sourceAssemblySha256) 'Exact tested production assembly'
Assert ((Get-Item -LiteralPath (Join-Path $InstallPath 'PrivateChat.exe')).VersionInfo.FileVersion -eq ($Version+'.0')) ('Executable version '+$Version)
$info=[Diagnostics.ProcessStartInfo]::new($BrokerPath)
$info.ArgumentList.Add('--installer-broker');$info.ArgumentList.Add((Join-Path $InstallPath 'PrivateChat.exe'))
$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$info.StandardInputEncoding=[Text.UTF8Encoding]::new($false);$info.StandardOutputEncoding=[Text.Encoding]::UTF8
$info.Environment['DOTNET_ROOT']='C:\PrivateChat-Missing-Runtime'
$broker=$null;$worker=$null;$password=$null
function CallCore($request){
 $broker.StandardInput.WriteLine(($request|ConvertTo-Json -Depth 8 -Compress));$broker.StandardInput.Flush()
 do{$line=$broker.StandardOutput.ReadLineAsync();if(-not $line.Wait(20000) -or $null -eq $line.Result){throw 'Installed core response timeout'};$response=$line.Result|ConvertFrom-Json}while($response.id -ne $request.id)
 if($response.fault){throw 'Installed core rejected request'}
 return $response.data
}
try{
 $broker=[Diagnostics.Process]::Start($info)
 $errorTask=$broker.StandardError.ReadToEndAsync()
 $hello=$broker.StandardOutput.ReadLineAsync()
 Assert ($hello.Wait(20000) -and $null -ne $hello.Result) 'Restricted installed worker starts'
 $worker=[Diagnostics.Process]::GetProcessById(($hello.Result|ConvertFrom-Json).workerPid)
 $password=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
 $opened=CallCore @{id=1;op='init';path=(Join-Path $testRoot 'chat');password=$password}
 Assert ($opened.type -eq 'ok') 'Native core opens a synthetic encrypted profile'
 $created=CallCore @{id=2;op='cmd';command='/_create user {"profile":{"displayName":"SyntheticMaintenanceInstall","fullName":""},"pastTimestamp":false}'}
 Assert ($created.result.user.userId -eq 1) 'Native core creates synthetic user'
 $read=CallCore @{id=3;op='cmd';command='/u'}
 Assert ($read.result.user.localDisplayName -eq 'SyntheticMaintenanceInstall') 'Native core reads synthetic user'
 $runtime=@($worker.Modules|Where-Object ModuleName -eq 'coreclr.dll'|Select-Object -ExpandProperty FileName)
 Assert ($runtime.Count -eq 1 -and $runtime[0].StartsWith($InstallPath+'\',[StringComparison]::OrdinalIgnoreCase)) 'Worker loads bundled runtime from installation'
 [ordered]@{status='passed';version=$Version;at=[DateTime]::UtcNow;installPath=$InstallPath;assemblySha256=$package.sourceAssemblySha256;payloadFiles=$package.payload.Count;checks=$checks;syntheticProfile=$testRoot;scope='Payload integrity and actual restricted native core from installation; no real user profile opened'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $ReportPath -Encoding utf8
 [pscustomobject]@{status='passed';payloadFiles=$package.payload.Count;checks=$checks.Count;report=$ReportPath}|ConvertTo-Json
}finally{
 $password=$null
 if($broker){try{$broker.StandardInput.Close();if(-not $broker.WaitForExit(10000)){$broker.Kill($true);$broker.WaitForExit()}}finally{$broker.Dispose()}}
 if($worker){$worker.Dispose()}
}
