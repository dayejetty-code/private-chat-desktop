param([string]$Version='0.10.4')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version'}
$release=Join-Path $root ('artifacts\release-'+$Version)
$package=Get-Content (Join-Path $release 'package-local.json') -Raw|ConvertFrom-Json
$setup=Join-Path $release $package.installer
if((Get-FileHash $setup).Hash -ne $package.sha256){throw 'Installer changed'}
$testRoot=Join-Path $root ('tests\installer-qa\'+[guid]::NewGuid().ToString('N'))
$install=Join-Path $testRoot 'installed'
$profile=Join-Path $testRoot 'profile'
$desktopLink=Join-Path ([Environment]::GetFolderPath('Desktop')) 'Private Chat.lnk'
$registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B1CA174-E752-4DDC-92D7-D93A84052B7E}_is1'
# This test must not replace a real installation or shortcut.
if((Test-Path $registry) -or (Test-Path $desktopLink)){throw 'A real installation or desktop shortcut already exists; use a separate Windows test account'}
New-Item -ItemType Directory $testRoot,$profile -Force|Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Assert($condition,[string]$name){if(-not $condition){throw $name};$checks.Add($name);Write-Output ('PASS '+$name)}
function HashProfile([string]$path){@((Get-ChildItem -LiteralPath $path -File -ErrorAction SilentlyContinue|Sort-Object Name|ForEach-Object {$_.Name+':'+(Get-FileHash $_.FullName).Hash})) -join '|'}
$userData=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PrivateChatDesktop'
$userBefore=HashProfile $userData
function RunSetup([string]$log){
 $p=Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LANG=chinesesimplified',('/DIR="'+$install+'"'),('/GROUP="Private Chat 安装测试 '+[IO.Path]::GetFileName($testRoot)+'"'),('/LOG="'+(Join-Path $testRoot $log)+'"')) -WindowStyle Hidden -PassThru -Wait
 return $p.ExitCode
}
$script:worker=$null
function StartWorker {
 if([version]$Version -ge [version]'0.10.4') {
  $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'tests/network-isolation/probe/Probe.exe'))
  $info.ArgumentList.Add('--installer-broker');$info.ArgumentList.Add((Join-Path $install 'PrivateChat.exe'))
  $info.UseShellExecute=$false;$info.CreateNoWindow=$true
  $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
  $info.StandardInputEncoding=[Text.UTF8Encoding]::new($false);$info.StandardOutputEncoding=[Text.Encoding]::UTF8
  $info.Environment['DOTNET_ROOT']='C:\PrivateChat-Missing-Runtime'
  $script:broker=[Diagnostics.Process]::Start($info)
  $script:stderr=$script:broker.StandardError.ReadToEndAsync()
  $hello=$script:broker.StandardOutput.ReadLineAsync()
  if(-not $hello.Wait(15000) -or $null -eq $hello.Result) { throw 'Installed sandbox did not start' }
  $script:worker=[Diagnostics.Process]::GetProcessById(($hello.Result|ConvertFrom-Json).workerPid)
  return
 }
 $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $install 'PrivateChat.exe'),'--worker')
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true
 $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardInputEncoding=[Text.UTF8Encoding]::new($false);$info.StandardOutputEncoding=[Text.Encoding]::UTF8
 $info.Environment['DOTNET_ROOT']='C:\PrivateChat-Missing-Runtime'
 $script:worker=[Diagnostics.Process]::Start($info)
 $script:stderr=$script:worker.StandardError.ReadToEndAsync()
}
function StopWorker {
 if($script:broker) {
  try{$script:broker.StandardInput.Close();if(-not $script:broker.WaitForExit(8000)){$script:broker.Kill($true);$script:broker.WaitForExit()}}
  finally{$script:broker.Dispose();$script:broker=$null;if($script:worker){$script:worker.Dispose();$script:worker=$null}}
  return
 }
 if($script:worker){
  try{$script:worker.StandardInput.Close();if(-not $script:worker.WaitForExit(8000)){$script:worker.Kill($true);$script:worker.WaitForExit()}}
  finally{$script:worker.Dispose();$script:worker=$null}
 }
}
function CallCore($request){
 $pipeProcess=if($script:broker){$script:broker}else{$worker}
 $pipeProcess.StandardInput.WriteLine(($request|ConvertTo-Json -Depth 10 -Compress));$pipeProcess.StandardInput.Flush()
 do {
  $line=$pipeProcess.StandardOutput.ReadLineAsync()
  if(-not $line.Wait(15000)){throw 'Installed worker timed out'}
  if($null -eq $line.Result){throw 'Installed worker disconnected'}
  $response=$line.Result|ConvertFrom-Json
 }while($response.id -ne $request.id)
 if($response.fault){throw 'Installed worker rejected synthetic request'}
 return $response.data
}
try {
 Assert ((RunSetup 'fresh.log') -eq 0) 'Fresh per-user installation succeeds without elevation'
 Assert (Test-Path (Join-Path $install 'PrivateChat.exe')) 'Application installed to isolated directory'
 foreach($file in $package.payload){if((Get-FileHash (Join-Path $install $file.path)).Hash -ne $file.sha256){throw ('Installed payload differs: '+$file.path)}}
 Assert $true 'Every installed payload file matches the verified package staging manifest'
 $link=(New-Object -ComObject WScript.Shell).CreateShortcut($desktopLink)
 Assert ($link.TargetPath -eq (Join-Path $install 'PrivateChat.exe') -and $link.Arguments -eq '') 'Desktop shortcut points to the installed application'
 $registration=Get-ItemProperty $registry
 Assert ($registration.DisplayVersion -eq $Version) 'Windows per-user uninstall registration contains the expected version'
 StartWorker
 $password=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
 $prefix=Join-Path $profile 'chat'
 $created=CallCore @{id=1;op='init';path=$prefix;password=$password}
 Assert ($created.type -eq 'ok') 'Installed native core creates an isolated encrypted profile'
 $createdUser=CallCore @{id=2;op='cmd';command='/_create user {"profile":{"displayName":"SyntheticInstaller","fullName":""},"pastTimestamp":false}'}
 Assert ($createdUser.result.user.userId -eq 1) 'Installed core commands work without external runtime installation'
 $runtime=@($worker.Modules|Where-Object ModuleName -eq 'coreclr.dll'|Select-Object -ExpandProperty FileName)
 Assert ($runtime.Count -eq 1 -and $runtime[0].StartsWith($install,[StringComparison]::OrdinalIgnoreCase)) 'Installed worker loads the bundled .NET runtime'
 $dllHash=(Get-FileHash (Join-Path $install 'PrivateChat.dll')).Hash
 $blocked=RunSetup 'running-upgrade.log'
 Assert ($blocked -ne 0 -and -not $worker.HasExited) 'Upgrade refuses to overwrite a running application and does not kill it'
 Assert ((Get-FileHash (Join-Path $install 'PrivateChat.dll')).Hash -eq $dllHash) 'Refused upgrade leaves the installed program unchanged'
 StopWorker
 $beforeUpgrade=HashProfile $profile
 Assert ((RunSetup 'upgrade.log') -eq 0) 'Reinstallation updates program files after the application exits'
 Assert ((HashProfile $profile) -eq $beforeUpgrade) 'Upgrade preserves encrypted synthetic profile files byte-for-byte'
 StartWorker
 $opened=CallCore @{id=3;op='init';path=$prefix;password=$password}
 $user=CallCore @{id=4;op='cmd';command='/u'}
 Assert ($opened.type -eq 'ok' -and $user.result.user.localDisplayName -eq 'SyntheticInstaller') 'Encrypted profile reopens after an installer upgrade'
 StopWorker
 $beforeUninstall=HashProfile $profile
 $unknown=Join-Path $install 'synthetic-user-note.txt'
 [IO.File]::WriteAllText($unknown,'SYNTHETIC-USER-FILE-KEEP')
 $uninstaller=Join-Path $install 'unins000.exe'
 $p=Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $testRoot 'uninstall.log')+'"')) -WindowStyle Hidden -PassThru -Wait
 Assert ($p.ExitCode -eq 0) 'Uninstall completes without elevation'
 Assert (-not (Test-Path (Join-Path $install 'PrivateChat.exe')) -and -not (Test-Path $desktopLink) -and -not (Test-Path $registry)) 'Uninstall removes program, desktop shortcut and application registration'
 Assert ((Test-Path $unknown) -and (HashProfile $profile) -eq $beforeUninstall) 'Uninstall preserves unknown user files and encrypted profile'
 Assert ((HashProfile $userData) -eq $userBefore) 'Existing actual user profile files were not modified by installer testing'
 [ordered]@{status='passed';version=$Version;installerSha256=$package.sha256;checks=$checks.ToArray();scope='Silent installation, in-use blocking, same-version upgrade, actual installed native core and uninstall; no real user profile opened';testRoot=$testRoot} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $release 'installer-qa-local.json') -Encoding utf8
} catch {
 [ordered]@{status='failed';reason=$_.Exception.Message;checks=$checks.ToArray();testRoot=$testRoot} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $release 'installer-qa-local.json') -Encoding utf8
 throw
} finally {StopWorker;$password=$null}
