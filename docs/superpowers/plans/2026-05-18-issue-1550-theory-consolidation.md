# Issue #1550: 同構造 Fact テスト群を Theory + InlineData に統合 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 5 つのテストクラスにおいて同構造の Fact/Theory メソッド群を `[Theory] + [InlineData]` に統合し、テストコードの DRY 化と可読性向上を実現する（テストケース数は不変）。

**Architecture:** クラス単位で順次リファクタリング。各クラスごとに「統合前の `dotnet test --list-tests` 件数記録 → 統合実装 → 件数差分ゼロ確認 → コミット」の保全 TDD サイクルを実行。最後に 5 クラス分の総合検証と必要に応じた件数表 §1.1a の同期更新を行う。

**Tech Stack:** xUnit 2.x（`[Theory]` + `[InlineData]`）、.NET Framework 4.8、FluentAssertions、Moq。WSL2 環境のため `"/mnt/c/Program Files/dotnet/dotnet.exe"` を使用。

**Branch:** `refactor/issue-1550-theory-consolidation`（既に作成済み、設計書もコミット済み）

---

## File Structure

リファクタリング対象は以下 5 ファイル。本体コードは変更しない。

| ファイル | 統合前メソッド数 | 統合後メソッド数 | 影響範囲 |
|---|---|---|---|
| `ICCardManager/tests/ICCardManager.Tests/Common/ConvertersTests.cs` | 4 → | 1 | `FileSizeConverterTests` クラスのみ（他 Converter テストは無関係） |
| `ICCardManager/tests/ICCardManager.Tests/Common/PathValidatorTests.cs` | 8 → | 1 | `ValidateBackupPath_*Traversal*` 系のみ。`SafeLookalikePaths`・`ContainsTraversalSegment` は触らない |
| `ICCardManager/tests/ICCardManager.Tests/Infrastructure/Security/FormulaInjectionSanitizerTests.cs` | 2 → | 1 | `IsDangerous` の False 系 2 メソッドのみ。True 系は分離維持 |
| `ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs` | 3 → | 1 | `UpdateSyncDisplayText_*` テキスト系 3 メソッドのみ。Fact 1 つと Stale Theory は分離維持 |
| `ICCardManager/tests/ICCardManager.Tests/Services/StationMasterServiceTests.cs` | 10 → | 1 | `GetStationName_*` 系 10 メソッドすべてを 1 Theory に統合 |

加えて必要に応じて:
- `ICCardManager/docs/design/07_テスト設計書.md` §1.1a（件数表）の同期更新
- `ICCardManager/CHANGELOG.md` の Unreleased セクションに refactor エントリ追加

---

## Task 0: 環境準備とベースライン取得

**Files:**
- Read-only: 既存 5 テストファイル
- Snapshot 出力: 一時ファイル（コミット対象外）

- [ ] **Step 1: 現在のブランチが `refactor/issue-1550-theory-consolidation` であることを確認**

Run:
```bash
git branch --show-current
```
Expected: `refactor/issue-1550-theory-consolidation`

異なる場合: `git checkout refactor/issue-1550-theory-consolidation`

- [ ] **Step 2: 全テスト件数のベースライン取得（コミット対象外）**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-baseline-tests.txt
wc -l /tmp/issue-1550-baseline-tests.txt
```
Expected: 数千件のテスト名リストが出力される（具体的件数は環境依存だが、本プランの判定基準は「リファクタリング前後で同じ件数」）

このファイルは作業終了後に削除する一時記録。

- [ ] **Step 3: 対象 5 クラスのテスト件数を個別記録**

Run:
```bash
grep -E "FileSizeConverterTests|PathValidatorTests|FormulaInjectionSanitizerTests|SharedModeMonitorTests|StationMasterServiceTests" /tmp/issue-1550-baseline-tests.txt | sort | uniq -c | sort -k2 > /tmp/issue-1550-baseline-by-class.txt
cat /tmp/issue-1550-baseline-by-class.txt
```
Expected: クラス別のテストケース総数を確認できる。各クラスごとの統合前後比較に使う。

- [ ] **Step 4: ビルド成功とテスト全 Pass を確認（リファクタリング開始前の安全基準）**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -20
```
Expected: `0 Error`、警告ゼロ（既存方針）

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet --filter "FullyQualifiedName~FileSizeConverterTests|FullyQualifiedName~PathValidatorTests|FullyQualifiedName~FormulaInjectionSanitizerTests|FullyQualifiedName~SharedModeMonitorTests|FullyQualifiedName~StationMasterServiceTests" 2>&1 | tail -5
```
Expected: `Passed: N, Failed: 0, Skipped: 0`

---

## Task 1: `FileSizeConverterTests` を Theory に統合

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Common/ConvertersTests.cs`（行 151-182 周辺の 4 メソッド）

- [ ] **Step 1: 統合前のテスト件数を記録（このクラスだけ）**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~FileSizeConverterTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task1-before.txt
wc -l /tmp/issue-1550-task1-before.txt
```
Expected: 4 つの統合対象メソッドが計 7 ケース（バイトTheory 3件 + KB Theory 2件 + MB Fact 1件 + GB Fact 1件）を含む。クラス内の他テストも併せて表示される。

- [ ] **Step 2: 該当箇所を確認**

Read `ICCardManager/tests/ICCardManager.Tests/Common/ConvertersTests.cs` の行 151-182 を読み、対象 4 メソッドの正確な位置と前後の文脈を把握する。

- [ ] **Step 3: 統合実装**

行 151-182 の 4 メソッド（`Convert_バイト単位の場合B表示になること` Theory、`Convert_KB単位の場合KB表示になること` Theory、`Convert_MB単位の場合MB表示になること` Fact、`Convert_GB単位の場合GB表示になること` Fact）を、以下の 1 メソッドに置換:

```csharp
[Theory]
[InlineData(0L, "0 B")]
[InlineData(512L, "512 B")]
[InlineData(1023L, "1023 B")]
[InlineData(1024L, "1 KB")]
[InlineData(1536L, "1.5 KB")]
[InlineData(1048576L, "1 MB")]
[InlineData(1073741824L, "1 GB")]
public void Convert_バイト数を適切な単位で表示すること(long input, string expected)
{
    var result = _converter.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);
    result.Should().Be(expected);
}
```

注意: `_converter` フィールドと `using System.Globalization;` の参照は既存のままで動く。元の各メソッド冒頭にあった `// Arrange / Act / Assert` のコメント形式に揃えるかは既存コードの慣例に従う。元コードがコメント無しならコメント無しで書く。

- [ ] **Step 4: ビルドとテストで件数完全一致を検証**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: `0 Error`、警告ゼロ

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~FileSizeConverterTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task1-after.txt
diff /tmp/issue-1550-task1-before.txt /tmp/issue-1550-task1-after.txt
```
Expected: diff の出力は「テストメソッド名の置換のみ」。**件数（行数）は完全一致**。具体的には:
- 削除: `Convert_バイト単位の場合B表示になること(input: 0, expected: "0 B")` など 7 行
- 追加: `Convert_バイト数を適切な単位で表示すること(input: 0, expected: "0 B")` など 7 行

```bash
wc -l /tmp/issue-1550-task1-before.txt /tmp/issue-1550-task1-after.txt
```
Expected: 両ファイルの行数が完全一致

- [ ] **Step 5: テスト実行で全 Pass を確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet --filter "FullyQualifiedName~FileSizeConverterTests" 2>&1 | tail -3
```
Expected: `Passed: N, Failed: 0, Skipped: 0`（N は Task 0 で記録した件数と同じ）

- [ ] **Step 6: コミット**

Run:
```bash
git add ICCardManager/tests/ICCardManager.Tests/Common/ConvertersTests.cs
git commit -m "$(cat <<'EOF'
refactor: FileSizeConverter テスト 4 メソッドを 1 Theory に統合 (Issue #1550)

同構造の Fact 2 + Theory 2 を Convert_バイト数を適切な単位で表示すること(long, string) の Theory 1 つに統合。InlineData 7 件で各ケースを保持し、dotnet test --list-tests の件数は不変。

Refs #1550

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `PathValidatorTests` の Traversal 系を Theory に統合

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Common/PathValidatorTests.cs`（行 297-381 周辺）

- [ ] **Step 1: 統合前のテスト件数を記録**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~PathValidatorTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task2-before.txt
wc -l /tmp/issue-1550-task2-before.txt
```
Expected: 統合対象 8 メソッドが計 11 ケース（Fact 6件 + MixedSeparator Theory 3件 + DotSpace Theory 2件）を含む。

- [ ] **Step 2: 該当箇所を確認**

Read `ICCardManager/tests/ICCardManager.Tests/Common/PathValidatorTests.cs` の行 290-400 を読み、統合対象 8 メソッドの位置と前後の文脈を把握する。

- [ ] **Step 3: 統合実装**

統合対象の 8 メソッド（`ValidateBackupPath_PathTraversal_ReturnsInvalid`、`_PathTraversalInMiddle_`、`_PathTraversalAtEnd_`、`_UncPathWithTraversal_`、`_UncPathDeepTraversal_`、`_UrlEncodedTraversal_`、`_MixedSeparatorTraversal_`、`_DotSpaceTraversal_`）を、以下の 1 メソッドに置換:

```csharp
[Theory]
// 単純トラバーサル
[InlineData(@"C:\backup\..\Windows\System32")]
[InlineData(@"C:\Users\test\..\admin\backup")]
[InlineData(@"C:\Users\test\..")]
// UNC パス
[InlineData(@"\\server\share\..\admin\iccard.db")]
[InlineData(@"\\server\share\..\..\admin\iccard.db")]
// URL エンコード
[InlineData(@"C:\backup\%2E%2E\Windows\System32")]
// 混在セパレータ
[InlineData(@"C:\backup/../Windows")]
[InlineData(@"C:/backup\..\Windows")]
[InlineData(@"C:\backup/..\..\Windows")]
// ドットと空白の組み合わせ
[InlineData(@"C:\backup\.. \Windows")]
[InlineData(@"C:\backup\..  \Windows")]
public void ValidateBackupPath_パストラバーサルパターンを検出すること(string path)
{
    var result = PathValidator.ValidateBackupPath(path);
    result.IsValid.Should().BeFalse();
    result.ErrorMessage.Should().Contain("..");
}
```

注意:
- `_SafeLookalikePaths_NotFlaggedAsTraversal` Theory（逆方向検証）と `ContainsTraversalSegment_DetectsCorrectly` Theory（別 API）は**触らない**
- `[InlineData]` のコメント分類（単純トラバーサル / UNC パス / 等）は元 Fact メソッド名から再現

- [ ] **Step 4: ビルドと件数完全一致検証**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: `0 Error`、警告ゼロ

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~PathValidatorTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task2-after.txt
wc -l /tmp/issue-1550-task2-before.txt /tmp/issue-1550-task2-after.txt
```
Expected: 両ファイルの行数が完全一致

- [ ] **Step 5: テスト実行で全 Pass を確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet --filter "FullyQualifiedName~PathValidatorTests" 2>&1 | tail -3
```
Expected: `Passed: N, Failed: 0, Skipped: 0`

- [ ] **Step 6: コミット**

Run:
```bash
git add ICCardManager/tests/ICCardManager.Tests/Common/PathValidatorTests.cs
git commit -m "$(cat <<'EOF'
refactor: PathValidator Traversal テスト 8 メソッドを 1 Theory に統合 (Issue #1550)

ValidateBackupPath の Traversal 系 Fact 6 + Theory 2 を ValidateBackupPath_パストラバーサルパターンを検出すること(string) の Theory 1 つに統合。InlineData 11 件で全パターンを保持し、Assert 内容（IsValid=false && ErrorMessage Contains "..") は不変。

Refs #1550

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `FormulaInjectionSanitizerTests` の False 系を Theory に統合

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Infrastructure/Security/FormulaInjectionSanitizerTests.cs`

- [ ] **Step 1: 統合前のテスト件数を記録**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~FormulaInjectionSanitizerTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task3-before.txt
wc -l /tmp/issue-1550-task3-before.txt
```
Expected: 統合対象 2 メソッドが計 9 ケース（DoesNotStartWithDangerousChar 7件 + NullOrEmpty 2件）を含む。

- [ ] **Step 2: 該当箇所を確認**

Read `ICCardManager/tests/ICCardManager.Tests/Infrastructure/Security/FormulaInjectionSanitizerTests.cs` を全行読み、`IsDangerous_DoesNotStartWithDangerousChar_ReturnsFalse` と `IsDangerous_NullOrEmpty_ReturnsFalse` の正確な位置を把握する。

- [ ] **Step 3: 統合実装**

`IsDangerous_DoesNotStartWithDangerousChar_ReturnsFalse` と `IsDangerous_NullOrEmpty_ReturnsFalse` を、以下の 1 メソッドに置換:

```csharp
[Theory]
// 危険文字で始まらない通常文字列
[InlineData("hello")]
[InlineData("123abc")]
[InlineData("日本語")]
[InlineData("  =1+1")]      // スペース先頭（スペース自体は危険文字ではない）
[InlineData("1=2")]         // 途中に=があっても先頭ではない
[InlineData("'=1+1")]       // 既にサニタイズ済み（'は危険文字ではない）
[InlineData("\n=1+1")]      // LF は Excel の先頭スキップ対象外
// Null / 空文字列
[InlineData(null)]
[InlineData("")]
public void IsDangerous_危険文字で始まらない入力はFalseを返すこと(string? input)
{
    FormulaInjectionSanitizer.IsDangerous(input).Should().BeFalse();
}
```

注意:
- `IsDangerous_StartsWithDangerousChar_ReturnsTrue` Theory（True 系）は**触らない**（分離維持）
- C# の null 許容アノテーション `string?` は既存コードの慣例に合わせる。元コードが `string input` であれば `string input` のままにする（CS8625 警告抑制のため `#pragma` がもし元コードにあったらそのまま保持）

- [ ] **Step 4: ビルドと件数完全一致検証**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: `0 Error`、警告ゼロ

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~FormulaInjectionSanitizerTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task3-after.txt
wc -l /tmp/issue-1550-task3-before.txt /tmp/issue-1550-task3-after.txt
```
Expected: 両ファイルの行数が完全一致

- [ ] **Step 5: テスト実行で全 Pass を確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet --filter "FullyQualifiedName~FormulaInjectionSanitizerTests" 2>&1 | tail -3
```
Expected: `Passed: N, Failed: 0, Skipped: 0`

- [ ] **Step 6: コミット**

Run:
```bash
git add ICCardManager/tests/ICCardManager.Tests/Infrastructure/Security/FormulaInjectionSanitizerTests.cs
git commit -m "$(cat <<'EOF'
refactor: FormulaInjectionSanitizer False 系テスト 2 メソッドを 1 Theory に統合 (Issue #1550)

IsDangerous の False 期待 Theory 2 つ（DoesNotStartWithDangerousChar / NullOrEmpty）を IsDangerous_危険文字で始まらない入力はFalseを返すこと(string?) の 1 Theory に統合。InlineData 9 件。True 系は意図ドキュメント性を保つため分離維持。

Refs #1550

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `SharedModeMonitorTests` の UpdateSyncDisplayText テキスト系を Theory に統合

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs`

- [ ] **Step 1: 統合前のテスト件数を記録**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~SharedModeMonitorTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task4-before.txt
wc -l /tmp/issue-1550-task4-before.txt
```
Expected: 統合対象 3 メソッドが計 10 ケース（5秒未満 Theory 3件 + 5〜60秒 Theory 4件 + 60秒以上 Theory 3件）を含む。

- [ ] **Step 2: 該当箇所を確認**

Read `ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs` を全行読み、`UpdateSyncDisplayText_5秒未満は…`、`_5秒以上60秒未満は…`、`_60秒以上は…` の正確な位置と、`SetLastRefreshAgo` helper・`SyncDisplayEventArgs` イベント受信パターンを把握する。

- [ ] **Step 3: 統合実装**

3 つの Theory を、以下の 1 メソッドに置換:

```csharp
[Theory]
// 5秒未満
[InlineData(0, "最終同期: たった今")]
[InlineData(2, "最終同期: たった今")]
[InlineData(4, "最終同期: たった今")]
// 5秒以上60秒未満
[InlineData(5, "最終同期: 5秒前")]
[InlineData(15, "最終同期: 15秒前")]
[InlineData(30, "最終同期: 30秒前")]
[InlineData(59, "最終同期: 59秒前")]
// 60秒以上
[InlineData(60, "最終同期: 1分前")]
[InlineData(120, "最終同期: 2分前")]
[InlineData(3599, "最終同期: 59分前")]
public void UpdateSyncDisplayText_経過時間に応じたテキストを生成すること(int elapsedSeconds, string expectedText)
{
    SetLastRefreshAgo(elapsedSeconds);
    SyncDisplayEventArgs? captured = null;
    _monitor.SyncDisplayUpdated += (_, e) => captured = e;
    _monitor.UpdateSyncDisplayText();
    captured.Should().NotBeNull();
    captured!.Text.Should().Be(expectedText);
}
```

注意:
- `UpdateSyncDisplayText_最終同期がない場合は同期待ちと表示されること` Fact（初期状態の検証で `IsStale` も Assert）は**触らない**
- `UpdateSyncDisplayText_経過時間がStaleThresholdSeconds以上ならIsStaleがtrueになること` Theory（`IsStale` を Assert）は**触らない**

- [ ] **Step 4: ビルドと件数完全一致検証**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: `0 Error`、警告ゼロ

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~SharedModeMonitorTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task4-after.txt
wc -l /tmp/issue-1550-task4-before.txt /tmp/issue-1550-task4-after.txt
```
Expected: 両ファイルの行数が完全一致

- [ ] **Step 5: テスト実行で全 Pass を確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet --filter "FullyQualifiedName~SharedModeMonitorTests" 2>&1 | tail -3
```
Expected: `Passed: N, Failed: 0, Skipped: 0`

- [ ] **Step 6: コミット**

Run:
```bash
git add ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs
git commit -m "$(cat <<'EOF'
refactor: SharedModeMonitor UpdateSyncDisplayText テキスト系を 1 Theory に統合 (Issue #1550)

経過時間と表示テキストの対応を検証する 3 つの Theory を UpdateSyncDisplayText_経過時間に応じたテキストを生成すること(int, string) の 1 Theory に統合。InlineData 10 件。Fact 1 つと Stale 判定 Theory は Assert 対象が異なるため分離維持。

Refs #1550

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `StationMasterServiceTests` の GetStationName 系 10 メソッドを Theory に統合

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Services/StationMasterServiceTests.cs`

- [ ] **Step 1: 統合前のテスト件数を記録**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~StationMasterServiceTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task5-before.txt
wc -l /tmp/issue-1550-task5-before.txt
```
Expected: 統合対象 10 メソッドが計 65 ケースを含む。

- [ ] **Step 2: 該当箇所を全部読む**

Read `ICCardManager/tests/ICCardManager.Tests/Services/StationMasterServiceTests.cs` を全行読み、10 メソッドそれぞれの全 InlineData 行（駅コードと駅名のペア計 65 件）と、`KitaOsakaKyuko_Extension` の TOICA 固有コメントを把握する。

- [ ] **Step 3: 統合実装**

統合対象 10 メソッド全てを、以下の 1 メソッドに置換。`[InlineData]` は `#region` で路線別にグルーピングする:

```csharp
[Theory]
#region 福岡市営地下鉄 空港線（はやかけん）
[InlineData(0xE701, CardType.Hayakaken, "姪浜")]
// ... 残り 12 件（既存の AirportLine_WithHayakaken の InlineData をすべて転記）
#endregion

#region 福岡市営地下鉄 箱崎線（はやかけん）
// ... 7 件
#endregion

#region 福岡市営地下鉄 七隈線（はやかけん）
// ... 9 件
#endregion

#region JR 鹿児島本線（はやかけん利用、九州エリア）
// ... 15 件
#endregion

#region JR 山手線（Suica）
[InlineData(0x2501, CardType.Suica, "品川")]
// ... 残り 4 件
#endregion

#region JR 東海道線 関東区間（Suica）
// ... 3 件
#endregion

#region 北陸新幹線 延伸区間（Suica）
// ... 7 件
#endregion

#region 相鉄・JR 直通 / 相鉄新横浜線（PASMO）
// ... 2 件
#endregion

#region 東急新横浜線（PASMO）
// ... 2 件
#endregion

#region 北大阪急行 千里中央〜箕面萱野延伸区間（TOICA）
// Area 2 優先のため TOICA 利用ケースで検証する（既存テスト由来の業務ロジック）
[InlineData(0xDE06, CardType.TOICA, "箕面船場阪大前")]
[InlineData(0xDE07, CardType.TOICA, "箕面萱野")]
#endregion
public void GetStationName_カード種別と駅コードに応じた駅名を返すこと(int stationCode, CardType cardType, string expectedName)
{
    var service = new StationMasterService();
    var result = service.GetStationName(stationCode, cardType);
    result.Should().Be(expectedName);
}
```

注意（実装時の重要事項）:
- 「`...残り N 件`」の部分は **絶対に省略せず**、Step 2 で読んだ実コードから **全 65 件をそのまま転記**する。1 件でも欠落させない
- `KitaOsakaKyuko_Extension` の TOICA に関する固有コメントは region 内のコメントブロックとして残す
- `using` ディレクティブ（`CardType` enum の名前空間など）が必要なら確認・追加する（恐らく既存のままで動く）

- [ ] **Step 4: ビルドと件数完全一致検証**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: `0 Error`、警告ゼロ

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet --filter "FullyQualifiedName~StationMasterServiceTests" 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-task5-after.txt
wc -l /tmp/issue-1550-task5-before.txt /tmp/issue-1550-task5-after.txt
```
Expected: 両ファイルの行数が完全一致（65 件不変）

万一一致しない場合: 「省略した InlineData がある」「駅コード/駅名のタイプミス」を疑い、Step 2 で読んだ原本と Step 3 の統合後コードを照合する。

- [ ] **Step 5: テスト実行で全 Pass を確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet --filter "FullyQualifiedName~StationMasterServiceTests" 2>&1 | tail -3
```
Expected: `Passed: N, Failed: 0, Skipped: 0`（N = 65、または StationMasterServiceTests 全体件数）

- [ ] **Step 6: コミット**

Run:
```bash
git add ICCardManager/tests/ICCardManager.Tests/Services/StationMasterServiceTests.cs
git commit -m "$(cat <<'EOF'
refactor: StationMasterService GetStationName 10 メソッドを 1 Theory に統合 (Issue #1550)

CardType (Hayakaken/Suica/PASMO/TOICA) と駅コード→駅名のマッピングを検証する 10 メソッドを GetStationName_カード種別と駅コードに応じた駅名を返すこと(int, CardType, string) の 1 Theory に統合。InlineData 65 件で全駅コードを保持。路線別 region でレビュー性を確保し、TOICA 固有コメントも保存。

Refs #1550

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: 全体検証とテスト件数表 §1.1a の同期判定

**Files:**
- Read: `ICCardManager/docs/design/07_テスト設計書.md`（§1.1a）
- Modify (条件付き): 同上

- [ ] **Step 1: 全テスト件数のリファクタリング後スナップショット取得**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --list-tests --nologo --verbosity quiet 2>&1 | grep -E "^\s+ICCardManager" | sort -u > /tmp/issue-1550-after-tests.txt
wc -l /tmp/issue-1550-baseline-tests.txt /tmp/issue-1550-after-tests.txt
diff /tmp/issue-1550-baseline-tests.txt /tmp/issue-1550-after-tests.txt | head -50
```
Expected: 両ファイルの**行数（テストケース数）は完全一致**。diff の中身は「メソッド名の置換のみ」（削除されたメソッド名と追加されたメソッド名が同数）。

- [ ] **Step 2: 全テスト Pass の最終確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet 2>&1 | tail -5
```
Expected: `Passed: N, Failed: 0, Skipped: 0` (N は Task 0 で記録した総数と同じ)

- [ ] **Step 3: ビルド警告ゼロの最終確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | grep -E "warning|Warning" | wc -l
```
Expected: `0`

- [ ] **Step 4: テスト件数表 §1.1a の集計単位を確認**

Read `ICCardManager/docs/design/07_テスト設計書.md` の §1.1a セクション全体（30〜100行程度）を読み、件数が「メソッド数ベース」か「ケース数ベース」かを判定する。

- もしファイルが大きい場合は `grep -n "§1.1a\|1.1a\|現在.*件\|テスト件数" ICCardManager/docs/design/07_テスト設計書.md | head -20` で位置を特定してから該当範囲を読む。

- [ ] **Step 5: 件数表の同期判定とコミット**

判断分岐:

**ケース A: 件数表が「テストケース数（[InlineData] 展開後）」基準** → 件数は不変のため**更新不要**。Step 6 へ進む。

**ケース B: 件数表が「テストメソッド数」基準** → 以下を実施:
1. 統合対象の 5 クラス分の「統合前メソッド数 - 統合後メソッド数」を計算:
   - FileSizeConverter: 4 → 1 (−3)
   - PathValidator Traversal: 8 → 1 (−7)
   - FormulaInjectionSanitizer False: 2 → 1 (−1)
   - SharedModeMonitor UpdateSyncDisplayText テキスト系: 3 → 1 (−2)
   - StationMasterService: 10 → 1 (−9)
   - **合計: −22 メソッド**
2. §1.1a の該当行を `Edit` で更新（具体的な数値は実際のドキュメント記載に従う）
3. コミット:
   ```bash
   git add ICCardManager/docs/design/07_テスト設計書.md
   git commit -m "$(cat <<'EOF'
   docs: テスト件数表 §1.1a を Theory 統合に同期更新 (Issue #1550)

   5 クラスで合計 22 個のテストメソッドが Theory + InlineData に統合されたことを反映。
   テストケース数（dotnet test --list-tests 上）は不変。

   Refs #1550

   Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
   EOF
   )"
   ```

- [ ] **Step 6: 一時ファイル削除**

Run:
```bash
rm -f /tmp/issue-1550-*.txt
```

---

## Task 7: CHANGELOG 更新と PR 作成

**Files:**
- Modify: `ICCardManager/CHANGELOG.md`

- [ ] **Step 1: CHANGELOG の現状確認**

Read `ICCardManager/CHANGELOG.md` の冒頭 50 行を読み、Unreleased セクションがあるかを確認する。

- [ ] **Step 2: Unreleased セクションに refactor エントリ追加**

Unreleased セクションの `### Changed`（無ければ作成）に以下を追加:

```markdown
- refactor: 同構造の Fact / Theory テスト群 5 クラス 27 メソッドを `[Theory] + [InlineData]` に統合し、テストコードを DRY 化（テストケース数は不変） (#1550)
```

注意: 既存の CHANGELOG エントリの書式（`- type:` 形式か `- 改善:` 形式かなど）に合わせる。プロジェクトの慣例は `ICCardManager/CHANGELOG.md` を読んで確認する。

- [ ] **Step 3: CHANGELOG コミット**

Run:
```bash
git add ICCardManager/CHANGELOG.md
git commit -m "$(cat <<'EOF'
docs: CHANGELOG に Issue #1550 Theory 統合リファクタリングを追記

Refs #1550

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 4: ブランチをリモートへ push**

Run:
```bash
git push -u origin refactor/issue-1550-theory-consolidation
```
Expected: ブランチが GitHub に push される。

- [ ] **Step 5: PR 作成**

Run:
```bash
gh pr create --title "refactor: 同構造 Fact/Theory テスト群を Theory + InlineData に統合 (5 クラス)" --body "$(cat <<'EOF'
## Summary
- 5 つのテストクラスで同構造の Fact/Theory メソッド計 27 個を `[Theory] + [InlineData]` に統合
- 統合後のメソッド数は 5（各クラス 1 メソッドずつ）。テストケース数（`dotnet test --list-tests` 上）は不変
- テストコードの DRY 化と可読性向上が目的（機能影響なし、本体コード無変更）

## 対象クラスと統合内容
| クラス | 統合前 → 統合後 | InlineData 件数 |
|---|---|---|
| `FileSizeConverterTests` | Fact 2 + Theory 2 → 1 Theory | 7 |
| `PathValidatorTests`（Traversal 系） | Fact 6 + Theory 2 → 1 Theory | 11 |
| `FormulaInjectionSanitizerTests`（False 系） | Theory 2 → 1 Theory | 9 |
| `SharedModeMonitorTests`（UpdateSyncDisplayText テキスト系） | Theory 3 → 1 Theory | 10 |
| `StationMasterServiceTests` | Theory 10 → 1 Theory | 65 |

## 設計判断
- True/False で意味の異なるテスト（`FormulaInjectionSanitizer`）は分離維持
- Assert 対象が異なるテスト（`SharedModeMonitor` の `IsStale` Theory、`同期待ち` Fact）は分離維持
- 逆方向検証（`PathValidator` の `SafeLookalikePaths`）は分離維持
- `StationMasterService` の 65 件 InlineData は路線別 `#region` でグルーピングしレビュー性確保

## 設計書
- `ICCardManager/docs/superpowers/specs/2026-05-18-issue-1550-theory-consolidation-design.md`

## Test plan
- [x] `dotnet test --list-tests` の合計件数がリファクタリング前後で完全一致（変更不要）
- [x] 統合対象 5 クラスのテストが全 Pass（`Passed: N, Failed: 0`）
- [x] ビルド警告ゼロを維持
- [x] CI のテスト件数表自動検証（test-count-sync-check workflow）通過

Closes #1550

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL が出力される。`gh pr edit` が GraphQL 警告で exit 1 する場合は `gh api -X PATCH .../pulls/{n} -F body=@file` をフォールバックに使う（memory: feedback_gh_pr_edit_graphql_workaround）。

- [ ] **Step 6: PR URL を確認しユーザーに報告**

Run:
```bash
gh pr view --json url --jq .url
```

最終報告でこの URL をユーザーに伝える。

---

## 各タスク失敗時の復旧

- **ビルドエラー**: 直前の Edit を `git diff` で確認、構文ミス・括弧の対応を点検
- **テスト Fail**: `dotnet test --logger "console;verbosity=detailed" --filter "FullyQualifiedName~XxxTests"` で詳細ログを取り、InlineData の値転記ミスを疑う
- **件数不一致**: `diff /tmp/issue-1550-taskN-before.txt /tmp/issue-1550-taskN-after.txt` で具体的に何が消えたか確認。「省略した InlineData がある」「分離維持すべきテストを誤って統合した」のいずれかが原因
- **CI（test-count-sync-check）失敗**: §1.1a 件数表の判定（Task 6 Step 4-5）をやり直す
