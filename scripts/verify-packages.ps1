param([switch]$Install)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$release = Join-Path $root 'artifacts/release'
$version = '1.0.0'
foreach ($entry in Get-Content "$release/SHA256SUMS.txt") {
    if ($entry -notmatch '^([0-9a-f]{64})  (OVRIX-[a-z0-9.-]+\.(exe|zip))$') { throw 'Geçersiz SHA-256 girişi.' }
    $expected = $Matches[1]
    $file = Join-Path $release $Matches[2]
    if ((Get-FileHash -LiteralPath $file).Hash -ne $expected) { throw "SHA-256 uyuşmadı: $file" }
}
$qa = Join-Path $root ('artifacts/package-qa-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $qa | Out-Null

function Test-Window([string]$Folder) {
    foreach ($asset in 'OVRIX.exe','ovrix_gpu.dll','argos_worker.py','licenses','docs') {
        if (!(Test-Path -LiteralPath (Join-Path $Folder $asset))) { throw "Eksik paket dosyası: $asset" }
    }
    $settings = '"' + (Join-Path $qa ('profile-' + [guid]::NewGuid().ToString('N'))) + '"'
    $instance = Start-Process -FilePath (Join-Path $Folder 'OVRIX.exe') -ArgumentList '--smoke',$settings -PassThru
    try {
        if (!$instance.WaitForExit(30000)) { throw 'OVRIX pencere testi zaman aşımına uğradı.' }
        if ($instance.ExitCode -ne 0) { throw "OVRIX pencere testi çıkış kodu: $($instance.ExitCode)" }
    } finally {
        $instance.Refresh()
        if (!$instance.HasExited) { $instance.Kill() }
        $instance.Dispose()
    }
}

try {
    $portable = Join-Path $qa 'portable'
    Expand-Archive -LiteralPath "$release/OVRIX-$version-win-x64-portable.zip" -DestinationPath $portable
    Test-Window $portable
    "PASS Portable ZIP · SHA-256, dosyalar ve WPF penceresi"

    if ($Install) {
        $key = 'Software/Microsoft/Windows/CurrentVersion/Uninstall/{C7E10D8A-89D1-4A19-9487-9D4186771923}_is1'
        if (Test-Path "HKCU:/$key") { throw 'Bu kullanıcıda OVRIX zaten kurulu. İzole bir Windows hesabında sınayın.' }
        $isolated = Join-Path $qa 'installed'
        $installer = "$release/OVRIX-$version-win-x64-setup.exe"
        $dirArgument = '/DIR="' + $isolated + '"'
        $run = Start-Process -FilePath $installer -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',$dirArgument -WindowStyle Hidden -Wait -PassThru
        if ($run.ExitCode) { throw "Kurulum çıkış kodu: $($run.ExitCode)" }
        try {
            Test-Window $isolated
            "PASS Kurulum EXE · Kurulum ve WPF penceresi"
        } finally {
            $uninstall = Join-Path $isolated 'unins000.exe'
            if (Test-Path -LiteralPath $uninstall) {
                $run = Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -Wait -PassThru
                if ($run.ExitCode) { throw "Kaldırma çıkış kodu: $($run.ExitCode)" }
            }
        }
    }
} finally {
    $qaItem = Get-Item -LiteralPath $qa -ErrorAction SilentlyContinue
    $artifactRoot = (Resolve-Path -LiteralPath "$root/artifacts").Path
    if ($qaItem -and $qaItem.Parent.FullName -eq $artifactRoot -and $qaItem.Name -match '^package-qa-[0-9a-f]{32}$' -and
        !(Test-Path -LiteralPath (Join-Path $qaItem.FullName 'installed/unins000.exe'))) {
        Remove-Item -LiteralPath $qaItem.FullName -Recurse -Force
    }
}
