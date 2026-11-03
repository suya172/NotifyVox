# NotifyVox 環境構築・ライブラリ導入・技術分析レポート

本ドキュメントは、設計書（[SPEC.md](../SPEC.md)）に基づき実施した **開発環境構築**、**ライブラリ選定・導入**、および **主要 API の詳細分析・実機検証結果** をまとめた技術レポートです。

---

## 1. 開発環境の構築

### 1.1 ランタイムおよび SDK
- **.NET バージョン**: **.NET 10 LTS**（SDK 10.0.403）
  - SPEC 初版では .NET 8 指定でしたが、.NET 8 の公式サポート終了が 2026 年 11 月 10 日に迫っていること、および開発 PC に .NET 10 SDK が導入されたことを受け、長期運用に適した .NET 10 LTS に選定しました。
- **アーキテクチャ**: `x64`（Windows 11 / Windows 10 1903 以降）

### 1.2 ソリューション構成
単体テストの独立性・保守性および WinRT / UI の責務分離のため、4 プロジェクト構成を採用しました。

```
NotifyVox/
├── NotifyVox.slnx                    # ソリューション定義
├── Directory.Build.props              # 全プロジェクト共通ビルド設定 (C# latest, Nullable, etc.)
├── Directory.Packages.props           # NuGet パッケージ一元管理 (Central Package Management)
├── global.json                        # SDK バージョン固定 & Microsoft.Testing.Platform 有効化
│
├── src/
│   ├── NotifyVox.Core/               # 【純粋ロジック層】(net10.0)
│   │   # TextFormatter, FilterPolicy, SpeechQueue, SettingsStore, VoicevoxClient (HTTP)
│   │   # WinRT や UI に依存せず、純粋な C# として高カバレッジで単体テスト可能
│   │
│   ├── NotifyVox.Windows/            # 【Windows 固有層】(net10.0-windows10.0.19041.0)
│   │   # UserNotificationListener (WinRT), NAudio 再生エンジン, Win32 P/Invoke
│   │
│   └── NotifyVox.App/                # 【WPF アプリ本体・UI層】(net10.0-windows10.0.19041.0)
│       # タスクトレイ常駐 (H.NotifyIcon), 設定ウィンドウ, MVVM, Serilog, StartupTask
│
├── tests/
│   └── NotifyVox.Core.Tests/         # 【単体テスト】(xUnit v3 + MTP + NSubstitute)
│
├── packaging/                        # 【MSIX パッケージ定義】
│   ├── AppxManifest.xml              # userNotificationListener 権限・StartupTask 宣言
│   └── Assets/                       # アプリアイコン・ロゴ画像
│
└── scripts/
    ├── register-dev.ps1              # 開発用: loose-layout でマニフェストを高速登録
    └── send-test-toast.ps1           # 結合テスト用: トースト通知を発行する PowerShell スクリプト
```

---

## 2. ライブラリ選定と導入状況

[Directory.Packages.props](../Directory.Packages.props) により、最新の安定版パッケージを一元管理しています。

| パッケージ | 導入バージョン | 用途・採用理由 |
|---|---|---|
| `Microsoft.Windows.SDK.NET.Ref` | 10.0.19041.38 | WinRT projection (TFM `net10.0-windows10.0.19041.0` により自動インポート) |
| `NAudio` | 3.3.0 | 音声ストリームチャンクの逐次再生 (`BufferedWaveProvider`)、出力デバイス制御 |
| `H.NotifyIcon.Wpf` | 2.4.1 | WPF 専用のタスクトレイ常駐制御（WinForms NotifyIcon より近代的な WPF 連携） |
| `CommunityToolkit.Mvvm` | 8.4.2 | MVVM ソースジェネレータ（`[ObservableProperty]`, `[RelayCommand]`） |
| `Microsoft.Extensions.Hosting` | 10.0.14 | アプリケーションライフサイクル、依存性注入 (DI) |
| `Microsoft.Extensions.Http` | 10.0.14 | `IHttpClientFactory` による VOICEVOX HTTP 通信管理 |
| `Serilog.Extensions.Hosting` / `Serilog.Sinks.File` | 10.0.0 / 7.0.0 | ローテーション付きログ出力（5MB × 3 世代、本文マスク） |
| `xunit.v3` / `xunit.runner.visualstudio` | 4.0.2 / 4.0.1 | 次世代 xUnit テストフレームワーク（.NET 10 MTP 完全対応） |
| `NSubstitute` | 6.2.0 | モックライブラリ |
| `Microsoft.Extensions.TimeProvider.Testing` | 10.11.0 | `FakeTimeProvider` による時間経過・重複排除・間引きルールの決定論的テスト |

---

## 3. 主要 API の分析および検証結果

### 3.1 Windows 通知取得 (`UserNotificationListener`)

実機でのプローブ検証プログラム（unpackaged / packaged 両方）を実行し、以下の決定的な挙動を確認しました。

- **Unpackaged (非パッケージ) 実行**: `GetAccessStatus()` や過去通知の取得 `GetNotificationsAsync()` は動作しますが、通知着信イベント `listener.NotificationChanged += ...` を登録すると **`COMException: 0x80070490`（要素が見つかりません）** がスローされます。
- **Packaged (MSIX loose-layout) 実行**: `AppxManifest.xml`（`uap3:Capability Name="userNotificationListener"`）を介して登録したプロセスからは、イベント購読が正常に完了し、**トースト通知が発行されてからわずか約 210ms 後に `Added` イベントを受信できる** ことを実機実証しました。

#### 通知データの正規化仕様
```csharp
// トースト通知からタイトルと本文を確実に抽出するコード
var binding = userNotification.Notification.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
if (binding != null)
{
    var texts = binding.GetTextElements();
    string title = texts.Count > 0 ? texts[0].Text : string.Empty;
    string body = texts.Count > 1 ? texts[1].Text : string.Empty;
    string appName = userNotification.AppInfo?.DisplayInfo?.DisplayName ?? "不明なアプリ";
    string appId = userNotification.AppInfo?.AppUserModelId ?? string.Empty;
}
```

---

### 3.2 VOICEVOX ENGINE 0.26.0 ストリーミング音声合成 API

公式エンジンリポジトリ（0.26.0 / PR #1823, #1876, #1880）のソースコードを精査し、仕様を確定しました。

#### (1) エンドポイント仕様
- **パス**: `POST /streaming_synthesis`
- **クエリパラメータ**:
  - `speaker`: スタイルID（int, 必須）
  - `start_offset`: 音声の開始位置（float, 既定: 0）
  - `segment_length`: 一度に合成する音声の長さ（float, 既定: 0.3 秒）
  - `enable_interrogative_upspeak`: 疑問文語尾自動調整（bool, 既定: true）
- **リクエストボディ**:
  - 従来の `POST /synthesis` と完全に同一の `AudioQuery`（JSON）
- **レスポンス**:
  - `Content-Type`: `audio/wav`
  - `Content-Length`: 生成される全体の WAV ファイルサイズ（ヘッダ 44 バイト + 全体 PCM サイズ）
  - **ストリーム仕様**: 最初に 44 バイトの標準 RIFF/WAVE ヘッダが送信され、以降は推論されたセグメント（既定約 0.3 秒分）ごとに 16bit PCM データが逐次 yield されます。

#### (2) スタイル判定とフォールバック要件
- キャラクターメタ情報 `GET /speakers` 内のスタイル情報に `type` フィールドが存在します。
  - `type == "streaming_talk"`: ストリーミング合成に対応。
  - `type == "talk"` または古いエンジンの場合: ストリーミング不可。
- **フォールバック設計**:
  `type != "streaming_talk"` または `/streaming_synthesis` で 404/400 等が返された場合は、SPEC §4.5 (F22-4) に従い従来の `POST /synthesis`（一括合成）へ透過的にフォールバックするパイプラインを設計します。

---

### 3.3 音声再生 (`NAudio 3.3.0`)

NAudio 3.3.0 の型定義をリフレクション解析し、以下の設計を確定しました。

1. **ストリーミング再生アーキテクチャ**:
   - HTTP レスポンスストリームから最初の 44 バイト（WAV ヘッダ）を読み取り、サンプリングレート（既定 24,000Hz）とチャンネル数（Mono/Stereo）を取得。
   - `BufferedWaveProvider`（`WaveFormat` 指定）を初期化。
   - バックグラウンドで HTTP チャンクを受信するたびに `bufferedWaveProvider.AddSamples(buffer)` を呼び出す。
   - 音声再生インスタンスは `bufferedWaveProvider` をソースとして即座に `Play()` を開始（文全体の合成完了を待たずに 1 チャンク目から発話開始）。
2. **API 刷新に伴う推奨実装**:
   - `WaveOutEvent` は `WaveOut` にリネーム（旧名は非推奨）。
   - 低遅延・出力デバイス切り替えには `WasapiPlayerBuilder` / `WasapiPlayer` が推奨されているため、WASAPI 経由でのデバイス切り替え・音量制御を実装。

---

## 4. ビルドおよびテスト動作確認

- **ソリューションビルド**:
  - `dotnet build NotifyVox.slnx` -> **0 エラー, 0 警告**
- **単体テスト**:
  - `dotnet test NotifyVox.slnx` -> **Microsoft.Testing.Platform 経由で正常完了（1/1 合格）**
- **開発用パッケージング**:
  - `scripts/register-dev.ps1` による loose-layout 登録が動作確認済み。
