param([Parameter(Mandatory)][string]$NativeSource)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root 'artifacts/release-0.10.12'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archive=Join-Path $output 'SimpleX-7.0.3-privatechat-parser1-source.zip'
if (Test-Path -LiteralPath $archive) { throw 'Source archive already exists; inspect before replacing' }
$expected='C52FB0842CB0B2BE2747A5F437FEEB2C7035B240CBE5CD59B5F2287294A59C34'
if ((Get-FileHash -LiteralPath (Join-Path $NativeSource 'src/Simplex/Chat/Protocol.hs')).Hash -ne $expected) { throw 'Native source differs from accepted parser' }
& git -C $NativeSource archive --format=zip ('--output='+$archive) b3907c9de7a2c596075762bf05ba3e1d2532aff1
if ($LASTEXITCODE -ne 0) { throw 'Stable source archive failed' }
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Update)
try {
    foreach ($relative in @('src/Simplex/Chat/Protocol.hs','tests/ProtocolTests.hs')) {
        $zip.GetEntry($relative).Delete()
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $NativeSource $relative),$relative) | Out-Null
    }
    $provenance=Join-Path $root 'tests/security-fixes-0.10.12/native-backport'
    foreach ($file in Get-ChildItem -LiteralPath $provenance -Recurse -File | Where-Object { $_.Extension -in '.ps1','.sh','.hs','.cabal','.patch','.config','.local' -or $_.Name -in 'build-inputs.json','toolchain-downloads.json' }) {
        $relative=[IO.Path]::GetRelativePath($provenance,$file.FullName).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,('privatechat-build/'+$relative)) | Out-Null
    }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $root 'BUILD.md'),'PRIVATECHAT-BUILD.md') | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $root 'SECURITY-PARSER-BACKPORT-0.10.12.md'),'PRIVATECHAT-PATCH.md') | Out-Null
} finally { $zip.Dispose() }
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try {
    if (-not $zip.GetEntry('LICENSE')) { throw 'Missing native license' }
    $stream=$zip.GetEntry('src/Simplex/Chat/Protocol.hs').Open()
    try { $sha=[Security.Cryptography.SHA256]::Create(); $hash=[Convert]::ToHexString($sha.ComputeHash($stream)); $sha.Dispose() } finally { $stream.Dispose() }
    if ($hash -ne $expected) { throw 'Archived parser differs from tested source' }
} finally { $zip.Dispose() }
[pscustomobject]@{file=$archive;bytes=(Get-Item -LiteralPath $archive).Length;sha256=(Get-FileHash -LiteralPath $archive).Hash;parserSha256=$hash} | ConvertTo-Json
exit 0
