param([string]$Generator)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!$cmake -and (Test-Path $locator)) {
    $cmake = & $locator -latest -products '*' -find '**/CMake/bin/cmake.exe' | Select-Object -First 1
}
if (!$cmake) { throw 'CMake bulunamadı.' }
$argsConfigure = @('-S', "$root/src/frame-engine", '-B', "$root/artifacts/native", '-A', 'x64')
$shaderCompiler = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin/*/x64/fxc.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (!$shaderCompiler) { throw 'Windows SDK fxc.exe bulunamadı.' }
$argsConfigure += "-DOVRIX_FXC=$($shaderCompiler.FullName)"
if ($Generator) { $argsConfigure += @('-G', $Generator) }
& $cmake @argsConfigure
if ($LASTEXITCODE) { throw 'CMake yapılandırması başarısız.' }
& $cmake --build "$root/artifacts/native" --config Release
if ($LASTEXITCODE) { throw 'Yerel GPU derlemesi başarısız.' }
