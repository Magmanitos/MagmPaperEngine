param([string]$Application = "$PSScriptRoot/../artifacts/native/MagmPaper.exe")
$ErrorActionPreference = 'Stop'
$report = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/smoke'
New-Item -ItemType Directory -Path $report -Force | Out-Null
$process = Start-Process -FilePath $Application -ArgumentList '--smoke-test', ('"{0}"' -f $report) -PassThru
if (-not $process.WaitForExit(180000)) { $process.Kill(); throw 'Native WPF/GPU test timed out' }
$failure = Join-Path $report 'failure.txt'
if (Test-Path $failure) {
  $message = (Get-Content $failure -Raw).Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A')
  Write-Output "::error::$message"
  throw 'Native UI/GPU validation failed'
}
if ($process.ExitCode -ne 0) { throw "Native application exited with $($process.ExitCode)" }
$result = Get-Content (Join-Path $report 'report.json') -Raw | ConvertFrom-Json
if (-not $result.passed) { throw 'Native app did not confirm validation' }
Get-Content (Join-Path $report 'report.json') -Raw | Write-Output
