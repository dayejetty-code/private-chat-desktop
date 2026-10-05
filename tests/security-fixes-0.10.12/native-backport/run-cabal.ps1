param([Parameter(ValueFromRemainingArguments=$true)][string[]]$CabalArgs)
$ErrorActionPreference = 'Stop'
$taskRoot = 'C:\Users\Lenovo\pc-native'
$env:CABAL_DIR = "$taskRoot\cabal"
$env:PATH = "$taskRoot\ghc\ghc-9.6.3-x86_64-unknown-mingw32\bin;$taskRoot\winlibs\mingw64\bin;C:\Program Files\Git\cmd;C:\Program Files\Git\usr\bin;$env:PATH"
Set-Location -LiteralPath "$taskRoot\simplex-chat"
# Read the executable location from Cabal's resolved build plan after a
# successful build. Invoking Cabal merely to list it refreshes Git submodules.
if ($CabalArgs.Count -eq 2 -and $CabalArgs[0] -eq 'list-bin' -and $CabalArgs[1] -eq 'parser-regression') {
  $taskPlan = Get-Content -Raw -LiteralPath "$taskRoot\simplex-chat\dist-newstyle\cache\plan.json" | ConvertFrom-Json
  $taskEntries = @($taskPlan.'install-plan' | Where-Object { $_.'pkg-name' -eq 'privatechat-parser-regression' -and $_.'component-name' -eq 'exe:parser-regression' })
  if ($taskEntries.Count -ne 1) { throw 'Expected one parser executable in build plan' }
  $taskBinary = [IO.Path]::GetFullPath($taskEntries[0].'bin-file')
  if (-not $taskBinary.StartsWith("$taskRoot\simplex-chat\dist-newstyle\", [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $taskBinary -PathType Leaf)) { throw 'Invalid parser executable path' }
  Write-Output $taskBinary
  exit 0
}
# Cabal 3.10 refreshes Git submodules and cannot remove read-only pack files
# on Windows. Only relax file attributes in this generated source cache.
$taskCache = [IO.Path]::GetFullPath("$taskRoot\simplex-chat\dist-newstyle\src")
if ($taskCache -ne 'C:\Users\Lenovo\pc-native\simplex-chat\dist-newstyle\src') { throw 'Unexpected source cache' }
if (Test-Path -LiteralPath $taskCache) {
  Get-ChildItem -LiteralPath $taskCache -Recurse -File -Force |
    Where-Object { $_.FullName.Contains('\.git\') -and $_.IsReadOnly } |
    ForEach-Object { $_.IsReadOnly = $false }
}
& "$taskRoot\cabal-bin\cabal.exe" "--config-file=$taskRoot\cabal\config" @CabalArgs
exit $LASTEXITCODE
