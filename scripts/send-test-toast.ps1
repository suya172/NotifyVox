# テスト用トースト通知を送信する (SPEC §11 結合テスト用)
# Windows PowerShell 5.1 (powershell.exe) で実行すること。pwsh 7 では WinRT 型を直接ロードできない。
#   powershell.exe -NoProfile -File scripts/send-test-toast.ps1 -Title "件名" -Body "本文"
param(
    [string] $Title = 'NotifyVox テスト',
    [string] $Body = 'これは疎通確認の通知です。',
    # 既定は PowerShell 自身の AUMID (どの環境にも存在する)
    [string] $AppId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
)
$ErrorActionPreference = 'Stop'
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null

$esc = { param($s) [System.Security.SecurityElement]::Escape($s) }
$xml = @"
<toast>
  <visual>
    <binding template="ToastGeneric">
      <text>$(& $esc $Title)</text>
      <text>$(& $esc $Body)</text>
    </binding>
  </visual>
</toast>
"@
$doc = New-Object Windows.Data.Xml.Dom.XmlDocument
$doc.LoadXml($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($AppId).Show([Windows.UI.Notifications.ToastNotification]::new($doc))
Write-Host ("toast sent {0:HH:mm:ss.fff}" -f (Get-Date))
