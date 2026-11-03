#Requires -Version 7
<#
.SYNOPSIS
  NotifyVox を Self-Contained で発行し、自己署名済み MSIX と導入スクリプト一式を含む配布用 ZIP を生成する。

.DESCRIPTION
  1. dotnet publish (Release / win-x64 / Self-Contained) で .NET 10 ランタイム同梱バイナリを出力
  2. packaging/AppxManifest.Release.xml にバージョンを埋め込んで配置
  3. Windows SDK の makeappx.exe で .msix パッケージを生成
  4. 指定された .pfx 証明書 (未指定時は .certs/NotifyVoxSigning.pfx、存在しなければ一時証明書を自動生成) で signtool.exe 署名
  5. Install.cmd / Uninstall.cmd / NotifyVox.cer / README.txt を同梱した配布用 ZIP と SHA-256 チェックサムを出力

.PARAMETER Version
  リリースバージョン (例: "0.1.0" または "v0.1.0")。省略時は Directory.Build.props の <Version> を使用する。

.PARAMETER PfxPath
  署名用 PFX ファイルのパス。省略時は .certs/NotifyVoxSigning.pfx があれば使用し、なければ一時自己署名証明書を自動生成する。

.PARAMETER PfxPassword
  PFX ファイルのパスワード。省略時は環境変数 MSIX_SIGNING_CERT_PASSWORD を参照する。

.PARAMETER OutputDir
  成果物の出力先ディレクトリ (既定: artifacts/release)。
#>
param(
    [string] $Version = '',
    [string] $PfxPath = '',
    [string] $PfxPassword = '',
    [string] $OutputDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publisher = 'CN=suya172'

function Find-WindowsSdkTool([string] $toolName) {
    $fromPath = Get-Command $toolName -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $found = Get-ChildItem -Path $kitsRoot -Directory -Filter '10.0.*' |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$toolName" } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1

        if ($found) {
            return $found
        }
    }

    throw "Windows SDK ツール '$toolName' が見つかりません。Windows 10/11 SDK をインストールしてください。"
}

function Resolve-VersionInfo([string] $rawVersion, [string] $repoRoot) {
    if ([string]::IsNullOrWhiteSpace($rawVersion)) {
        $propsPath = Join-Path $repoRoot 'Directory.Build.props'
        [xml]$propsXml = Get-Content -LiteralPath $propsPath -Raw
        $rawVersion = ($propsXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
        if ([string]::IsNullOrWhiteSpace($rawVersion)) {
            $rawVersion = '0.1.0'
        }
    }

    $clean = $rawVersion.Trim()
    if ($clean.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $clean = $clean.Substring(1)
    }

    # SemVer のプレリリースサフィックス (例: 0.1.0-beta.1) を分離し、MSIX 用 4 桁数値バージョンを組み立てる
    $numericPart = ($clean -split '-')[0]
    $segments = @($numericPart -split '\.')
    while ($segments.Count -lt 3) { $segments += '0' }

    if ($segments.Count -eq 3) {
        $semVer = $clean
        $appxVersion = "$($segments[0]).$($segments[1]).$($segments[2]).0"
    } elseif ($segments.Count -eq 4) {
        $semVer = "$($segments[0]).$($segments[1]).$($segments[2])"
        $appxVersion = "$($segments[0]).$($segments[1]).$($segments[2]).$($segments[3])"
    } else {
        throw "不正なバージョン形式です: $rawVersion (例: 0.1.0 または v0.1.0)"
    }

    return [PSCustomObject]@{
        SemVer      = $semVer
        AppxVersion = $appxVersion
    }
}

function Write-Utf8BomCrLf([string] $path, [string] $content) {
    $normalized = ($content -replace "`r`n", "`n") -replace "`n", "`r`n"
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($path, $normalized, $utf8Bom)
}

$ver = Resolve-VersionInfo -rawVersion $Version -repoRoot $root
$makeAppx = Find-WindowsSdkTool 'makeappx.exe'
$signTool = Find-WindowsSdkTool 'signtool.exe'

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root 'artifacts\release'
}

if ([string]::IsNullOrWhiteSpace($PfxPassword) -and -not [string]::IsNullOrWhiteSpace($env:MSIX_SIGNING_CERT_PASSWORD)) {
    $PfxPassword = $env:MSIX_SIGNING_CERT_PASSWORD
}

if ([string]::IsNullOrWhiteSpace($PfxPath)) {
    $defaultPfx = Join-Path $root '.certs\NotifyVoxSigning.pfx'
    if (Test-Path -LiteralPath $defaultPfx) {
        $PfxPath = $defaultPfx
    }
}

Write-Host "=== NotifyVox リリースパッケージ作成 (v$($ver.SemVer) / MSIX $($ver.AppxVersion)) ===" -ForegroundColor Cyan
Write-Host "  makeappx : $makeAppx"
Write-Host "  signtool : $signTool"

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

$stageDir = Join-Path $OutputDir '_stage'
if (Test-Path -LiteralPath $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}

$appDir = Join-Path $stageDir 'app'
$bundleName = "NotifyVox-v$($ver.SemVer)-win-x64"
$bundleDir = Join-Path $stageDir $bundleName
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null

$tempCertThumbprint = $null
try {
    # 1. Self-Contained Release Publish
    Write-Host '[1/5] dotnet publish (win-x64, Self-Contained) を実行中...'
    $appProj = Join-Path $root 'src\NotifyVox.App\NotifyVox.App.csproj'
    dotnet publish $appProj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:Version=$($ver.SemVer) `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        --nologo `
        -o $appDir
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish に失敗しました。'
    }

    # 不要な .pdb 等が混入していれば除外
    Get-ChildItem -Path $appDir -Filter '*.pdb' -Recurse -File -ErrorAction SilentlyContinue | Remove-Item -Force

    # 2. 本番用 AppxManifest.xml の配置とバージョン反映
    Write-Host '[2/5] 本番用 AppxManifest.xml を生成中...'
    $releaseManifestSrc = Join-Path $root 'packaging\AppxManifest.Release.xml'
    $manifestXml = Get-Content -LiteralPath $releaseManifestSrc -Raw -Encoding UTF8
    $manifestXml = [regex]::Replace($manifestXml, '(<Identity\b[^>]*?\bVersion=")[^"]+(")', "`${1}$($ver.AppxVersion)`${2}", 1)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $appDir 'AppxManifest.xml'), $manifestXml, $utf8NoBom)

    # 3. MSIX パッケージ生成
    $msixFileName = "NotifyVox_$($ver.AppxVersion)_x64.msix"
    $msixPath = Join-Path $bundleDir $msixFileName
    Write-Host "[3/5] MSIX パッケージを作成中 ($msixFileName)..."
    & $makeAppx pack /d $appDir /p $msixPath /o
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx pack に失敗しました (ExitCode: $LASTEXITCODE)。"
    }

    # 4. 証明書の準備と MSIX 署名
    if ([string]::IsNullOrWhiteSpace($PfxPath) -or -not (Test-Path -LiteralPath $PfxPath)) {
        Write-Warning '署名用 PFX が指定されていないため、今回限り有効な自己署名証明書を一時生成します。継続リリースで同じ証明書を使い回すには scripts/new-signing-cert.ps1 を実行してください。'
        $PfxPath = Join-Path $stageDir 'temp-signing.pfx'
        $guidBytes = [Guid]::NewGuid().ToByteArray()
        $PfxPassword = [Convert]::ToBase64String($guidBytes)
        $secPwd = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText

        $tempCert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $publisher `
            -KeyUsage DigitalSignature `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -FriendlyName 'NotifyVox Ephemeral MSIX Signing Certificate' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -NotAfter (Get-Date).AddYears(5) `
            -TextExtension @(
                '2.5.29.37={text}1.3.6.1.5.5.7.3.3',
                '2.5.29.19={text}'
            )
        $tempCertThumbprint = $tempCert.Thumbprint
        Export-PfxCertificate -Cert $tempCert -FilePath $PfxPath -Password $secPwd | Out-Null
    } else {
        $PfxPath = (Resolve-Path -LiteralPath $PfxPath).Path
        Write-Host "      署名証明書を使用: $PfxPath"
    }

    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    $pfxCert = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile($PfxPath, $PfxPassword, $flags)
    if ($pfxCert.Subject -ne $publisher) {
        throw "証明書の Subject '$($pfxCert.Subject)' がマニフェストの Publisher '$publisher' と一致しません。"
    }

    # 公開鍵のみの .cer を出力 (秘密鍵は一切含めない)
    $cerPath = Join-Path $bundleDir 'NotifyVox.cer'
    $cerBytes = $pfxCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($cerPath, $cerBytes)

    Write-Host "[4/5] MSIX パッケージに署名中 (Thumbprint: $($pfxCert.Thumbprint))..."
    $signArgs = @('sign', '/fd', 'SHA256', '/f', $PfxPath)
    if (-not [string]::IsNullOrEmpty($PfxPassword)) {
        $signArgs += @('/p', $PfxPassword)
    }
    $signArgs += $msixPath

    & $signTool @signArgs
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign に失敗しました (ExitCode: $LASTEXITCODE)。"
    }

    $sig = Get-AuthenticodeSignature -LiteralPath $msixPath
    if ($null -eq $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $pfxCert.Thumbprint) {
        throw 'MSIX パッケージの署名検証に失敗しました。'
    }

    # 5. インストーラ補助ファイル同梱 & ZIP 圧縮
    Write-Host '[5/5] 配布用 ZIP アーカイブを作成中...'
    $installerTemplateDir = Join-Path $root 'packaging\installer'
    Copy-Item -LiteralPath (Join-Path $installerTemplateDir 'Install.cmd') -Destination (Join-Path $bundleDir 'Install.cmd') -Force
    Copy-Item -LiteralPath (Join-Path $installerTemplateDir 'Uninstall.cmd') -Destination (Join-Path $bundleDir 'Uninstall.cmd') -Force

    # Windows PowerShell 5.1 でも日本語が文字化けしないよう UTF-8 (BOM 付き) + CRLF で書き出す
    $installPs1 = Get-Content -LiteralPath (Join-Path $installerTemplateDir 'install.ps1') -Raw -Encoding UTF8
    Write-Utf8BomCrLf -path (Join-Path $bundleDir 'install.ps1') -content $installPs1

    $uninstallPs1 = Get-Content -LiteralPath (Join-Path $installerTemplateDir 'uninstall.ps1') -Raw -Encoding UTF8
    Write-Utf8BomCrLf -path (Join-Path $bundleDir 'uninstall.ps1') -content $uninstallPs1

    $readmeTxt = Get-Content -LiteralPath (Join-Path $installerTemplateDir 'README.txt') -Raw -Encoding UTF8
    $readmeTxt = $readmeTxt.Replace('{{VERSION}}', $ver.SemVer)
    Write-Utf8BomCrLf -path (Join-Path $bundleDir 'README.txt') -content $readmeTxt

    $zipFileName = "$bundleName.zip"
    $zipPath = Join-Path $OutputDir $zipFileName
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $bundleDir '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sha256Path = "$zipPath.sha256"
    [System.IO.File]::WriteAllText($sha256Path, "$hash  $zipFileName`n", [System.Text.Encoding]::ASCII)

    $zipSizeMb = [Math]::Round(((Get-Item -LiteralPath $zipPath).Length / 1MB), 2)
    Write-Host ''
    Write-Host '=== 配布パッケージの作成が完了しました ===' -ForegroundColor Green
    Write-Host "  ZIP ファイル : $zipPath ($zipSizeMb MB)"
    Write-Host "  SHA-256      : $hash"
    Write-Host "  チェックサム : $sha256Path"
}
finally {
    if ($null -ne $tempCertThumbprint) {
        $storeItem = "Cert:\CurrentUser\My\$tempCertThumbprint"
        if (Test-Path -LiteralPath $storeItem) {
            Remove-Item -LiteralPath $storeItem -Force
        }
    }
    if (Test-Path -LiteralPath $stageDir) {
        Remove-Item -LiteralPath $stageDir -Recurse -Force
    }
}
