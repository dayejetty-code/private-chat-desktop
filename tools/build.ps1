param([string]$OutputDirectory = 'dist', [switch]$EnableQa, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $PSScriptRoot 'dotnet\dotnet.exe'
$destination = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (-not $destination.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay in this project' }
$dependencyLock = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'dependencies.lock.json') | ConvertFrom-Json
foreach ($entry in $dependencyLock.simplex.nativeFiles.PSObject.Properties) {
    $nativePath = Join-Path $projectRoot ('vendor\simplex-extracted\SimpleX\app\resources\' + $entry.Name)
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $nativePath).Hash -ne $entry.Value) { throw ('Upstream native hash mismatch: ' + $entry.Name) }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$coreDir = Join-Path $destination 'runtime\core'
New-Item -ItemType Directory -Path $coreDir -Force | Out-Null
foreach ($existingFile in Get-ChildItem -LiteralPath $coreDir -File) {
    if ($existingFile.Name -notin @($dependencyLock.simplex.nativeFiles.PSObject.Properties.Name)) { throw 'Unexpected file in core output directory; use a clean build output' }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor\simplex-extracted\SimpleX\app\resources\libsimplex.dll') -Destination $coreDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor\simplex-extracted\SimpleX\app\resources\libcrypto-3-x64.dll') -Destination $coreDir -Force
$torDir = Join-Path $destination 'runtime\tor'
New-Item -ItemType Directory -Path $torDir -Force | Out-Null
$torArchive = Join-Path $projectRoot 'vendor\tor-expert.tar.gz'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $torArchive).Hash -ne $dependencyLock.tor.sha256) { throw 'Tor archive hash mismatch' }
& tar -xzf $torArchive -C $torDir
if ($LASTEXITCODE -ne 0) { throw 'Tor extraction failed' }
$expectedTorFiles = @(& tar -tzf $torArchive | Where-Object { -not $_.EndsWith('/') })
foreach ($existingFile in Get-ChildItem -LiteralPath $torDir -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($torDir, $existingFile.FullName).Replace('\', '/')
    if ($relative -notin $expectedTorFiles) { throw 'Unexpected file in Tor output directory; use a clean build output' }
}
$manifest = [ordered]@{ core = [ordered]@{}; tor = [ordered]@{} }
foreach ($file in Get-ChildItem -LiteralPath $coreDir -File) {
    $relative = [IO.Path]::GetRelativePath($destination, $file.FullName).Replace('\', '/')
    $manifest.core[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
}
foreach ($file in Get-ChildItem -LiteralPath $torDir -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($destination, $file.FullName).Replace('\', '/')
    $manifest.tor[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'runtime-manifest.json') -Encoding utf8
$manifestPath = Join-Path $destination 'runtime-manifest.json'
& $sdk publish (Join-Path $projectRoot 'src\PrivateChat.csproj') -c Release -r win-x64 -o $destination --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() "-p:EnableQa=$($EnableQa.IsPresent.ToString().ToLowerInvariant())" "-p:RuntimeManifestPath=$manifestPath" -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'reference\LICENSE') -Destination (Join-Path $destination 'SIMPLEX-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE'),(Join-Path $projectRoot '使用说明.txt'),(Join-Path $projectRoot 'dependencies.lock.json') -Destination $destination -Force
Write-Output ('Built: ' + (Join-Path $destination 'PrivateChat.exe'))
