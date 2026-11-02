# CI テストのシャーディング Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ci.yml の build-and-test を Release / Debug × 2 シャードに分け、全件がちょうど 1 回ずつ実行されたことを照合し、カバレッジを分割前と同じ値で報告する集約ジョブを置いて、PR の CI の壁時計を約 70〜90 秒縮める。

**Architecture:** シャードは名前空間の接頭辞 1 つ（`env: TEST_SHARD_NAMESPACE`）から、matrix で `~`（肯定）と `!~`（否定）を選んで作る。照合とカバレッジの合算は pwsh 7 専用の関数ファイル `ICCardManager/tools/ci-test-shards.ps1` に置き、xUnit から `pwsh.exe` を起動して単体テストする。code-quality は「整形の検査」（code-quality）と「警告ゼロのビルド＋全件の一覧」（build-warnings）の 2 ジョブに分ける。

**Tech Stack:** GitHub Actions、.NET Framework 4.8 / xUnit 2.9.3 / VSTest（`dotnet test`）、coverlet.collector 3.2.0、PowerShell 7（pwsh）、FluentAssertions、YamlDotNet（既存の規約テストが使用）

**Spec:** `ICCardManager/docs/superpowers/specs/2026-11-02-ci-test-sharding-design.md`

## Global Constraints

- ブランチは `origin/main` から `chore/ci-test-sharding` で切る。コミットは `git add <ファイル>` で個別に（`-A` / `.` 禁止）
- アクションは commit SHA で固定し、末尾に `# vX.Y.Z` を付ける。新規の `actions/upload-artifact` / `actions/download-artifact` は最新リリースのタグを `gh api repos/actions/<name>/git/ref/tags/<tag>` で引いて SHA を得る（`.object.type` が `commit` であること）
- `run:` の中では `matrix` / `strategy` / `runner` 以外の `${{ }}` を書かない。`strategy:` に式を書かない（開発者ガイド §5.9）
- 維持するもの: `concurrency`、SDK の条件付き導入、NuGet キャッシュ、`dotnet restore --locked-mode`、`--no-restore` / `--no-build`、全 `dotnet test` の `--blame-hang-timeout 5m --blame-hang-dump-type none`、全ジョブの `timeout-minutes`（1〜60）、`permissions: contents: read`、`fail-fast: false`、Release のビルドの `-p:DebugType=portable -p:DebugSymbols=true` の式
- 整形の検査の `run:` は許可形のまま（`dotnet format --no-restore --verify-no-changes --verbosity diagnostic`）
- カバレッジは閾値で合否を決めない（#2117）。カバレッジの収集は Release だけ
- 新規 .cs は CRLF・BOM なし・先頭に `#nullable enable`（テストプロジェクトは csproj で有効だが既存の流儀に合わせる）。.ps1 は BOM 付き UTF-8（日本語を含むため）。.yml / .md は LF
- テストの実行は `"/mnt/c/Program Files/dotnet/dotnet.exe"`。件数は Release 構成の `--list-tests` で数える
- エラーメッセージは「何が／なぜ／どうすれば」の 3 要素
- 既存テストの期待を変えるのは §5 で承認済みの「カバレッジの報告は収集と同じ構成と場所を読み合否を決めないこと」だけ。それ以外の既存テストの期待を変える必要が出たら、止めてユーザーに確認する

## Review Focus

1. 開発機（日本語ロケール）の `--list-tests` は見出しが日本語になる — 見出しの文言に依存せず、字下げされた行だけを名前として読むこと（Task 1 にテスト）
2. テストが失敗したシャードは、失敗したテストも「実行された」として数え、集約ジョブはシャードの赤を緑で覆わない（Task 1 に Failed を含む trx のテスト、Task 4 で集約ジョブがシャードの成否を変えない構成）
3. シャードがハング・タイムアウト・ビルド失敗で結果を残さなかった — 集約ジョブは「結果が無い」で失敗する（黙って残りだけで照合しない）（Task 3 にテスト）
4. Debug は `#if DEBUG` のテストの分だけ Release より多い — 照合は構成ごとに、その構成の一覧とだけ突き合わせる（Task 3 にテスト）
5. Release のシャードでカバレッジの json が出なかった — 警告を出して照合は続ける（例外で落ちて照合ごと消えない）（Task 3 にテスト）

---

### Task 1: 照合の関数と pwsh の起動口

**Files:**
- Create: `ICCardManager/tools/ci-test-shards.ps1`（BOM 付き UTF-8。関数定義だけで、読み込んでも何も実行しない）
- Modify: `ICCardManager/tests/ICCardManager.Tests/Tools/ScreenshotSyncScriptTests.cs`（`PowerShellScriptRunner` に pwsh 版を足す）
- Create: `ICCardManager/tests/ICCardManager.Tests/Tools/CiTestShardsScriptTests.cs`

**Interfaces:**
- Produces（`PowerShellScriptRunner`）:
  - `public static string PwshPath { get; }` — `PATH` 上の `pwsh.exe`、無ければ `%ProgramFiles%\PowerShell\7\pwsh.exe`。どちらも無ければ `InvalidOperationException`（「pwsh 7 が見つからない。ci-test-shards.ps1 の検査はスキップせず失敗させる。PowerShell 7 を入れてください」の趣旨）
  - `public static ScriptResult RunPwshCommand(string command)` — 既存 `RunCommand` と同じ（`-EncodedCommand`、UTF-8 出力、60 秒で打ち切り）で実行ファイルだけ `PwshPath`。共通部分は private メソッドへ寄せ、`RunCommand` の挙動は変えない
- Produces（`ci-test-shards.ps1`）:
  - `Get-TestKey -DisplayName <string> -> string` — 末尾の理論の引数 `\([A-Za-z_][A-Za-z0-9_]*: .*\)$` だけを除く。それ以外の括弧（全角・コロンの無い括弧）は残す
  - `Read-ListTestsOutput -Path <string> -> string[]` — 4 文字以上の空白で始まる行を `Trim()` して返す。見出し・空行・字下げの無い行は捨てる
  - `Read-TrxTestNames -Path <string> -> string[]` — trx の `UnitTestResult` 要素の `testName` 属性をすべて（outcome を問わず）返す
  - `Test-ShardPartition -Expected <string[]> -Shards <hashtable: シャード名 -> string[]> -> pscustomobject` — 戻り値のプロパティ: `ExpectedCount`（期待のキーの数）、`ExecutedCount`（全シャードの名前の総数。理論の展開後）、`Missing`（string[]）、`Unexpected`（期待に無いのに実行されたキー）、`Duplicated`（2 つ以上のシャードに現れたキー）、`EmptyShards`（string[]）、`IsValid`（bool。Missing・Unexpected・Duplicated・EmptyShards がすべて空）。比較は `Get-TestKey` を通したキーの集合で行う

- [ ] **Step 1: 失敗するテストを書く**（`CiTestShardsScriptTests`、`[Trait("Category", "Unit")]`、各テストは `. '<ps1>'; <呼び出し> | ConvertTo-Json -Compress` を `RunPwshCommand` で実行して結果を読む）
  - `キー_理論の引数だけを除く`: `"A.B.M(x: 1, y: \"a\")"` → `"A.B.M"`、`"A.B.M"` → `"A.B.M"`、`"表示名（全角の括弧）"` → そのまま、`"Name (note)"` → そのまま
  - `一覧_見出しの言語に依存せず字下げされた行だけを読む`: 英語の見出し（`The following Tests are available:`）と日本語の見出し（`次のテストを使用できます:`）の 2 通りの入力で、どちらも `["A.B.M1", "A.B.M2(x: 1)"]`。前置きの行（`Test run for ...`、空行）は含まない
  - `trx_成功と失敗とスキップのすべてを読む`: Passed・Failed・NotExecuted の 3 つの `UnitTestResult` を持つ最小の trx（名前空間 `http://microsoft.com/schemas/VisualStudio/TeamTest/2010`）から 3 件
  - `分割_全件がちょうど1回ずつなら有効`: Expected `["A.M1","A.M2","B.M3"]`、Shards `@{ s = @("A.M1","A.M2"); o = @("B.M3") }` → `IsValid=true`、`ExpectedCount=3`、`ExecutedCount=3`
  - `分割_理論の展開は1つのキーとして数える`: Expected `["A.T"]`、Shards `@{ s = @("A.T(x: 1)","A.T(x: 2)"); o = @("B.M") }`、Expected に `"B.M"` を足して → `IsValid=true`、`ExecutedCount=3`
  - `分割_欠落を検出する`: Expected 3 件、シャードの和が 2 件 → `IsValid=false`、`Missing=["B.M3"]`
  - `分割_重複を検出する`: `A.M1` が両シャードに → `Duplicated=["A.M1"]`
  - `分割_空のシャードを検出する`: `o = @()` で残りを `s` が全部持つ → `EmptyShards=["o"]`、`IsValid=false`
  - `分割_期待に無い実行を検出する`: シャードに `C.M9` → `Unexpected=["C.M9"]`
  - `pwshが無ければスキップせず失敗する`: `PwshPath` の解決を差し替えられるよう `internal static Func<string?> PwshLocator` を置き、`() => null` で `RunPwshCommand` が `InvalidOperationException` を投げることを表明（テスト後に元へ戻す）

- [ ] **Step 2: 実行して失敗を確かめる**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj -c Release --filter "FullyQualifiedName~CiTestShardsScriptTests"`
Expected: FAIL（`RunPwshCommand` が無い／スクリプトが無い）

- [ ] **Step 3: `PwshPath` / `PwshLocator` / `RunPwshCommand` と、`ci-test-shards.ps1` の 4 関数を実装する**

trx は `[xml]` で読み、名前空間は `$xml.TestRun.Results.UnitTestResult` のドット記法で辿る（名前空間マネージャ不要）。キーの集合は `[System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)`。

- [ ] **Step 4: 実行して通ることを確かめる**（Step 2 と同じコマンド。Expected: PASS、10 件）

- [ ] **Step 5: コミット**

```bash
git add ICCardManager/tools/ci-test-shards.ps1 ICCardManager/tests/ICCardManager.Tests/Tools/ScreenshotSyncScriptTests.cs ICCardManager/tests/ICCardManager.Tests/Tools/CiTestShardsScriptTests.cs
git commit -m "test(ci): シャードの照合（全件がちょうど 1 回ずつ）を行う関数と pwsh 7 の起動口を追加"
```

### Task 2: カバレッジの合算と、実データでの一致の確認

**Files:**
- Modify: `ICCardManager/tools/ci-test-shards.ps1`
- Modify: `ICCardManager/tests/ICCardManager.Tests/coverlet.runsettings`（`<Format>cobertura</Format>` → `<Format>json,cobertura</Format>`）
- Modify: `ICCardManager/tests/ICCardManager.Tests/Tools/CiTestShardsScriptTests.cs`

**Interfaces:**
- Consumes: Task 1 の `ci-test-shards.ps1`・`RunPwshCommand`
- Produces: `Merge-CoverletJson -Paths <string[]> -> pscustomobject` — `LinesCovered`・`LinesTotal`・`BranchesCovered`・`BranchesTotal`（int）、`LineRate`・`BranchRate`（0〜1 の double。Total が 0 なら 0）、`KeysConsistent`（bool。全ファイルで行・分岐のキーの集合が一致）

- [ ] **Step 1: coverlet の json の形を実物で確かめる**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj -c Release -p:DebugType=portable -p:DebugSymbols=true --collect:"XPlat Code Coverage" --settings ICCardManager/tests/ICCardManager.Tests/coverlet.runsettings --results-directory <scratchpad>/cov --filter "FullyQualifiedName~Common"`（runsettings を json,cobertura にした後）
Expected: `<scratchpad>/cov/<guid>/` に `coverage.json` と `coverage.cobertura.xml` の両方。json が `{ モジュール: { ファイル: { クラス: { メソッド: { "Lines": { "行番号": ヒット数 }, "Branches": [ { "Line", "Offset", "EndOffset", "Path", "Ordinal", "Hits" } ] } } } } }` の形であること。形が違えば、以下のキーの組み立てを実物に合わせる（設計 §3.4 の「メソッドごとに数える」は変えない）

- [ ] **Step 2: 失敗するテストを書く**（入力の json はテスト内で一時ファイルに書く）
  - `合算_シャードごとのヒットの和集合で数える`: 同じ形の 2 ファイル。行 10・11・12 のうち、シャード A は 10 だけ、B は 11 だけヒット → `LinesCovered=2`・`LinesTotal=3`。分岐 2 つのうち A が 1 つ、B がもう 1 つ → `BranchesCovered=2`・`BranchesTotal=2`
  - `合算_同じ行が別のメソッドにあれば別々に数える`: 1 ファイル、2 つのコンストラクタがどちらも行 5 を持ち、片方だけヒット → `LinesCovered=1`・`LinesTotal=2`
  - `合算_キーの集合が食い違えば知らせる`: B にだけ行 13 がある → `KeysConsistent=false`（合算はキーの和集合で行う）
  - `合算_1ファイルなら行と分岐の割合はそのファイルの数え上げと一致する`: `LineRate = LinesCovered / LinesTotal`

- [ ] **Step 3: 実行して失敗を確かめる**（Task 1 Step 2 と同じコマンド。新しい 4 件が FAIL）

- [ ] **Step 4: `Merge-CoverletJson` を実装する** — `ConvertFrom-Json -AsHashtable`（pwsh 7）で読み、行のキーは `モジュール|ファイル|クラス|メソッド|行番号`、分岐のキーは `モジュール|ファイル|クラス|メソッド|Line|Offset|EndOffset|Path|Ordinal`。ヒット数を足し、> 0 を covered と数える

- [ ] **Step 5: 実行して通ることを確かめる**（Expected: PASS）

- [ ] **Step 6: 受け入れ基準 3 — 実データで cobertura と一致することを確かめる**

Step 1 と同じ収集を**フィルタ無し**（`--filter "Category!=UI"`）で 1 回行い、`Merge-CoverletJson -Paths <その coverage.json>` の `LineRate`・`BranchRate` を、同じ実行の `coverage.cobertura.xml` のルートの `line-rate`・`branch-rate` と比べる。Expected: 小数第 4 位まで一致。一致しなければ、キーの数え方を coverlet に合わせて直す（ここで止まって原因を調べる。値を丸めて合わせない）。さらに `--filter "Category!=UI&FullyQualifiedName~ICCardManager.Tests.Services."` と `"Category!=UI&FullyQualifiedName!~ICCardManager.Tests.Services."` の 2 回の json を合算した値が、フィルタ無しの値と一致すること。結果（4 つの値）を PR の本文に書くためメモしておく

- [ ] **Step 7: コミット**

```bash
git add ICCardManager/tools/ci-test-shards.ps1 ICCardManager/tests/ICCardManager.Tests/coverlet.runsettings ICCardManager/tests/ICCardManager.Tests/Tools/CiTestShardsScriptTests.cs
git commit -m "test(ci): シャードのカバレッジを coverlet と同じ数え方で合算する関数を追加"
```

### Task 3: 集約の入口 `Invoke-CiTestSummary`

**Files:**
- Modify: `ICCardManager/tools/ci-test-shards.ps1`
- Modify: `ICCardManager/tests/ICCardManager.Tests/Tools/CiTestShardsScriptTests.cs`

**Interfaces:**
- Consumes: Task 1・2 の関数
- Produces: `Invoke-CiTestSummary -ArtifactsDirectory <string> -Configurations <string[]> -SummaryPath <string> -> int`（終了コード。0 = 照合がすべて有効、1 = いずれかが無効または結果の欠落）
  - 期待する artifact の配置（Task 4 の ci.yml が作る。`actions/download-artifact` で全 artifact を 1 つのディレクトリへ下ろすと artifact 名のサブディレクトリになる）:
    - `<Artifacts>/test-results-<構成>-<シャード>/**/list-tests.txt`（`--list-tests` の出力。構成ごとに、その構成のいずれか 1 つのシャードの artifact にちょうど 1 つある。ci.yml では `services` シャードが作るが、スクリプトはシャード名に依存しない。0 個または 2 個以上は `::error::` で 1）
    - `<Artifacts>/test-results-<構成>-<シャード>/**/results.trx`
    - `<Artifacts>/test-results-Release-<シャード>/**/coverage.json`（Release だけ）
  - シャード名は `test-results-<構成>-` に続く部分をディレクトリ名から導出する（ci.yml の matrix の値を二重に持たない）
  - サマリー（Markdown、`SummaryPath` へ追記）: 構成ごとに「照合したテスト（名前の数）」「実行件数（理論の展開後）」「シャードごとの件数」、カバレッジの行・分岐（%、小数 1 桁）。照合が無効なら、欠落・期待外・重複の名前を各 30 件まで
  - 照合が無効なら `::error::` を 3 要素で出す（何が：構成とシャード／なぜ：欠落・重複・空・期待外の件数／どうすれば：ci.yml の TEST_SHARD_NAMESPACE と matrix.shard、ビルドの失敗の有無を確かめる）。本体に `Category=UI` のテストが足された可能性にも触れる（設計 §3.3）
  - 一覧または trx が無い構成・シャードは `::error::`（「結果が無い」）で 1 を返す。カバレッジの json が無い／`KeysConsistent=false` は `::warning::` だけ（終了コードに影響しない）

- [ ] **Step 1: 失敗するテストを書く**（一時ディレクトリに artifact の配置を作る。trx・一覧・json は Task 1・2 のテストと同じ最小形を使う）
  - `集約_すべて揃えば0を返しサマリーに件数とカバレッジを書く`: Release・Debug × シャード `services`・`others`。終了コード 0、サマリーに両構成の「照合したテスト」と「行」「分岐」を含む
  - `集約_構成ごとにその構成の一覧とだけ照合する`: Debug の一覧にだけ `D.DebugOnly` があり、Debug の `others` が実行している → 0（Release 側で期待外と数えない）
  - `集約_シャードの結果が無ければ失敗する`: `test-results-Debug-others` を作らない → 1、出力に `::error::` と `Debug` と `others`
  - `集約_一覧が無ければ失敗する`: Debug のどのシャードにも `list-tests.txt` を置かない → 1、出力に `Debug`
  - `集約_一覧が2つあれば失敗する`: Release の両シャードに `list-tests.txt` を置く → 1
  - `集約_欠落があれば失敗し名前を出す`: Release の一覧に `B.Lost` を足す → 1、出力に `B.Lost`
  - `集約_カバレッジが無くても照合は続け警告だけ出す`: Release の json を置かない → 0、出力に `::warning::`
  - `集約_カバレッジで合否を決めない`: すべてのヒットが 0 の json → 0

- [ ] **Step 2: 実行して失敗を確かめる**（Expected: 新しい 7 件が FAIL）

- [ ] **Step 3: `Invoke-CiTestSummary` を実装する**

- [ ] **Step 4: 実行して通ることを確かめる**（Expected: PASS）

- [ ] **Step 5: コミット**

```bash
git add ICCardManager/tools/ci-test-shards.ps1 ICCardManager/tests/ICCardManager.Tests/Tools/CiTestShardsScriptTests.cs
git commit -m "test(ci): シャードの照合とカバレッジの合算をまとめて報告する集約の入口を追加"
```

### Task 4: 表示名の一意性の規約テスト

**Files:**
- Create: `ICCardManager/tests/ICCardManager.Tests/TestDisplayNameUniquenessConventionTests.cs`

**Interfaces:**
- Consumes: なし（テストアセンブリ自身を反射で読む）
- Produces: なし

- [ ] **Step 1: テストを書く**
  - `テストメソッドの表示名は引数を除いて一意であること`: `typeof(<このクラス>).Assembly` の具象・公開の全型について、`FactAttribute`（`TheoryAttribute` を含む派生）を持つ公開メソッドを、継承したものも含めて（`BindingFlags.Public | Instance | Static`、`ReflectedType` は具象型）列挙し、表示名 = `FactAttribute.DisplayName` が空でなければそれ、空なら `{型の FullName}.{メソッド名}`。表示名で `GroupBy` し、2 件以上のグループが無いこと。失敗時は重複した表示名と型・メソッドを並べ、「シャードの照合（ci-test-shards.ps1）は表示名で突き合わせるため、重複すると片方が落ちても検出できない。DisplayName を一意にしてください」の趣旨
  - `検出ロジックが表示名の重複を見つけられること`: 列挙と重複判定を `internal static IReadOnlyList<string> FindDuplicateDisplayNames(IEnumerable<(Type Type, MethodInfo Method)>)` に切り出し、同じ `DisplayName` を持つ 2 メソッドを含むサンプル（このファイル内の入れ子の非公開クラス。`[Fact]` を付けずに `FactAttribute` を `GetCustomAttribute` の代わりに引数で渡すなど、テストとして発見されない形にする）で 1 件返すことを表明（#1786「検査ロジックを既知のサンプル入力で固定」）

- [ ] **Step 2: 実行する**

Run: `... test ... --filter "FullyQualifiedName~TestDisplayNameUniquenessConventionTests"`
Expected: 検出ロジックのテストは PASS。本番の走査が FAIL した場合は、重複している `DisplayName` を（テストの意図を変えない範囲で）一意な文言へ直す。直した対象と理由を PR 本文に書く

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/TestDisplayNameUniquenessConventionTests.cs
git commit -m "test: テストメソッドの表示名が一意であることを固定（シャードの照合の前提）"
```

### Task 5: ci.yml の分割・集約と規約テストの改訂

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `ICCardManager/tests/ICCardManager.Tests/CiWorkflowConventionTests.cs`

**Interfaces:**
- Consumes: Task 3 の `Invoke-CiTestSummary` と artifact の配置
- Produces（ci.yml）:
  - ワークフローの `env:` に `TEST_SHARD_NAMESPACE: 'ICCardManager.Tests.Services.'`
  - build-and-test の matrix: `configuration: [ Release, Debug ]`、`shard: [ services, others ]`
  - Run tests の 1 行: `dotnet test --no-build --configuration ${{ matrix.configuration }} --verbosity normal --filter "Category!=UI&FullyQualifiedName${{ matrix.shard == 'services' && '~' || '!~' }}$env:TEST_SHARD_NAMESPACE" --blame-hang-timeout 5m --blame-hang-dump-type none --results-directory ./test-results --logger "trx;LogFileName=results.trx" ${{ matrix.configuration == 'Release' && '--collect:"XPlat Code Coverage" --settings tests/ICCardManager.Tests/coverlet.runsettings' || '' }}`
  - UITests の GUI 不要のテストのステップの後に「List tests (シャードの照合の正解)」ステップ: `if: matrix.shard == 'services'`、`env: DOTNET_CLI_UI_LANGUAGE: en`、run は 1 行 `dotnet test tests/ICCardManager.Tests/ICCardManager.Tests.csproj --no-build --configuration ${{ matrix.configuration }} --list-tests --blame-hang-timeout 5m --blame-hang-dump-type none | Out-File -FilePath test-results/list-tests.txt -Encoding utf8`（既定のシェル pwsh。`$LASTEXITCODE` はランナーのラッパーが終了コードに使う）。設計 §3.6 の改訂（build-warnings ではなく services シャードで作る）による
  - その後に `actions/upload-artifact`（`if: always()`、`name: test-results-${{ matrix.configuration }}-${{ matrix.shard }}`、`path: ICCardManager/test-results`、`retention-days: 3`、`if-no-files-found: warn`）
  - 「Report coverage」ステップは削除（集約ジョブへ移る）
  - code-quality: 整形の検査まで（Checkout・SDK・キャッシュ・Restore・Check code format）。ジョブ名は `code-quality` のまま
  - 新ジョブ `build-warnings`（windows-latest、`timeout-minutes: 30`、`defaults.run.working-directory: ICCardManager`）: Checkout・SDK・キャッシュ・Restore・既存の「Build with analyzers (ビルド警告ゼロを検証)」をそのまま移す（一覧は作らない）
  - 新ジョブ `test-summary`（ubuntu-latest、`timeout-minutes: 10`、`needs: [ build-and-test ]`、`if: always()`）: Checkout → `actions/download-artifact`（`path: artifacts`、パターン指定なし）→ pwsh で `. ./ICCardManager/tools/ci-test-shards.ps1` してから `exit (Invoke-CiTestSummary -ArtifactsDirectory artifacts -Configurations @('Release','Debug') -SummaryPath $env:GITHUB_STEP_SUMMARY)`

- [ ] **Step 1: `--list-tests` にハング検出のフラグを付けても動くことを手元で確かめる**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --no-build -c Release --list-tests --blame-hang-timeout 5m --blame-hang-dump-type none`（`DOTNET_CLI_UI_LANGUAGE=en`）
Expected: 終了コード 0 で一覧が出る。**エラーになる場合は止めてユーザーに確認する**（「すべての dotnet test にハング検出」の規約テストの期待を変えることになるため）

- [ ] **Step 2: 新しい規約テストを書く**（`CiWorkflowConventionTests` に追加。既存の `ExtractJobs` / `ExtractMatrixValues` / `ExtractDotnetTestCommands` / `ExtractExpressions` / `WorkflowSteps` を使う）
  - `シャードのフィルタは1つの名前空間から肯定と否定で作られていること`: ① ワークフローの `env:` の `TEST_SHARD_NAMESPACE` が `ICCardManager.Tests.` で始まり `.` で終わる、② build-and-test の `ExtractMatrixValues(block, "shard")` がちょうど 2 値、③ ソリューション全体を対象にする `dotnet test`（csproj を指定しないもの）の `--filter` の値が正規表現 `^Category!=UI&FullyQualifiedName\$\{\{ matrix\.shard == '(?<s>[^']+)' && '~' \|\| '!~' \}\}\$env:TEST_SHARD_NAMESPACE$` に一致し、`s` が matrix の値の 1 つ、④ そのフィルタに `FullyQualifiedName` が 1 回しか現れない
  - `検出ロジックがシャードのフィルタの許可形を判定できること`: 許可形 → true。否定側を名前空間の列挙に書き換えた形（`FullyQualifiedName~A.|FullyQualifiedName~B.`）・接頭辞を直書きした形・両方 `~` の形 → false（#1786）
  - `集約ジョブが全シャードの後に照合と合算を行うこと`: `test-summary` の `needs` が `build-and-test` を含み、`if: always()`、run に `Invoke-CiTestSummary` と `-Configurations @('Release','Debug')`（matrix の configuration と同じ集合）を含む。`build-and-test` に `if: always()` の `upload-artifact` があり名前に `${{ matrix.configuration }}` と `${{ matrix.shard }}` を含み、`path` が一覧の出力先（`test-results/list-tests.txt` の親）と `--results-directory` を含む。一覧のステップの `--list-tests` の行が `--configuration ${{ matrix.configuration }}` を持ち、その `if:` が matrix の shard の値の 1 つを指す
- [ ] **Step 3: 既存の規約テスト「カバレッジの報告は収集と同じ構成と場所を読み合否を決めないこと」を改訂する**（設計 §5 で承認済み）
  - 残す表明: 収集の式が 1 つ・`--settings` のファイルが存在・Release のビルドの PDB の式・合否を決めない
  - 変える表明: `--results-directory` はテストのコマンドの無条件の部分にあり（式の外）、build-and-test の upload-artifact の `path` がその場所を指す（`ICCardManager/` ＋ `--results-directory` の値。`./` を除いて比べる）。報告は `test-summary` ジョブの run（`Invoke-CiTestSummary`）が行い、その run が `exit 1` を直書きしない／`::error::` を直書きしない（合否はスクリプトの照合だけが決める）。スクリプト側でカバレッジが終了コードに影響しないことは Task 3 のテストが固定している
  - 旧ステップ（`coverage.cobertura.xml` を読む `Report coverage`）が ci.yml に無いこと

- [ ] **Step 4: 実行して新規・改訂の規約テストが失敗することを確かめる**

Run: `... test ... --filter "FullyQualifiedName~CiWorkflowConventionTests"`
Expected: 新規 3 件と改訂 1 件が FAIL、他は PASS

- [ ] **Step 5: ci.yml を Interfaces のとおりに書き換える**（コメントは既存の密度で、各変更の理由とこの設計書への参照を書く。`concurrency` 以下の既存の注記は残す）

- [ ] **Step 6: 規約テスト全体と YAML の構文を確かめる**

Run: `... test ... --filter "FullyQualifiedName~CiWorkflowConventionTests"` → Expected: すべて PASS
Run: `python3 -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml', encoding='utf-8'))"` → Expected: 例外なし

- [ ] **Step 7: 変異検査（受け入れ基準 2）** — 1 つずつ当てて戻す（戻した後は `touch` してから再実行。差分ビルドの取り違えを避ける）
  - ci.yml の否定側を列挙に書き換える（`others` のとき `FullyQualifiedName~ICCardManager.Tests.Data.` にする形）→ 規約テストのシャードのフィルタの検査が赤。同じ欠落を Task 3 の入力（`Common` を誰も実行しない配置）で再現すると `Invoke-CiTestSummary` が 1
  - `TEST_SHARD_NAMESPACE` の末尾の `.` を消す → 赤
  - 一覧のステップの `if:` を存在しない shard の値（`'nothing'`）にする → 規約テストが赤。同じ状態（一覧が無い）を Task 3 の入力で再現すると `Invoke-CiTestSummary` が 1
  - 結果（どれがどのテストで赤になったか）を PR の本文用にメモする

- [ ] **Step 8: コミット**

```bash
git add .github/workflows/ci.yml ICCardManager/tests/ICCardManager.Tests/CiWorkflowConventionTests.cs
git commit -m "ci: build-and-test を構成×2 シャードに分け、全件の照合とカバレッジの合算を集約ジョブで行う"
```

### Task 6: ドキュメントと件数

**Files:**
- Modify: `ICCardManager/docs/design/00a_技術スタック用語集.md`（「ci.yml の処理内容」の行。シャード・集約ジョブ・build-warnings・カバレッジの報告場所）
- Modify: `ICCardManager/docs/manual/開発者ガイド.md`（「ci.yml の code-quality ジョブ」がビルド警告ゼロを数えると書いた箇所 → build-warnings。シャードの境界の変え方（`TEST_SHARD_NAMESPACE`）と、照合が赤になったときの読み方を CI の節に短く）
- Modify: `.claude/rules/development-conventions.md`（「ビルド警告ゼロ検証」ステップの所在を `code-quality` → `build-warnings` ジョブへ）
- Modify: `ICCardManager/docs/design/07_テスト設計書.md`（§1.1a の合計、§2 の `CiWorkflowConventionTests` の件数と説明、新しい `CiTestShardsScriptTests`・`TestDisplayNameUniquenessConventionTests` の行、§8.1）
- Modify: `ICCardManager/CHANGELOG.md`（`### Unreleased` の「開発基盤」に追記。見込みと実測は Task 7 の後で数字を入れる）

- [ ] **Step 1: 件数を実測する**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj -c Release --list-tests | grep -c '^    '`（UITests も同様）
Expected: 数値。07 の各所をこの値に合わせる

- [ ] **Step 2: 文書を更新し、件数の同期の検査を手元で実行する**

Run: `python3 tools/check-test-count-sync.py --doc ICCardManager/docs/design/07_テスト設計書.md --unit-csproj ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --ui-csproj ICCardManager/tests/ICCardManager.UITests/ICCardManager.UITests.csproj`（Windows の dotnet を使うよう、test-count-sync-check.yml と同じ前提で。動かない場合は CI の結果で確かめる）
Expected: 一致

- [ ] **Step 3: ソリューション全体の検証**

Run（`ICCardManager` で）: `"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager.sln -c Release` と `-c Debug` → 警告 0。`"/mnt/c/Program Files/dotnet/dotnet.exe" format --verify-no-changes` → 違反なし。`"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager.sln -c Release --filter "Category!=UI"` → 全件 PASS

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/docs/design/00a_技術スタック用語集.md ICCardManager/docs/manual/開発者ガイド.md .claude/rules/development-conventions.md ICCardManager/docs/design/07_テスト設計書.md ICCardManager/CHANGELOG.md
git commit -m "docs: CI のテストのシャーディングと集約ジョブを設計書・開発者ガイド・規約に反映"
```

### Task 7: PR と実測（受け入れ基準 1・4）

- [ ] **Step 1: 設計書と計画をコミットし、push して PR を作る**（`ICCardManager/docs/superpowers/specs/2026-11-02-ci-test-sharding-design.md`・`ICCardManager/docs/superpowers/plans/2026-11-02-ci-test-sharding.md`。PR 本文に: 目的、構成、Task 2 Step 6 のカバレッジの一致、Task 5 Step 7 の変異検査、Task 4 で直した表示名（あれば）、既存の規約テストを 1 件改訂したこと）
- [ ] **Step 2: PR の CI を確かめる** — Release・Debug × 2 シャード、code-quality、build-warnings、test-summary がすべて緑。test-summary のサマリーに両構成の「照合したテスト」とカバレッジが出ていること。カバレッジの値が直前の main の Release の値と一致（テストの追加分の差は説明できること）
- [ ] **Step 3: 再実行してもう 1 回測り、壁時計（最初のジョブの開始から test-summary の終了まで）とジョブごとの時間を、設計 §6 の見込みと比べて PR に記録する**。見込みから大きく外れたら原因を書く
- [ ] **Step 4: CHANGELOG に実測値を入れてコミット・push する**
