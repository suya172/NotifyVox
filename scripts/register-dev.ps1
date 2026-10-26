#Requires -Version 7
<#
.SYNOPSIS
  NotifyVox をビルドし、ビルド出力フォルダを MSIX loose layout として登録する (開発用)。

.DESCRIPTION
  UserNotificationListener はパッケージ ID を持つプロセスからしか使えない (SPEC §3.2)。
  開発者モード ON の環境では、署名なしで AppxManifest.xml を直接登録できる。
  登録後は「スタートメニューの NotifyVox (Dev)」または -Launch で起動すること。
  (bin 内の NotifyVox.exe を直接ダブルクリックするとパッケージ ID が付かない)

.PARAMETER Configuration
  Debug / Release

.PARAMETER Launch
  登録後にパッケージ経由で起動する

.PARAMETER Unregister
  登録解除のみ行う
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [switch] $Launch,
    [switch] $Unregister
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packageName = 'NotifyVox.Dev'

function Remove-DevPackage {
    $existing = Get-AppxPackage -Name $packageName
    if ($existing) {
        Write-Host "既存の登録を解除: $($existing.PackageFullName)"
        Get-Process -Name NotifyVox -ErrorAction SilentlyContinue | Stop-Process -Force
        Remove-AppxPackage -Package $existing.PackageFullName
    }
}

$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($devMode -ne 1) {
    throw '開発者モードが無効です。設定 > システム > 開発者向け で有効化してください。'
}

Remove-DevPackage
if ($Unregister) { return }

$proj = Join-Path $root 'src/NotifyVox.App/NotifyVox.App.csproj'
dotnet build $proj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'ビルド失敗' }

$outDir = Join-Path $root "src/NotifyVox.App/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64"
$manifest = Join-Path $outDir 'AppxManifest.xml'
if (-not (Test-Path $manifest)) { throw "マニフェストが見つかりません: $manifest" }

Write-Host "loose layout を登録: $outDir"
Add-AppxPackage -Register $manifest -ForceApplicationShutdown

$pkg = Get-AppxPackage -Name $packageName
Write-Host "登録完了: $($pkg.PackageFamilyName)"
Write-Host "AUMID   : $($pkg.PackageFamilyName)!NotifyVox"

if ($Launch) {
    Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!NotifyVox"
}
