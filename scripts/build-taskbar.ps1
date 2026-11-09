$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
cmake -S "$root/native/taskbar" -B "$root/native/build" -A x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cmake --build "$root/native/build" --config Release --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$testOutput = & ctest --test-dir "$root/native/build" -C Release --output-on-failure 2>&1
$testCode = $LASTEXITCODE
$testOutput | Write-Output
if ($testCode -ne 0) {
  if ($env:GITHUB_ACTIONS -eq 'true') {
    $message = ($testOutput -join "`n").Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Output "::error::$message"
  }
  exit $testCode
}
New-Item -ItemType Directory -Path "$root/native/bin" -Force | Out-Null
Copy-Item "$root/native/build/Release/MagmTaskbar.dll", "$root/native/build/Release/MagmTaskbarHost.exe" "$root/native/bin/" -Force
