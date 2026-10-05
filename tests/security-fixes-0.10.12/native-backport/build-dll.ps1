$ErrorActionPreference = 'Stop'
$taskRoot = 'C:\Users\Lenovo\pc-native'
$taskSource = "$taskRoot\simplex-chat"
$taskResults = "$taskRoot\results"
foreach ($taskStage in @('baseline', 'patched')) {
  $taskResult = Get-Content -Raw -LiteralPath "$taskResults\$taskStage-summary.json" | ConvertFrom-Json
  if (-not $taskResult.expectedOutcomeObserved) { throw "Missing regression gate: $taskStage" }
}
$taskPatched = Get-Content -Raw -LiteralPath "$taskResults\patched-summary.json" | ConvertFrom-Json
if ((Get-FileHash -LiteralPath "$taskSource\src\Simplex\Chat\Protocol.hs").Hash -ne $taskPatched.sourceSha256) { throw 'Source changed after regression' }

# Match the stable upstream Windows DLL recipe, within this portable compiler.
$taskSettings = "$taskRoot\ghc\ghc-9.6.3-x86_64-unknown-mingw32\lib\settings"
$taskOriginal = "$taskResults\ghc-settings-original"
if (-not (Test-Path -LiteralPath $taskOriginal)) { Copy-Item -LiteralPath $taskSettings -Destination $taskOriginal }
$taskBody = [IO.File]::ReadAllText($taskOriginal)
if (-not $taskBody.Contains('ld.lld.exe')) { throw 'Unexpected GHC linker configuration' }
[IO.File]::WriteAllText($taskSettings, $taskBody.Replace('ld.lld.exe', 'abracadabra.exe'), [Text.UTF8Encoding]::new($false))

$taskProject = "$taskSource\cabal.project.local"
$taskParserProject = "$taskResults\cabal.project.parser.local"
if (-not (Test-Path -LiteralPath $taskParserProject)) { Copy-Item -LiteralPath $taskProject -Destination $taskParserProject }
$taskBody = [IO.File]::ReadAllText($taskParserProject)
$taskDllOptions = @'

package simplex-chat
  ghc-options: -shared -threaded -optl-LC:\Users\Lenovo\pc-native\openssl-3.0.15 -optl-lcrypto-3-x64 -o libsimplex.dll libsimplex.dll.def
'@
[IO.File]::WriteAllText($taskProject, $taskBody + $taskDllOptions + "`n", [Text.UTF8Encoding]::new($false))
& "$taskRoot\run-cabal.ps1" build lib:simplex-chat *> "$taskResults\dll-build.log"
if ($LASTEXITCODE -ne 0) { throw 'Native DLL build failed; inspect dll-build.log' }
$taskDll = "$taskSource\libsimplex.dll"
if (-not (Test-Path -LiteralPath $taskDll)) { throw 'DLL was not produced' }
[ordered]@{
  status = 'built-pending-application-tests'
  dll = $taskDll
  dllSha256 = (Get-FileHash -LiteralPath $taskDll).Hash
  bytes = (Get-Item -LiteralPath $taskDll).Length
  protocolSha256 = $taskPatched.sourceSha256
  settingsSha256 = (Get-FileHash -LiteralPath $taskSettings).Hash
  projectSha256 = (Get-FileHash -LiteralPath $taskProject).Hash
} | ConvertTo-Json | Set-Content -LiteralPath "$taskResults\dll-summary.json"
Get-Content -LiteralPath "$taskResults\dll-summary.json"
