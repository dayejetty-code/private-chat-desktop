param([string]$Version='0.10.4')
$ErrorActionPreference='Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') {throw 'Invalid version'}
$root=Split-Path -Parent $PSScriptRoot
$distribution=Join-Path $root ('dist-v'+$Version)
$qa=Get-Content (Join-Path $distribution 'security-qa.json') -Raw | ConvertFrom-Json
if ($qa.version -ne $Version -or $qa.nativeAndNetwork.status -ne 'passed' -or $qa.ui.status -ne 'passed') {throw 'Expected passed release checks'}
if ([version]$Version -ge [version]'0.9.0' -and ($qa.files.status -ne 'passed' -or -not $qa.files.networkRequested -or $qa.fileUi.status -ne 'passed')) {throw 'Expected passed encrypted file transfer and file UI checks'}
if ([version]$Version -ge [version]'0.10.0' -and $qa.textFiles.status -ne 'passed') {throw 'Expected passed text-only attachment checks'}
if ([version]$Version -ge [version]'0.10.2' -and $qa.cache.status -ne 'passed') {throw 'Expected passed cache and reservation regression checks'}
if ([version]$Version -ge [version]'0.10.5' -and [version]$Version -lt [version]'0.10.10' -and ($qa.backup.status -ne 'passed' -or $qa.backupUi.status -ne 'passed' -or $qa.backupNetwork.status -ne 'passed')) {throw 'Expected passed local backup, recovery, UI and restored-conversation checks'}
if ([version]$Version -ge [version]'0.10.6') {
    foreach ($suite in @('conversation','conversationUi','conversationNetwork')) {
        if ($qa.$suite.status -ne 'passed' -or $qa.$suite.workerAssemblySha256 -ne $qa.assemblySha256) {throw ('Expected current-worker unread and recovery checks: '+$suite)}
    }
}
if ([version]$Version -ge [version]'0.10.7') {
    foreach ($suite in @('deletion','deletionUi')) {
        if ($qa.$suite.status -ne 'passed' -or $qa.$suite.workerAssemblySha256 -ne $qa.assemblySha256) {throw ('Expected current-worker profile destruction checks: '+$suite)}
    }
}
if ([version]$Version -ge [version]'0.10.8') {
    if ($qa.keys.status -ne 'passed' -or $qa.keys.workerAssemblySha256 -ne $qa.assemblySha256) {throw 'Expected current-worker key migration/destruction checks'}
    if ([version]$Version -lt [version]'0.10.10' -and $qa.backupNetwork.workerAssemblySha256 -ne $qa.assemblySha256) {throw 'Expected current-worker restored-conversation checks'}
}
if ([version]$Version -ge [version]'0.10.9') {
    if ($qa.erasure.status -ne 'passed' -or $qa.erasure.workerAssemblySha256 -ne $qa.assemblySha256) {throw 'Expected current-worker overwrite and interrupted-destruction checks'}
}
if ([version]$Version -ge [version]'0.10.10') {
    foreach ($suite in @('privacy','privacyUi','privacyNetwork')) {
        if ($qa.$suite.status -ne 'passed' -or $qa.$suite.workerAssemblySha256 -ne $qa.assemblySha256) {throw ('Expected current-worker no-backup encryption checks: '+$suite)}
    }
}
if ([version]$Version -ge [version]'0.10.3') {
    foreach($mode in @('obfs4','snowflake')) {
        $bridge=$qa.bridges.$mode
        if($bridge.status -ne 'passed' -or -not $bridge.networkRequested -or $bridge.workerAssemblySha256 -ne $qa.assemblySha256) {throw ('Expected current-worker bridge delivery checks: '+$mode)}
    }
}
if ([version]$Version -ge [version]'0.11.0' -and $qa.torRoutes.status -ne 'passed') {throw 'Expected passed Tor route control checks'}
if ([version]$Version -ge [version]'0.10.4') {
    if($qa.legacyUpgrade.status -ne 'passed' -or $qa.legacyUpgrade.workerAssemblySha256 -ne $qa.assemblySha256) {throw 'Expected current-worker legacy profile compatibility check'}
    foreach($mode in @('direct','obfs4','snowflake')) {
        $isolation=$qa.networkIsolation.$mode
        if($isolation.status -ne 'passed' -or $isolation.workerAssemblySha256 -ne $qa.assemblySha256) {throw ('Expected current-worker OS isolation checks: '+$mode)}
    }
}
if ((Get-FileHash (Join-Path $distribution 'PrivateChat.exe')).Hash -ne $qa.exeSha256 -or (Get-FileHash (Join-Path $distribution 'PrivateChat.dll')).Hash -ne $qa.assemblySha256) {throw 'Tested release changed'}
$manifest=Get-Content (Join-Path $distribution 'runtime-manifest.json') -Raw | ConvertFrom-Json
foreach($category in @('core','tor')) {foreach($file in $manifest.$category.PSObject.Properties) {if((Get-FileHash (Join-Path $distribution $file.Name)).Hash -ne $file.Value){throw ('Runtime changed: '+$file.Name)}}}
$stage=Join-Path $root ('artifacts\staging-'+$Version+'-'+[guid]::NewGuid().ToString('N'))
$output=Join-Path $root ('artifacts\release-'+$Version)
New-Item -ItemType Directory -Path $stage,$output -Force | Out-Null
foreach($item in Get-ChildItem -LiteralPath $distribution) {
    if($item.Name -notin @('security-qa.json','README.md')) {Copy-Item -LiteralPath $item.FullName -Destination $stage -Recurse}
}
if (Get-ChildItem $stage -Recurse -File | Where-Object {$_.Name -match '\.db($|-)|\.log$|\.pdb$|instance\.lock$|qa-result|security-qa'}) {throw 'Private or QA data in payload'}
Copy-Item (Join-Path $root 'installer\安装说明.txt') -Destination $stage
Copy-Item (Join-Path $root '使用说明.txt') -Destination $stage -Force
Copy-Item (Join-Path $root 'installer\TESTING.md') -Destination $stage
if(Test-Path (Join-Path $root 'installer\notices')) {Copy-Item (Join-Path $root 'installer\notices') -Destination $stage -Recurse}
& (Join-Path $PSScriptRoot 'create-installer-icon.ps1') | Out-Null
$compiler=Join-Path $PSScriptRoot 'inno-setup\ISCC.exe'
if(-not (Test-Path $compiler)){throw 'Install the verified Inno Setup compiler under tools/inno-setup first'}
& $compiler ('/DPayloadDir='+$stage) ('/DReleaseVersion='+$Version) ('/DOutputPath='+$output) (Join-Path $root 'installer\PrivateChat.iss') *> (Join-Path $output 'compiler-local.log')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
$setup=Join-Path $output ('PrivateChat-'+$Version+'-Windows-x64-Setup.exe')
if((Get-Item $setup).Length -lt 1000000){throw 'Installer unexpectedly small'}
$files=@(Get-ChildItem $stage -Recurse -File | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($stage,$_.FullName);sha256=(Get-FileHash $_.FullName).Hash}})
[ordered]@{version=$Version;installer=[IO.Path]::GetFileName($setup);sha256=(Get-FileHash $setup).Hash;bytes=(Get-Item $setup).Length;payload=$files;stage=$stage;sourceAssemblySha256=$qa.assemblySha256} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'package-local.json') -Encoding utf8
((Get-FileHash $setup).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($setup)) | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output ('Installer ready: '+$setup)
