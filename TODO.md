# 開発ToDo リスト

## 現在のフェーズ: コア機能実装完了 → 動作確認待ち

---

## 実装タスク一覧

### Phase 1: 環境確認
- [x] Windows側でビルド・実行確認
- [ ] PaSoRi接続テスト

### Phase 2: Models層の実装
- [x] Staff.cs（職員エンティティ）
- [x] IcCard.cs（交通系ICカードエンティティ）
- [x] Ledger.cs（履歴概要エンティティ）
- [x] LedgerDetail.cs（履歴詳細エンティティ）
- [x] OperationLog.cs（操作ログエンティティ）
- [x] AppSettings.cs（設定エンティティ）

### Phase 3: Data層の実装
- [x] schema.sql（テーブル定義）
- [x] DbContext.cs（DB接続管理）
- [x] IStaffRepository / StaffRepository
- [x] ICardRepository / CardRepository
- [x] ILedgerRepository / LedgerRepository
- [x] ISettingsRepository / SettingsRepository
- [x] IOperationLogRepository / OperationLogRepository

### Phase 4: Services層の実装
- [x] CardTypeDetector.cs（カード種別判別）※完了
- [x] WarekiConverter.cs（和暦変換）※完了
- [x] SummaryGenerator.cs（摘要文字列生成）
- [x] LendingService.cs（貸出・返却処理）
- [x] ReportService.cs（月次帳票作成）
- [x] BackupService.cs（バックアップ処理）
- [x] OperationLogger.cs（操作ログ記録）

### Phase 5: Infrastructure層の実装
- [x] ICardReader.cs（インターフェース）
- [x] PcScCardReader.cs（PaSoRi連携）
- [x] MockCardReader.cs（テスト用モック）
- [x] ISoundPlayer.cs（インターフェース）
- [x] SoundPlayer.cs（効果音再生）

### Phase 6: ViewModels層の実装
- [x] ViewModelBase.cs（基底クラス）
- [x] MainViewModel.cs（メイン画面）
- [ ] HistoryViewModel.cs（履歴表示）
- [ ] SettingsViewModel.cs（設定画面）
- [ ] ReportViewModel.cs（帳票作成）
- [ ] CardManageViewModel.cs（カード管理）
- [ ] StaffManageViewModel.cs（職員管理）

### Phase 7: Views層の実装
- [x] MainWindow.xaml（メイン画面）
- [ ] HistoryView.xaml（履歴表示画面）
- [ ] SettingsDialog.xaml（設定ダイアログ）
- [ ] ReportDialog.xaml（帳票作成ダイアログ）
- [ ] CardManageDialog.xaml（カード管理ダイアログ）
- [ ] StaffManageDialog.xaml（職員管理ダイアログ）
- [ ] BusStopInputDialog.xaml（バス停入力ダイアログ）

### Phase 8: テスト拡充
- [x] CardTypeDetectorTests.cs ※完了
- [x] WarekiConverterTests.cs ※完了
- [x] SummaryGeneratorTests.cs ※完了
- [ ] LendingServiceTests.cs
- [ ] ReportServiceTests.cs
- [ ] StaffRepositoryTests.cs
- [ ] CardRepositoryTests.cs
- [ ] LedgerRepositoryTests.cs

### Phase 9: 統合・リリース
- [ ] Windows側でのビルド確認
- [ ] CI通過確認
- [ ] 結合テストの実施
- [ ] リリーステスト（v0.1.0タグで自動リリース確認）
- [ ] ユーザーマニュアル作成
- [ ] 管理者マニュアル作成

---

## 完了したタスク

### CI/CD環境構築（完了）
- [x] .gitignore 作成
- [x] ディレクトリ構造作成
- [x] ICCardManager.sln 作成
- [x] ICCardManager.csproj 作成
- [x] ICCardManager.Tests.csproj 作成
- [x] Directory.Build.props / .editorconfig 作成
- [x] GitHub Actions ワークフロー作成（ci.yml, release.yml）
- [x] 最小限のソースコード作成（App.xaml, MainWindow）
- [x] CardTypeDetector / WarekiConverter 実装
- [x] 単体テスト作成
- [x] GitHub Actions CI 確認 ✅

### コア機能実装（完了）
- [x] Models層（エンティティ定義）
- [x] Data層（DB接続、リポジトリ）
- [x] Services層（ビジネスロジック）
- [x] Infrastructure層（カードリーダー、効果音）
- [x] ViewModels層（MainViewModel）
- [x] Views層（メイン画面）
- [x] DIコンテナ設定（App.xaml.cs）

---

## 次のステップ

1. **Windows側でビルド確認**
   - `dotnet build` でエラーがないか確認
   - アプリケーションが起動するか確認

2. **PaSoRi接続テスト**
   - 実機での動作確認

3. **残りのダイアログ実装**
   - 履歴表示、設定、帳票作成など

---

## 参考リンク

- GitHub リポジトリ: https://github.com/kuwayamamasayuki/ICCardManager
- GitHub Actions: https://github.com/kuwayamamasayuki/ICCardManager/actions
