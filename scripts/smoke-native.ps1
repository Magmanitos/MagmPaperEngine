param([string]$Application = "$PSScriptRoot/../artifacts/native/MagmPaper.exe")
$ErrorActionPreference = 'Stop'
try {
$report = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/smoke'
New-Item -ItemType Directory -Path $report -Force | Out-Null
Remove-Item (Join-Path $report 'failure.txt'), (Join-Path $report 'report.json'), (Join-Path $report 'trace.txt') -ErrorAction SilentlyContinue
$dumpKey = 'HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\MagmPaper.exe'
New-Item $dumpKey -Force | Out-Null
New-ItemProperty $dumpKey -Name DumpFolder -Value $report -PropertyType ExpandString -Force | Out-Null
New-ItemProperty $dumpKey -Name DumpType -Value 2 -PropertyType DWord -Force | Out-Null
$process = Start-Process -FilePath $Application -ArgumentList '--smoke-test', ('"{0}"' -f $report) -PassThru
if (-not $process.WaitForExit(180000)) { $process.Kill(); throw 'Native WPF/GPU test timed out' }
$failure = Join-Path $report 'failure.txt'
if (Test-Path $failure) {
  $message = (Get-Content $failure -Raw).Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A')
  if (Test-Path (Join-Path $report 'trace.txt')) { $message += '%0A' + (Get-Content (Join-Path $report 'trace.txt') -Raw).Replace("`r",'%0D').Replace("`n",'%0A') }
  Remove-Item $dumpKey -ErrorAction SilentlyContinue
  $events = Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=(Get-Date).AddMinutes(-4); Level=2 } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'MagmPaper|av_libglesv2' }
  if ($events) { $message += '%0A' + ($events.Message -join "`n").Replace("`r",'%0D').Replace("`n",'%0A') }
  Write-Output "::error::$message"
  throw 'Native UI/GPU validation failed'
}
if ($process.ExitCode -ne 0) { throw "Native application exited with $($process.ExitCode)" }
$result = Get-Content (Join-Path $report 'report.json') -Raw | ConvertFrom-Json
if (-not $result.passed) { throw 'Native app did not confirm validation' }
Remove-Item $dumpKey -ErrorAction SilentlyContinue
Get-Content (Join-Path $report 'report.json') -Raw | Write-Output

} catch {
  $message = ($_ | Out-String).Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A')
  if (Test-Path (Join-Path $report 'trace.txt')) { $message += '%0A' + (Get-Content (Join-Path $report 'trace.txt') -Raw).Replace("`r",'%0D').Replace("`n",'%0A') }
  Remove-Item $dumpKey -ErrorAction SilentlyContinue
  $events = Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=(Get-Date).AddMinutes(-4); Level=2 } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'MagmPaper|av_libglesv2' }
  if ($events) { $message += '%0A' + ($events.Message -join "`n").Replace("`r",'%0D').Replace("`n",'%0A') }
  Write-Output "::error::$message"
  throw
}
