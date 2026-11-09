$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
cmake -S "$root/native/taskbar" -B "$root/native/build" -A x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cmake --build "$root/native/build" --config Release --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
ctest --test-dir "$root/native/build" -C Release --output-on-failure
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
New-Item -ItemType Directory -Path "$root/native/bin" -Force | Out-Null
Copy-Item "$root/native/build/Release/MagmTaskbar.dll", "$root/native/build/Release/MagmTaskbarHost.exe" "$root/native/bin/" -Force
