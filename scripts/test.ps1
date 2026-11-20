param([switch]$Gpu, [switch]$Ocr, [switch]$Media, [switch]$Performance, [string]$Python, [string]$Model, [string]$Nixani)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$arguments = @()
if ($Gpu) { $arguments += '--gpu' }
if ($Ocr) { $arguments += '--ocr' }
if ($Media) { $arguments += '--media' }
if ($Performance) { $arguments += '--performance' }
if ($Python -and $Model) { $arguments += '--model', $Python, $Model }
if ($Nixani) { $arguments += '--nixani', $Nixani }
dotnet run --project "$root/tests/Ovrix.Tests.csproj" -c Release -- @arguments
if ($LASTEXITCODE) { throw 'OVRIX testleri başarısız.' }
