# Issue #1559: 共有モード判定ロジック修正 + デフォルト復元UI

## 背景

`DbContext.cs:222` の `IsSharedMode = databasePath != null;` は **「database_config.txt に何か書いてあれば共有モード」** という実装になっており、ローカルフォルダのフルパスを設定しただけでも共有モード（`busy_timeout=15s` / `journal_mode=DELETE` / `SharedModeMonitor` / 15秒キャッシュ TTL）として動作してしまう。

CLAUDE.md / `business-logic.md` / `development-conventions.md` の記載は **「UNCパス（`\\server\share`）指定時に自動的に共有モードとして動作」** であり、実装と設計書が乖離している。

Issue #1470（共有DB接続状態の3状態表示）で導入した SharedModeMonitor も、ローカルフォルダ指定時に誤って起動してしまう。

## 採用方針

**案 C: UNC + マップドドライブ判定（折衷案）+ UI改善**

```csharp
IsSharedMode = databasePath != null &&
    (IsUncPath(databasePath) || IsNetworkDrive(databasePath));
```

### 採用理由

- 設計書のニュアンス（「ネットワーク共有 = 共有モード」）を保ちつつ、マップドドライブ（`Z:\share\iccard.db`）経由の運用も `DriveType.Network` でカバー
- ローカルフルパス指定時（バックアップ先と同居など）は誤って共有モードにならない
- 加えて、UI に「デフォルトに戻す」ボタンを設け、`database_config.txt` を自動削除して復旧経路を提供

## 実装範囲

### 1. `DbContext.cs`

- `IsNetworkDrive(string path)` メソッドを新設
  - `Path.GetPathRoot` でルート取得 → `DriveInfo` 作成 → `DriveType == DriveType.Network` を返す
  - 例外時は `false`（ローカル扱い）にフォールバック（マウント外れ等の防御）
- `IsSharedMode = databasePath != null && (IsUncPath(...) || IsNetworkDrive(...))` に変更
- XMLコメントを「UNCパスまたはマップドネットワークドライブ指定時に共有モード」に更新

### 2. `SettingsViewModel.cs`

- `ResetDatabasePathToDefaultCommand` を追加
  - 確認ダイアログ表示後、`database_config.txt` を `File.Delete` で削除
  - `DatabasePath` を空文字に、`IsDatabasePathChanged = true` を立てる
  - ステータスメッセージで「次回起動からデフォルトパスに戻ります」を案内
- ダイアログ実装には既存の `IDialogService` を利用（テスト容易性のため）

### 3. `SettingsDialog.xaml`

- 「データベース保存先」 GroupBox 内、参照ボタンの右に **「デフォルトに戻す(_R)」** ボタンを追加
- ToolTip で「`database_config.txt` を削除して、ローカルデフォルトパス（`C:\ProgramData\ICCardManager\iccard.db`）に戻します」と案内

### 4. 単体テスト

| テスト対象 | テストクラス | 主なケース |
|-----------|--------------|----------|
| `DbContext.IsSharedMode` | `DbContextSharedModeDetectionTests`（新規） | デフォルト=false / UNC=true / ローカルフルパス=false / マップドドライブ判定（モック困難なので `IsNetworkDrive` を `internal` 化してテスト不要パスはスキップ） |
| `SettingsViewModel.ResetDatabasePathToDefault` | `SettingsViewModelDatabasePathTests`（既存または新規） | コマンド実行時に config ファイル削除が呼ばれ、UI状態が更新されることを確認 |

マップドドライブの判定は実機の `DriveInfo` 取得を伴うためテスト環境では再現困難。代替として `IsNetworkDrive` の振る舞いを「不正パス→false」「null→false」など防御パスのみテストする。

### 5. ドキュメント更新

- `.claude/rules/business-logic.md`: 「共有フォルダモード」セクションの「UNCパス指定時」→「UNCパス または ネットワークドライブ指定時」に修正
- `.claude/rules/development-conventions.md`: 「共有フォルダモード」の項目を同様に修正
- `CHANGELOG.md`: `### Unreleased` セクションに `### Fixed` として追記

## 影響範囲

- ローカルフルパスを設定済みのユーザーは、次回起動時にローカルモード（5秒タイムアウト/WAL）に切り替わる
  - `journal_mode` は次回接続時に自動切替（既存の `ConfigureJournalMode` ロジックがハンドリング）
  - 既存 DB ファイルとは互換（モード切替のみで構造変更なし）
- マップドドライブ運用者は引き続き共有モード扱い（挙動変化なし）
- UNC 運用者は引き続き共有モード扱い（挙動変化なし）

## 非対応事項（YAGNI）

- マップドドライブの「指している先がローカルか」を判定する深堀り（過剰）
- 起動時に共有モード判定の差分を検出して警告する機能（次回 Issue とする）
- インストーラ側の設定 UI 改修（範囲外）

## 関連

- Issue #1107（共有モード時の journal_mode フォールバック）
- Issue #1470（共有DB接続状態の3状態表示） PR #1558
- `.claude/rules/development-conventions.md`、`.claude/rules/business-logic.md`
- `ICCardManager/src/ICCardManager/Data/DbContext.cs:148, 221-222`
- `ICCardManager/src/ICCardManager/App.xaml.cs:225-236`
