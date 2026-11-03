# テスト用 DB テンプレート Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ファイル DB を出発点にするだけのテストが、テストプロセスにつき 1 回だけ作る「全マイグレーション適用済みのテンプレート DB」のコピーを使うようにし、CI の「Run tests」を縮める。

**Architecture:** テスト側のヘルパー `MigratedDatabaseTemplate` がテンプレートを遅延初期化で作り、`CopyTo` で呼び出し側専用のコピーを渡す。テンプレートが空からの初期化と同じであることは規約テストで固定する。各テストは従来どおり `new DbContext(path)` と `InitializeDatabase()` を呼び、表明は変えない。

**Tech Stack:** .NET Framework 4.8、xUnit 2.9.3、FluentAssertions、System.Data.SQLite

**Spec:** `ICCardManager/docs/superpowers/specs/2026-11-03-test-db-template-design.md`

## Global Constraints

- ブランチは `origin/main` から `chore/test-db-template` で切る。コミットは `git add <ファイル>` で個別に
- 本番のコード（`ICCardManager/src/**`）と CI のワークフローは変えない
- **既存テストの表明は 1 つも変えない**。置き換えで表明を変えないと通らないテストは置き換えずに残し、理由を記録して報告する
- 新規 .cs は CRLF・BOM なし・先頭に `#nullable enable`。.md は LF
- dotnet は `"/mnt/c/Program Files/dotnet/dotnet.exe"`。件数は Release の `--list-tests` で数える。全テストの実行を 2 本並行で走らせない
- エラーメッセージ・because は日本語で「何が／なぜ／どうすれば」

## Review Focus

1. 並列に走る別クラスが同時に初回の `CopyTo` を呼ぶ — テンプレートの作成は 1 回だけで、作りかけのファイルを誰もコピーしない（Task 1 にテスト）
2. テンプレートの作成に失敗した（ディスクが一杯・権限）— 例外は呼び出し元のテストを失敗させ、次の呼び出しで作りかけを使い回さない（Task 1 にテスト）
3. コピー先の `-journal` 等が残っている／コピー先が既に存在する — 前のテストの残骸を黙って使わない（Task 1 にテスト）
4. マイグレーションを足したのにテンプレートが古い — プロセスごとに作るので起きないが、テンプレートと空からの初期化の一致を規約テストで固定する（Task 1）
5. 置き換え後に、空から作る経路を確かめていたテストが黙ってコピーを使うようになる — 置き換えは §3.3 の表のクラスに限り、`DbContextVersionGuardTests` は触らない（Task 2・3 のレビューで確認）

---

### Task 1: テンプレートのヘルパーと一致の規約テスト

**Files:**
- Create: `ICCardManager/tests/ICCardManager.Tests/Infrastructure/MigratedDatabaseTemplate.cs`
- Create: `ICCardManager/tests/ICCardManager.Tests/Infrastructure/MigratedDatabaseTemplateTests.cs`

**Interfaces:**
- Produces: `internal static class MigratedDatabaseTemplate`
  - `public static string CopyTo(string destinationPath)` — テンプレートを `destinationPath` へコピーして同じパスを返す。コピー先が既に存在する、または `destinationPath + "-journal" / "-wal" / "-shm"` が存在するなら `InvalidOperationException`（前のテストの残骸を使わない）。コピー先のディレクトリが無ければ作る
  - テンプレートは `Lazy<string>`（`LazyThreadSafetyMode.ExecutionAndPublication`）で、テストプロセス専用の一時フォルダー（`Path.GetTempPath()` 配下、`ICCardManagerTestTemplate_<GUID>`）に `new DbContext(path).InitializeDatabase()` で作り、`Dispose` → `SQLiteConnection.ClearAllPools()` の後に付随ファイルが無いことを確かめる。作成に失敗したら作りかけのファイルを消し、例外を投げる（`Lazy` は例外をキャッシュするので、失敗時は以後の呼び出しも同じ例外で失敗する — それでよい。作りかけを使い回さないことが要点）
  - 後片付けは `AppDomain.ProcessExit` で一時フォルダーを消す（失敗は無視）
- Produces（テスト用の比較）: `internal static DatabaseShape ReadShape(string dbPath)` と比較 — `sqlite_master` の（type, name, tbl_name, sql）の集合、`schema_migrations` の版数の集合、`settings` のキーの集合（値は比べない。比べない理由を XML doc に書く）。どちらのファイルに置くかは実装者が決める（テストクラス内の private でもよい）

- [ ] **Step 1: 失敗するテストを書く**（`MigratedDatabaseTemplateTests`）
  - `コピーは空から初期化したDBと同じスキーマと版数を持つこと`: 一時フォルダーで、(a) `CopyTo` したファイル、(b) `new DbContext(p).InitializeDatabase()` で空から作ったファイル、の `ReadShape` が一致
  - `検出ロジックはスキーマの食い違いを見つけること`: (b) と同じものを 2 つ作り、片方で表を 1 つ DROP → 不一致。もう片方の組で `schema_migrations` の 1 行を削除 → 不一致（#1786）
  - `コピーにInitializeDatabaseを呼んでもスキーマと版数は変わらないこと`: `CopyTo` → `new DbContext(copy).InitializeDatabase()` → `ReadShape` がテンプレートのコピー直後と一致
  - `コピーは互いに独立していること`: 2 つのコピーの片方で表を作っても、もう片方には無い
  - `コピー先に残骸があれば使わないこと`: コピー先に空ファイル、または `-journal` を置いて `CopyTo` → `InvalidOperationException`
  - `並列に初回の要求が来てもテンプレートは1回だけ作られること`: 8 スレッドから一斉に `CopyTo`（それぞれ別のコピー先）→ すべて成功し、各コピーの `ReadShape` が (b) と一致。作成回数を数える `internal static int BuildCount`（テスト用）が 1
  - `テンプレートの作成後に付随ファイルが残っていないこと`: テンプレートのパス（`internal static string TemplatePathForTests`）に `-journal` / `-wal` / `-shm` が無い

- [ ] **Step 2: 実行して失敗を確かめる**（`--filter "FullyQualifiedName~MigratedDatabaseTemplateTests"` → コンパイルエラーまたは FAIL）
- [ ] **Step 3: `MigratedDatabaseTemplate` を実装する**
- [ ] **Step 4: 実行して通ることを確かめる**。あわせて変異を 1 つ当てる: テンプレートの作成でマイグレーションの最後の 1 つを適用しない形を一時的に再現する（例: 作成後にテンプレートの `schema_migrations` から最大の版を消す）→ 一致の規約テストが赤。結果を報告に書き、元に戻す
- [ ] **Step 5: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Infrastructure/MigratedDatabaseTemplate.cs ICCardManager/tests/ICCardManager.Tests/Infrastructure/MigratedDatabaseTemplateTests.cs
git commit -m "test: 全マイグレーション適用済みのテンプレート DB をコピーで渡すヘルパーを追加"
```

### Task 2: BackupService 系のテストをテンプレートのコピーへ置き換える

**Files（Modify。出発点の作り方だけを変える）:**
- `ICCardManager/tests/ICCardManager.Tests/Services/BackupServiceTests.cs`（コンストラクタ、接続を開いたままにするテストの 1 か所）
- `.../Services/BackupServiceAtomicWriteTests.cs` / `BackupServiceEnumerationOrderTests.cs` / `BackupServiceManualBackupRecordingTests.cs` / `BackupServiceHealthRecordingTests.cs`（コンストラクタ）
- `.../Services/BackupServiceRestoreSafetyTests.cs`（各テストの約 8 か所）
- `.../Services/BackupServiceUiThreadGuardTests.cs`（各テストの 7 か所。初期化はガード設定の前のまま）
- `.../Services/BackupServiceRestoreValidationTests.cs`（ヘルパー `CreateAppDatabaseFile` と、直接初期化している約 6 か所）
- `.../ViewModels/SystemManageViewModelManualBackupHealthTests.cs`（コンストラクタ）

**Interfaces:**
- Consumes: `MigratedDatabaseTemplate.CopyTo(string destinationPath)`

置き換えの形: `_dbContext = new DbContext(_testDbPath); _dbContext.InitializeDatabase();` の前に `MigratedDatabaseTemplate.CopyTo(_testDbPath);` を入れる（`InitializeDatabase()` の呼び出しは残す）。DB のパスが「まだ存在しないこと」を前提にしている箇所（パスを作り直す・ディレクトリ自体を DB パスにする等）は置き換えない。

- [ ] **Step 1: 置き換え前の所要時間を測る** — 対象 9 クラスを Release で 1 回実行し、クラスごとの合計時間と件数を記録する（`--logger "console;verbosity=normal"` の出力を集計）
- [ ] **Step 2: 置き換える**
- [ ] **Step 3: 対象 9 クラスを実行する** — 件数が Step 1 と同じで、すべて緑。クラスごとの合計時間を Step 1 と並べて記録する。表明を変えないと通らないテストが出たら、そのテストの置き換えを戻し、理由を報告に書く
- [ ] **Step 4: コミット**（変えたファイルを個別に add。メッセージ例 `test: BackupService 系のテストの出発点をテンプレート DB のコピーにする`）

### Task 3: DB 層のテストを置き換え、規約と文書を更新する

**Files:**
- Modify（出発点の作り方だけ）: `ICCardManager/tests/ICCardManager.Tests/Data/DbContextVacuumTests.cs`、`Data/Repositories/RepositoryInsertFailureLoggingTests.cs`、`Data/Repositories/RepositoryInsertRetryTests.cs`、`Data/DbContextConcurrentAccessTests.cs`・`Data/DbContextConnectionLeaseTests.cs`・`Data/DbContextConnectionLifecycleTests.cs`（初期化している箇所だけ。UNC パス・ディレクトリを DB パスにする箇所・`InitializeDatabase` を呼ばない箇所は触らない）
- 触らない: `Data/DbContextVersionGuardTests.cs`、`Data/Migrations/DbContextMigrationTests.cs`
- Modify: `.claude/rules/testing.md`（設計 §3.4 の 1 節）、`ICCardManager/docs/design/07_テスト設計書.md`（§1.1a の件数、§2 に `Infrastructure/MigratedDatabaseTemplateTests` の行）、`ICCardManager/CHANGELOG.md`（`### Unreleased` の「開発基盤」に追記。実測値は Task 4 の後で入れる）

- [ ] **Step 1: 置き換え前の所要時間を測る**（対象 6 クラス、Task 2 Step 1 と同じ方法）
- [ ] **Step 2: 置き換える**
- [ ] **Step 3: 対象 6 クラスを実行し、件数が同じで緑・所要時間を並べて記録する**
- [ ] **Step 4: 文書を更新し、件数を実測する**（`--list-tests -c Release`。`python3 tools/check-test-count-sync.py ...` を Windows の dotnet を指す PATH で実行し一致を確かめる）
- [ ] **Step 5: 全体の検証** — ソリューション全体（Release、`Category!=UI`）を 1 回、全件緑。Release / Debug のビルドで警告 0、`dotnet format --verify-no-changes`
- [ ] **Step 6: コミット**

### Task 4: PR と実測

- [ ] **Step 1: 設計書・計画をコミットし、push して PR を作る**（本文に目的、置き換えたクラスと残したクラス、Task 1 の変異の結果、Task 2・3 のクラスごとの前後の所要時間）
- [ ] **Step 2: CI を 2 回実行し、build-and-test（Release / Debug）の「Run tests」と CI 全体の壁時計を、監査時（Run tests 約 205〜244 秒、CI 全体の中央値 約 399 秒）と比べて PR に記録する**
- [ ] **Step 3: CHANGELOG に実測値を入れてコミット・push する**
