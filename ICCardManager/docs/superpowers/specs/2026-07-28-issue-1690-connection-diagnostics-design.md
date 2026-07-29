# アプリ内接続診断（Issue #1690）設計書

- Issue: #1690【Phase2】アプリ内接続診断（DB到達性・PaSoRi・書込権限の自己診断と結果コピー）
- 作成日: 2026-07-28

## 背景と目的

インターネット非接続の官公庁環境では IT 担当が物理的に遠く、障害時の切り分けが電話越しになりコストが高い。
現状のトラブル対応はマニュアル §10 の対応表のみで、庶務担当が自力で原因を切り分ける手段がない。

本機能はシステム管理画面（F6）から起動できる「接続診断」を追加し、
アプリが依存する外部リソース（DB・ICカードリーダー・バックアップ保存先・ディスク）の状態を
一括で確認して「何が・なぜ・どうすれば」の 3 要素で提示する。
診断結果はワンクリックでクリップボードへコピーでき、そのまま IT 担当へ共有できる。

## 非目標

- 自動修復は行わない。診断は読み取り専用（書込可否の確認で一時ファイルを作成・削除する以外に副作用を持たない）。
- 定期的な自動診断は行わない。既存の 15 秒ヘルスチェック（`SharedModeMonitor`）と役割を分ける。
  本機能は「ユーザーが障害を疑ったときに手動で叩く」もの。
- ネットワーク経路の詳細調査（ping / tracert 相当）は行わない。オフライン環境の運用者に解釈できないため。

## アーキテクチャ

```
Views/Dialogs/ConnectionDiagnosticsDialog.xaml
        │ DataContext
ViewModels/ConnectionDiagnosticsViewModel
        │ 診断実行            │ コピー
        ▼                     ▼
Services/IConnectionDiagnosticsService   Services/IClipboardService
        │                                (WpfClipboardService)
        ▼                                        ▲
Services/ConnectionDiagnosticsService            │
        │                              Common/DiagnosticReportFormatter
        │  ┌────────────────────────────┘（DiagnosticReport → プレーンテキスト）
        ▼
Dtos/DiagnosticReport ( DiagnosticItem[] + 環境情報 )
        ▲
        │ 判定元
   IDatabaseInfo / ICardReader / BackupService / IBackupHealthService
   ISharedDbConnectionStateProvider / Common.FolderWriteAccessProbe / Common.DiskSpaceHelper
```

責務分離の要点:

- **`ConnectionDiagnosticsService`** は UI に一切依存せず `DiagnosticReport` を返す。判定ロジックと文言は
  すべてここに閉じるため、単体テストで全分岐を網羅できる。
- **`DiagnosticReportFormatter`** は `DiagnosticReport` を受け取ってテキストを返す純関数。
  クリップボード API に触れないためテスト可能。
- **`IClipboardService`** を挟むことで ViewModel の単体テストが STA スレッドを要求しない。
- **`FolderWriteAccessProbe`** はフォルダ書込可否の実測のみを行う静的ヘルパー。`DiskSpaceHelper`（#1689）と同じ位置づけ。

## データモデル

`Dtos/DiagnosticReport.cs` に以下を定義する。

### `DiagnosticStatus`

| 値 | 意味 | UI アイコン |
|----|------|------------|
| `Ok` | 正常 | ✔ |
| `Warning` | 動作は継続できるが注意が必要 | ⚠ |
| `Error` | 機能が使えない、または近く使えなくなる | ✖ |
| `NotApplicable` | この環境では診断対象外 | － |

### `DiagnosticItemKind`

`DatabaseReachability` / `DatabaseWritable` / `JournalMode` / `SharedFolderConnection` /
`CardReader` / `BackupFolderWritable` / `DiskFreeSpace` / `BackupHealth`

### `DiagnosticItem`

| プロパティ | 説明 |
|-----------|------|
| `Kind` | `DiagnosticItemKind` |
| `Title` | 一覧に出す項目名（例: 「データベース到達性」） |
| `Status` | `DiagnosticStatus` |
| `SummaryText` | 一覧の右列に出す 1 行要約（例: 「接続できます」） |
| `DetailText` | 詳細ペイン／コピー結果に出す 3 要素メッセージ |

### `DiagnosticReport`

| プロパティ | 説明 |
|-----------|------|
| `DiagnosedAt` | 診断日時（`ISystemClock.Now`。テスト時に固定できる） |
| `Items` | `IReadOnlyList<DiagnosticItem>`。`DiagnosticItemKind` の宣言順で並ぶ |
| `AppVersion` | `AppVersionInfo.CurrentString` |
| `MachineName` | `Environment.MachineName` |
| `OsDescription` | `Environment.OSVersion.VersionString` |
| `DatabasePath` | `IDatabaseInfo.DatabasePath` |
| `IsSharedMode` | `IDatabaseInfo.IsSharedMode` |
| `OverallStatus` | `Items` の最悪値（`Error` > `Warning` > `Ok`。`NotApplicable` は集約に含めない） |

`OverallStatus` を DTO 側の計算プロパティに置くことで、ViewModel と Formatter が同じ判定を共有する。

## 診断項目の仕様

各項目は独立した `try`/`catch` で実行する。1 項目が例外を投げても診断全体は継続し、
その項目だけ `Error` として「診断自体に失敗した」旨を出す。
これは `BackupHealthService`（#1689）が採る「1 項目の失敗で画面が開けなくなる事態を避ける」方針の踏襲である。

### 1. データベース到達性 (`DatabaseReachability`)

`IDatabaseInfo.CheckConnection()` の結果。`false` なら `Error`。

### 2. データベース書込権限 (`DatabaseWritable`)

`IDatabaseInfo.CheckWritable()` の結果。`false` なら `Error`。
**到達性が NG の場合は実行せず `NotApplicable`**（到達できないのに書込を試すと同じ原因で二重に失敗し、
利用者が原因を 2 つあると誤解するため）。

### 3. ジャーナルモード (`JournalMode`)

`IDatabaseInfo.IsJournalModeDegraded` が `true` なら `Warning`（`CurrentJournalMode` を文言に含める）。
`false` なら `Ok`。クラッシュ耐性の低下は「今すぐ使えない」わけではないため `Error` にしない。

### 4. 共有フォルダ接続状態 (`SharedFolderConnection`)

ローカルモードでは `NotApplicable`。共有モードでは `ISharedDbConnectionStateProvider.CurrentConnectionState`
（= `SharedModeMonitor` の 15 秒ヘルスチェックの直近結果）を報告する。

| 状態 | ステータス |
|------|-----------|
| `Connected` | `Ok` |
| `Reconnecting` | `Warning` |
| `Disconnected` | `Error` |

項目 1（即時の到達性）と重複するように見えるが、**即時チェックは通っても直近に断続的な切断が起きていた**
ケースを捉えられるため独立した価値がある。

### 5. ICカードリーダー (`CardReader`)

`ICardReader.ConnectionState` を報告する。`Connected` → `Ok`、`Reconnecting` → `Warning`、
`Disconnected` → `Error`。

### 6. バックアップ保存先の書込可否 (`BackupFolderWritable`)

`BackupService.ResolveBackupFolderAsync()` で実際に使われる保存先を解決し、
`FolderWriteAccessProbe.Probe(folder)` で一時ファイルの作成・削除を試す。
失敗理由（フォルダ不在／権限なし／その他）に応じて文言を変える。

### 7. 空きディスク容量 (`DiskFreeSpace`)

項目 6 で解決した保存先について `DiskSpaceHelper.TryGetAvailableFreeSpace()` を使う。
`null`（取得不能）は `Warning`（「不明」）。
`AppConstants.DiagnosticsLowDiskSpaceWarningBytes`（= 1 GB）未満なら `Warning`。

しきい値を `Error` にしないのは、空き容量不足はバックアップが失敗し始める予兆であって
本体の動作を即座に妨げるものではないため。

### 8. バックアップ健全性 (`BackupHealth`)

`IBackupHealthService.GetHealthAsync()` の `LastSuccessAt` を使う。

- 成功記録なし → `NotApplicable`（#1689 で確立した「導入前からの既存環境で必ず警告が出るオオカミ少年化の防止」方針を踏襲）
- 最終成功から `AppConstants.BackupStaleWarningDays`（= 7 日）を超過 → `Warning`
- それ以外 → `Ok`

## メッセージ品質

`Warning` / `Error` の `DetailText` は `.claude/rules/error-messages.md` の 3 要素を満たす。

- **何が**: どのリソースか（DB パス・保存先パス・リーダー名などの具体値を含める）
- **なぜ**: なぜそれが問題か（ネットワーク断／権限不足／クラッシュ耐性低下 など）
- **どうすれば**: 具体的な次のアクション。文末は行動指示（「〜してください。」）で終える

`SummaryText` は一覧の列幅制約があるため短くてよい（#1688 で確立した「表示領域が制約された箇所には
20 文字基準を適用しない」方針）。ただし品質テストは `DetailText` に対して行う。

`Ok` / `NotApplicable` の `DetailText` は状態の説明のみで、行動指示は不要。

## クリップボード出力形式

`DiagnosticReportFormatter.Format(report)` が返すプレーンテキスト。

```
■ ピッすい 接続診断結果
診断日時: 2026-07-28 14:30:15
総合判定: 異常あり
アプリバージョン: 2.11.0
PC名: SOMU-PC01
OS: Microsoft Windows NT 10.0.22631.0
データベース: \\fileserver\share\iccard.db（共有モード）

[正常] データベース到達性: 接続できます
[対象外] データベース書込権限: ...
[異常] ICカードリーダー: 接続されていません
       ICカードリーダー（PaSoRi）が認識されていません。USB ケーブルが抜けているか、
       ドライバーが停止している可能性があります。PaSoRi を USB ポートに挿し直し、
       改善しない場合はパソコンを再起動してください。
```

`Error` / `Warning` の項目のみ `DetailText` を続けて出力する（`Ok` の詳細まで載せると要点が埋もれるため）。

## UI

`Views/Dialogs/ConnectionDiagnosticsDialog.xaml`（モーダル、`SizeToContent` ではなく固定サイズ＋可変レイアウト）。

- 上部: 診断日時と総合判定（アイコン＋テキスト＋色。色のみに依存しない — UI/UX 原則）
- 中央左: 項目一覧（`ListView`。アイコン / 項目名 / 要約）
- 中央下: 選択項目の `DetailText`（`TextWrapping="Wrap"`）
- 下部: 「再診断」「結果をコピー」「閉じる」

レイアウト上の注意（`.claude/rules/development-conventions.md`）:

- 詳細テキストは `TextWrapping="Wrap"` を明示し、幅制約のある `Grid`/`DockPanel` 配下に置く。
  横方向 `StackPanel` の直下には置かない（子が無限幅で測定されるため折り返さない）。
- 色は `AccessibilityStyles.xaml` のブラシキーを `DynamicResource` で参照する。色値リテラルを書かない。
  ステータス色は ViewModel/DTO から**リソースキー名**を返し `ResourceKeyToBrushConverter` でブラシ解決する。

### F6（システム管理画面）の変更

既存の「接続をテスト」ボタン（#1686、`TestDatabaseConnectionAsync`）を「接続診断」ボタンに置き換える。
DB 到達性・書込権限は診断項目 1・2 に内包されるため機能後退はない。
似た 2 つのボタンが並ぶ混乱を避けるための統合である。

## テスト

| テストクラス | 検証内容 |
|-------------|---------|
| `ConnectionDiagnosticsServiceTests` | 8 項目それぞれの `Ok` / `Warning` / `Error` / `NotApplicable` 判定、到達性 NG 時に書込権限が `NotApplicable` になること、項目別例外時に他項目が生き残ること、`OverallStatus` の集約 |
| `ConnectionDiagnosticsServiceTests.AllProblemItems_SatisfyErrorMessageQualityCriteria` | `Warning`/`Error` の `DetailText` が 20 文字以上・行動指示で終わる・具体値を含む |
| `DiagnosticReportFormatterTests` | ヘッダー（日時・総合判定・環境情報）、項目行、`Ok` の詳細を省くこと、null 安全 |
| `FolderWriteAccessProbeTests` | 実在フォルダ（成功）／存在しないフォルダ／空文字・null |
| `ConnectionDiagnosticsViewModelTests` | 診断実行で項目が入ること、コピーコマンドが `IClipboardService` を整形済みテキストで呼ぶこと、未診断時にコピーが実行できないこと |
| `SystemManageViewModelTests`（既存へ追記） | 接続診断コマンドが `INavigationService.ShowDialogAsync<ConnectionDiagnosticsDialog>` を呼ぶこと |
| `ConnectionDiagnosticsDialogLayoutTests` | XAML テキスト上の静的検証。詳細テキストに `TextWrapping="Wrap"` があること、横 `StackPanel` 直下に置かれていないこと、色値リテラルを含まないこと |

### 単体テストで担保できない範囲（手動検証）

WPF の実描画と物理デバイスに依存するため、以下は手動確認が必要:

1. PaSoRi を USB から抜いた状態で診断し、「ICカードリーダー」が異常表示されること
2. 共有モードでネットワークを切断した状態で診断し、DB 到達性・共有フォルダ接続状態が異常表示されること
3. 文字サイズ「特大」で詳細テキストが折り返され、ボタンや隣接要素の下へはみ出さないこと
4. 「結果をコピー」後にメモ帳へ貼り付け、設計書の出力形式どおりであること

## 影響範囲

新規: `Dtos/DiagnosticReport.cs`、`Services/IConnectionDiagnosticsService.cs`、
`Services/ConnectionDiagnosticsService.cs`、`Services/IClipboardService.cs`、
`Services/WpfClipboardService.cs`、`Services/ISharedDbConnectionStateProvider.cs`、
`Common/FolderWriteAccessProbe.cs`、`Common/DiagnosticReportFormatter.cs`、
`ViewModels/ConnectionDiagnosticsViewModel.cs`、`Views/Dialogs/ConnectionDiagnosticsDialog.xaml(.cs)`

変更: `Services/SharedModeMonitor.cs`（`ISharedDbConnectionStateProvider` 実装）、
`Common/AppConstants.cs`（しきい値追加）、`ViewModels/SystemManageViewModel.cs`（ボタン置換）、
`Views/Dialogs/SystemManageDialog.xaml`、`App.xaml.cs`（DI 登録）

ドキュメント: `docs/design/03_画面設計書.md`、`docs/design/07_テスト設計書.md`、
`docs/manual/管理者マニュアル.md` §10、`CHANGELOG.md`、`.claude/rules/error-messages.md`

DB マイグレーションは不要（新規の永続化データを持たない）。
