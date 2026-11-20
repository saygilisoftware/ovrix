param([string]$Iscc, [switch]$PortableOnly, [string]$Generator)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$release = "$root/artifacts/release"
$stage = "$root/artifacts/publish/OVRIX"
New-Item -ItemType Directory -Force $release, $stage | Out-Null
& "$PSScriptRoot/build-native.ps1" -Generator $Generator
dotnet publish "$root/apps/desktop/Ovrix.csproj" -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:DebugType=None -o $stage
if ($LASTEXITCODE) { throw 'Uygulama yayın derlemesi başarısız.' }
if (!(Test-Path "$stage/ovrix_gpu.dll")) { throw 'GPU modülü yayın klasöründe yok.' }
Copy-Item -LiteralPath "$root/LICENSE","$root/README.md","$root/THIRD_PARTY_NOTICES.md" -Destination $stage
Copy-Item -LiteralPath "$root/docs","$root/packaging/licenses" -Destination $stage -Recurse -Force
$zip = "$release/OVRIX-1.0.0-win-x64-portable.zip"
Compress-Archive -Path "$stage/*" -DestinationPath $zip -CompressionLevel Fastest -Force
$files = @($zip)
if (!$PortableOnly) {
    if (!$Iscc) {
        $Iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
        if (!$Iscc) {
            foreach ($homeFolder in @($env:LOCALAPPDATA, ${env:ProgramFiles(x86)})) {
                $matches = Get-ChildItem "$homeFolder/Programs/Inno Setup*/ISCC.exe","$homeFolder/Inno Setup*/ISCC.exe" -ErrorAction SilentlyContinue
                if ($matches) { $Iscc = ($matches | Select-Object -First 1).FullName; break }
            }
        }
    }
    if (!$Iscc) { throw 'Inno Setup 6 bulunamadı. -Iscc seçeneğiyle ISCC.exe yolunu belirtin.' }
    & $Iscc /Q "/DStageDir=$stage" "/DOutputFolder=$release" "$root/packaging/ovrix.iss"
    if ($LASTEXITCODE) { throw 'Kurulum paketi oluşturulamadı.' }
    $files += "$release/OVRIX-1.0.0-win-x64-setup.exe"
}
$hashes = $files | ForEach-Object { "$((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)" }
Set-Content -LiteralPath "$release/SHA256SUMS.txt" -Value $hashes -Encoding ascii
Get-ChildItem -LiteralPath $release | Select-Object Name, Length
