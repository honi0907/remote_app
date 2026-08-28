param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "publish\app"
$distDir = Join-Path $root "dist"

Write-Host "==> Publishing Remote Desktop LAN v$Version"
Push-Location $root
try {
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }

    dotnet restore RemoteDesktop.sln
    dotnet test tests\ProtocolTests\ProtocolTests.csproj -c $Configuration
    dotnet publish src\RemoteDesktop.App\RemoteDesktop.App.csproj `
        -c $Configuration `
        -r win-x64 `
        -p:Platform=x64 `
        -p:Version=$Version `
        -p:InformationalVersion=$Version `
        -p:WindowsAppSDKSelfContained=true `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false `
        --self-contained true `
        -o $publishDir

    if (-not (Get-Command iscc.exe -ErrorAction SilentlyContinue)) {
        throw "Inno Setup (iscc.exe) が見つかりません。https://jrsoftware.org/isinfo.php からインストールしてください。"
    }

    if (-not (Test-Path $distDir)) {
        New-Item -ItemType Directory -Path $distDir | Out-Null
    }

    Write-Host "==> Building installer"
    & iscc.exe "/DMyAppVersion=$Version" (Join-Path $root "installer\RemoteDesktopLAN.iss")

    Write-Host "==> Done"
    Get-ChildItem $distDir -Filter "RemoteDesktopLAN-Setup-*.exe" | ForEach-Object { Write-Host $_.FullName }
}
finally {
    Pop-Location
}
