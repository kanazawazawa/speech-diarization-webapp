# リアルタイム音声文字起こしシステム

Azure Speech ServiceとAzure AI Foundryを使用したリアルタイム音声文字起こし、話者分離、AI要約のデモアプリケーションです。

## ⚠️ 重要な注意事項・免責事項

**このシステムはデモンストレーション目的のみです。以下の点にご注意ください：**

### 🔒 データとプライバシー
- **音声データは一時的にサーバーに保存されます**（処理後に自動削除）
- 文字起こし結果と要約は**クラウドサービスに送信**されます
- **会話履歴はサービス側に保存**されます
- データは暗号化された通信で送信されますが、**完全な秘匿性は保証されません**

### 🚫 絶対に入力してはいけない情報
- **実名、住所、電話番号**などの個人を特定できる情報（PII）
- **証明書番号、ID番号、マイナンバー**
- その他の**機密情報や個人情報**

### ⚠️ 免責事項
- 音声認識と話者分離の精度は**保証されません**
- AI要約の内容の正確性は**保証されません**
- このシステムの使用による**いかなる損害についても責任を負いません**

### 🌍 個人情報の取り扱い
- **このアプリはデモ・検証目的です**
- **実在の個人情報は入力しないでください**
- 本番環境での使用には法的審査が必要です

### 💡 推奨される使い方
架空のシナリオでのデモンストレーションにご利用ください。テストデータには「サンプルA」「テストケース1」などをお使いください。

## 機能

- 🎤 **リアルタイム音声文字起こし**: ブラウザマイクからの音声をリアルタイムで文字起こし
- 👥 **話者分離**: 停止後に高精度な話者分離を実行（Azure Speech Fast Transcription API使用）
- 🤖 **AI要約生成**: Azure AI Foundry Agentによる内容要約
- 📝 **速記者メモ**: 文字起こし中にメモを挿入可能
- 💾 **音声保存**: サーバー側でWAV形式で一時保存（処理後自動削除）
- 🎨 **2カラムUI**: リアルタイム文字起こし(左)と話者分離結果(右)を同時表示

## 必要な環境

- .NET 8.0 SDK
- Azure Speech Serviceサブスクリプション
- Azure AI Foundryプロジェクトとエージェント
- モダンブラウザ(Chrome, Edge推奨)

## セットアップ

### 1. リポジトリのクローン

```bash
git clone <repository-url>
cd 20251204-speech2text
```

### 2. Azure Speech Serviceの設定

1. [Azure Portal](https://portal.azure.com)でSpeech Serviceリソースを作成
2. APIキーとエンドポイントを取得
3. 開発環境用の設定ファイルを作成:

```bash
cp appsettings.json appsettings.Development.json
```

4. `appsettings.Development.json`に実際の値を設定:

```json
{
  "AzureSpeech": {
    "Key": "YOUR_AZURE_SPEECH_KEY_HERE",
    "Endpoint": "https://YOUR_REGION.cognitiveservices.azure.com/",
    "Region": "YOUR_REGION"
  },
  "AzureAIFoundry": {
    "Endpoint": "https://YOUR_AI_FOUNDRY_ENDPOINT/api/projects/YOUR_PROJECT",
    "AgentName": "YOUR_AGENT_NAME"
  },
  "Recognition": {
    "Language": "ja-JP"
  }
}
```

**⚠️ セキュリティ警告**: 
- `appsettings.Development.json`は`.gitignore`に含まれています
- `appsettings.json`は本番用のプレースホルダー値のみでGitにコミット可能です
- APIキーを含むファイルをコミットしないでください

### 3. アプリケーションの実行

```bash
dotnet build
dotnet run
```

ブラウザで `https://localhost:5001` を開きます。

## 使い方

1. 初回アクセス時に**プライバシーポリシーと利用規約**を確認し、同意する
2. **マイクON**ボタンをクリックして録音開始
3. 音声が自動的にリアルタイムで文字起こしされます(左側)
4. **速記者メモ**欄にメモを入力してEnterで追加
5. **停止ボタン**をクリックすると話者分離が実行されます(右側)
6. **要約生成**ボタンで内容を要約（Azure AI Foundry Agent使用）
7. 音声データはサーバー側で一時保存され、処理後に自動削除されます

## 技術スタック

- **バックエンド**: .NET 8.0 Blazor Server
- **Azure Speech SDK**: 1.47.0
- **Fast Transcription API**: 2024-11-15
- **Azure AI Foundry**: Azure.AI.Projects 1.2.0-beta.4
- **認証**: Azure.Identity (DefaultAzureCredential / Managed Identity)
- **フロントエンド**: Bootstrap 5.3.0
- **音声処理**: Web Audio API (ScriptProcessorNode)

## プロジェクト構造

```
├── Components/
│   └── Pages/
│       └── Home.razor          # メインUI（プライバシー通知含む）
├── Services/
│   ├── SpeechRecognitionService.cs  # Azure Speech統合
│   └── SummarizationService.cs      # Azure AI Foundry統合
├── wwwroot/
│   └── js/
│       └── audioRecorder.js    # ブラウザ音声キャプチャ
├── appsettings.example.json    # 設定ファイルのテンプレート
└── Program.cs                  # アプリケーションエントリポイント
```

## Azure App Serviceへのデプロイ

詳細は `DEPLOY.md` を参照してください（このファイルはGitHubにアップロードされません）。

### Managed Identityの設定

本番環境ではManaged Identityを使用して認証します：

1. App ServiceでSystem-assigned Managed Identityを有効化
2. 以下のロールを割り当て：
   - **Azure AI User** (AI Foundryプロジェクトに対して)
   - **Cognitive Services User** (Speech Serviceに対して)

## セキュリティとプライバシー

### 開発環境
- APIキーは環境変数または安全なシークレット管理サービスに保存してください
- `appsettings.Development.json`、`appsettings.Production.json`をGitにコミットしないでください（.gitignoreで除外済み）
- HTTPS接続を使用してください

### 本番環境
- **本番環境では適切な認証・認可を実装してください**
- Managed Identityを使用してAPIキーをコード内に含めないようにしてください
- **実運用には適切な法的審査とコンプライアンス対応が必要です**
- 個人情報を扱う場合は、適用される法規制（GDPR、HIPAA、個人情報保護法など）への準拠を確認してください

### データ保護
- 音声データはサーバー側で一時的に保存され、処理後に自動削除されます
- ログに個人情報が含まれないように注意してください
- 本番環境では適切なデータ保持ポリシーを実装してください

## 免責事項

このアプリケーションはデモンストレーション目的のみです：
- 音声認識と話者分離の精度は保証されません
- AI要約の内容の正確性は保証されません
- このシステムの使用によるいかなる損害についても責任を負いません
- **実在の個人情報や機密情報は使用しないでください**

## ライセンス

このプロジェクトはデモンストレーション目的で作成されています。

## トラブルシューティング

### マイクアクセスエラー
- ブラウザのマイク許可設定を確認してください
- HTTPSで接続していることを確認してください

### 音声認識が動作しない
- Azure Speech Serviceのリージョンが正しいか確認してください
- APIキーが有効か確認してください
- ブラウザコンソールでエラーを確認してください

### 話者分離が実行されない
- WebMファイルがダウンロードフォルダに保存されているか確認してください
- Fast Transcription APIがリージョンでサポートされているか確認してください

## 参考資料

- [Azure Speech Service Documentation](https://learn.microsoft.com/azure/cognitive-services/speech-service/)
- [Fast Transcription API](https://learn.microsoft.com/azure/cognitive-services/speech-service/fast-transcription-create)
- [Conversation Transcription](https://learn.microsoft.com/azure/cognitive-services/speech-service/conversation-transcription)
