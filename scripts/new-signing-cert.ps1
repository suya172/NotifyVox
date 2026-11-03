#Requires -Version 7
<#
.SYNOPSIS
  NotifyVox 配布 MSIX 署名用の自己署名証明書 (.pfx / .cer) と GitHub Secrets 用 Base64 を生成する。

.DESCRIPTION
  MSIX の Identity Publisher (CN=suya172) に一致するコードサイニング用自己署名証明書を生成し、
  リポジトリ内の .certs/ フォルダ (gitignore 対象) に出力する。
  生成後、PC の個人証明書ストア (Cert:\CurrentUser\My) からは自動削除するため環境を汚さない。

  同じ .pfx を使い回すことで、ユーザーは 2 回目以降のバージョンアップ時に証明書の再登録なしで
  そのまま上書きアップデートできるようになる。

.PARAMETER Password
  PFX ファイルに設定するパスワード。省略時はランダムな強固パスワードを自動生成して表示する。

.PARAMETER ValidYears
  証明書の有効年数 (既定: 5 年)。

.PARAMETER OutputDir
  出力先フォルダ (既定: リポジトリ直下の .certs)。

.PARAMETER Force
  既存の .pfx がある場合に上書きする。
#>
param(
    [string] $Password = '',
    [ValidateRange(1, 20)] [int] $ValidYears = 5,
    [string] $OutputDir = '',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root '.certs'
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$pfxPath = Join-Path $OutputDir 'NotifyVoxSigning.pfx'
$cerPath = Join-Path $OutputDir 'NotifyVoxSigning.cer'
$b64Path = Join-Path $OutputDir 'NotifyVoxSigning.pfx.base64.txt'

if ((Test-Path -LiteralPath $pfxPath) -and -not $Force) {
    throw "既に証明書が存在します: $pfxPath`n上書きする場合は -Force を指定してください（注意: 証明書を作り直すと既存ユーザーの上書き更新時に証明書の再登録が必要になります）。"
}

if ([string]::IsNullOrWhiteSpace($Password)) {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $Password = [Convert]::ToBase64String($bytes)
}

$securePassword = ConvertTo-SecureString -String $Password -Force -AsPlainText
$subject = 'CN=suya172'

$cert = $null
try {
    # Microsoft 公式の MSIX パッケージ署名要件に準拠した拡張属性 (Code Signing EKU + Basic Constraints CA=false)
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -FriendlyName 'NotifyVox MSIX Signing Certificate (suya172)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears($ValidYears) `
        -TextExtension @(
            '2.5.29.37={text}1.3.6.1.5.5.7.3.3',
            '2.5.29.19={text}'
        )

    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

    $pfxBytes = [System.IO.File]::ReadAllBytes($pfxPath)
    $b64 = [Convert]::ToBase64String($pfxBytes)
    [System.IO.File]::WriteAllText($b64Path, $b64, [System.Text.Encoding]::ASCII)

    Write-Host ''
    Write-Host '=== 自己署名証明書の生成が完了しました ===' -ForegroundColor Green
    Write-Host "  Subject    : $($cert.Subject)"
    Write-Host "  Thumbprint : $($cert.Thumbprint)"
    Write-Host "  有効期限   : $($cert.NotAfter.ToString('yyyy-MM-dd'))"
    Write-Host "  PFX 出力先 : $pfxPath"
    Write-Host "  CER 出力先 : $cerPath"
    Write-Host "  Base64     : $b64Path"
    Write-Host ''
    Write-Host '=== GitHub Actions リリース用の Secrets 登録手順 ===' -ForegroundColor Cyan
    Write-Host 'リポジトリの Settings > Secrets and variables > Actions > New repository secret に以下2つを登録してください:'
    Write-Host '  1. MSIX_SIGNING_CERT_BASE64   : 上記 NotifyVoxSigning.pfx.base64.txt の中身すべて'
    Write-Host "  2. MSIX_SIGNING_CERT_PASSWORD : $Password"
    Write-Host ''
    Write-Host '※ .certs フォルダは .gitignore 済みです。秘密鍵 (.pfx) とパスワードは公開せず大切に保管してください。' -ForegroundColor Yellow
}
finally {
    if ($null -ne $cert) {
        $storeItem = "Cert:\CurrentUser\My\$($cert.Thumbprint)"
        if (Test-Path -LiteralPath $storeItem) {
            Remove-Item -LiteralPath $storeItem -Force
        }
    }
}
