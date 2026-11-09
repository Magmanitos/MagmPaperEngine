# Exercise the actual packaged installer on the disposable Windows CI runner.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run this installation test only on a disposable GitHub Actions runner.' }
$installer = @(Get-ChildItem "$PSScriptRoot/../dist/MagmPaper-Setup-*.exe")
if ($installer.Count -ne 1) { throw 'Expected exactly one installer' }
$installDir = Join-Path $env:LOCALAPPDATA 'Programs/MagmPaper'
$exe = Join-Path $installDir 'MagmPaper.exe'
$dataDir = Join-Path $env:APPDATA 'MagmPaper'
$legacyDir = Join-Path $env:APPDATA 'magmpaper-engine'
$library = Join-Path $dataDir 'wallpapers.json'
$taskbarBackup = Join-Path $dataDir 'taskbar-backup.json'
$appearance = Join-Path $dataDir 'taskbar-appearance.json'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupName = 'com.magmanitos.magmpaper'
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'MagmPaper.lnk'
$menuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'MagmPaper.lnk'
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Wait-Until($condition, $message) {
  for ($attempt = 0; $attempt -lt 60; $attempt++) {
    if (& $condition) { return }
    Start-Sleep -Milliseconds 500
  }
  throw $message
}
function Stop-App {
  Get-Process MagmPaper -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
  Wait-Until { @(Get-Process MagmPaper -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -eq 0 } 'Application did not stop'
}
function Install-App($setup = $installer[0].FullName) {
  $process = Start-Process -FilePath $setup -ArgumentList '/S', '/currentuser' -Wait -PassThru
  Assert ($process.ExitCode -eq 0) "Installer failed: $($process.ExitCode)"
}
Assert (-not (Test-Path $installDir)) 'Runner already has an installation'
Assert (-not (Test-Path $dataDir)) 'Runner already has application data'
Assert (-not (Test-Path $legacyDir)) 'Runner already has legacy application data'
Assert (-not (Get-ItemProperty -Path $runKey -Name $startupName -ErrorAction SilentlyContinue)) 'Runner already has an autostart entry'
New-Item -ItemType Directory -Path $legacyDir | Out-Null
$sample = @{ version=1; items=@(@{ id='installer-fixture'; name='Preserved wallpaper'; code='void main() { gl_FragColor = vec4(1.0); }' }); activeId=$null; settings=@{ mouseEnabled=$false; pauseWhenCovered=$true; pauseOnBattery=$false; fps=30; renderScale=0.75 } } | ConvertTo-Json -Depth 5
Set-Content -Path (Join-Path $legacyDir 'wallpapers.json') -Value $sample -Encoding utf8NoBOM
try {
  # Upgrade the published version, not just a second copy of the new installer.
  $previous = Join-Path $env:TEMP 'MagmPaper-Setup-0.2.1.exe'
  Invoke-WebRequest 'https://github.com/Magmanitos/MagmPaperEngine/releases/download/v0.2.1/MagmPaper-Setup-0.2.1.exe' -OutFile $previous
  Install-App $previous
  Assert (Test-Path $exe) 'Application was not installed in LocalAppData/Programs/MagmPaper'
  Assert (Test-Path (Join-Path $installDir 'resources/app.asar')) 'Bundled application missing'
  Assert (@(Get-ChildItem (Join-Path $installDir 'resources') -Filter '*.node' -Recurse).Count -gt 0) 'Native Windows dependency missing'
  Assert ((Get-Item $exe).VersionInfo.ProductName -eq 'MagmPaper') 'Executable product metadata missing'
  $shell = New-Object -ComObject WScript.Shell
  foreach ($link in @($desktopLink, $menuLink)) {
    Assert (Test-Path $link) "Shortcut missing: $link"
    Assert ($shell.CreateShortcut($link).TargetPath -eq $exe) "Incorrect shortcut target: $link"
  }
  $uninstallEntries = @(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object { $_.DisplayName -eq 'MagmPaper' })
  Assert ($uninstallEntries.Count -eq 1) 'Windows installed-apps registration missing'
  Assert (-not (Get-ItemProperty -Path $runKey -Name $startupName -ErrorAction SilentlyContinue)) 'Installation enabled autostart without user preference'
  # Launch by the real shortcut, without Node.js or the source checkout.
  $null = $shell.Run(('"{0}"' -f $desktopLink), 1, $false)
  Wait-Until { Test-Path $library } 'Installed app did not migrate the existing collection'
  Start-Sleep -Seconds 2
  Assert (@(Get-Process MagmPaper -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -gt 0) 'Installed application exited unexpectedly'
  $data = Get-Content $library -Raw | ConvertFrom-Json
  Assert ($data.items[0].id -eq 'installer-fixture' -and $data.settings.fps -eq 30 -and $data.settings.mouseEnabled -eq $false) 'Collection or preferences were lost during migration'
  Stop-App
  $saved = Get-Content $library -Raw
  $legacyBackup = Get-Content ($library + '.v1-backup') -Raw
  Set-Content -Path $taskbarBackup -Value '{"native":{"alignment":1,"widgets":1},"sizes":null}' -Encoding utf8NoBOM
  $savedTaskbar = Get-Content $taskbarBackup -Raw
  Set-Content -Path $appearance -Value '{"enabled":false,"height":60,"icon":32,"button":48,"color":"#223344","opacity":50,"hideWidgets":true,"radius":8}' -Encoding utf8NoBOM
  $savedAppearance = Get-Content $appearance -Raw
  # An upgrade must preserve data and relocate an existing development autostart.
  New-Item -Path $runKey -Force | Out-Null
  Set-ItemProperty -Path $runKey -Name $startupName -Value '"C:\Old source\electron.exe" "C:\Old source\MagmPaper" --autostart'
  Install-App
  Assert ((Get-Content $library -Raw) -eq $saved) 'Reinstallation modified user data'
  Assert ((Get-Content $taskbarBackup -Raw) -eq $savedTaskbar) 'Upgrade changed taskbar recovery settings'
  Assert ((Get-ItemPropertyValue -Path $runKey -Name $startupName) -eq ('"{0}" --autostart' -f $exe)) 'Installer did not relocate autostart'
  foreach ($file in @('MagmTaskbar.dll','MagmTaskbarHost.exe')) {
    Assert (Test-Path (Join-Path $installDir "resources/taskbar/$file")) "Bundled taskbar component missing: $file"
  }
  Assert (@(Get-ChildItem (Join-Path $installDir 'resources/taskbar')).Count -eq 2) 'Test harness must not ship in installer'
  Assert ((Get-Content $appearance -Raw) -eq $savedAppearance) 'Upgrade changed appearance preferences'
  Assert ((Get-Item $exe).VersionInfo.ProductVersion -like '0.3.1*') 'Upgrade left the old executable'
  $entries = @(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object { $_.DisplayName -eq 'MagmPaper' })
  Assert ($entries.Count -eq 1 -and $entries[0].DisplayVersion -eq '0.3.1') 'Upgrade did not replace the old installed-apps registration'
  $null = $shell.Run(('"{0}"' -f $desktopLink), 1, $false)
  Wait-Until { (Get-Content $library -Raw | ConvertFrom-Json).version -eq 2 } 'Installed upgrade did not migrate to GLSL files'
  Start-Sleep -Seconds 2
  Assert (@(Get-Process MagmPaper -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -gt 0) 'Upgraded application exited'
  Stop-App
  $data = Get-Content $library -Raw | ConvertFrom-Json
  Assert ($data.items[0].id -eq 'installer-fixture' -and $data.settings.fps -eq 30 -and $data.settings.mouseEnabled -eq $false) 'Upgrade lost collection or settings'
  $shader = Join-Path (Join-Path $dataDir 'Wallpapers') $data.items[0].file
  Assert ((Get-Content $shader -Raw) -eq 'void main() { gl_FragColor = vec4(1.0); }') 'GLSL file content changed'
  Assert ((Get-Content ($library + '.v1-backup') -Raw) -eq $legacyBackup) 'Original collection backup changed'
  Assert ((Get-Content $taskbarBackup -Raw) -eq $savedTaskbar) 'Launch changed taskbar recovery settings'
  $saved = Get-Content $library -Raw
  Install-App
  Assert ((Get-Content $library -Raw) -eq $saved) 'Reinstallation changed the migrated library'
  Assert ((Get-Content $shader -Raw) -eq 'void main() { gl_FragColor = vec4(1.0); }') 'Reinstallation changed GLSL code'
  $uninstaller = Join-Path $installDir 'Uninstall MagmPaper.exe'
  Assert (Test-Path $uninstaller) 'Uninstaller missing'
  $process = Start-Process -FilePath $uninstaller -ArgumentList '/S', '/currentuser' -Wait -PassThru
  Assert ($process.ExitCode -eq 0) 'Uninstaller failed'
  Wait-Until { -not (Test-Path $exe) -and -not (Test-Path $desktopLink) -and -not (Test-Path $menuLink) } 'Uninstall did not remove application and shortcuts'
  Assert ((Get-Content $library -Raw) -eq $saved) 'Uninstall deleted the collection'
  Assert (Test-Path $shader) 'Uninstall deleted GLSL files'
  Assert ((Get-Content $appearance -Raw) -eq $savedAppearance) 'Uninstall deleted appearance preferences'
  Assert ((Get-Content $taskbarBackup -Raw) -eq $savedTaskbar) 'Uninstall deleted taskbar recovery settings'
  Assert (-not (Get-ItemProperty -Path $runKey -Name $startupName -ErrorAction SilentlyContinue)) 'Uninstall left a stale autostart entry'
  Assert (@(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object { $_.DisplayName -eq 'MagmPaper' }).Count -eq 0) 'Uninstall left an installed-apps entry'
  Write-Output 'PASS: install, native dependencies, metadata, both shortcuts, packaged launch, library migration, reinstall, autostart relocation and uninstall with data preserved.'
} catch {
  $message = ($_ | Out-String).Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
  Write-Output "::error::$message"
  throw
} finally {
  Stop-App
  Remove-ItemProperty -Path $runKey -Name $startupName -ErrorAction SilentlyContinue
}
