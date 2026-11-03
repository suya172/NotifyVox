# NotifyVox

Windows の通知（トースト通知）を [VOICEVOX](https://voicevox.hiroshiba.jp/) でリアルタイムに読み上げるタスクトレイ常駐アプリです。

### 機能

- **超低遅延ストリーミング発話**: VOICEVOX 0.26.0+ のストリーミング合成に対応し、通知着信から即座に発話
- **アプリごとの個別設定**: 読み上げの ON/OFF、キャラクター（話者スタイル）の割り当て、読み上げ優先度
- **柔軟なミュート・抑制**:
  - ホットキー（既定: `Ctrl+Alt+M`）やトレイからの即時ミュート
  - 重複通知の自動スキップ（10秒以内同報）とスパム間引き要約
  - おやすみ時間帯の自動ミュート
  - 全画面アプリ（ゲーム・動画）起動中の自動ミュート
  - 指定キーワードのミュート
- **文章の自動整形**: URL の省略読み替え、連続絵文字の除去、最大文字数制限
- **完全ローカル動作**: 外部サーバーへの通信は行わず、通知本文のログ出力も行いません

---

### インストール方法

1. [VOICEVOX](https://voicevox.hiroshiba.jp/) をインストールして起動しておきます（.NET のインストールは不要です）。
2. [Releases](https://github.com/suya172/NotifyVox/releases) から最新の `NotifyVox-v*-win-x64.zip` をダウンロードして解凍します。
3. フォルダ内の **`Install.cmd`** を実行します。
   - ※ 初回のみ署名証明書登録のため管理者権限の確認が表示されます。
   - ※ アンインストールしたいときは同梱の `Uninstall.cmd` を実行すれば綺麗に削除できます。

---

### 開発者向け情報

詳しいアーキテクチャや仕様については [SPEC.md](SPEC.md) を参照してください。

```powershell
# ビルド
dotnet build NotifyVox.slnx

# テスト実行
dotnet test NotifyVox.slnx

# 開発用パッケージ登録・起動 (要 開発者モード)
pwsh scripts/register-dev.ps1 -Launch
```

---

### ライセンス

[MIT License](LICENSE) (c) 2026 suya172
