param(
    [switch] $KeepCertificate,
    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
$packageName = 'suya172.NotifyVox'

Write-Host '====================================================' -ForegroundColor Cyan
Write-Host ' NotifyVox アンインストール' -ForegroundColor Cyan
Write-Host '====================================================' -ForegroundColor Cyan
Write-Host ''

try {
    Get-Process -Name 'NotifyVox' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $pkgs = @(Get-AppxPackage -Name $packageName)
    if ($pkgs.Count -gt 0) {
        foreach ($pkg in $pkgs) {
            Write-Host "[1/2] アプリ本体を削除しています ($($pkg.PackageFullName))..."
            Remove-AppxPackage -Package $pkg.PackageFullName
        }
        Write-Host '      NotifyVox アプリを削除しました。' -ForegroundColor Green
    } else {
        Write-Host '[1/2] NotifyVox アプリはインストールされていません。' -ForegroundColor Yellow
    }

    if (-not $KeepCertificate) {
        $thumbprints = @()
        $cerPath = Join-Path $PSScriptRoot 'NotifyVox.cer'
        if (Test-Path -LiteralPath $cerPath) {
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)
            if (Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)") {
                $thumbprints += $cert.Thumbprint
            }
        } else {
            $matched = Get-ChildItem -Path 'Cert:\LocalMachine\TrustedPeople' -ErrorAction SilentlyContinue |
                Where-Object { $_.Subject -eq 'CN=suya172' -and $_.Issuer -eq 'CN=suya172' }
            foreach ($m in $matched) {
                $thumbprints += $m.Thumbprint
            }
        }

        if ($thumbprints.Count -gt 0) {
            Write-Host '[2/2] 登録済みの自己署名証明書を「信頼されたユーザー (TrustedPeople)」から削除します。' -ForegroundColor Yellow
            $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
            $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

            $removeScript = ($thumbprints | ForEach-Object {
                "if (Test-Path 'Cert:\LocalMachine\TrustedPeople\$_') { Remove-Item 'Cert:\LocalMachine\TrustedPeople\$_' -Force }"
            }) -join '; '

            if ($isAdmin) {
                Invoke-Expression $removeScript
            } else {
                Write-Host '      管理者権限の確認画面 (UAC) が表示されたら「はい」を選択してください...'
                $proc = Start-Process -FilePath 'powershell.exe' -Verb RunAs -PassThru -Wait -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $removeScript)
                if ($proc.ExitCode -ne 0) {
                    throw "証明書の削除に失敗しました (ExitCode: $($proc.ExitCode))。"
                }
            }
            Write-Host '      自己署名証明書を削除しました。' -ForegroundColor Green
        } else {
            Write-Host '[2/2] 削除対象の自己署名証明書は登録されていません。' -ForegroundColor Yellow
        }
    }

    Write-Host ''
    Write-Host 'アンインストールが完了しました。' -ForegroundColor Green
    Write-Host '※ %APPDATA%\NotifyVox および %LOCALAPPDATA%\NotifyVox に設定・ログが残っている場合は必要に応じて手動で削除してください。'

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
