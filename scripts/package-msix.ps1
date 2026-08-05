param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$Version = "0.2.0.0",
    [string]$Publisher = "CN=SerialLog",
    [string]$SignCertificatePath = "",
    [string]$SignCertificatePassword = ""
)

$ErrorActionPreference = "Stop"

$publishRoot = Resolve-Path -LiteralPath $PublishDir
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$packageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("serial-log-msix-" + [guid]::NewGuid().ToString("N"))
$vfsRoot = Join-Path $packageRoot "VFS\ProgramFilesX64\SerialLog"
$assetRoot = Join-Path $packageRoot "Assets"
$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)

New-Item -ItemType Directory -Path $vfsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null
Copy-Item -Path (Join-Path $publishRoot "*") -Destination $vfsRoot -Recurse -Force
Get-ChildItem -LiteralPath $vfsRoot -Filter "*-preview.png" -File | Remove-Item -Force
Copy-Item -LiteralPath "packaging\msix\AppxManifest.xml" -Destination (Join-Path $packageRoot "AppxManifest.xml") -Force

$manifestPath = Join-Path $packageRoot "AppxManifest.xml"
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$manifest = $manifest.Replace("__VERSION__", $Version).Replace("__PUBLISHER__", $Publisher)
Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding UTF8

$logoAssetRoot = Join-Path (Resolve-Path -LiteralPath "packaging\msix") "Assets"
foreach ($logoName in @("Square44x44Logo.png", "Square150x150Logo.png", "StoreLogo.png")) {
    $logoSource = Join-Path $logoAssetRoot $logoName
    if (-not (Test-Path -LiteralPath $logoSource)) {
        throw "MSIX logo asset was not found: $logoSource"
    }

    Copy-Item -LiteralPath $logoSource -Destination (Join-Path $assetRoot $logoName) -Force
}

$sdkBin = Join-Path $programFilesX86 "Windows Kits\10\bin"
$makeAppx = Get-ChildItem $sdkBin -Recurse -Filter makeappx.exe |
    Where-Object { $_.FullName -like "*\x64\makeappx.exe" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if ($null -eq $makeAppx) {
    throw "makeappx.exe was not found. Please install Windows 10/11 SDK."
}

New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($outputFullPath)) -Force | Out-Null
& $makeAppx.FullName pack /d $packageRoot /p $outputFullPath /overwrite
if ($LASTEXITCODE -ne 0) {
    throw "makeappx.exe failed with exit code $LASTEXITCODE."
}

if ($SignCertificatePath) {
    $signtool = Get-ChildItem $sdkBin -Recurse -Filter signtool.exe |
        Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($null -eq $signtool) {
        throw "signtool.exe was not found. Please install Windows 10/11 SDK."
    }

    & $signtool.FullName sign /fd SHA256 /a /f $SignCertificatePath /p $SignCertificatePassword $outputFullPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe failed with exit code $LASTEXITCODE."
    }
}

Remove-Item -LiteralPath $packageRoot -Recurse -Force
