param([string]$Payload = "$PSScriptRoot/../artifacts/native")
$ErrorActionPreference = 'Stop'
try {
  $root = Split-Path $PSScriptRoot -Parent
  $Payload = (Resolve-Path $Payload).Path
  $version = ([xml](Get-Content "$root/desktop/Directory.Build.props" -Raw)).Project.PropertyGroup.Version
  foreach ($file in @('MagmPaper.exe','MagmPaper.dll','MagmPaper.runtimeconfig.json','av_libglesv2.dll','taskbar/MagmTaskbar.dll','taskbar/MagmTaskbarHost.exe','Licenses/ANGLE.txt')) {
    if (-not (Test-Path (Join-Path $Payload $file))) { throw "Missing native application file: $file" }
  }
  if (Test-Path "$Payload/resources/app.asar") { throw 'Electron must not ship in the native application' }
  if (@(Get-ChildItem $Payload -Recurse -File | Where-Object { $_.Name -match '^(chrome|electron|icudtl|snapshot_blob|v8_context_snapshot)' }).Count) { throw 'Unexpected Chromium payload' }
  $nsis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
  if (-not $nsis) { $nsis = "${env:ProgramFiles(x86)}/NSIS/makensis.exe" }
  if (-not (Test-Path $nsis)) { throw 'Install NSIS 3 (makensis) before packaging.' }
  $dist = New-Item -ItemType Directory -Path "$root/dist" -Force
  $files = @(Get-ChildItem $Payload -Recurse -File)
  $cleanup = @($files | ForEach-Object { 'Delete "$INSTDIR\' + [IO.Path]::GetRelativePath($Payload, $_.FullName).Replace('$','$$') + '"' })
  $cleanup += @(Get-ChildItem $Payload -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object { 'RMDir "$INSTDIR\' + [IO.Path]::GetRelativePath($Payload, $_.FullName).Replace('$','$$') + '"' })
  $deleteFile = "$root/artifacts/native-uninstall.nsh"
  Set-Content $deleteFile -Value ($cleanup -join "`r`n") -Encoding utf8NoBOM
  $size = [int][Math]::Ceiling(($files | Measure-Object Length -Sum).Sum / 1024)
  & $nsis /V2 "/DVERSION=$version" "/DPAYLOAD=$Payload" "/DOUTPUT=$($dist.FullName)/MagmPaper-Setup-$version.exe" "/DICON=$root/build/icon.ico" "/DDELETE_FILES=$deleteFile" "/DSIZE=$size" "$root/build/native-installer.nsi"
  if ($LASTEXITCODE -ne 0) { throw "NSIS failed: $LASTEXITCODE" }
  Compress-Archive -Path "$Payload/*" -DestinationPath "$($dist.FullName)/MagmPaper-$version-win.zip" -Force
} catch {
  $message = ($_ | Out-String).Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A')
  Write-Output "::error::$message"
  throw
}
