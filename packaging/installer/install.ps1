param(
    [switch] $NoLaunch,
    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
$packageName = 'suya172.NotifyVox'

Write-Host '====================================================' -ForegroundColor Cyan
Write-Host ' NotifyVox セットアップ' -ForegroundColor Cyan
Write-Host '====================================================' -ForegroundColor Cyan
Write-Host ''

try {
    $cerPath = Join-Path $PSScriptRoot 'NotifyVox.cer'
    $msix = Get-ChildItem -Path $PSScriptRoot -Filter '*.msix' -File -ErrorAction SilentlyContinue | Select-Object -First 1

    if (-not (Test-Path -LiteralPath $cerPath) -or -not $msix) {
        throw '必要なファイル (.msix / NotifyVox.cer) が見つかりません。ZIP ファイルをすべて展開（解凍）してから Install.cmd を実行してください。'
    }

    Unblock-File -LiteralPath $cerPath -ErrorAction SilentlyContinue
    Unblock-File -LiteralPath $msix.FullName -ErrorAction SilentlyContinue

    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)
    $thumbprint = $cert.Thumbprint
    $certStorePath = "Cert:\LocalMachine\TrustedPeople\$thumbprint"

    if (Test-Path -LiteralPath $certStorePath) {
        Write-Host "[1/2] 署名証明書は登録済みです (Thumbprint: $thumbprint)" -ForegroundColor Green
    } else {
        Write-Host '[1/2] 初回セットアップ: 署名証明書を「信頼されたユーザー (TrustedPeople)」に登録します。' -ForegroundColor Yellow
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

        if ($isAdmin) {
            Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        } else {
            Write-Host '      管理者権限の確認画面 (UAC) が表示されたら「はい」を選択してください...'
            $escapedCer = $cerPath.Replace("'", "''")
            $cmd = "Import-Certificate -FilePath '$escapedCer' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null"
            $proc = Start-Process -FilePath 'powershell.exe' -Verb RunAs -PassThru -Wait -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $cmd)
            if ($proc.ExitCode -ne 0) {
                throw "証明書の登録に失敗しました (ExitCode: $($proc.ExitCode))。"
            }
        }

        if (-not (Test-Path -LiteralPath $certStorePath)) {
            throw '証明書の登録を確認できませんでした。'
        }
        Write-Host "      証明書を登録しました (Thumbprint: $thumbprint)" -ForegroundColor Green
    }

    Write-Host "[2/2] NotifyVox パッケージ ($($msix.Name)) をインストールしています..."
    Get-Process -Name 'NotifyVox' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown

    $pkg = Get-AppxPackage -Name $packageName
    if (-not $pkg) {
        throw 'パッケージ登録後の確認に失敗しました。'
    }

    Write-Host ''
    Write-Host 'インストールが完了しました！' -ForegroundColor Green
    Write-Host "  バージョン : $($pkg.Version)"
    Write-Host '  起動方法   : スタートメニューの「NotifyVox」からいつでも起動できます。'

    if (-not $NoLaunch) {
        Write-Host ''
        Write-Host 'NotifyVox を起動しています...'
        Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!NotifyVox"
    }

    if (-not $NonInteractive) {
        Write-Host ''
        [void](Read-Host 'Enter キーを押してこのウィンドウを閉じます')
    }
    exit 0
}
catch {
    Write-Host ''
    Write-Host "[エラー] $($_.Exception.Message)" -ForegroundColor Red
    if (-not $NonInteractive) {
        Write-Host ''
        [void](Read-Host 'Enter キーを押してこのウィンドウを閉じます')
    }
    exit 1
}
