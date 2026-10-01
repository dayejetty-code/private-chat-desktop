param([string]$Version='0.8.1')
$ErrorActionPreference='Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') {throw 'Invalid version'}
$root=Split-Path -Parent $PSScriptRoot
$distribution=Join-Path $root ('dist-v'+$Version)
$qa=Get-Content (Join-Path $distribution 'security-qa.json') -Raw | ConvertFrom-Json
if ($qa.version -ne $Version -or $qa.nativeAndNetwork.status -ne 'passed' -or $qa.ui.status -ne 'passed') {throw 'Expected passed release checks'}
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
