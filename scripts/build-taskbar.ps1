$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Invoke-CheckedTool([string]$Tool, [string[]]$Arguments) {
  $output = & $Tool @Arguments 2>&1
  $code = $LASTEXITCODE
  $output | Write-Output
  if ($code -ne 0) {
    if ($env:GITHUB_ACTIONS -eq 'true') {
      $message = (($output | Select-Object -Last 80) -join "`n").Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
      Write-Output "::error::$message"
    }
    exit $code
  }
}
Invoke-CheckedTool cmake @('-S', "$root/native/taskbar", '-B', "$root/native/build", '-A', 'x64')
Invoke-CheckedTool cmake @('--build', "$root/native/build", '--config', 'Release', '--parallel')
Invoke-CheckedTool ctest @('--test-dir', "$root/native/build", '-C', 'Release', '--output-on-failure')
New-Item -ItemType Directory -Path "$root/native/bin" -Force | Out-Null
Copy-Item "$root/native/build/Release/MagmTaskbar.dll", "$root/native/build/Release/MagmTaskbarHost.exe" "$root/native/bin/" -Force
