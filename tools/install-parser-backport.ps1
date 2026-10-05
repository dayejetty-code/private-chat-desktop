$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$release=Join-Path $root 'artifacts/release-0.10.12'
$record=Join-Path $root ('artifacts/local-upgrade-0.10.12/'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
$package=Get-Content -LiteralPath (Join-Path $release 'package-local.json') -Raw | ConvertFrom-Json
$setup=Join-Path $release $package.installer
$install=Join-Path $env:LOCALAPPDATA 'Programs/Private Chat'
$profile=Join-Path $env:LOCALAPPDATA 'PrivateChatDesktop'
if ((Get-FileHash -LiteralPath $setup).Hash -ne $package.sha256) { throw 'Installer changed' }
New-Item -ItemType Directory -Path $record -Force | Out-Null
foreach ($process in @(Get-Process PrivateChat -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $install 'PrivateChat.exe') })) {
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(20000)) { throw 'Close the running application normally before updating' }
}
function Get-ProfileHashes {
    if (-not (Test-Path -LiteralPath $profile)) { return @() }
    $items=@(Get-ChildItem -LiteralPath $profile -Recurse -Force)
    if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Unexpected link in profile; preservation needs review' }
    return @( $items | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName | ForEach-Object { [pscustomobject]@{path=[IO.Path]::GetRelativePath($profile,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash;bytes=$_.Length} } )
}
$before=@(Get-ProfileHashes)
$before | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $record 'profile-before.json')
$p=Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LANG=chinesesimplified','/TASKS=desktopicon',('/DIR="'+$install+'"'),('/LOG="'+(Join-Path $record 'installer.log')+'"')) -WindowStyle Hidden -PassThru
if (-not $p.WaitForExit(180000)) { throw 'Installer still running; inspect before continuing' }
if ($p.ExitCode -ne 0) { throw ('Installer failed: '+$p.ExitCode) }
$after=@(Get-ProfileHashes)
$after | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $record 'profile-after.json')
if (($before | ConvertTo-Json -Depth 4 -Compress) -cne ($after | ConvertTo-Json -Depth 4 -Compress)) { throw 'Profile changed during installation; inspect preservation records' }
foreach ($file in $package.payload) {
    if ((Get-FileHash -LiteralPath (Join-Path $install $file.path)).Hash -ne $file.sha256) { throw ('Installed payload mismatch: '+$file.path) }
}
$linkPath=Join-Path ([Environment]::GetFolderPath('Desktop')) 'Private Chat.lnk'
if (-not (Test-Path -LiteralPath $linkPath)) { throw 'Missing desktop shortcut' }
$link=(New-Object -ComObject WScript.Shell).CreateShortcut($linkPath)
if ($link.TargetPath -ne (Join-Path $install 'PrivateChat.exe') -or $link.Arguments) { throw 'Desktop shortcut target differs' }
$registration=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8B1CA174-E752-4DDC-92D7-D93A84052B7E}_is1'
if ($registration.DisplayVersion -ne '0.10.12') { throw 'Wrong registered version' }
$run=(Get-Content -LiteralPath (Join-Path $root 'tests/security-fixes-0.10.12/backport-latest-run.txt') -Raw).Trim()
& (Join-Path $root 'tests/maintenance-install-smoke.ps1') -InstallPath $install -ReportPath (Join-Path $record 'native-smoke.json') -Version '0.10.12' -BrokerPath (Join-Path $run 'isolation/Probe.exe')
$smoke=Get-Content -LiteralPath (Join-Path $record 'native-smoke.json') -Raw | ConvertFrom-Json
if ($smoke.status -ne 'passed') { throw 'Installed native smoke did not pass' }
if (($before | ConvertTo-Json -Depth 4 -Compress) -cne (@(Get-ProfileHashes) | ConvertTo-Json -Depth 4 -Compress)) { throw 'Profile changed during synthetic smoke' }
[ordered]@{status='passed';version='0.10.12';installerSha256=$package.sha256;assemblySha256=$package.sourceAssemblySha256;coreSha256=$package.coreSha256;profileUnchanged=$true;profileFiles=$before.Count;payloadFiles=$package.payload.Count;desktopShortcut=$linkPath;nativeSmokePassed=$true;at=[DateTime]::UtcNow.ToString('o');scope='Installed payload, shortcut, registration and synthetic core; real profile not decrypted or migrated'} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $record 'installation-result.json')
$record | Set-Content -LiteralPath (Join-Path $release 'latest-install-record.txt')
Get-Content -LiteralPath (Join-Path $record 'installation-result.json')
exit 0
