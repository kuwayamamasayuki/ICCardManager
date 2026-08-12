# Issue #1746: 起動時自動バックアップの UNC 検証が UI スレッドをブロックする問題の設計

- 日付: 2026-08-13
- Issue: #1746（リポジトリ全体バグ監査 2026-08-07 由来、severity: medium / confirmed）
- 対象: `Services/BackupService.cs` / `Common/PathValidator.cs`

## 問題

起動時の自動バックアップ経路（`App.PerformStartupTasksAsync` → `StartupTaskRunner.RunAsync` → `BackupService.ExecuteAutoBackupAsync`）は UI スレッドから始まる。`ResolveBackupFolderAsync` 冒頭の `GetAppSettingsAsync()` はキャッシュヒット時（本番の通常経路。Issue #1361 で確認済みの機構）に完了済み Task を返すため、`ConfigureAwait(false)` があってもスレッドホップせず UI スレッドで継続する。その直後の **同期版** `PathValidator.ValidateBackupPath(backupPath)` が:

1. UNC パスの到達性チェック（`existsTask.Wait(DefaultUncTimeoutMs)`）で**最大5秒**、
2. 到達可能だが低速な共有では `CheckWritePermission` の `File.WriteAllText` / `File.Delete` で**タイムアウトなしに**、

呼び出しスレッド＝UI スレッドをブロックする。`PathValidator.ValidateBackupPath` の XML remarks 自身が「UI スレッドから呼ぶ場合は `ValidateBackupPathAsync` を使うこと」と明記しており（Issue #1269 で規約化、`SettingsViewModel` は準拠済み）、`BackupService` だけがこの規約から漏れている。Issue #1361 の対策は `BackupDatabaseTo` の `Task.Run` オフロードのみで、その手前の検証は未保護だった。

## 修正方針

### 1. `ResolveBackupFolderAsync` を非同期検証へ切替（Issue の中核）

```csharp
// 修正前
var validationResult = PathValidator.ValidateBackupPath(backupPath);
// 修正後
var validationResult = await PathValidator.ValidateBackupPathAsync(backupPath).ConfigureAwait(false);
```

`ValidateBackupPathAsync` は検証全体（到達性チェック・書き込み権限プローブを含む）を `Task.Run` でスレッドプールへオフロードする既存 API（Issue #1269）。`ConfigureAwait(false)` により await 後の継続（`EnsureDirectoryExists` 等）もスレッドプール側で走るため、UNC 設定時の後続 I/O も UI スレッドから外れる。

この 1 箇所の修正で、同メソッドを共有する 3 経路（起動時自動バックアップ / システム管理画面 F6 の「バックアップ状況」(`BackupHealthService`) / 接続診断 (`ConnectionDiagnosticsService`)）すべてが保護される。

### 2. 兄弟メソッド `GetBackupFilesAsync` も横断是正

`GetBackupFilesAsync`（リストア画面 `SystemManageViewModel` から UI スレッドで呼ばれる）は `ResolveBackupFolderAsync` と同一の「キャッシュヒット → 同期検証」形状を持つため、同じ切替を適用する。`.claude/rules/development-conventions.md` の「規約を新設したら、同種の既存箇所を横断で洗う」（Issue #1730 で兄弟メソッドの取り残しが再発 Issue 化した教訓）に従う。

**対象外とその理由**: `CreateBackup`（BackupService.cs 内 3 つ目の同期検証呼び出し）は同期メソッド自身の内部であり、UI スレッドからの呼び出しは `CreateBackupAsync`（`Task.Run` オフロード、Issue #1361）が受けるため既に保護されている。sync 版はテスト経路用の残置（#1361 の設計判断）。

### 3. テスト容易性: `PathValidator` に注入フックを追加

`DbContext.IsOnUiThread`（Issue #1281/#1372 で確立した流儀）と同様の、テストから差し替え可能な static フックを追加する:

```csharp
internal static Func<string, int, bool> UncReachabilityChecker = DefaultUncReachabilityChecker;
```

公開 2 エントリポイント（sync `ValidateBackupPath(string)` / `ValidateBackupPathAsync`）はこのフックを経由する。既定値は従来の `DefaultUncReachabilityChecker`（readonly のまま維持）のため本番挙動は不変。

併せて `ValidateBackupPathAsync` 内の `await Task.Run(...)` に `.ConfigureAwait(false)` を付与する（`Common/**` の async 規約、Issue #1287）。

## テスト設計

`BackupServiceUiThreadGuardTests`（`DbContextUiThreadHookCollection` でシリアル実行済み）に 2 件追加する。

**判別方法**: スレッド ID 比較は「await でスレッドが解放された後、同一スレッドがプールに戻って Task.Run の仕事を拾う」可能性があり理論上フレーキーなため使わない。代わりに「**到達性チェックの完了前に呼び出しが制御を返すこと**」（= Task が未完了のまま返ること）を表明する:

1. フックを「開始を通知 → 解放イベントを待つ（暴走防止の上限 `DefaultUncTimeoutMs`）→ false を返す」フェイクに差し替え
2. `var task = service.ResolveBackupFolderAsync();` を呼ぶ
3. チェック開始の通知を待ち、`task.IsCompleted == false` を表明（修正前の同期実装ではこの呼び出し自体がブロックし、返ってきた時点で完了済みのため **Red**）
4. 解放後、既定パスへのフォールバックという既存挙動が保たれることも表明

**並列実行レースの排除**: フェイクはテスト固有のマーカー（GUID 入り UNC パス）に一致した場合のみ介入し、それ以外は `DefaultUncReachabilityChecker` へ委譲する。他テストクラスが並列で公開 API を呼んでも挙動が変わらない。フックの復元は `finally` と `Dispose()` の両方で行う。

| No | テストケース | 期待結果 |
|----|-------------|---------|
| 4 | UNC 到達性チェックがブロックしている間に `ResolveBackupFolderAsync` を呼ぶ | チェック完了前に制御が返り（Task 未完了）、完了後は既定パスへフォールバック |
| 5 | 同条件で `GetBackupFilesAsync` を呼ぶ | チェック完了前に制御が返り、完了後は例外なく一覧（フォールバック先）を返す |

## ドキュメント更新

- `06_シーケンス図.md`: バックアップのシーケンス中 `PathValidator.ValidateBackupPath()` → `ValidateBackupPathAsync()`（Task.Run オフロード）へ
- `07_テスト設計書.md`: UT-059a の表に 2 行追加、§1.1a / §8.1 の件数を +2
- `05_クラス設計書.md`: BackupService の該当記述があれば同期
- `CHANGELOG.md`: `### Unreleased` の **修正** に追記
- マニュアル: ユーザーから見える挙動の変化は「起動直後・F6 画面で固まらなくなる」のみで操作手順の変更はないため更新不要

## 却下した代替案

- **`ResolveBackupFolderAsync` 全体を `Task.Run` で包む**: 検証以外（設定読み出し）まで不要にオフロードし、かつ `ValidateBackupPathAsync` という規約準拠 API が既にあるのに二重の仕組みを作ることになる
- **BackupService に検証デリゲートをコンストラクタ注入**: テストがオフロードごと差し替えてしまい、「本物の async 版が使われていること」を検証できなくなる
- **ソーステキストの静的規約テスト**（「sync 版を呼んでいないこと」を grep で固定）: 挙動ではなく表記を固定するため、等価な迂回（別名メソッド追加等）に無力。挙動ベースの表明が可能なため不採用
