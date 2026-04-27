# 更新履歴

### Unreleased

**セキュリティ修正**
- 監査ログ（operation_log）への一括操作の記録範囲を拡張。従来 INSERT/UPDATE/DELETE/RESTORE/MERGE/SPLIT のみ記録されていたが、データ移行・災害復旧系統の CSV インポート / CSV・Excel エクスポート / 手動バックアップ取得 / DB リストアも `operation_log` に残すようにした。`OperationLogger.Actions` に `IMPORT` / `EXPORT` / `BACKUP` を新設（`RESTORE` は既存のレコード単位復元と `TargetTable` で区別）、`Tables` に `database` / `ledger_detail` を追加。4 つの新 API（`LogImportAsync` / `LogExportAsync` / `LogBackupAsync` / `LogRestoreAsync`）を `DataExportImportViewModel` / `SystemManageViewModel` から呼び出す。操作者は `ICurrentOperatorContext` から解決し、セッション失効時は `GuiOperator`（IDm=`0000000000000000` / Name=`GUI操作`）へフォールバック（#1265 の方針踏襲）。これにより個人情報持ち出しや履歴改変の事後追跡が可能となる。`AfterData` JSON にはファイルパス・件数（Inserted/Skipped/Error、Record 等）を格納。単体テスト 7 件追加（#1302）
- 監査ログ（operation_log）への操作者なりすましを防止。`OperationLogger` の operator_idm / operator_name は `ICurrentOperatorContext`（職員証タッチ成功時に `StaffAuthService` が自動設定）からのみ解決される。旧 API（`operatorIdm` 引数付き）は `[Obsolete]` となり、渡された引数は無視される（#1265）
- `felicalib.dll` の完全性検証を起動時に実行（DLL Hijacking 対策）。既知の SHA-256 ハッシュと不一致の場合はエラーダイアログを表示してアプリを終了する。内部者が偽造 DLL を配置して IDm を盗聴・改ざんする攻撃を防止（#1266）
- CSV/Excel 式インジェクション (CSV Injection / Formula Injection) 対策。セル先頭が `=` / `+` / `-` / `@` / タブ / CR で始まる文字列にシングルクォート `'` を付与してテキスト・リテラルとして扱わせる。CSV インポート時の `note` / `summary` / `entry_station` / `exit_station` / `bus_stops` と、CSV/Excel エクスポート時の全ユーザー入力由来テキスト列に適用（#1267）
- `PathValidator.ContainsPathTraversal` のパストラバーサル検出を強化。従来の `path.Contains("..")` 単純検査を多段階検出に置き換え、(1) URL エンコード (`%2E%2E`) のデコード再検査、(2) セグメント単位の `..` 判定（混合区切り `/` と `\` 対応）、(3) 末尾空白を考慮した `".. "` の正規化判定、(4) UNC パスで `Path.GetFullPath` 後に元の `\\server\share` プレフィクスが保持されるかの境界チェック、を実施。エラーメッセージもユーザー向けに明瞭化（#1268）
- `PathValidator.ValidateBackupPath` に UNC パス到達性チェック（5秒タイムアウト）を追加。`Directory.Exists` がネットワーク不安定時に数十秒ハングする問題を解決。到達不可時は「ネットワーク共有に到達できません。ネットワーク接続を確認してください」と明確なエラーを表示。非同期版 `ValidateBackupPathAsync` も提供し、設定画面等 UI スレッドからの呼び出しでブロックしないように `SettingsViewModel.SaveAsync` を更新（#1269）

**開発基盤**
- ビルド時に大量発生していたコンパイラ／アナライザ警告 272 件を 0 件に整理。(1) `OperationLogger` の Issue #1265 で `[Obsolete]` 化された旧オーバーロード（`operatorIdm` 引数付き）を呼ぶ 7 ファイル・18 箇所を新 API に移行（`CardManageViewModel` / `StaffManageViewModel` / `MainViewModel` / `LedgerRowEditViewModel` / `LedgerDetailViewModel` / `LedgerSplitService` / `LedgerMergeService`）。操作者解決は `ICurrentOperatorContext` 経由へ一本化され、呼び出し元が渡していた `null` / `authResult.Idm` / `_operatorIdm` / `operatorIdm` は記録に使われなくなる（#1265 の方針完結）、(2) `MigrationHelpers.AddColumnIfNotExists` に欠落していた `<param>` 4 件（`connection` / `transaction` / `table` / `column`）を追記して CS1573 を解消、(3) `PathValidator` の XML ドキュメント内 `<see cref="Task.Run"/>` がオーバーロード曖昧（CS0419）かつ具体シグネチャ指定すると cref 解決失敗（CS1574）する両ハマりだったため、意味を保ったまま `<c>Task.Run</c>` 表記へ変更、(4) テストプロジェクト (`ICCardManager.Tests.csproj`) に `<NoWarn>` を追加してテスト特有の警告 9 種を限定抑制: `CS0618`（`[Obsolete]` API の挙動互換性検証で旧オーバーロードを直接呼ぶ）、`CS8600` / `CS8602` / `CS8603` / `CS8620` / `CS8625`（`null` を意図的に渡す／受けるテストケース）、`xUnit1012`（`null` を non-nullable パラメータに渡すテスト）、`xUnit1030`（`.claude/rules/async-configureawait.md` 規約でテストでは `ConfigureAwait(false)` を付けない）、`xUnit1031`（既存テストの同期 `.Result` / `.Wait()` 使用）。プロダクションコード側では抑制せず警告を引き続き有効化
- UI スレッドガード系テスト (`DbContextUiThreadGuardTests` / `BackupServiceUiThreadGuardTests`) が並列実行時に CI でフレーキー失敗する問題を解消。両クラスは `DbContext.IsOnUiThread` 内部フック（Issue #1281 で `AsyncLocal<Func<bool>?>` 化済み）を書き換えるが、`BackupService.ExecuteAutoBackupAsync` 内の `Task.Run` が親 ExecutionContext の AsyncLocal 値を継承する特性と xUnit のテストクラス並列実行が相互作用し、稀に別テストクラスのフックが読み出されるレースが発生していた（PR #1371 の初回 CI で顕在化）。新設した xUnit Collection 定義 `DbContextUiThreadHookCollection` (`DisableParallelization = true`) に両テストクラスを所属させ、同フックを書き換えるテストをシリアル実行させることで解消。本番コードは変更なし（影響範囲をテストプロジェクトに限定）。回帰防止として `DbContextUiThreadHookCollectionConfigurationTests`（5件）を追加し、(a) Collection 定義の `CollectionDefinition` 属性と `DisableParallelization=true` の維持、(b) 対象 2 クラスへの `[Collection]` 属性付与、(c) 対象クラスが `IDisposable` を実装してフックを既定値へ戻す形であることを静的解析で検証。`docs/design/07_テスト設計書.md §2.46` に並列実行制御の運用ルールを追記（#1372）
- リポジトリルートに `.gitattributes` を新設し、`packages.lock.json` / `*.json` / `*.yml` / `*.yaml` / dotfile (`.editorconfig` / `.gitignore` / `.gitattributes`) / `*.sh` の改行コードを **LF に正規化**。WSL / Windows 併用開発時に `dotnet restore` が生成する lock file の CRLF/LF 混在が毎回 "modified" として git 差分に現れる「誤差分ループ」問題を解消（Issue #1361 調査中に顕在化）。`.cs` / `.xaml` / `.csproj` など Windows 主戦場ファイルは Visual Studio の保存設定との衝突を避けるため **`* text=auto` を意図的に指定せず**対象外（将来 Issue で別途検討）。また `.db` / `.xlsx` / `.docx` / `.png` / `.ico` / `.wav` / `.dll` / `.exe` 等のバイナリを明示的に `binary` 指定して誤検出を防止。既存ファイルで CRLF だった `ICCardManager/.gitignore` と `ICCardManager/tests/ICCardManager.UITests/packages.lock.json` の 2 件を `git add --renormalize .` で LF に一括正規化。開発者ガイド §5.8 に改行コードポリシーを追記（#1368）
- 依存パッケージの既知 CVE 継続監視の仕組みを導入。(1) GitHub Actions `vulnerability-scan.yml` が週次 + csproj 更新時に `dotnet list package --vulnerable --include-transitive` を自動実行し、検出時にジョブ失敗で通知、(2) Dependabot 設定で本体/テスト/UIテスト/github-actions の4エコシステムを週次監視・PR自動作成、(3) 開発者ガイド §5.7 に重大度別 SLA（Critical/High は24時間以内）と対応手順を明記、(4) リリーススキル (`/release`) に Phase 1 前のセキュリティチェック項目を追加（#1272）

**アクセシビリティ改善**
- メイン画面の F1（帳票）/ F7（ヘルプ）ボタンが Windows 慣習（F1=ヘルプ）と異なる割当であることをユーザーへ明示。(1) `MainWindow.xaml` の F1 ボタン ToolTip に「※ヘルプは F7 です」、F7 ボタン ToolTip に「※F1 ではありません」を追記、(2) `AutomationProperties.HelpText` にも同旨の注記を追加しスクリーンリーダーで読み上げられるようにした、(3) ユーザーマニュアル「3.1 メイン画面」の機能ボタン表直下、管理者マニュアル「付録 A. ショートカットキー」直下に採用理由を含む注記を追加、(4) 画面設計書 `03_画面設計書.md §5.1` にメイン画面ファンクションキー表（F1〜F7）と Issue #1289 案2（現状維持＋明示責任）の設計判断を記載。案2 を採用した理由は、月次帳票が本アプリ最頻出操作であり F1 の優位性を維持すること、および既存ユーザーの操作記憶を守ること。回帰防止として `MainWindowKeyBindingTests`（4件）を追加し、KeyBinding の紐付け先コマンドと F1/F7 ボタンの ToolTip/HelpText に慣習差異の注記が含まれることを静的解析で検証（#1289）
- トースト通知が文字サイズ「大/特大」設定時に画面外へはみ出したり読み切れなくなる問題を修正。(1) ウィンドウサイズを固定 360px から `MinWidth`/`MaxWidth=520`/`MaxHeight` の動的制約に変更、(2) フォントサイズに応じて MinWidth と MaxHeight を線形スケール（Medium 360×220 → ExtraLarge 468×292）、(3) タイトルは `TextTrimming=CharacterEllipsis` で単一行に収め高さ暴走を防止、(4) 低残高警告文を簡潔化（「残額が少なくなっています（しきい値: 10,000円）」→「残額不足（<10,000円）」）。計算ロジックは `Common/ToastLayoutCalculator` に抽出して単体テスト可能化（#1273）
- 色覚多様性対応: 貸出/返却/払戻済状態を「色＋アイコン＋テキスト＋スクリーンリーダー用説明文」の4要素で統一。`Common/LendingStatusPresenter` 新規実装で、状態表示の三点セット（アイコン `📤`/`📥`/`🚫`、短ラベル、完全説明文）を一元管理。`CardBalanceDashboardItem` / `CardDto` に `LentStatusAccessibilityText` を追加し、MainWindow ダッシュボード・カード管理ダイアログの `AutomationProperties.Name` にバインド。カード管理ダイアログでは従来テキストのみだった状態列にアイコンを追加（#1274）
- 主要ダイアログ（LedgerRowEditDialog / SettingsDialog / DataExportImportDialog / BusStopInputDialog）のラベルを `TextBlock` から WPF `Label` + `Target` 属性へ移行。スクリーンリーダーがラベルと入力コントロールの関連付けを認識できるようにし（WCAG 2.1 SC 1.3.1 達成）、同時に `_X` 記法で Alt+キーのアクセスキーを付与（例: 日付=Alt+D、摘要=Alt+S）。`Target="{Binding ElementName=...}"` でラベルと対象コントロールを紐づけ、`AutomationProperties.Name` と併用してスクリーンリーダー/キーボード両方を改善。割り当て一覧は `03_画面設計書.md §5.3` に記載。ダイアログ内でアクセスキーは重複させない原則で統一（#1276）
- 全ダイアログ（CardManageDialog / SettingsDialog / DataExportImportDialog / LedgerRowEditDialog / BusStopInputDialog）に初期フォーカスを設定。Window ルート要素に `FocusManager.FocusedElement="{Binding ElementName=...}"` を追加し、起動時に最も操作頻度の高いコントロール（カード一覧 / トースト位置 / データ種別 / 日付 など）へフォーカスを当てるようにした。BusStopInputDialog は ListView 内の動的 TextBox を対象とするため、既存の `ContentRendered` + `VisualTreeHelper` 走査方式（Issue #1133）で対応済みであることを確認。回帰防止として `DialogInitialFocusTests` を新規追加し、XAML 上の属性存在とコードビハインドの `Focus()` 呼出しを静的解析で検証。割り当て一覧は `03_画面設計書.md §5.2` に記載（#1277）
- `AutomationProperties.Name` / `HelpText` を全面整備（WCAG 2.1 AA 達成基準 4.1.2 準拠）。(1) MainWindow の使い方ガイド Border に HelpText 追加、(2) CardManageDialog のカード件数 TextBlock に `CountToAccessibilityTextConverter` 経由で動的な読み上げテキスト（「登録カードは5件です」/「登録カードはまだありません。新規登録してください」）と `LiveSetting="Polite"` を付与、(3) ReportDialog の先月/今月トグルボタンに選択状態を反映した `Name`/`HelpText`（「先月（選択中）」など）を DataTrigger ベースで実装、(4) ReportDialog の行チェックボックス・ListBox・ProgressBar・ステータステキスト・キャンセルボタン等に欠落していた `Name`/`HelpText` を追加、ステータステキストは `LiveSetting="Assertive"` で即時読み上げ。件数表示用に `Common/CountToAccessibilityTextConverter` を新規実装し 0 件 / 1 件以上 / null / 非数値 / 主語未指定等の境界ケースを `CountToAccessibilityTextConverterTests`（14件）で検証（#1278）
- 入力検証エラー時の視覚的フィードバックとフォーカス移動を追加（WCAG 2.1 SC 3.3.1 エラーの特定）。(1) `Common/Validation/NumericRangeValidationRule` を新規実装し、SettingsDialog の残額警告しきい値（0〜20,000 円）と LedgerRowEditDialog の受入・払出金額（0 以上）の WPF Binding に組み込み、入力エラー時に `Validation.HasError=true` を発火、(2) `AccessibilityStyles.xaml` に全 TextBox 用の暗黙的 `Style` + `Validation.ErrorTemplate` を追加し、赤枠（2px、色覚多様性対応のため枠線太さと⚠アイコンも併用）と ToolTip でエラーメッセージを表示、⚠アイコンに `AutomationProperties.Name="入力エラーあり"` を付与、(3) ViewModel 側に `FirstErrorField` プロパティを追加、`LedgerRowEditViewModel.Validate()` と `SettingsViewModel.SaveAsync()` が最初のエラー発生プロパティ名を設定、(4) Dialog のコードビハインドが `ContentRendered` 時（初期表示で既にエラー）および保存ボタン押下で `CanSave=false` のとき該当 TextBox に `Focus()` + `SelectAll()` を実行。`NumericRangeValidationRuleTests`（10件）と `LedgerRowEditViewModelTests` に `FirstErrorField` 検証テスト（5件）を追加（#1279）
- 全ダイアログに `MinHeight` / `MinWidth` を設定し低解像度環境（1366×768 ノート PC 等）で OK/キャンセルボタンが画面外に隠れる問題を解消。(1) LedgerRowEditDialog は `ResizeMode="NoResize"` → `CanResize`、`MinHeight=400` / `MinWidth=500` を追加し、入力フォーム領域を `ScrollViewer` で囲んでリサイズ時に縦スクロール可能に、(2) SettingsDialog は初期高さ 820px → 680px（1366×768 で収まる値）、`MinHeight=500` / `MinWidth=480`、`CanResize` へ変更、(3) SystemManageDialog に `MinWidth=520` を追加、(4) CardRegistrationModeDialog / CardTypeSelectionDialog / StaffAuthDialog（`SizeToContent="Height"` の小ダイアログ）に `MinWidth=380` / `MinHeight=220〜260` を追加。回帰防止として `DialogMinimumSizeTests` を新規追加し、(a) 全 17 ダイアログに `MinHeight` / `MinWidth` 属性が存在すること、(b) `MinHeight ≤ 720`（1366×768 実用高さ）、(c) 入力フォーム型ダイアログは ScrollViewer で囲まれていること、(d) LedgerRowEditDialog/SettingsDialog が `CanResize` であることを静的解析で検証（73件）（#1280）

**バグ修正**
- 新規職員登録ダイアログで職員証をタッチした直後、氏名入力欄へキーボードフォーカスが移らずユーザーがマウスで氏名欄をクリックしないと文字入力できなかった問題を修正。`StaffManageDialog.xaml` には氏名 TextBox への明示的なフォーカス指定（`x:Name`／`FocusManager.FocusedElement`）が無く、`StaffManageViewModel.StartNewStaffWithIdmAsync` が `IsWaitingForCard = false` を立てた後も View 側にフォーカス指示が伝わらない構造だった。Issue 推奨の「案 1（MVVM 原則準拠）」を採用し、(1) `StaffManageViewModel` に `event EventHandler? RequestNameFocus` を追加して未登録職員証分岐の末尾で発火（既登録／削除済み分岐は `return true` でダイアログを閉じるため発火させない）、(2) `StaffManageDialog.xaml` の氏名 TextBox に `x:Name="NameTextBox"` を付与、(3) `StaffManageDialog` コードビハインドがイベントを購読し `Dispatcher.BeginInvoke(... DispatcherPriority.Input)` で `NameTextBox.Focus()` + `Keyboard.Focus(NameTextBox)` を呼ぶ。`Input` 優先度はレイアウト更新と入力処理が落ち着いてからフォーカスを投入するため、`Focus()` + `Keyboard.Focus()` 二段呼びは論理フォーカスとキーボードフォーカスを同期させ即タイプ可能な状態にするための WPF イディオム。回帰防止として `StaffManageViewModelTests` に 2 件追加し、(a) 未登録職員証で `RequestNameFocus` が 1 回発火し `shouldClose==false`、(b) 既登録職員証では発火せず `shouldClose==true`（ダイアログを閉じる分岐）であることを検証。実フォーカス確定は WPF Dispatcher 動作が必要なため手動テストで確認。`07_テスト設計書.md §2.42a` に手動テスト項目を追記（#1429）
- PaSoRi カードリーダーが物理的に未接続のままアプリを起動するとステータスバー右下が「リーダー: 切断」ではなく「リーダー: 接続中」のまま表示されていた問題を修正。`FelicaCardReader.CheckFelicaLibAvailable()` が `DllNotFoundException` 以外の例外をすべて「DLL は存在する → 接続中」と判定していたため、PaSoRi 物理未接続でも `StartReadingAsync` が成功扱いとなり `Connected` 遷移していた。(1) `CheckFelicaLibAvailable()` を `IsFelicaLibLoaded()`（DLL ロード判定）と `IsReaderConnected()`（PaSoRi 物理接続判定）に二段化。後者は `FelicaUtility.GetIDm` の例外メッセージから "pasori" / "open" / "device" / "reader" キーワード（大文字小文字無視）を検出して未接続判定し、それ以外は「カードなし」扱いで接続中として扱う。判定ロジックは `IsReaderUnavailableException(Exception)` という静的純粋関数に切り出してテスト可能化、(2) `StartReadingAsync` 冒頭で DLL ロード成功後に PaSoRi 接続確認を追加し、未接続時はタイマーを起動せずに `Disconnected` を確実に通知、(3) 状態の初期確定通知（初期値 `Disconnected` のまま `Disconnected` を確定するパターン）が既存の差分通知ロジック (`SetConnectionState`) では抑止されてしまうため、初期化時のみ用の `SetConnectionStateForceNotify` を追加、(4) ヘルスチェック (`OnHealthCheckTimerElapsed`) も二段判定を使うようにし、起動後の PaSoRi 抜き差し検出も実態に即した形に修正、(5) `MainWindow.xaml` のステータスバー（接続状態アイコン・テキスト）のデフォルト Setter を「リーダー: 接続中」緑→「リーダー: 切断」赤に反転し、`Connected` を明示 DataTrigger 化（`Disconnected` トリガはデフォルトと一致するため削除）。これにより felicalib 例外メッセージの将来変化に対する fail-safe 表示として二重防御。再接続ボタンの可視性は変更なし（`Disconnected` 時表示の挙動を維持）。回帰防止として `FelicaCardReaderHelpersTests`（16件）を新規追加し、`IsReaderUnavailableException` の判定を網羅検証: `DllNotFoundException` の型判定、PaSoRi 未接続キーワード（pasori/open/device/reader）の小文字・大文字混在パターン、カードなし相当メッセージ（polling timeout / no card detected / system code not supported）の接続中扱い、空 / null メッセージ / null 例外の境界ケース。XAML の DataTrigger 評価は単体テスト困難のため実機検証で確認（#1428）
- データエクスポート時、「○○を保存しました」ダイアログが表示されたあとも、裏で動いていたプログレスバー（`IsBusy=true`）が消えない問題を修正。CSV エクスポート（`DataExportImportViewModel.ExportAsync`）と操作ログ Excel エクスポート（`OperationLogSearchViewModel.ExportToExcelAsync`）の両方で、`using (BeginBusy("エクスポート中..."))` スコープ内から `IDialogService.ShowInformation` / `ShowError`（内部は `MessageBox.Show`）を呼んでいた。`MessageBox.Show` は同期モーダルでユーザーが OK を押すまでブロックするため、`BusyScope.Dispose()` が走らず `IsBusy=true` のままとなり、ダイアログ背後にプログレスバーが残存していた。対応として、エクスポート本体を `ExportToFileAsync(string)` / `ExportToExcelFileAsync(string)` の内部メソッドに抽出し、`using` スコープ終了後（`IsBusy=false` 確定後）にダイアログを表示する構造へ変更。あわせて `CsvExportService` の Export 系メソッドと `OperationLogExcelExportService.ExportAsync` を `virtual` 化し、`SaveFileDialog` を介さずにエクスポート経路を直接起動できるようにして単体テストから検証可能とした。回帰防止として `DataExportImportViewModelTests` に 3 件（成功時・失敗時・例外時の `IsBusy` 状態）、`OperationLogSearchViewModelTests` に 2 件（成功時・失敗時）を追加し、`ShowInformation` / `ShowError` の `Callback` で呼び出し時点の `IsBusy` を捕捉して `false` であることをアサート（#1383）
- 共有フォルダモードで履歴画面を開いたまま30秒以上待っても、他 PC の貸出/返却/チャージ操作が表示中の履歴画面に反映されない問題を修正。カード一覧（`LentCards`）とダッシュボード（`CardBalanceDashboard`）は `SharedModeMonitor.HealthCheckCompleted` 経由の `RefreshSharedDataAsync` で 30 秒ごとに再読み込みされていたが、履歴画面（`HistoryLedgers`）の再読み込みだけが同経路から欠落していた。そのため、履歴画面を開いている利用者は「いったん別カードの履歴を表示してから戻る」操作でしか最新状態を得られず、他 PC 側の操作反映の把握に支障があった。`RefreshSharedDataAsync` に、貸出（L997）・返却（L1072）・チャージ（L2326）等のローカル操作完了ハンドラで Issue #526 から採用されている `if (IsHistoryVisible) await LoadHistoryLedgersAsync();` パターンをそのまま適用して整合性を取った。`CurrentState == AppState.Processing` のスキップガード（#1282 で明文化）は手前にあるため、カードタッチ対応中に不要な再描画が走ることはない。回帰防止として `MainViewModelSyncDisplayTests` に 3 件（履歴表示中は `GetPagedAsync` が対象カード IDm で呼ばれること / 履歴非表示時は呼ばれないこと / 処理中スキップ時は呼ばれないこと）を追加（#1381）
- 利用履歴詳細 CSV インポートのプレビューで表示される件数がインポート後の処理件数と一致しない問題を修正。プレビューは「Ledger グループ単位」（ledger_id ごとに +1）、インポート後は「CSV 行数単位」（`detailRows.Count` 加算）でカウントしていたため、例えば `ledger_id=1` に 3 行ある CSV だとプレビューは `UpdateCount=1`、インポート後は `ImportedCount=3` と表示され、利用者から「プレビュー 20 件 / 読み込み後 32 件」のような乖離として報告されていた。インポート側（`NewLedgerFromSegmentsBuilder.BuildAndInsertAsync` は `detailRows.Count` を返却、既存 Ledger 経路も `importedCount += detailRows.Count` / `skippedCount += detailRows.Count`）が既に行数ベースであることから、プレビュー側を行数ベースに揃える方針で `CsvImportService.Detail.cs` の `PreviewLedgerDetailsInternalAsync` を修正。`newCount` は新規 Ledger セグメント非分割時 `detailList.Count` を、セグメント分割時は各 `segment.Details.Count` を加算、`updateCount` / `skipCount` は `detailRows.Count` を加算する形に統一。プレビュー画面のアイテム件数（Items.Count）は従来どおり表示単位の Ledger グループ／セグメント単位を維持し、集計値（NewCount/UpdateCount/SkipCount）のみ CSV 行数ベースに変更。回帰防止として `LedgerDetails_既存更新_プレビューとインポートの件数が一致` / `LedgerDetails_既存スキップ_プレビューとインポートの件数が一致` / `LedgerDetails_新規作成_プレビューとインポートの件数が一致` の 3 件を `CsvImportServiceTests` に追加し、(a) プレビューとインポートの個別件数が CSV 行数で一致、(b) プレビュー合計（New+Update+Skip）とインポート合計（Imported+Skipped）が完全一致することを検証。既存テスト 3 件（`PreviewLedgerDetailsAsync_正常データ_プレビュー成功` / `PreviewLedgerDetailsAsync_異なる日付の空欄ID行_日付ごとに別プレビュー` / `PreviewLedgerDetailsAsync_利用履歴ID空欄_新規作成としてプレビュー`）も新仕様に合わせて期待値を更新（#1379）
- 履歴画面の「変更」ボタンを押した直後の編集ダイアログで利用者欄が空欄になる問題を修正。返却時に作成される利用 Ledger（鉄道・バス・残高不足統合）で `LenderIdm` カラムが設定されておらず（`StaffName` のみ設定）、編集ダイアログが `s.StaffIdm == ledger.LenderIdm` で職員照合していたため一致せず空欄表示となっていた。さらに `SelectedStaff = null` のまま備考等を修正して保存すると `StaffName` まで null で上書きされ、スナップショット情報も失われる二次被害があった。(1) `LendingService.CreateUsageLedgersAsync` のシグネチャに `staffIdm` を追加し、生成する全利用 Ledger（残高不足マージ・通常利用・既存統合補正）で `LenderIdm` を設定。呼び出し元 `PersistReturnAsync` は `lentRecord.LenderIdm` を、`RegisterCardWithUsageAsync` は `null` を渡す（カード登録時は利用者情報なしの既存仕様維持）。ポイント還元のみの Ledger は機械操作扱いで `LenderIdm`/`StaffName` とも `null` を維持。(2) 既存データの `LenderIdm = NULL` 行救済として `LedgerRowEditViewModel.InitializeForEditAsync` に `StaffName` フォールバックを追加。`LenderIdm` 一致が無い場合は同名アクティブ職員を選択（同名別職員を選ぶリスクはあるが物品出納簿は氏名表示のみで区別不可のため許容）。回帰防止として `LedgerRowEditViewModelTests` に 4 件、`LendingServiceTests` に 2 件のテストを追加。`02_DB設計書.md` の `lender_idm` 列説明と `07_テスト設計書.md` の UT-017a / UT-029a2 セクションも更新（#1303）
- カード/職員 CSV インポートのプレビューが備考欄の変更を検出しない問題を修正。`skipExisting=false`（既存データ上書きモード）で備考のみを書き換えた CSV をインポートした場合、プレビュー画面は「変更点なし → スキップ」と表示していたが、実インポートでは備考が更新されるためプレビュー表示と実挙動が乖離していた。原因は `CsvImportService.Card.cs` / `CsvImportService.Staff.cs` のプレビュー処理が備考フィールドをそもそも読み取っておらず、差分検出メソッド (`DetectCardChanges` / `DetectStaffChanges`) の比較対象に備考が含まれていなかったこと。(1) プレビューでも CSV の備考列を読み取って式インジェクション対策 (`FormulaInjectionSanitizer.Sanitize`) を適用、(2) 差分検出メソッドの引数に `newNote` を追加し既存 Note と比較、(3) 比較時はインポート本体と同じ `IsNullOrWhiteSpace` 正規化を適用して `null` と空文字を同一扱いに統一。これによりプレビューの `ImportAction.Update` / `Skip` 判定が実インポート結果と一致するようになった。回帰防止として `PreviewCardsAsync_NoteChanged_DetectsAsUpdate` / `PreviewCardsAsync_AllFieldsIdentical_DetectsAsSkip` / `PreviewCardsAsync_NoteNullVsEmpty_TreatedAsIdentical` / `PreviewStaffAsync_NoteChanged_DetectsAsUpdate` / `PreviewStaffAsync_AllFieldsIdentical_DetectsAsSkip` の5件を追加（#1370）
- カード/職員 CSV インポートで「既存データはスキップする」ON（既定）のときも備考を含む差分がある行は更新するように仕様変更（#1370 の続編）。`skipExisting=true` の従来仕様は「既存行（IDm 一致）は内容を問わず即スキップ」であり、備考だけを書き換えた CSV を再インポートしても反映されず、ユーザーは「既存データはスキップする」を OFF にして全項目上書き運用を強いられていた。本修正で「スキップ」の意味を **全項目（カード種別/管理番号/備考 または 氏名/職員番号/備考）が一致する場合のみスキップ** に再定義し、1 項目でも差分があれば更新対象とする。(1) `CsvImportService.Card.cs` / `CsvImportService.Staff.cs` のインポート本体とプレビューの両方で `DetectCardChanges` / `DetectStaffChanges` を常に呼び、差分 0 件かつ `skipExisting=true` のときのみスキップ（Import / Preview の判定ロジックを統一）、(2) `skipExisting=false` は従来どおり「常に更新」（no-op 更新も含む）として Ledger の既存挙動と整合。回帰防止として `ImportCardsAsync_NoteChanged_SkipExistingTrue_UpdatesInsteadOfSkip` / `ImportStaffAsync_NoteChanged_SkipExistingTrue_UpdatesInsteadOfSkip` / `PreviewCardsAsync_NoteChanged_SkipExistingTrue_DetectsAsUpdate` / `PreviewStaffAsync_NoteChanged_SkipExistingTrue_DetectsAsUpdate` の 4 件を追加。既存テスト 7 件（`ImportCardsAsync_ExistingCard_Skipped` / `ImportStaffAsync_ExistingStaff_Skipped` / `PreviewCardsAsync_ExistingCard_ShowsAsSkip` / `PreviewCardsAsync_AllFieldsIdentical_DetectsAsSkip` / `PreviewCardsAsync_NoteNullVsEmpty_TreatedAsIdentical` / `PreviewStaffAsync_AllFieldsIdentical_DetectsAsSkip` / `ImportCardsAsync_AlreadyRestoredCard_SkipExistingTrue_Skipped`）も新仕様に合わせて「CSV と既存レコードを完全一致」構成で保持（#1376）
- 自動バックアップと手動バックアップが起動直後から「DbContext.LeaseConnection() は WPF UI スレッドから呼び出せません」で失敗する問題を修正。Issue #1281 で追加した UI スレッドガードを回避するための Issue #1356 オフロード対応 (`InitializeDatabaseAsync` / `CleanupOldDataAsync` / `VacuumAsync`) の際、`BackupService` が対象外になっていた。(1) 自動バックアップ経路: `App.PerformStartupTasksAsync` (UI スレッド) から fire-and-forget される `BackupService.ExecuteAutoBackupAsync` は最初の `await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false)` がキャッシュヒット時（`App.ApplySavedSettings` で事前キャッシュ済み）に同期完了するため UI スレッドに留まり、続く `BackupDatabaseTo` → `LeaseConnection()` でガード発火。`BackupDatabaseTo` 呼び出しを `await Task.Run(() => BackupDatabaseTo(backupFilePath)).ConfigureAwait(false)` で包む。(2) 手動バックアップ経路: `SystemManageViewModel.CreateBackupAsync` が同期 `_backupService.CreateBackup(...)` を UI スレッドで呼んでおり同じくガード発火。`BackupService` に `public virtual Task<bool> CreateBackupAsync(string)` を追加（内部で `Task.Run` により既存 sync 実装へ委譲、sync 版はテスト経路で継続利用のため残置）、`SystemManageViewModel` の 3 呼び出し箇所（手動バックアップ + リストア前バックアップ DB/設定）を `await _backupService.CreateBackupAsync(...)` へ置換。回帰防止として `BackupServiceUiThreadGuardTests`（3件）を新設し、`DbContext.IsOnUiThread` を `ManagedThreadId` 判定フックに差し替えて (a) sync 版 `CreateBackup` が UI スレッド模擬時にガード発火して `false` を返すこと、(b) `CreateBackupAsync` が UI スレッド模擬時でも成功すること、(c) `ExecuteAutoBackupAsync` が UI スレッド模擬時でもバックアップファイルを生成することを検証（#1361）
- 共有フォルダモードの自動同期が起動直後から機能せず表示が「同期待ち...」のまま固定され、手動同期ボタンでのみ一時的に反映される問題を修正。Issue #1350 (Phase 2) で `SharedModeMonitor.ExecuteHealthCheckAsync` に `.ConfigureAwait(false)` を追加した際、30 秒タイマー経由の `HealthCheckCompleted` イベントが thread pool スレッドから発火されるようになり、購読側の `MainViewModel.OnSharedModeHealthCheckCompleted` が UI バインド済み `ObservableCollection` (`WarningMessages` / `LentCards` / `CardBalanceDashboard`) を非 UI スレッドから更新する形になっていた。実機 WPF では `NotSupportedException` が発生し `RefreshSharedDataAsync` の `try/catch` (LogDebug) で握り潰されるため `SharedModeMonitor.RecordRefresh()` が呼ばれず同期表示が更新されない。手動同期パス (`ManualRefreshAsync`) はボタンクリックから UI スレッドで完結するため正常に動くことで「一度は効くが止まる」症状として現れていた。`OnSharedModeHealthCheckCompleted` を `_dispatcherService.InvokeAsync(Func<Task>)` で UI スレッドへマーシャリングするよう修正（既存 `OnCardRead` と同一パターン、Service 層の `ConfigureAwait(false)` 規約は維持）。回帰防止として `MainViewModelIntegrationTests` に `SharedMode_HealthCheckCompleted_UI依存更新はIDispatcherServiceでmarshallingされること` テストを追加し、`SynchronousDispatcherService` に呼び出し回数カウンタ (`InvokeAsyncFuncCallCount` / `InvokeAsyncActionCallCount`) を追加して marshalling 経路の使用を検証（xUnit は `DispatcherSynchronizationContext` を持たず本バグは既存テストで検出不能だったため経路検証で固定化）（#1359）
- 起動時に `DbContext.LeaseConnection()` の UI スレッドガード (#1281) が発火して「起動エラーが発生しました。DbContext.LeaseConnection() は WPF UI スレッドから呼び出せません。」と表示され起動失敗する問題を修正。Issue #1281 (#1343) で UI スレッド検出ガードを入れた際、`App.OnStartup` から直接呼ばれる 3 つの同期 API (`DbContext.InitializeDatabase` / `CleanupOldData` / `Vacuum`) のオフロード対応が漏れていた。(1) `DbContext` に `InitializeDatabaseAsync` / `CleanupOldDataAsync` / `VacuumAsync` を追加（内部は `Task.Run` で既存 sync 実装へ委譲、sync 版はテスト経路で継続利用のため残置）、(2) `App.OnStartup` を `async void` 化、`App.InitializeDatabase` → `InitializeDatabaseAsync` / `PerformStartupTasks` → `PerformStartupTasksAsync` に改名して 3 呼び出しを await、(3) `PerformStartupTasksAsync` 内の `Task.Run(() => settingsRepository.GetAppSettings()).GetAwaiter().GetResult()` も素直な `await settingsRepository.GetAppSettingsAsync()` に置換。xUnit は `DispatcherSynchronizationContext` を持たないため既存テストでは検出不能で、実機起動のみで顕在化していた（#1356）
- `DbContext.LeaseConnection()`（同期版）を WPF UI スレッドから呼び出すとデッドロックするリスクを排除。(1) メソッド入り口に `Dispatcher.CheckAccess()` 相当の UI スレッド検出を追加し、該当時は `InvalidOperationException` を代替手段（`LeaseConnectionAsync()` / `Task.Run` オフロード）の案内付きでスロー。検出は `System.Windows` への直接依存を避けるため `SynchronizationContext.Current` の型名（`DispatcherSynchronizationContext`）で判定、テストで差し替え可能な静的フック `IsOnUiThread` を提供、(2) UI スレッドから同期版を呼んでいた既存箇所を修正: `App.xaml.cs` の DI SummaryGenerator ファクトリ・`ApplySavedSettings`・`PerformStartupTasks` は `Task.Run(...).GetAwaiter().GetResult()` でバックグラウンドスレッドにオフロード（起動時は競合する非同期操作がないため安全）、`LendingService.LendAsync` のチャージ摘要生成と `ReportService.CreateMonthlyReportAsync`/`CreateMonthlyReportsAsync` の設定取得は `GetAppSettingsAsync()` 版に置換。回帰防止として `DbContextUiThreadGuardTests`（6件）を追加し、(a) UI スレッド模擬時の例外発生、(b) エラーメッセージに代替 API 案内が含まれること、(c) 非 UI スレッド/`LeaseConnectionAsync`/`Task.Run` 経由では正常動作、(d) 既定実装が xUnit 環境で false を返すことを検証（#1281）
- タイムアウト系マジック定数を `Common/AppConstants.cs` に集約（`DefaultStaffCardTimeoutSeconds=60` / `DefaultCardRetouchTimeoutSeconds=30` / `DefaultCardLockTimeoutSeconds=5`）。`AppOptions` のデフォルト値は `AppConstants` を参照する形に統一。業務ルール由来のため各定数に `.claude/rules/business-logic.md` への参照コメントを追加。値は不変のため振る舞いに変化なし（appsettings.json オーバーライドも引き続き機能）（#1288）
- Service 層の async メソッドに `ConfigureAwait(false)` を一貫適用（Phase 2: 残り 11 ファイル、約 94 箇所）。Phase 1 (#1287) の残作業として、`DebugDataService` / `CsvExportService` / `LedgerMergeService` / `LedgerSplitService` / `DashboardService` / `PrintService` / `SharedModeMonitor` / `LedgerConsistencyChecker` / `NewLedgerFromSegmentsBuilder` / `OperationLogExcelExportService` / `WarningService` を対応。`(await task).ToDictionary` や括弧内 await など、Phase 1 で未対応だったパターンも個別に修正。UI 依存の `DialogService` / `StaffAuthService` は個別検討のため対象外（#1350）
- Service 層の async メソッドに `ConfigureAwait(false)` を一貫適用（Phase 1: 11 ファイル、約 130 箇所）。WPF UI スレッドへの不要な継続 dispatch を排除し、性能向上とデッドロック予防を図る。対象: `DbContext`, `LendingService`, `OperationLogger`, `CsvImportService` 全 partial, `BackupService`, `ReportService`, `ReportDataBuilder`。`.editorconfig` で `CA2007` を Service 層のみ suggestion レベルに有効化（ViewModels/Views/tests は無効化）。`.claude/rules/async-configureawait.md` に規約文書化。残りの Service は #1350 (Phase 2) で対応予定（#1287）
- `SharedModeMonitor` に `IDisposable` を実装し、`App.OnExit` で明示的に `Dispose()` を呼ぶようにした。従来は Singleton 登録された本サービスが `IDisposable` を実装していなかったため、アプリ終了時にタイマー（30秒ヘルスチェック + 1秒同期表示）が確実に停止される保証がなかった。`Dispose()` は `Stop()` を呼び冪等フラグを立てる実装で、二重 Dispose に対し冪等。Dispose 後の `Start()` は `ObjectDisposedException` をスロー。共有モードでネットワーク切断状態のまま終了するケースでのリソースリーク予防。単体テスト 5 件を追加（#1286）
- 例外処理の無言握りつぶしを解消し、トラブル時のデバッグ性・トレーサビリティを向上（5箇所）。(1) `DbContext.CheckConnection` の `catch (Exception)` で戻り値 false を返す処理に `LogDebug(ex, ...)` を追加（疎通失敗は頻繁に起きる想定のためログ肥大化回避で Debug レベル）、(2) `CardManageViewModel` のカード登録後の初期残額レコード作成失敗を `LogWarning` で記録（稀な想定のため Warning レベル、フィールド `_logger` を注入）、(3) `MainViewModel.RefreshSharedDataAsync` の共有モードリフレッシュ失敗を `LogDebug` で記録（タイマー起動で頻繁に呼ばれるため Debug）、(4)(5) `CsvImportService.Card`/`CsvImportService.Staff` の CSV インポートトランザクション内 `catch (SQLiteException)` と `catch (Exception)` 両方で `LogError(ex, ...)` を追加してロールバック理由をログに残してから再スロー。ViewModels の ILogger は optional constructor 引数（既存テストとの互換性維持）、CsvImportService は 6 引数オーバーロードで Moq プロキシ互換性を維持。`DbContextCheckConnectionLoggingTests`（3件）と `CsvImportServiceExceptionLoggingTests`（4件）を追加（#1282）

**ユーザー体験改善**
- エラーメッセージを「何が / なぜ / どうすれば」の3要素を含む具体的な文言に改善。`ValidationService` の全バリデーター（CardIdm/CardNumber/CardType/StaffIdm/StaffName/WarningBalance）と `LedgerRowEditViewModel.Validate` の7種のメッセージを、実際の入力値・期待値・解決アクションを明示する形に統一。例: 「カード種別を選択してください」→「カード種別が未選択です。ドロップダウンから「はやかけん」「nimoca」等を選択してください」。`.claude/rules/error-messages.md` に品質ガイドライン（3要素構成・禁止パターン・行動指示型語尾・最小文字数基準）を追加し、`ValidationServiceErrorMessageQualityTests` で自動検証（#1275）

**リファクタリング**
- `MainViewModel.SetState()` のデッドコードを除去。`backgroundColor` 引数 + 旧色リテラル (`#FFE0B2` / `#B3E5FC` / `#FFEBEE`) を case キーとする switch 式と、その代入先である未バインド 5 プロパティ (`StatusBackgroundColor` / `StatusBorderColor` / `StatusForegroundColor` / `StatusLabel` / `StatusIconDescription`) を削除。唯一の呼び出し元 `ResetState()` は `backgroundColor` 引数を省略しており switch 式は常にデフォルトケースに落ちる + XAML 側 Binding が一切存在しない（grep 0 件）状態で、Issue #1392 で UI 背景色を `LendingBackgroundBrush` 等のリソースキーへ集約した際に取り残された死骸だった。`SetInternalState()` 内の同 5 プロパティへのクリア処理も同時削除。回帰防止として `MainViewModelTests` に `MainViewModel_ShouldNotExposeDeadStatusStyleProperties` を追加（Reflection で削除済 5 プロパティの再導入を検出）。残った旧色リテラルは `CardBalanceDashboardItem.RowBackgroundColor` の `#FFEBEE`（残額警告行ハイライト用、別目的のため対象外）のみとなり、`MainViewModel` から旧色文字列は完全消滅（#1398）
- `LendingService.LendAsync` / `ReturnAsync` を責務ごとに internal ヘルパーメソッドへ分割し、可読性とテスト容易性を向上（`LendAsync` 121行 → 62行、`ReturnAsync` 182行 → 99行）。抽出したヘルパー: `ValidateLendPreconditionsAsync` / `ValidateReturnPreconditionsAsync` / `ResolveLentRecordAsync` / `ResolveInitialBalanceAsync` / `InsertLendLedgerAsync` / `FilterUsageSinceLent` / `ResolveReturnBalanceAsync` / `ApplyBalanceWarningAsync` / `PersistReturnAsync`。public API は一切変更せず、既存テストは全件 pass。抽出ヘルパー向けの `LendingServiceHelperTests`（23件）を追加（#1283）
- DB マイグレーションの冪等性（二重実行安全性）を担保。`MigrationHelpers.AddColumnIfNotExists` を新設し、非冪等だった 5 つの `ALTER TABLE ADD COLUMN` 型マイグレーション（#002/#003/#005/#006/#009）を冪等化。共有モードで複数 PC が初回起動時にマイグレーション競合した場合や、`schema_migrations` テーブル部分破損時の再適用エラーを防止。全 9 マイグレーションの二重実行テスト (`MigrationIdempotencyTests`, 9件) と `MigrationHelpers` 単体テスト (7件) を追加。`.claude/rules/migrations.md` に冪等性チェックリストを新設し、開発者ガイド §3.5 を自動検出ロジック (`DiscoverMigrations()`) に合わせて更新（#1285）
- `CsvImportService.Ledger.cs`（1031行 → 520行）と `Detail.cs`（1042行 → 761行）を責務分割。(1) Import/Preview 間で重複していた利用履歴行パース ~200 行を `LedgerCsvRowParser` に共通化、(2) 利用履歴詳細の 13 列パースを `LedgerDetailCsvRowParser` に抽出、(3) Detail の「履歴ID空欄→新規 Ledger 自動作成」ロジックを `NewLedgerFromSegmentsBuilder` に責務分離（Issue #906/#918/#1053 関連のロジック）、(4) 検証系 helper 4 メソッドを `CsvImportService.LedgerValidation.cs` partial に分離。public API は一切変更せず、既存 94 件のテストは全件 pass。抽出クラス向けの単体テスト 21 件を追加（`LedgerCsvRowParserTests` / `LedgerDetailCsvRowParserTests` / `NewLedgerFromSegmentsBuilderTests`）（#1284）

**ドキュメント**
- テスト設計書に「1.6 テストコードの読み方（非プログラマ向け）」節を追加。利用者（検収担当含む）が §2 以降のテストコード引用を参照する際に、テストファイル/メソッド命名の読み方、Arrange-Act-Assert 3 段構造、xUnit/FluentAssertions/Moq の主要記法を把握できるよう解説。実在テスト `DeleteOrClearFileTests` を引用し、C# やプログラミングの詳細な文法に詳しくなくても「何を確認しているテストか」が読み取れる構成とした。あわせて §0 の「対象読者」欄に利用者・検収担当を追加、§1.4 章マップ導入文に §1.6 への誘導注記を追加（#1385）
- 管理者マニュアル（1430 行・11 章構成）の冒頭に「こんなときはどこを見る？（作業別クイックガイド）」と「管理者の年間作業イメージ」の 2 セクションを新設。辞書型で網羅的に書かれた本マニュアルを、IT 操作に必ずしも詳しくない庶務担当者でも作業起点（「新しく職員が加わった」「アプリが起動しない」「残高不足で現金チャージした」等）で逆引きできるよう、6 カテゴリ（導入・職員・カード・データ・設定セキュリティ・トラブル）× 計 40 余りのタスクを既存章のアンカーにリンクする表として整備。あわせて発生頻度別（最初の導入時のみ／毎月／随時／年 1 回／日常）の早見表で「多くの作業は最初と随時に集中し、毎月の定例作業は帳票出力程度」という管理者の業務イメージを提示。§1.1 末尾にクイックガイドへの導線を追加、目次にも 2 項目と欠落していた §11・付録を追加。新規リンク 53 件は `#<番号>-<見出し>` の GFM 準拠スラッグで生成済みで、対応する見出しが本文に実在することを自動検証（#1387）
- 管理者マニュアル §2.1（複数 PC で利用する場合の事前準備）から、庶務担当者が読まなくてよい技術的な ACL 設定記述（SMB 共有権限／NTFS 権限／PowerShell `Grant-SmbShareAccess`・`icacls` コマンド・ドメイングループの付与手順）を削除し、脚注参照＋新規付録 C「アクセス権限の設定（IT 担当者向け）」へ移設。本文には「各 PC のログインアカウントでその共有フォルダが開けること（エクスプローラで開いて新規にファイルを保存できること）を確認」という作業起点の指示だけを残した。想定環境では組織内ファイルサーバの共有フォルダに読み取り／書き込み権限があらかじめ付与されているため、追加の権限設定は通常不要。新設フォルダや既定ドメイングループ外の利用など例外ケースは付録 C で案内。隣接する §2.3（2 台目以降のセットアップ）の前提注釈にも残っていた「ACL 設定」も同期的に「共有フォルダの準備」に平文化。目次に付録 C を追加。自動検証で本文側（付録 C 以外）から対象技術用語の残存がないことを確認（#1389）
- 自動バックアップで保持される世代数を「最新の数世代のみ」「古いバックアップは自動的に削除されます」という曖昧表現から、実装値である**最大 30 世代**へ具体化。ユーザーマニュアル §7.2「バックアップ先フォルダ」、管理者マニュアル §3.3「バックアップ設定」「バックアップの世代管理」、§6.1「自動バックアップ」の 3 箇所を更新し、(a) 保持件数（30）と削除タイミング（次回の自動バックアップ実行時）、(b) 設定画面から変更不可である旨、(c) それ以上の長期保管が必要な場合は手動バックアップで別の場所に退避する運用案内、(d) 1 日 1 回起動運用なら直近約 1 か月分が復元可能な目安、を明記。実装定数 `BackupService.MaxBackupGenerations` (= 30, `private const`) とマニュアル記載値の同期を `BackupServiceTests.MaxBackupGenerations_MatchesDocumentedValue` で固定化（Reflection で定数値を読み 30 と一致することを表明、定数変更時は同テストとマニュアルの両方を更新する必要があるため doc rot を構造的に防止）。既存の `_Over30Generations_DeletesOldBackups` / `_Exactly30Generations_DeletesOldest` テストは振る舞いを上限方向のみ検証していたため、本テストで「ちょうど 30」を双方向に固定（#1408）
- ユーザーマニュアル巻末に「付録 A 用語集」を新設。本文中で初出する専門用語（ピッすい／交通系ICカード／職員証／FeliCa／IDm／ICカードリーダー／PaSoRi／30秒ルール／貸出中／バス停名未入力／物品出納簿／共有フォルダモード）の 12 項目を Markdown テーブルで掲載し、後方から参照できる索引を提供。非 IT ユーザー（庶務担当者等）が初読時にわからない用語に出会うたびに本文を遡る手間を解消する。管理者マニュアル付録 B「用語集」と重複する `IDm` / `物品出納簿` の定義はユーザー文脈に合わせて補足を加えつつ意味は同期。目次にも 10 項目目として「付録 A 用語集」を追加（#1419）
- 管理者マニュアル付録 B「用語集」を 3 項目から 12 項目へ拡充。本文（特に §10 トラブルシューティング）で頻出する技術用語のうち、従来未掲載だった 9 項目（共有モード／繰越月／開始ページ／30秒ルール／バス利用／journal_mode=DELETE／busy_timeout／VACUUM／SQLITE_BUSY・SQLITE_LOCKED）を追加。共有フォルダモード・SQLite 関連用語には対応する解説節（§2.5 / §5.2 / §6.5 / §10.4）への内部リンクを併記し、用語集→詳細解説へ自己解決動線を構築。既存 3 項目（IDm / 論理削除 / 物品出納簿）はユーザーマニュアル付録 A の表現と意味を同期しつつ、`is_deleted` フラグ名や ledger / operation_log の物理削除方針など管理者文脈の補足を加味して再記述（#1420）

### v2.7.0 (2026-04-15)

**新機能**
- インストーラーが既存設定をデフォルト表示＆帳票出力先ページ追加（#1235）
- Value ObjectとModel層Rich化によるドメイン知識の型安全な表現（#1221）

**バグ修正**
- CsvImportServiceのXMLドキュメントコメント警告を修正（#1232）

**リファクタリング**
- MainViewModelSyncDisplayTests のリフレクション依存を ISystemClock に移行 (#1228)（#1228）
- IntToVisibilityConverter の重複クラスを統合 (#1227)（#1227）
- インターフェース階層分離でCQRS風の責務分離（#1224）
- partial classで責務分割（3,311行→5ファイル）（#1223）
- サービス抽出でDbContext直接依存を排除し責務を分離（#1222）

**ドキュメント**
- 技術スタック用語集を新設し初学者向けに解説を充実（#1231）

**テスト**
- テストカバレッジ補強(DashboardService等3サービス + 既存テスト追加)（#1226）


### v2.6.1 (2026-04-10)

**バグ修正**
- 年度途中繰越ledgerの受入欄が空欄となるように修正（#1219）


### v2.6.0 (2026-04-09)

**新機能**
- 年度途中導入でも累計受入・払出を適切に設定 (#1215)（#1215）

**バグ修正**
- 持ち替え時に新しい職員名で認識トーストを再表示 (#1211)（#1211）
- ICカード待ち中に別の職員証で上書きできるよう修正 (#1211)（#1211）
- GetConnection() を LeaseConnection() に移行 (#1209)（#1209）

**ドキュメント**
- 設計書全体を見直してわかりやすさを向上 (#1214)（#1214）


### v2.5.0 (2026-04-08)

**新機能**
- 既存値が★のみの場合は空欄として初期化 (#1205)（#1205）
- バス停名入力のUX改善（直近利用優先・類似検出）（#1143）

**バグ修正**
- 返却時の複数バス利用を1つのダイアログでまとめて入力 (#1203)（#1203）
- GetStartingPageNumberForMonth の L2 空シートスキップを明示化 (#1197)（#1197）
- RepairLentStatusConsistencyAsync の暗黙契約を解消 (#1196)（#1196）
- LogLedgerDeleteAsync の operatorIdm を nullable に統一 (#1188)（#1188）
- DbContextの並行アクセスをConnectionLeaseパターンで保護 (#1165)（#1165）
- FileLoggerProviderのログキュー溢れによるサイレントドロップを修正 (#1173)（#1173）
- ジャーナルモード設定失敗時のUI警告を追加 (#1172)（#1172）
- CardLockManagerのクリーンアップ競合によるObjectDisposedExceptionを修正 (#1171)（#1171）
- CleanupOldDataのトランザクション欠如によるテーブル間不整合を修正 (#1170)（#1170）
- ReadHistoryAsyncの例外飲み込みによりリーダーエラーと履歴ゼロ件が区別不能な問題を修正 (#1169)（#1169）
- ICカードリーダー再接続10回上限後の永続停止を低頻度リトライで回復可能に修正 (#1168)（#1168）
- 共有モードでのキャッシュ競合と古いデータによる二重貸出リスクを修正 (#1167)（#1167）
- リストア中のバックグラウンド接続再オープンによるDBファイル消失リスクを修正 (#1166)（#1166）
- 返却時に孤立した貸出中レコードが残る問題を修正（#1164）

**リファクタリング**
- ParseHistoryData を FelicaHistoryBlockDecoder への委譲に統一 (#1198)（#1198）
- テーブル行データ構築を純粋関数に抽出（#1194）
- ページネーション計算メソッドを internal static に変更（#1192）
- 履歴ブロック解析を純粋関数に抽出（#1185）

**ドキュメント**
- 設計書・マニュアルと実装の整合性を修正（#1183）

**テスト**
- BuildPrintTableRows の単体テストを追加（#1195）
- ページネーション計算の単体テストを追加（#1193）
- ページ番号計算メソッドの直接単体テストを追加（#1191）
- RepairLentStatusConsistencyAsync の境界条件を追加（#1190）
- 全メソッドのカバレッジ拡充（#1189）
- 全LogXxxAsyncメソッドのカバレッジ拡充（#1187）
- 履歴ブロック解析の単体テストを追加（#1186）
- 残高不足パターン検出・チャージ境界分割等のテストを追加（#1184）


### v2.4.2 (2026-04-06)

**新機能**
- メイン画面の履歴表示に繰越行を追加 (#1155)（#1155）

**バグ修正**
- システム管理画面のバックアップ一覧が表示されない問題を修正 (#1153)（#1153）
- リストア完了ダイアログ表示時にプログレスバーが残る問題を修正 (#1154)（#1154）
- バス停名入力画面でスキップ時に入力済みの内容が反映される問題を修正 (#1156)（#1156）


### v2.4.1 (2026-04-03)

**バグ修正**
- 同一日に複数職員が同じカードを利用すると履歴が統合されるバグ（#1148）

**ドキュメント**
- バージョンアップ時の注意点を管理者マニュアルに追記（#1150）
- 借りた人と返した人が違う場合のFAQを追加（#1151）


### v2.4.0 (2026-04-02)

**新機能**
- 履歴編集ワークフローの簡略化（#1144）
- 残高警告メッセージにしきい値を表示する（#1142）

**テスト**
- Infrastructure層のテストカバレッジ拡充 (#1135)（#1135）


### v2.3.0 (2026-04-01)

**新機能**
- 共有モードでデータの最終同期経過時間を表示 (#1131)（#1131）

**バグ修正**
- 返却時のトースト通知で残額が0と表示される場合がある (#1139)（#1139）
- operation_logも6年経過後に自動削除する (#1123)（#1123）
- 近年開業した4路線13駅を駅マスターデータに追加 (#1120)（#1120）
- 七隈線の櫛田神社前駅（233-033）を駅マスターデータに追加 (#1120)（#1120）

**ドキュメント**
- ユーザーマニュアルにカードタッチ図を追加（#1137）
- TODO.mdを実際の開発状況に合わせて更新 (#1128)（#1128）


### v2.2.0 (2026-03-31)

**新機能**
- 共有モードのテストカバレッジ拡充とドキュメント整備 (#1111)（#1111）
- 共有モード時のUX改善（再接続・エラー表示・更新時刻）（#1116）

**バグ修正**
- レビュー指摘の修正 - テストアサーション・ドキュメント精度改善 (#1111)（#1111）
- DeleteAsync/SetRefundedAsync の失敗原因をユーザーに通知（#1115）
- 共有モードでのリストア操作の安全性を確保 (#1108)（#1108）
- 共有モードのSQLite耐障害性を改善 (#1107)（#1107）
- カード番号採番の競合状態を防止するUNIQUE制約を追加 (#1106)（#1106）
- 日本語を含む共有フォルダパスで起動エラーになる問題を修正（#1104）


### v2.1.3 (2026-03-30)

**ドキュメント**
- 管理者マニュアルの共有フォルダ設定セクションを再構成 (#1100)（#1100）
- 管理者マニュアルからPaSoRiドライバインストール手順を削除 (#1098)（#1098）
- 管理者マニュアルの共有フォルダパス記載例を修正 (#1096)（#1096）


### v2.1.2 (2026-03-29)

**バグ修正**
- 共有モード時にDB権限設定をスキップ（#1093）
- 共有モード時にダッシュボードと貸出中カードを定期リフレッシュ


### v2.1.1 (2026-03-28)

**バグ修正**
- インストーラーDB保存先ページの文字切れを修正（#1091）


### v2.1.0 (2026-03-28)

**バグ修正**
- DB保存先設定の書き込み権限エラーを修正・インストーラーに保存先選択を追加（#1089）

**ドキュメント**
- かんたん導入ガイドを追加（#1088）


### v2.0.0 (2026-03-28)

**新機能**
- 共有フォルダDB方式による複数PC同時利用機能を追加（#1086）


### v1.27.4 (2026-03-26)

**バグ修正**
- インストーラーでProgramDataディレクトリにUsers権限を設定（#1084）


### v1.27.3 (2026-03-26)

**バグ修正**
- ProgramData配下のディレクトリ権限設定とログローテーションの耐障害性を改善（#1082）
- 部署設定ファイルの削除失敗時に内容クリアで代替する (#1080)（#1080）
- DBファイルのアクセス権限をUsersグループに拡大（#1079）
- テストデータの残高整合性を保証する (#1075)（#1075）
- DEBUGビルド時のテストデータ生成を単一トランザクションで高速化 (#1074)（#1074）


### v1.27.2 (2026-03-25)

**バグ修正**
- 月計・累計行の金額列に「縮小して全体を表示する」を設定 (#1071)（#1071）


### v1.27.1 (2026-03-24)

**バグ修正**
- データインポート後のカード一覧の最終利用日が正しく表示されない問題を修正 (#1068)（#1068）
- WSL経由のgh pr createでPR本文がbashに解釈される問題を修正（#1067）


### v1.27.0 (2026-03-24)

**新機能**
- 詳細レベルの残高チェーン整合性チェックを追加 (#1059)（#1059）
- 残高不整合警告クリック時に該当行をハイライト表示 (#1052)（#1052）

**バグ修正**
- データエクスポート/インポート画面のプレビュー領域スクロールを修正 (#1063)（#1063）
- データインポート/エクスポート時のプログレスバー表示タイミングを修正 (#1062)（#1062）
- インポート後に全カード対象の残高整合性チェックを実行するよう修正 (#1058)（#1058）
- 履歴詳細インポート時のチャージ/ポイント還元分割と受入金額を修正 (#1053)（#1053）

**リファクタリング**
- DailySegmentをLendingHistoryAnalyzerに移動し循環参照を解消 (#1047)（#1047）

**ドキュメント**
- サードパーティライブラリのライセンス一覧を作成 (#1054)（#1054）


### v1.26.0 (2026-03-23)

**新機能**
- メンテナンス性・テスト・UXの品質向上（10項目）

**バグ修正**
- 徹底レビューで発見された問題の修正
- コードレビュー指摘事項の修正
- 帳票作成時のプログレスバーが表示されない問題を修正
- プログレスバーの視認性改善とUIスレッド問題修正

**リファクタリング**
- StationMasterServiceの静的シングルトンを完全排除（#1046）
- MainViewModelテストのTask.Delay(100)を決定論的待機に置換（#1045）


### v1.25.1 (2026-03-20)

**バグ修正**
- Ledger.Summaryがnullの場合に発生する潜在的NullReferenceExceptionを修正（#1042）
  - LedgerOrderHelper、MainViewModel、IncompleteBusStopViewModel、PrintService、IncompleteBusStopDialogの5箇所

**テスト**
- テストカバレッジギャップを検出するエッジケーステスト101件を追加（#1041）

### v1.25.0 (2026-03-19)

**新機能**
- リリース自動化スクリプトの追加（#1038）


### v1.24.0 (2026-03-19)

**新機能**
- 操作ログExcelエクスポートで変更部分をハイライト表示（#1027）
- フォルダパスの直接入力を可能に（#1026）

**バグ修正**
- バックアップファイルの更新日時をバックアップ実行時の現在時刻に設定（#1028）
- データエクスポート/インポート画面の二重スクロールバーを解消（#1021）
- 帳票出力画面の出力先フォルダを永続化（#1029）

**リファクタリング**
- 表示フォーマット・ソート・年度計算のユーティリティ共通化（#1024）
- 帳票行変換パイプラインとRouteDisplayロジックの共通化（#1023）
- SummaryGeneratorの鉄道・バス共通パイプラインを抽出（#1022）

### v1.23.0 (2026-03-18)

**新機能**
- バックアップパスのUNCパス対応（#1018）
  - `\\server\share\backup` のようなネットワーク共有パスをバックアップ先に指定可能に

**バグ修正**
- バス停名入力ダイアログを閉じた後に履歴画面が即時反映されない問題を修正（#1010）
- バス利用の摘要順序を時系列順に修正（#1012）
- 操作ログ・統合履歴のタイムスタンプをローカル時刻で保存するよう修正（#1014）
  - MigrationRunnerのoperation_logタイムスタンプがUTCで記録されていた問題を修正
  - 履歴統合時のタイムスタンプがUTCで記録されていた問題を修正

**テスト・ドキュメント**
- テスト設計書の修正（#1017）
  - TC004の期待結果を残高チェーンによる時系列判定に修正
  - 不要テスト3件（TC034/TC035/TC037）を削除
  - 千早/西鉄千早の乗り継ぎテスト（TC049）を追加
  - 暗黙還元・明示還元の用語定義をFeliCa生データとの違いを含めて追加
  - UT-005/UT-018の入力データ・金額根拠を明記

### v1.22.3 (2026-03-17)

**バグ修正**
- CSVエクスポート時の同一日内の並び順を残高チェーン順に修正（#1004）
  - ポイント還元と利用が同日にある場合、ID順ではなく残高チェーン順で出力
  - LedgerDetailChronologicalSorterのポイント還元対応を修正
  - LedgerConsistencyCheckerも残高チェーン順で整合性チェックするよう修正
  - CalculateGroupFinancials・GetGroupDateのカスタムソートを残高チェーン順に統一

**ドキュメント**
- 機能設計書・クラス設計書を残高チェーン順ソートの実装に合わせて更新（#1007）
- テスト設計書を実コード（1,796件）に基づいて大幅に詳細化（#1008）

### v1.22.2 (2026-03-16)

**バグ修正**
- 通常チャージが残高不足パターンとして誤検出される問題を修正（#1001）
  - 利用後残高の閾値チェックを追加して誤検出をさらに防止

**コード整理**
- 使われていないコードを削除（#998）

**テスト**
- WPFコンバーターとDTO表示プロパティの単体テストを追加（#1000）

**ドキュメント**
- 実装済みだが未ドキュメントの機能をドキュメントに反映（#999）

### v1.22.1 (2026-03-16)

**バグ修正**
- バス停名入力画面で複数履歴がある場合にTABキーで次の入力欄にフォーカスが移動しない問題を修正（#991）
- 利用履歴画面で列幅を広げても横スクロールバーが表示されない問題を修正（#993）

**コード整理**
- 使われていない旧HistoryDialog（別ウィンドウ方式）を削除（#995）

### v1.22.0 (2026-03-16)

**新機能**
- バスの往復利用を検出して「バス（A～B 往復）」と表示（#985）
- バスの乗り継ぎ（連続区間）を統合して「バス（A～C）」と表示（#985）
- 組織固有のハードコード値（摘要テキスト・往復/乗継ルール・エリア優先順位・帳票レイアウト等）をappsettings.jsonで設定可能に（#974）

**バグ修正**
- バス停名を編集後にレコードを統合すると修正前の摘要に戻る問題を修正（#983）

**ドキュメント**
- 管理者マニュアルにセクション7.4「組織固有設定（OrganizationOptions）」を追加
- クラス設計書にOrganizationOptionsクラス図を追加

### v1.21.0 (2026-03-13)

**新機能**
- 返却時のバス停名入力ダイアログを自動スキップする設定を追加（#975）
- 物品出納簿の備考欄フォントサイズを文字列長に応じて自動調整（#980）

**バグ修正**
- 残高不足パターン（不足分を現金でチャージした場合）の処理を修正（#978）
  - チャージ詳細をDBに保存して重複チェック漏れを防止
  - 端数チャージ（10円単位）にも対応するよう検出条件を緩和
  - 会計処理を修正（払出額=運賃-チャージ額、不足額=チャージ額、残額=利用後の実残高）

### v1.20.0 (2026-03-12)

**新機能**
- インポートプレビュー画面で追加・スキップ行もクリックで詳細データを表示（#969）
- インストーラーに「はじめに」マニュアルを追加（#967）

**改善**
- 音声（男性・女性）をより元気よい声に再生成（#971）
  - 話速・ピッチ・抑揚パラメータを調整
  - 音声生成スクリプトにパラメータ指定機能を追加

### v1.19.2 (2026-03-11)

**バグ修正**
- 利用履歴詳細CSVエクスポート時に一部の順序が逆になる問題を残高チェーンベースのソートで修正（#964）

### v1.19.1 (2026-03-11)

**バグ修正**
- データエクスポート/インポート画面の初期サイズを調整し、スクロールなしで設定が表示されるよう修正（#961）

### v1.19.0 (2026-03-11)

**新機能**
- 履歴画面の表示期間テキストをクリックで月選択ポップアップを表示（#945）
- 物品出納簿の摘要欄フォントサイズを文字列長に応じて自動調整（#946）
- 物品出納簿の金額列（受入・払出・残額）のフォントサイズを16ptに変更（#947）
- カード登録画面で無効欄クリック時にモード自動切替（#944）
- データエクスポート/インポート画面のプレビュー結果とエラー詳細の高さをリサイズ可能に（#958）

**バグ修正**
- 暗黙のポイント還元が鉄道利用とまとめられる問題を修正（#942）
- データエクスポート/インポート画面で閉じるボタンが画面外にはみ出す問題を修正（#948）

**ドキュメント**
- カード登録方法選択画面のヒント欄に管理者マニュアルへの案内を追記（#943）
- 月途中からの導入時の履歴について詳細な説明を追加（#950）

### v1.18.2 (2026-03-10)

**機能改善**
- 利用履歴インポートのプレビューでカードIDmに加えてカード名（種別＋管理番号）を表示（#937）
- 利用履歴詳細インポートのプレビューでカード名を表示（#937）
- 利用履歴詳細インポートのプレビューで「追加」行の詳細内容を確認可能に（#938）

**バグ修正**
- テストコードのnullable警告（CS8625等）を修正（#935）
- UIテストをdotnet testのソリューション全体実行から除外（#935）
- UIテストのタイムアウトをdotnet run + Application.Attach方式で修正（#935）

### v1.18.1 (2026-03-09)

**メンテナンス**
- 不要・重複テストを削除してテストスイートを整理（#928）
- CIワークフローでUIテストプロジェクトを明示的に除外（#927）

### v1.18.0 (2026-03-08)

**新機能**
- デバッグ用データビューアにFeliCaブロック番号(Seq)列を追加（#922）
- デバッグ用データビューアのDBデータタブにSQLiteのrowidを表示（#924）

**バグ修正**
- 利用履歴詳細インポート時に異なる日付のデータが1つのLedgerに統合されていた問題を修正（#918）
- 利用履歴詳細インポート時に親Ledgerの金額が再計算されない問題を修正（#918）
- 履歴を分割後に再統合すると摘要の経路順序がおかしくなる問題を修正（#920）

**ドキュメント**
- 管理者マニュアル：交通系ICカード新規登録時の最古履歴の利用金額に関する注意書きを追記（#916）

### v1.17.0 (2026-03-06)

**新機能**
- 利用履歴詳細インポート時に利用履歴IDが空欄の場合、カードIDmからLedgerレコードを自動作成する機能を追加（#906）

**バグ修正**
- 利用履歴画面の受入・払出列から不要な太字を除去（#901）
- データインポート時に他プロセスがファイルを使用中でも読み込み可能に（#902）
- データインポート時「既存データはスキップする」チェックが無視されていた問題を修正（#903）
- 利用履歴詳細エクスポート時のSequenceNumber順序を修正し残高整合性を確保（#904）
- 利用履歴詳細インポートプレビューの列ヘッダーをデータ種別に応じて動的表示（#905）
- 利用履歴インポート時にCSV最初の行もDB上の直前残高と整合性チェックするよう改善（#907）

### v1.16.7 (2026-03-06)

**ドキュメント**
- マニュアルのスクリーンショットにサイズ指定を追加（#897）
- マニュアルの章区切りで自動改ページするようreference.docxを更新（#897）

### v1.16.6 (2026-03-06)

**バグ修正**
- 利用履歴詳細の挿入順をFeliCa互換に修正し表示順逆転を解消（#888）
- 返却時に履歴表示を即座に更新するよう修正（#889）
- 新規カード登録時のデフォルトカード種別をnimocaに修正（#890）

**リファクタリング**
- 残高チェーンソートで表示順をrowid非依存にし統合テストを追加（#892）

**ドキュメント**
- 設計書と実装の整合性を修正（#891）

### v1.16.5 (2026-03-05)

**バグ修正**
- 同一日にチャージが利用の間に挟まる場合の残高チェーン不整合を修正（#886）

### v1.16.4 (2026-03-04)

**バグ修正**
- 履歴分割後のrowid再採番による表示順逆転を修正（#880）
- SummaryGeneratorのSequenceNumberソート順をFeliCa互換に修正（#880）

### v1.16.3 (2026-03-04)

**バグ修正**
- 利用履歴詳細画面の表示順を時系列順（古い順）に修正（#876）
- 天神駅と西鉄福岡(天神)駅を同一駅として認識するよう修正（#878）
- 履歴分割時の残高計算でDBの時系列順と一致するソート順を使用するよう修正（#880）

**ドキュメント**
- 庁内掲示板プロモーション記事を追加
- .gitignoreにPythonの__pycache__/を追加

### v1.16.2 (2026-03-03)

**バグ修正**
- リリースワークフローでUIテストを除外し、exeパス解決を構成対応に修正（#873）

**ドキュメント**
- 庁内プロモーション用チラシ（A4縦PDF）を追加（#871）
- プロモーション動画生成スクリプトと動画を追加・改善（#863）
- CLAUDE.md の冗長・不正確な記述を整理（#870）

### v1.16.1 (2026-03-02)

**ドキュメント**
- マニュアルにスクリーンショットを追加（#849）

### v1.16.0 (2026-03-02)

**新機能**
- システム表示名に愛称「ピッすい」を追加（#864）

**バグ修正**
- 物品出納簿の最初のシートで摘要・氏名・備考欄のフォントサイズが小さい問題を修正（#858）
- 最初のワークシートの表示倍率を100%に統一（#858）
- 会社名を「Your Organization」から正しい値に修正（#857）
- StaffAuthDialogのタイムアウトとXAML初期表示を設定値に合わせる（#854）
- 愛称追加に伴いウィンドウのデフォルト幅・最小幅を拡大

**リファクタリング**
- ハードコードされた設定値をappsettings.jsonに外部化（#854）
- INavigationServiceを導入してダイアログ管理を統一（#853）
- 静的フラグをWeakReferenceMessengerに置換（#852）

**ドキュメント**
- テスト設計書の誤り修正と記載漏れ補完（#850）
- スクリーンショット取得スクリプトにIssue #849の6画面を追加

### v1.15.1 (2026-02-25)

**バグ修正**
- 同一日の履歴を複数回返却時に統合するよう修正（#837）
- 4月の物品出納簿で累計行を省略し月計行に残額を表示（#813）
- プレビューの月計・累計行で受入・払出が0の場合も表示（#842）

**リファクタリング**
- 物品出納簿のデータ準備ロジックをReportDataBuilderに統合（#841）

**テスト**
- 未テストのViewModel・例外・ロガーに単体テスト215件を追加（#845, #846）

**ドキュメント**
- クラス設計書・機能設計書・シーケンス図・テスト設計書を実装に合わせて更新（#847）

**メンテナンス**
- StationCode.csv統合・重複除去・駅名更新（#838）

### v1.15.0 (2026-02-24)

**新機能**
- 帳票作成画面の「先月」「今月」ボタンを選択中はハイライト表示する（#825）
- VOICEVOXで生成した貸出・返却の音声WAVファイルに置き換え（#830）
  - 女性音声: 四国めたん、男性音声: 玄野武宏
  - 設定ダイアログに音声クレジット表記を追加

**バグ修正**
- DataGridがフォントサイズ設定の影響を受けない問題を修正（#823）
- 職員証タッチ時に音声モードでもビープ音を再生するよう修正（#832）
- テストプロジェクトのnullable参照型警告を全て解消（#834）

**テスト**
- UIテストのメインウィンドウ名判定をStartWithに変更しUIA Name不安定性に対応（#831）

**ドキュメント**
- READMEの更新履歴をCHANGELOG.mdに分離（#826）

### v1.14.0 (2026-02-21)

**バグ修正**
- 新規登録時の繰越額が履歴逆算値で上書きされるバグを修正（#819）

**新機能**
- FlaUIによるWPF UIテスト自動化基盤を追加（起動テスト6件・ダイアログ遷移テスト3件）
- CIでUIテストを自動除外する設定を追加

### v1.13.2 (2026-02-20)

**改善**
- インストーラービルドスクリプト（.bat）にDebugDataViewerのクリーンビルドを追加（#816）

### v1.13.1 (2026-02-20)

**バグ修正**
- チャージ・新規購入・ポイント還元・払戻しの氏名欄を空欄にする（#807）
- 物品出納簿の頁番号が翌月以降で正しく連続しない問題を修正（#809）
- エクスポート後にインポート側のデータ種別が見えなくなる問題を修正（#805）
- 帳票作成ボタン押下時に前回のStatusMessageをクリアする（#812）
- 残高不整合の自動修正を削除し、警告表示のみに変更（#785）

### v1.13.0 (2026-02-19)

**新機能**
- 操作ログエクスポートをCSVからExcel形式に変更（#786）

**バグ修正**
- 同一日のチャージ・利用の表示順を残高チェーンで正しく決定（#784）
- メイン画面・履歴画面のDataGridにも残高チェーン再構築を適用（#784）
- 起動時にic_card.is_lentとledger.is_lent_recordの整合性を修復（#790）
- 操作ログの表示順を古い順に変更し、初期表示で最新ログを表示（#787）
- DEBUGテストデータの残高チェーン不整合を修正（#803）

**ドキュメント**
- 管理者マニュアルの職員証再登録手順を実装に合わせて修正（#783）
- 管理者マニュアルのカード登録設定テーブルに「繰越額」を追記（#782）
- 管理者マニュアルのインポート記載を実装と一致させる（#781）
- ユーザー・管理者マニュアルの設定画面に「部署設定」を追記（#780）
- 管理者マニュアルに「利用履歴詳細」のエクスポート/インポートを追記（#779）
- ユーザーマニュアルのCSVインポート記載に管理者マニュアルへの参照を追加（#788）

**その他**
- リリースビルド時にマニュアルへバージョンを自動注入する仕組みを追加

### v1.12.0 (2026-02-18)

**新機能**
- データ入出力に利用履歴詳細のエクスポート/インポートを追加（#751）
- 削除ボタンを履歴一覧から編集ダイアログへ移動（#750）
- 部署選択をアプリ初回起動時からインストーラーに移動（#742）

**バグ修正**
- チェック済み行の背景色を水色でハイライト表示（#772）
- AlternatingRowBackground/DataGrid選択ハイライトの優先度問題を修正（#772）
- 利用履歴詳細インポートで変更なしデータをスキップ表示に修正（#751）
- ハードコードされた色コードをDynamicResourceに統一（#721）
- カード登録時の繰越額をユーザーが手入力できるように変更（#756）
- 物品出納簿のドキュメントプロパティ日時を出力時に更新（#752）
- インポート時の残高整合性チェックでスキップ行が除外される問題を修正（#754）
- インポート後に履歴一覧・ダッシュボードを即座に更新（#744）
- 履歴一覧の受入金額を緑色から黒色に変更（#749）
- 月次繰越行の受入金額欄を空欄に修正（#753）

**アクセシビリティ**
- Popupコントロール・DatePickerのアクセシビリティを改善（#720）
- ハードコードされたフォントサイズをDynamicResourceに統一（#719）

**ドキュメント修正**
- ユーザーマニュアルに職員証認識後の画面スクリーンショットを追加（#747）
- 物品出納簿のExcel出力スクリーンショットをユーザーマニュアルに追加（#746）
- スクリーンショットREADMEを現状の18枚構成に更新（#776）
- ユーザーマニュアルに「別々の履歴に分割」と「摘要のみ更新」の説明を追記（#755）
- 管理者マニュアルのCSVインポート手順から実画面にない記述を修正（#757）

**その他**
- 企業会計部局テンプレートから記載例ワークシートを削除（#745）

### v1.11.1 (2026-02-17)

**バグ修正**
- 起動時の CS4014 警告メッセージを修正（#736）

**コード品質改善**
- 使われていないメソッド12個を削除（#733）

### v1.11.0 (2026-02-17)

**新機能**
- 市長事務部局／企業会計部局の選択機能を追加（#659）
  - チャージ時の摘要を部署種別に応じて自動切替（役務費／旅費）
  - 帳票テンプレートを部署種別に応じて自動切替
  - 初回起動時に部署選択ダイアログを表示
  - 設定画面から部署種別を変更可能

**バグ修正**
- Releaseビルドで起動エラー時のスタックトレースを非表示にする（#716）
- Su-001のライフサイクル整合性とデポジット除外を修正（#731）

**パフォーマンス改善**
- .Result呼び出しをasync/awaitに置換してデッドロックリスクを解消（#717）

**アクセシビリティ**
- VirtualCardDialogにAutomationPropertiesを追加（#718）

**ドキュメント修正**
- スクリーンショット16枚を追加・更新しマニュアルの画像参照を修正（#729）
- 画面設計書のダイアログ名修正と不足ダイアログ追加（#714）
- システム概要設計書のOS要件・CPU要件を実装に合わせて修正（#713）

### v1.10.0 (2026-02-16)

**新機能**
- 職員一覧・カード一覧で新規登録・更新・復元後に該当行をハイライト表示（#707）
- バス停名入力後にハイライト表示してから一覧削除するよう改善（#709）

### v1.9.0 (2026-02-15)

**新機能**
- 履歴画面に行の挿入・削除・修正機能を追加（#635）
- システム警告をクリック可能にし対象カード履歴へ遷移（#672）
- デバッグモードで職員証認証ダイアログに仮想タッチボタンを追加（#688）

**バグ修正**
- 貸出時にカード残高が0になる問題を修正（#656）
- バス停名入力後・履歴変更後に警告メッセージの件数を更新する（#660）
- 設定の残額警告閾値変更後にシステム警告メッセージを更新する（#661）
- カード一覧のデフォルトソートを「カード名順」に変更（#662）
- デフォルトのウィンドウサイズを拡大しボタンが一行に収まるよう修正（#663）
- 繰越登録時の履歴不完全メッセージに実際の最古月を表示（#664）
- カード管理画面の新規登録時に履歴を事前読み取りする（#665）
- 履歴画面の払出金額の文字色を赤から黒に変更（#671）
- XMLドキュメントの不足paramタグを追加しCS1573警告を解消（#701）
- バス停名未入力一覧に利用日フィルタを追加し、フィルタ条件を維持するよう改善（#703）
- 「変更」と「修正」ボタンを統合し一本化（#705）

**ドキュメント修正**
- ユーザーマニュアルにヘルプボタン（F7）の説明を追加（#668）
- 管理者マニュアルの「管理」メニュー参照を修正し、カード登録手順を補完（#669）
- 管理者マニュアルのシステム設定セクションをappsettings.jsonの実際の内容に合わせて修正（#670）
- ユーザーマニュアルに乗車履歴の自動統合の説明を追加（#691）
- マニュアルのページ方向をA4横に変更（#692）
- 管理者マニュアルに手動バックアップは通常不要である旨を追記（#693）
- マニュアル「はじめに」を追加（#694）
- FAQ Q5に20件超の履歴追加方法を追記（#644）

### v1.8.0 (2026-02-13)

**新機能**
- 開発ビルド用の仮想ICカードタッチ機能を追加（#640）
- 新規購入カードに購入日を指定可能にする（#658）

**バグ修正**
- 新規購入カードの登録日が月初めになるバグを修正（#657）

**ドキュメント修正**
- ユーザーマニュアルにシステム警告エリアの説明を追加（#666）

### v1.7.0 (2026-02-12)

**新機能**
- メインウィンドウにヘルプボタンを追加し、マニュアルを直接開けるようにする（#641）
- 履歴詳細画面の分割で「摘要のみ更新」か「別々の履歴に分割」を選択可能にする（#634）
- 利用履歴の変更で利用者を空欄にできるようにする（#636）
- マニュアルのPDF変換スクリプトを追加（#642）
- インストーラー作成時にdocx/PDFマニュアルを自動生成（#643）

**バグ修正**
- 認証ダイアログのサイズが文字サイズに追従しない問題を修正（#631）
- 履歴詳細画面の分割操作が摘要に反映されない問題を修正（#633）
- 物品出納簿の上書き時に備考欄が消える問題を修正（#637）
- データインポートのプレビューが表示されない問題を修正（#638）
- 履歴インポートで金額変更が検出されずスキップされる問題を修正（#639）

### v1.6.0 (2026-02-10)

**新機能**
- カード登録時に当月履歴を自動読み取りしてledgerに登録する機能を追加（#596）
- カード返却時に今月の履歴完全性をチェックし、不足の可能性がある場合に警告を表示（#596）
- CSVインポート実行後に完了ダイアログを表示（#598）
- CSVインポートプレビューを表形式で直接表示するよう改善（#597）

**バグ修正**
- カード種別選択ダイアログのサイズが文字サイズに追従しない問題を修正（#628）
- プレビュー表示とExcelファイルの内容が一致しない問題を修正（#603）
- 履歴エクスポート時に同一カードの履歴をまとめて出力するよう修正（#592）
- 帳票上書き時にデータ行の太字書式をリセットするよう修正（#591）
- 設定変更後にカード一覧の残額警告表示が即時更新されない問題を修正（#604）
- 認証ダイアログのメッセージに操作理由と読点を追加（#602）
- 「ICカード」表記を「交通系ICカード」に統一（#594）
- 繰越レコードの日付を繰越月の翌月1日に修正（#599）
- バス停名入力画面でバス停名の入力ができないバグを修正（#593）
- 新規購入が同一日のチャージより後に表示されるバグを修正（#590）
- マニュアルの表が印刷時に罫線が消えるバグを修正（#600）

**ドキュメント修正**
- DB設計書・画面設計書・クラス設計書を実装に合わせて更新（#570-#575）
- ユーザーマニュアル・管理者マニュアルの記載を修正（#568, #601）
- READMEのpublish出力パス・テスト数等を修正（#559, #561）

### v1.5.1 (2026-02-09)

**機能改善**
- カード一覧部のタイトルを「カード残高」から「カード一覧」に変更（#552）

**ドキュメント修正**
- 管理者マニュアルに利用開始時の交通系ICカード登録手順を追加（#553）
- 管理者マニュアルにデータインポートのセクションを追加（#554）
- README.mdのプラットフォームアーキテクチャ記載を修正（#558）
- ユーザーマニュアルの存在しないメニュー参照を修正（#562）
- ユーザーマニュアルの残額警告デフォルト値と範囲を修正（#563）
- ユーザーマニュアルのカード管理ボタンラベルを修正（#564）
- ユーザーマニュアルの音声設定を4モード対応に拡充（#565）
- ユーザーマニュアルにトースト通知位置・バックアップ設定を追記（#566）
- 管理者マニュアルのバックアップファイル名を修正（#567）
- DB設計書のDBファイル保存場所を修正（#569）

### v1.5.0 (2026-02-06)

**新機能**
- 履歴一覧から複数エントリの統合機能を追加（チェックボックスで選択→統合ボタン）
- 統合の取り消し機能を追加（DB永続化された履歴から任意の統合を選んで元に戻せる）
- 統合履歴選択ダイアログを追加（一覧から取り消したい統合を選択）
- 履歴統合のユニットテスト31件を追加（LedgerMergeServiceTests）
- 払戻済カード状態の追加
- 操作ログに変更内容の詳細表示を追加
- 履歴編集画面で利用者の変更を可能に

**改善**
- 統合対象の選択をCtrl+クリックからチェックボックス方式に変更（初心者にもわかりやすく）
- 統合完了・取消完了の通知をトースト通知からMessageBoxに変更（操作中の画面で確認しやすく）
- 元に戻せる統合履歴がない場合は「統合を元に戻す」ボタンをグレーアウト
- カード一覧の幅を広げて金額・貸出者を表示
- カード一覧とシステム警告の高さ比率を7:3に調整
- ヘッダーボタンをWrapPanelで折り返し対応に変更

**バグ修正**
- チャージと利用の履歴を統合できないようバリデーションを追加
- SummaryGenerator: グループID付き往復検出の修正（「A～A」→「A～B 往復」）
- 統合→分割後の履歴ソート順を修正（balance DESCをタイブレーカーに追加）
- System.Text.Json 4.7でDictionary&lt;int,int&gt;がシリアライズできない問題を修正
- 統合説明テキストにシャローコピー由来のバグがあった問題を修正
- 物品出納簿の上書き時にフッターが消える問題を修正
- 貸出処理時にカード残高を読み取るよう修正
- 西鉄バス利用時のバス判定を修正
- カード登録モード選択ダイアログのUI修正
- サイドバー幅をフォントサイズに応じて動的調整

### v1.4.0 (2026-02-06)

**新機能**
- 乗り継ぎ駅のエイリアスマッピング機能を追加（博多↔筑紫口等の同一乗り継ぎ駅を認識）
- 利用履歴CSVインポート時にカードIDm空欄を許可
- データエクスポート完了時に保存完了メッセージを表示
- デバッグ用データビューアにDBファイル選択機能を追加
- 年度途中に本アプリを導入した場合の新規登録に対応
- 物品出納簿に12行ごとの改ページ機能を追加（ヘッダー・備考欄も各ページにコピー）

**改善**
- 起動時間を短縮
- 乗車履歴統合・分割UIを大幅改善（分割線クリック方式、アルファベットラベル追加）

**バグ修正**
- 空港グループを乗り継ぎ同一視から除外
- 物品出納簿の金額セルの表示形式を会計形式に修正
- バックアップからのリストアが失敗する問題を修正
- アンインストーラーのファイル名をわかりやすく変更
- 新規購入より前の月の物品出納簿を作成しないよう修正

### v1.3.0 (2026-02-05)

**新機能**
- 物品出納簿を年度ファイル・月別シート構成に変更（年度ごとに1ファイル、月ごとにシート）
- 乗車履歴の統合・分割機能を追加（複数の乗車履歴を1つの行程にまとめる）

**改善**
- 利用履歴詳細画面の金額表示を正の値に変更（符号なし表示）
- 同一日ではチャージを利用より先に表示
- 月次繰越の受入欄を空欄に変更
- 新規購入カードの繰越行を非表示にする
- インストーラービルド時にマニュアルを自動変換

**バグ修正**
- 貸出タッチ忘れ時の履歴が記録されない問題を修正
- カード登録後にダッシュボードを更新するよう修正
- カード種別選択前に残高を読み取るように修正

### v1.2.0 (2026-02-04)

**新機能**
- 重要な操作（履歴編集・職員削除・カード削除）の前に職員証タッチによる認証を必須化
- ステータスバーにアプリケーションのバージョン番号を表示
- インストーラーにWindows起動時の常駐オプションを追加

**バグ修正**
- 履歴詳細の表示順を正しい時系列順に修正（チャージを含む場合も対応）

### v1.1.0 (2026-02-03)

**新機能**
- 残高整合性チェック機能を追加（交通系ICカードの実残高と記録の不一致を検出）
- 開発者向けスクリーンショット撮影ツールを追加

**改善**
- ユーザーマニュアル・管理者マニュアルの内容を充実

### v1.0.8 (2026-02-01)

**ドキュメント**
- 配布手順書をインストーラー対応に更新
- 機能設計書の誤記を修正

### v1.0.7 (2026-01-31)

**新機能**
- アンインストール時にユーザーデータ・バックアップ・ログの削除を選択可能に
- インストーラーのバージョンをアプリ本体と自動同期

### v1.0.6 (2026-01-31)

**改善**
- ビルド時の警告を解消

### v1.0.5 (2026-01-31)

**改善**
- デフォルトの文字サイズですべてのボタンが画面内に収まるように調整

### v1.0.4 (2026-01-31)

**バグ修正**
- マイグレーションテストの不具合を修正

### v1.0.3 (2026-01-31)

**改善**
- 履歴の表示順を物品出納簿に合わせて古い順（昇順）に変更

### v1.0.2 (2026-01-31)

**新機能**
- 残高不足時の現金補填に対応（備考に不足額を自動記録）

### v1.0.1 (2026-01-30)

**新機能**
- ポイント還元によるチャージに対応
- 交通系ICカードの払い戻し処理に対応

### v1.0.0 (2025-12)

- 初版リリース
