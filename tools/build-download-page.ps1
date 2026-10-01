param([string]$Version='0.8.1',[string]$Repository='dayejetty-code/private-chat-desktop')
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$' -or $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$'){throw 'Invalid release configuration'}
$root=Split-Path -Parent $PSScriptRoot
$release=Join-Path $root ('artifacts\release-'+$Version)
$package=Get-Content (Join-Path $release 'package-local.json') -Raw | ConvertFrom-Json
$file=Join-Path $release $package.installer
if((Get-FileHash $file).Hash -ne $package.sha256){throw 'Package changed'}
$sourceUrl='https://github.com/'+$Repository
$releaseUrl=$sourceUrl+'/releases/tag/v'+$Version
$publicDownload=$sourceUrl+'/releases/download/v'+$Version+'/'
$template=Get-Content (Join-Path $root 'web\index.template.html') -Raw
foreach($mode in @('preview','public')) {
    $dir=if($mode -eq 'preview'){Join-Path $root 'artifacts\download-preview'}else{Join-Path $root 'docs'}
    New-Item -ItemType Directory $dir -Force | Out-Null
    $download=if($mode -eq 'preview'){'downloads/'}else{$publicDownload}
    $banner=if($mode -eq 'preview'){'<div class="preview-banner">本地预览 · 安装包可下载，公网入口尚未发布。</div>'}else{''}
    $html=$template.Replace('{{PREVIEW_BANNER}}',$banner).Replace('{{VERSION}}',$Version).Replace('{{DOWNLOAD_URL}}',$download+$package.installer).Replace('{{CHECKSUM_URL}}',$download+'SHA256SUMS.txt').Replace('{{SOURCE_URL}}',$sourceUrl).Replace('{{RELEASE_URL}}',$releaseUrl).Replace('{{SIZE}}',([math]::Round($package.bytes/1MB,1).ToString('0.0',[cultureinfo]::InvariantCulture))).Replace('{{FILENAME}}',$package.installer).Replace('{{SHA256}}',$package.sha256.ToLowerInvariant())
    [IO.File]::WriteAllText((Join-Path $dir 'index.html'),$html,[Text.UTF8Encoding]::new($false))
    Copy-Item (Join-Path $root 'web\styles.css'),(Join-Path $root 'web\mark.svg'),(Join-Path $root 'web\app-preview.png') -Destination $dir -Force
    [IO.File]::WriteAllText((Join-Path $dir '.nojekyll'),'')
    if($mode -eq 'preview') {New-Item -ItemType Directory (Join-Path $dir 'downloads') -Force|Out-Null;Copy-Item $file,(Join-Path $release 'SHA256SUMS.txt') -Destination (Join-Path $dir 'downloads') -Force}
}
Write-Output 'Prepared local preview and GitHub Pages files; this command does not publish.'
