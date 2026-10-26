# NotifyVox (ノティファイボックス)

Windows 上で指定したアプリケーションの通知（トースト通知）をリアルタイムに取得し、**VOICEVOX エンジン (0.26.0+) のストリーミング音声合成** を用いて即座に読み上げる、タスクトレイ常駐型デスクトップアプリケーションです。

> **設計書**: 仕様の詳細は [SPEC.md](SPEC.md) を参照してください。  
> **環境構築・API分析レポート**: 詳細な検証結果は [docs/ENVIRONMENT_AND_ANALYSIS_REPORT.md](docs/ENVIRONMENT_AND_ANALYSIS_REPORT.md) を参照してください。  
> **ウォークスルーガイド**: 動作確認・再現手順は [docs/WALKTHROUGH.md](docs/WALKTHROUGH.md) を参照してください。

---

## 主な特徴

- ⚡ **超低遅延ストリーミング発話**: VOICEVOX エンジン 0.26.0 で追加された `POST /streaming_synthesis` と NAudio `BufferedWaveProvider` の組み合わせにより、全文の合成完了を待たずに 1 チャンク目から即座に発話開始（通知着信から発話開始まで 1.5 秒以内）。
- 🔔 **公式 Windows 通知 API 連携**: WinRT `Windows.UI.Notifications.Management.UserNotificationListener` を使用し、アクションセンターのトースト通知をイベント駆動で即座に検知。
- 🛡️ **柔軟なフィルタリング**: アプリ許可リスト、キーワードミュート、重複排除（10秒以内同報）、間引き要約（30秒4件以上）、おやすみ時間帯ミュート。
- 🖥️ **WPF タスクトレイ常駐**: `H.NotifyIcon` によるトレイ常駐、ホットキーミュート、設定ウィンドウ。
- 🔒 **プライバシー保護**: 完全ローカル動作。テレメトリ収集なし、通信先はローカルホスト（`127.0.0.1:50021`）のみ。

---

## アーキテクチャ

```
┌────────────────┐   WinRT    ┌──────────────────┐
│ Windows 通知    │ ────────▶ │ NotificationWatcher│
│ (Action Center) │  イベント  │  (取得・正規化)     │
└────────────────┘ (~200ms)   └─────────┬──────────┘
                                        │ NotificationEvent
                                        ▼
                              ┌──────────────────┐
                              │ Filter / Policy   │  対象アプリ判定 / キーワード / 重複排除
                              └─────────┬──────────┘
                                        │ SpeechRequest
                                        ▼
                              ┌──────────────────┐
                              │ SpeechQueue       │  優先度・割込み制御 / 文分割
                              └─────────┬──────────┘
                                        │
                                        ▼
                              ┌──────────────────┐   HTTP    ┌───────────────┐
                              │ VoicevoxClient    │ ────────▶ │ VOICEVOX       │
                              │ (streaming 合成)  │ ◀──────── │ エンジン        │
                              └─────────┬──────────┘ 音声chunk│ (localhost)    │
                                        │ (16bit PCM)        └───────────────┘
                                        ▼
                              ┌──────────────────┐
                              │ AudioPlayer       │  NAudio BufferedWaveProvider
                              │ (WasapiPlayer)    │  逐次再生・出力先切替
                              └──────────────────┘
```

---

## 必要要件

1. **OS**: Windows 11（必須）または Windows 10 Version 1903 以降 (x64)
2. **.NET SDK**: **.NET 10 LTS** (SDK 10.0.403 以降)
3. **Windows 開発者モード**: `UserNotificationListener` を使うため、開発時は **開発者モード** を有効にしてください（「Windows 設定」>「システム」>「開発者向け」>「開発者モード」を ON）。
4. **VOICEVOX**: VOICEVOX エディタ または VOICEVOX エンジン 0.26.0 以降（デフォルトポート `50021` で起動）。

---

## プロジェクト構成

```
NotifyVox/
├── NotifyVox.slnx                    # ソリューション定義
├── Directory.Build.props              # 共通ビルド設定 (C# latest, Nullable, etc.)
├── Directory.Packages.props           # NuGet パッケージ一元管理 (Central Package Management)
├── global.json                        # .NET 10 SDK 固定 & Microsoft.Testing.Platform 有効化
│
├── src/
│   ├── NotifyVox.Core/               # 【純粋ロジック層】(net10.0)
│   │   # TextFormatter, FilterPolicy, SpeechQueue, SettingsStore, VoicevoxClient
│   │
│   ├── NotifyVox.Windows/            # 【Windows 固有層】(net10.0-windows10.0.19041.0)
│   │   # UserNotificationListener, NAudio 再生エンジン, Win32 P/Invoke
│   │
│   └── NotifyVox.App/                # 【WPF アプリ本体・UI層】(net10.0-windows10.0.19041.0)
│       # タスクトレイ常駐, 設定画面 (WPF), MVVM, Serilog, StartupTask
│
├── tests/
│   └── NotifyVox.Core.Tests/         # 【単体テスト】(xUnit v3 + MTP + NSubstitute)
│
├── packaging/                        # 【MSIX パッケージ定義】
│   ├── AppxManifest.xml              # userNotificationListener 権限・StartupTask 宣言
│   └── Assets/                       # ロゴ・アプリアイコン
│
├── scripts/
│   ├── register-dev.ps1              # 開発用 loose-layout 登録・起動スクリプト
│   └── send-test-toast.ps1           # 結合テスト用トースト通知発行スクリプト
│
└── docs/
    ├── ENVIRONMENT_AND_ANALYSIS_REPORT.md # 環境構築・API分析レポート
    └── WALKTHROUGH.md                # 動作確認・検証ウォークスルー
```

---

## クイックスタート (ビルド＆実行)

### 1. リポジトリのビルド
```powershell
dotnet build NotifyVox.slnx
```

### 2. 単体テストの実行
.NET 10 の `Microsoft.Testing.Platform` 経由でテストが実行されます。
```powershell
dotnet test NotifyVox.slnx
```

### 3. 開発版パッケージの登録と起動
`UserNotificationListener` はパッケージ ID を持つアプリケーションからのみ動作します。開発時は自己署名パッケージを作成し直すことなく、`scripts/register-dev.ps1` を実行することでビルド出力フォルダを直接 loose layout として登録・起動できます。

```powershell
# ビルドしてパッケージ登録し、アプリを起動する
pwsh scripts/register-dev.ps1 -Launch
```

> [!NOTE]
> 登録解除したい場合は `pwsh scripts/register-dev.ps1 -Unregister` を実行します。

### 4. テスト用トースト通知の発行
アプリ起動中に、別ターミナルからテスト通知を発行して動作を確認できます。

```powershell
# Windows PowerShell 5.1 経由でトースト通知を発行
powershell.exe -NoProfile -File scripts/send-test-toast.ps1 -Title "NotifyVox テスト" -Body "通知の読み上げテストです。"
```

---

## 採用技術・主要ライブラリ

| 領域 | 技術 / ライブラリ | バージョン | 特記事項 |
|---|---|---|---|
| 言語 / ランタイム | C# / .NET 10 LTS | 10.0.403 | 長期サポート LTS |
| 通知受信 | WinRT `UserNotificationListener` | 10.0.19041.38 | CsWinRT 経由。マニフェストで `userNotificationListener` 権限を宣言 |
| 音声再生 | `NAudio` | 3.3.0 | `BufferedWaveProvider` によるストリーミング再生、`WasapiPlayer` |
| トレイ常駐 | `H.NotifyIcon.Wpf` | 2.4.1 | WPF ネイティブ対応のタスクトレイコンポーネント |
| MVVM | `CommunityToolkit.Mvvm` | 8.4.2 | Source Generator によるボイラープレート削減 |
| DI / 通信 | `Microsoft.Extensions.Hosting` / `Http` | 10.0.14 | `IHttpClientFactory` による接続管理 |
| ログ | `Serilog` | 10.0.0 / 7.0.0 | ローテーション付き（5MB × 3 世代、本文マスク） |
| テスト | `xunit.v3` + `MTP` + `NSubstitute` | 4.0.2 / 6.2.0 | .NET 10 次世代テスト基盤 |

---

## ライセンス

本プロジェクトは個人利用・オープンソースとして開発されています。
各サードパーティライブラリおよび VOICEVOX の利用規約についてはそれぞれの公式ドキュメントを参照してください。
