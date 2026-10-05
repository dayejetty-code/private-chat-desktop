param([ValidateSet('baseline','patched')][string]$Stage = 'baseline')
$ErrorActionPreference = 'Stop'
$taskRoot = 'C:\Users\Lenovo\pc-native'
$taskResults = Join-Path $taskRoot 'results'
New-Item -ItemType Directory -Path $taskResults -Force | Out-Null
$taskProtocol = Join-Path $taskRoot 'simplex-chat\src\Simplex\Chat\Protocol.hs'
$taskBaselineHash = 'F791C1A8D6308795D46F2F28C23EFFC7CF0297AD1935EB775A2622260B568CA2'
$taskHash = (Get-FileHash -LiteralPath $taskProtocol).Hash
if ($Stage -eq 'baseline' -and $taskHash -ne $taskBaselineHash) { throw 'Baseline source changed' }
if ($Stage -eq 'patched' -and $taskHash -eq $taskBaselineHash) { throw 'Patch is not applied' }
& "$taskRoot\run-cabal.ps1" build parser-regression *> "$taskResults\$Stage-build.log"
if ($LASTEXITCODE -ne 0) { throw "Parser build failed; inspect $Stage-build.log" }
$taskExe = (& "$taskRoot\run-cabal.ps1" list-bin parser-regression | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Parser executable unavailable' }
$env:PATH = "$taskRoot\openssl-3.0.15;$taskRoot\winlibs\mingw64\bin;$env:PATH"
$taskReportPath = "$taskResults\$Stage-parser.json"
& $taskExe $taskReportPath *> "$taskResults\$Stage-parser.log"
$taskExit = $LASTEXITCODE
if (-not (Test-Path -LiteralPath $taskReportPath)) { throw 'Parser produced no report' }
$taskReport = Get-Content -Raw -LiteralPath $taskReportPath | ConvertFrom-Json
$taskPassed = @($taskReport.checks | Where-Object passed).Count
$taskFailed = @($taskReport.checks | Where-Object { -not $_.passed }).Count
$taskExpected = if ($Stage -eq 'baseline') { $taskExit -eq 1 -and $taskPassed -eq 4 -and $taskFailed -eq 6 } else { $taskExit -eq 0 -and $taskPassed -eq 10 -and $taskFailed -eq 0 }
[ordered]@{
  stage = $Stage
  expectedOutcomeObserved = $taskExpected
  sourceSha256 = $taskHash
  executableSha256 = (Get-FileHash -LiteralPath $taskExe).Hash
  exitCode = $taskExit
  passed = $taskPassed
  failed = $taskFailed
  report = $taskReportPath
} | ConvertTo-Json | Set-Content -LiteralPath "$taskResults\$Stage-summary.json"
Get-Content -LiteralPath "$taskResults\$Stage-summary.json"
if (-not $taskExpected) { throw 'Regression outcome differs from expectation' }
exit 0
