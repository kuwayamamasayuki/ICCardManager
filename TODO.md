# 開発ToDo リスト

## 現在のステータス: v2.2.0 リリース済み（全フェーズ完了）

---

## 実装タスク一覧

### Phase 1: 環境確認
- [x] Windows側でビルド・実行確認
- [x] PaSoRi接続テスト（単体テスト＋統合テスト作成済み）

### Phase 2: Models層の実装
- [x] Staff.cs（職員エンティティ）
- [x] IcCard.cs（交通系ICカードエンティティ）
- [x] Ledger.cs（履歴概要エンティティ）
- [x] LedgerDetail.cs（履歴詳細エンティティ）
- [x] OperationLog.cs（操作ログエンティティ）
- [x] AppSettings.cs（設定エンティティ）
- [x] MonthlyReportData.cs（月次帳票データ）
- [x] ReportRow.cs（帳票行データ）

### Phase 3: Data層の実装
- [x] schema.sql（テーブル定義）
- [x] DbContext.cs（DB接続管理）
- [x] IStaffRepository / StaffRepository
- [x] ICardRepository / CardRepository
- [x] ILedgerRepository / LedgerRepository
- [x] ISettingsRepository / SettingsRepository
- [x] IOperationLogRepository / OperationLogRepository

### Phase 4: Services層の実装
- [x] CardTypeDetector.cs（カード種別判別）
- [x] WarekiConverter.cs（和暦変換）
- [x] SummaryGenerator.cs（摘要文字列生成）
- [x] LendingService.cs（貸出・返却処理）
- [x] ReportService.cs（月次帳票作成）
- [x] BackupService.cs（バックアップ処理）
- [x] OperationLogger.cs（操作ログ記録）
- [x] ValidationService.cs（バリデーション）
- [x] CsvExportService.cs / CsvImportService.cs（CSV入出力）
- [x] LedgerMergeService.cs（履歴マージ）
- [x] LedgerSplitService.cs（履歴分割）
- [x] LedgerConsistencyChecker.cs（整合性チェック）
- [x] StationMasterService.cs（駅マスター管理）
- [x] DialogService.cs（ダイアログ管理）
- [x] PrintService.cs（印刷処理）
- [x] ToastNotificationService.cs（トースト通知）
- [x] OperationLogExcelExportService.cs（操作ログExcel出力）

### Phase 5: Infrastructure層の実装
- [x] ICardReader.cs（インターフェース）
- [x] FelicaCardReader.cs（PaSoRi + felicalib 経由でのFeliCa読み取り）
- [x] HybridCardReader.cs（ハイブリッドリーダー）
- [x] MockCardReader.cs（テスト用モック）
- [x] ISoundPlayer.cs（インターフェース）
- [x] SoundPlayer.cs（効果音再生）

### Phase 6: ViewModels層の実装
- [x] ViewModelBase.cs（基底クラス）
- [x] MainViewModel.cs（メイン画面）
- [x] SettingsViewModel.cs（設定画面）
- [x] ReportViewModel.cs（帳票作成）
- [x] CardManageViewModel.cs（カード管理）
- [x] StaffManageViewModel.cs（職員管理）
- [x] BusStopInputViewModel.cs（バス停入力）
- [x] LedgerDetailViewModel.cs（履歴詳細）
- [x] LedgerRowEditViewModel.cs（履歴行編集）
- [x] DataExportImportViewModel.cs（データ入出力）
- [x] IncompleteBusStopViewModel.cs（未入力バス停）
- [x] OperationLogSearchViewModel.cs（操作ログ検索）
- [x] PrintPreviewViewModel.cs（印刷プレビュー）
- [x] SystemManageViewModel.cs（システム管理）
- [x] VirtualCardViewModel.cs（仮想カード）

> **Note:** 履歴表示（HistoryViewModel）は独立した画面ではなく、MainViewModelおよびLedgerDetailViewModelに統合されている。

### Phase 7: Views層の実装
- [x] MainWindow.xaml（メイン画面）
- [x] SettingsDialog.xaml（設定ダイアログ）
- [x] ReportDialog.xaml（帳票作成ダイアログ）
- [x] CardManageDialog.xaml（カード管理ダイアログ）
- [x] StaffManageDialog.xaml（職員管理ダイアログ）
- [x] BusStopInputDialog.xaml（バス停入力ダイアログ）
- [x] LedgerDetailDialog.xaml（履歴詳細ダイアログ）
- [x] LedgerRowEditDialog.xaml（履歴行編集ダイアログ）
- [x] DataExportImportDialog.xaml（データ入出力ダイアログ）
- [x] IncompleteBusStopDialog.xaml（未入力バス停ダイアログ）
- [x] OperationLogDialog.xaml（操作ログダイアログ）
- [x] PrintPreviewDialog.xaml（印刷プレビューダイアログ）
- [x] SystemManageDialog.xaml（システム管理ダイアログ）
- [x] VirtualCardDialog.xaml（仮想カードダイアログ）
- [x] CardRegistrationModeDialog.xaml（カード登録モードダイアログ）
- [x] CardTypeSelectionDialog.xaml（カード種別選択ダイアログ）
- [x] MergeHistoryDialog.xaml（履歴マージダイアログ）
- [x] StaffAuthDialog.xaml（職員認証ダイアログ）
- [x] ToastNotificationWindow.xaml（トースト通知）

> **Note:** 履歴表示画面（HistoryView.xaml）は独立した画面ではなく、LedgerDetailDialog等に統合されている。

### Phase 8: テスト拡充
- [x] CardTypeDetectorTests.cs
- [x] WarekiConverterTests.cs
- [x] SummaryGeneratorTests.cs
- [x] LendingServiceTests.cs
- [x] ReportServiceTests.cs
- [x] StaffRepositoryTests.cs
- [x] CardRepositoryTests.cs
- [x] LedgerRepositoryTests.cs
- [x] その他 90以上のテストファイル（合計101ファイル、カバレッジ70%以上を維持）

### Phase 9: 統合・リリース
- [x] Windows側でのビルド確認
- [x] CI通過確認（GitHub Actions: ci.yml、カバレッジ70%閾値）
- [x] 結合テストの実施
- [x] リリーステスト（v0.1.0〜v2.2.0、67以上のタグでリリース実績あり）
- [x] ユーザーマニュアル作成（Markdown / Word / PDF）
- [x] 管理者マニュアル作成（Markdown / Word / PDF）
- [x] 開発者ガイド作成
- [x] かんたん導入ガイド作成

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
- [x] GitHub Actions CI 確認

### コア機能実装（完了）
- [x] Models層（エンティティ定義）
- [x] Data層（DB接続、リポジトリ）
- [x] Services層（ビジネスロジック）
- [x] Infrastructure層（カードリーダー、効果音）
- [x] ViewModels層（全画面）
- [x] Views層（メイン画面＋全ダイアログ）
- [x] DIコンテナ設定（App.xaml.cs）

### 追加機能（当初計画外で実装済み）
- [x] 共有フォルダモード（複数PC共有DB対応）
- [x] データ入出力（CSV Export/Import）
- [x] 履歴マージ機能
- [x] 履歴分割機能
- [x] 印刷プレビュー・印刷機能
- [x] トースト通知
- [x] 操作ログ検索・Excel出力
- [x] システム管理画面
- [x] 仮想カード機能
- [x] InnoSetupインストーラー

---

## 参考リンク

- GitHub リポジトリ: https://github.com/kuwayamamasayuki/ICCardManager
- GitHub Actions: https://github.com/kuwayamamasayuki/ICCardManager/actions
