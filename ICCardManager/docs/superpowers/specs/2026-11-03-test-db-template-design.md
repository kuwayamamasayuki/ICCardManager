# テスト用 DB の初期化をテンプレートのコピーに置き換える — 設計

- 日付: 2026-11-03
- 状態: 承認済み・実装済み（PR で CI 実測中）
- 関連: CI 監査（2026-11-01）の #4。シャーディング（PR #2396）は壁時計が縮まず閉じた。その原因調査の結果に基づく

## 1. 目的と背景

**目的は、PR を出してからマージできる状態になるまでの待ちを短くすること**（ユーザーの言葉）。CI の律速は build-and-test の「Run tests」（約 205〜244 秒）で、その約半分は「テストごとにファイル上の DB を新しく作り、全マイグレーションを流す」準備に使われている。

実測（2026-11-03、開発機。ビルド済みの本体を 32 ビットの PowerShell から直接呼んで計測）:

| 処理 | 1 回あたり |
|---|---|
| ファイル DB への `new DbContext(path).InitializeDatabase()` | 約 1,700〜2,200 ms |
| 同じ処理をメモリ上の DB で | 約 6 ms |
| 初期化済み DB を `File.Copy` → `InitializeDatabase()`（適用済みなので何もしない経路） | 約 5 ms（コピー約 1 ms ＋ 初期化約 4 ms） |

時間を使っているのはマイグレーションの処理ではなく、**コミットのたびのジャーナルの作成・削除とディスクへの同期書き込み**。CI のランナーはさらに数倍遅く、中身の無いテスト（定数の照合）でも 4 秒かかっていた。CI のログで 1 件あたりの中央値が 0.3 秒以上のクラスは 20 クラス・約 360 秒（全体の約 55%）。

## 2. ゴール・非ゴール・アンチゴール

**ゴール**
- テストプロセスにつき 1 回だけ「全マイグレーションを適用したファイル DB」（テンプレート）を作り、ファイル DB を出発点にするだけのテストは、それをコピーして使う
- 各テストは引き続き `new DbContext(path)` と `InitializeDatabase()` を呼ぶ（出発点がコピーになるだけ）。**表明は 1 つも変えない**
- CI の「Run tests」を実測で縮める（見込みは §6）

**非ゴール**
- 本番のコードの変更（テスト専用の分岐・設定を本番へ入れない。testing.md「本番コードに if (testMode) を入れない」）
- 固定の待機で遅いテスト（ロック待ち・タイムアウト・Sleep）の短縮。テストの挙動の変更になるため別途ユーザーの承認が要る
- シャーディングの再開

**アンチゴール**
- **テストが確かめている内容が黙って変わること**。とくに「空の DB から全マイグレーションを流す」経路・版数の記録・旧形式の移行・ファイル権限を確かめるテストが、コピーに替わってその経路を通らなくなること
- テンプレートが本番の初期化結果と食い違ったまま（マイグレーションを足したのにテンプレートが古い等）テストが緑になること
- テスト間でテンプレートを共有して書き換え、実行順に依存すること

## 3. 決めたこと（実装者＝私の判断を含む）

### 3.1 テンプレートの作り方

- テスト側のヘルパー（仮名 `Tests/Infrastructure/MigratedDatabaseTemplate`）が、初回の要求時に一度だけ（スレッド安全な遅延初期化）本番と同じ `new DbContext(path).InitializeDatabase()` でテンプレートを作る。**作るたびに現在のマイグレーションから作る**ので、古いテンプレートが残ることはない
- 作った後は接続を閉じ、接続プールを解放し（`SQLiteConnection.ClearAllPools()`）、`-journal` / `-wal` / `-shm` が残っていないことを確かめてから使う（`-journal` の不在を表明しているテストがあるため）
- 置き場所はテスト用の一時フォルダー（`AppDataPaths` の差し替え先とは別の、テストプロセス専用のフォルダー）。後片付けは失敗しても無視する
- 呼び出し側は `CopyTo(string destinationPath)` で自分専用のコピーを得る。**テンプレート自体を開いて使わせない**（共有して書き換えると実行順に依存する）

### 3.2 テンプレートが本番の初期化結果と同じであることを固定する

アンチゴール 2 への対策。テンプレートのコピーと「空から初期化した DB」を比べる規約テストを置く:
- `sqlite_master` の（type, name, tbl_name, sql）の集合が一致する
- `schema_migrations` の行（版数）が一致する
- 初期化が書く設定の行（キーの集合）が一致する。時刻など実行ごとに変わる値は値を比べない（比べない列はテストに理由を書く）
- 対の表明として、空から初期化した DB に 1 つだけ手を加えた入力（表を 1 つ DROP、版数を 1 行削除）では不一致になること（#1786「検査ロジックを既知のサンプル入力で固定する」）

### 3.3 置き換える範囲（2026-11-03 の調査で仕分け）

| 置き換える（コピーへ） | 残す（空から作る） |
|---|---|
| `BackupServiceTests`（コンストラクタ・接続を開いたままにするテストの 1 か所） | `DbContextVersionGuardTests`（初期化が自分の版数を記録すること自体を確かめる） |
| `BackupServiceAtomicWriteTests` | `DbContextMigrationTests`（メモリ上の DB で、マイグレーション自体が主題） |
| `BackupServiceEnumerationOrderTests` | 上の調査対象外で、`:memory:` を使うテスト（すでに速い） |
| `BackupServiceRestoreSafetyTests`（各テストの約 8 か所） | |
| `BackupServiceManualBackupRecordingTests` / `BackupServiceHealthRecordingTests` | |
| `BackupServiceUiThreadGuardTests`（各テストの 7 か所。初期化はガード設定の前） | |
| `BackupServiceRestoreValidationTests`（ヘルパー `CreateAppDatabaseFile` ＝ 26 か所から呼ばれる、と直接の初期化 約 6 か所） | |
| `SystemManageViewModelManualBackupHealthTests` | |
| `DbContextVacuumTests` | |
| `RepositoryInsertFailureLoggingTests` / `RepositoryInsertRetryTests` | |
| `DbContextConcurrentAccessTests` / `DbContextConnectionLeaseTests` / `DbContextConnectionLifecycleTests`（初期化している箇所だけ） | |

- 各クラスの DB のパスは、もともとテスト専用の一時フォルダーの下にある。**各テストが自分のコピーを持てば独立する**（スキーマや版数を書き換えるテストも、自分のコピーの中で閉じる）
- 置き換えは「出発点の作り方」だけで、各テストの手順と表明は変えない。置き換えの最中に「表明を変えないと通らない」ものが出たら、そのテストは置き換えずに残し、理由を記録する（ユーザーの承認なしに表明を変えない）
- `RepositoryInsertRetryTests` / `DbContextConnectionLeaseTests` / `DbContextConnectionLifecycleTests` / `DbContextConcurrentAccessTests` は、固定の待機（busy_timeout・Sleep・タイムアウト）が遅さの大半を占めるため、短縮は初期化の分だけ

### 3.4 新しく書くテストの規約

`.claude/rules/testing.md` に 1 節足す:「ファイル DB を出発点にするだけのテストは、テンプレートのコピーを使う。空の DB から作るのは、マイグレーション・版数・旧形式の移行・ファイル権限など初期化そのものを確かめるときだけ」。理由（1 回 1.7 秒・CI で数秒）も添える。

## 4. 変えないこと（制約）

- 本番のコード、CI のワークフロー、各テストの表明
- テストの件数は、新しい規約テストの分だけ増える（07_テスト設計書の §1.1a・§2 を同じ PR で更新する。件数は Release の `--list-tests` で数える）

## 5. 受け入れ基準

1. §3.2 の規約テストが緑で、変異（テンプレートを作るときに最後のマイグレーションを飛ばす等）で赤になる
2. 置き換えたクラスのテストがすべて、置き換え前と同じ件数で緑（表明の変更なし）
3. 手元で、置き換えたクラスの所要時間を置き換え前後で比べて記録する
4. PR の CI を 2 回実測し、「Run tests」と CI 全体の壁時計を、監査時（Run tests 約 205〜244 秒、CI 全体の中央値 約 399 秒）と比べて記録する
5. Release / Debug のビルドで警告 0、`dotnet format --verify-no-changes` が通る

## 6. 効果の見込み（根拠は §1）

- 置き換える 15 クラスは、CI の Release で合計 約 300 秒（うち固定の待機が主な 4 クラスを除くと 約 250 秒）。初期化のコストが 1 件あたり数秒 → 数 ms になるので、BackupService 系はおおむね 1/5〜1/10 になる見込み
- 「Run tests」の壁時計は、最長のクラス（BackupService 系の 70〜160 秒）が縮むことで、約 205〜244 秒 → **約 120〜160 秒**（次の律速は `RepositoryInsertRetryTests` 約 46 秒などの固定の待機）。CI 全体では 1 回あたり **約 60〜100 秒**の短縮を見込む。数字は実測で確かめる
