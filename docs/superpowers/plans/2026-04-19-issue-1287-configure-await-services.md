# Issue #1287: ConfigureAwait(false) Service 層適用（Phase 1）実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `.editorconfig` で CA2007 を有効化し、主要 Service 層 11 ファイルの全 `await` に `.ConfigureAwait(false)` を付与。Phase 2 以降の残作業は follow-up Issue にする。

**Architecture:** 最適化的変更（契約不変）なので既存テスト 3017 件が regression detector。各ファイルごとにタスクを区切り、編集→ビルド→テストの小サイクルで検証する。

**Tech Stack:** C# 10 / .NET Framework 4.8 / .editorconfig / Roslyn CA2007

---

## 事前確認

- ブランチ: `feat/issue-1287-configure-await-services`（main から分岐、spec commit 済み）
- 対象 11 ファイル・~130 await
- Test コマンド: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --nologo --verbosity minimal`
- 重要: 既存 ConfigureAwait(false) はそのまま残す。`Grep` で "ConfigureAwait" を事前検索して重複を避ける

## File Structure

### 新規

- `.editorconfig`（プロジェクトルート。既存を grep で確認、なければ新規）
- `.claude/rules/async-configureawait.md`（開発規約）

### 変更

- `ICCardManager/src/ICCardManager/Data/DbContext.cs`
- `ICCardManager/src/ICCardManager/Services/LendingService.cs`
- `ICCardManager/src/ICCardManager/Services/OperationLogger.cs`
- `ICCardManager/src/ICCardManager/Services/CsvImportService.cs`
- `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Card.cs`
- `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Staff.cs`
- `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs`
- `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs`
- `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.LedgerValidation.cs`
- `ICCardManager/src/ICCardManager/Services/ReportService.cs`
- `ICCardManager/src/ICCardManager/Services/ReportDataBuilder.cs`
- `ICCardManager/src/ICCardManager/Services/BackupService.cs`
- `ICCardManager/CHANGELOG.md`

---

## Task 1: Baseline 確認

- [ ] **Step 1: ブランチ + 全テスト**

```bash
git branch --show-current
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: ブランチ `feat/issue-1287-configure-await-services`、失敗 0 / 合格 3017 件程度

---

## Task 2: .editorconfig と規約文書の作成

**Files:**
- Create/Modify: `.editorconfig`（プロジェクトルート）
- Create: `.claude/rules/async-configureawait.md`

- [ ] **Step 1: 既存 .editorconfig を確認**

```bash
ls -la .editorconfig 2>&1 || echo "なし"
```

- [ ] **Step 2: .editorconfig に設定追加（新規作成の場合は以下全文、既存ある場合は末尾に追記）**

新規の場合はプロジェクトルートに以下を書く:

```editorconfig
root = true

# Issue #1287: Service 層では ConfigureAwait(false) を推奨
[ICCardManager/src/ICCardManager/**/*.cs]
dotnet_diagnostic.CA2007.severity = suggestion

# ViewModels は UI 文脈を維持するため CA2007 を無効化
[ICCardManager/src/ICCardManager/ViewModels/**/*.cs]
dotnet_diagnostic.CA2007.severity = none

[ICCardManager/src/ICCardManager/Views/**/*.cs]
dotnet_diagnostic.CA2007.severity = none

# テストコードでも無効化
[ICCardManager/tests/**/*.cs]
dotnet_diagnostic.CA2007.severity = none
```

既存 `.editorconfig` がある場合は `root = true` 行を重複させず、上記の `[...]` セクションのみ末尾に追記。

- [ ] **Step 3: 規約文書を作成**

`.claude/rules/async-configureawait.md`:

```markdown
# async / ConfigureAwait(false) 規約

## Service 層（Services/ 配下）

すべての `await` に `.ConfigureAwait(false)` を付与する。

```csharp
// ✅ 推奨
var card = await _cardRepository.GetByIdmAsync(idm).ConfigureAwait(false);

// ❌ 非推奨（UI スレッド同期で不要な dispatch）
var card = await _cardRepository.GetByIdmAsync(idm);
```

## ViewModel 層（ViewModels/ 配下）

`ConfigureAwait(false)` を付けない。`INotifyPropertyChanged` や UI バインディングが UI 文脈を要求するため。

## View 層（Views/ 配下）

同上。

## テストコード

付けない（regression detector としての純粋性を保つ）。

## アナライザ

`.editorconfig` で `CA2007` を severity=suggestion として設定。Service 層のみ対象、ViewModels/Views/tests は none で無効化。

## 参考

- 設計書: `docs/superpowers/specs/2026-04-19-issue-1287-configure-await-services-design.md`
- Issue #1287
```

- [ ] **Step 4: ビルド確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: エラー 0

- [ ] **Step 5: コミット**

```bash
git add .editorconfig .claude/rules/async-configureawait.md
git commit -m "$(cat <<'EOF'
feat: CA2007 アナライザ有効化と async 規約を追加 (Issue #1287)

- .editorconfig で Service 層の CA2007 を suggestion レベルに
- ViewModels/Views/tests は CA2007 を none で無効化
- .claude/rules/async-configureawait.md に規約文書化

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: DbContext.cs に ConfigureAwait(false) を適用

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Data/DbContext.cs` (4 awaits、既存 1 はそのまま)

- [ ] **Step 1: 対象 await を grep で特定**

```bash
grep -n "await " ICCardManager/src/ICCardManager/Data/DbContext.cs | grep -v "ConfigureAwait"
```

- [ ] **Step 2: 各行を Edit で修正**

grep で見つけた行ごとに `Edit` ツールで `.ConfigureAwait(false)` を追加する。原則は:
- `await <Expression>;` → `await <Expression>.ConfigureAwait(false);`
- `await <Expression>).XXX` → `await <Expression>.ConfigureAwait(false)).XXX`（稀）

セミコロン直前または代入/return 文末に `.ConfigureAwait(false)` を付ける。

- [ ] **Step 3: ビルド + テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -3
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~DbContext" --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: ビルドエラー 0、DbContext 関連テスト 0 失敗

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Data/DbContext.cs
git commit -m "refactor: DbContext に ConfigureAwait(false) を一貫適用 (Issue #1287)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: LendingService.cs に ConfigureAwait(false) を適用

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs` (53 awaits)

- [ ] **Step 1: 対象 await を特定**

```bash
grep -n "await " ICCardManager/src/ICCardManager/Services/LendingService.cs | grep -v "ConfigureAwait"
```

- [ ] **Step 2: 各行を Edit で修正**

- [ ] **Step 3: ビルド + LendingService テスト確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -3
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: ビルドエラー 0、失敗 0

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs
git commit -m "refactor: LendingService に ConfigureAwait(false) を一貫適用 (Issue #1287)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: CsvImportService 全 partial に ConfigureAwait(false) を適用

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/CsvImportService.cs` (3 awaits)
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Card.cs` (11 awaits)
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Staff.cs` (11 awaits)
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs` (12 awaits)
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs` (13 awaits)
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.LedgerValidation.cs` (1 await)

- [ ] **Step 1: 各ファイルの await を一括 grep**

```bash
for f in ICCardManager/src/ICCardManager/Services/CsvImportService.cs ICCardManager/src/ICCardManager/Services/Import/*.cs; do
  echo "=== $f ==="
  grep -n "await " "$f" | grep -v "ConfigureAwait"
done
```

- [ ] **Step 2: 各ファイルを順次修正**

ファイルごとに Edit で await 行を修正。全ファイル処理後に次の Step へ。

- [ ] **Step 3: ビルド + CsvImport テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -3
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~CsvImport|FullyQualifiedName~Services.Import" --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: ビルドエラー 0、失敗 0

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/CsvImportService.cs ICCardManager/src/ICCardManager/Services/Import/
git commit -m "refactor: CsvImportService 全 partial に ConfigureAwait(false) を一貫適用 (Issue #1287)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: OperationLogger + BackupService + ReportService + ReportDataBuilder

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/OperationLogger.cs` (13 awaits)
- Modify: `ICCardManager/src/ICCardManager/Services/BackupService.cs` (3 awaits)
- Modify: `ICCardManager/src/ICCardManager/Services/ReportService.cs` (6 awaits)
- Modify: `ICCardManager/src/ICCardManager/Services/ReportDataBuilder.cs` (7 awaits)

- [ ] **Step 1: 各ファイルの await を grep**

```bash
for f in ICCardManager/src/ICCardManager/Services/OperationLogger.cs \
         ICCardManager/src/ICCardManager/Services/BackupService.cs \
         ICCardManager/src/ICCardManager/Services/ReportService.cs \
         ICCardManager/src/ICCardManager/Services/ReportDataBuilder.cs; do
  echo "=== $f ==="
  grep -n "await " "$f" | grep -v "ConfigureAwait"
done
```

- [ ] **Step 2: 各ファイルを順次修正**

- [ ] **Step 3: ビルド + 関連テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -3
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~OperationLog|FullyQualifiedName~Backup|FullyQualifiedName~Report" --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: ビルドエラー 0、失敗 0

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/OperationLogger.cs ICCardManager/src/ICCardManager/Services/BackupService.cs ICCardManager/src/ICCardManager/Services/ReportService.cs ICCardManager/src/ICCardManager/Services/ReportDataBuilder.cs
git commit -m "refactor: OperationLogger/Backup/Report系に ConfigureAwait(false) を適用 (Issue #1287)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: 全体テスト + Phase 2 follow-up Issue 作成

- [ ] **Step 1: 全体テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --nologo --verbosity minimal 2>&1 | tail -3
```
Expected: 失敗 0、合格 3017 件程度（件数変化なし）

- [ ] **Step 2: ConfigureAwait の適用件数確認**

```bash
for f in ICCardManager/src/ICCardManager/Data/DbContext.cs \
         ICCardManager/src/ICCardManager/Services/LendingService.cs \
         ICCardManager/src/ICCardManager/Services/OperationLogger.cs \
         ICCardManager/src/ICCardManager/Services/CsvImportService.cs \
         ICCardManager/src/ICCardManager/Services/Import/*.cs \
         ICCardManager/src/ICCardManager/Services/ReportService.cs \
         ICCardManager/src/ICCardManager/Services/ReportDataBuilder.cs \
         ICCardManager/src/ICCardManager/Services/BackupService.cs; do
  awaits=$(grep -c "await " "$f" 2>/dev/null)
  configs=$(grep -c "ConfigureAwait" "$f" 2>/dev/null)
  echo "$f: $awaits awaits, $configs configured"
done
```
Expected: 各ファイルで `awaits == configured`（全 await に ConfigureAwait が付いている）

- [ ] **Step 3: Phase 2 follow-up Issue を作成**

```bash
gh issue create --title "enhancement: ConfigureAwait(false) Phase 2（残りサービス層）" --body "$(cat <<'EOF'
## 概要
Issue #1287 (PR #XXXX) で Phase 1 として主要 11 ファイルに `ConfigureAwait(false)` を適用した。本 Issue では残りの Service 層に対し同様の適用を行う。

## 対象ファイル（Phase 2）
以下の Service 層ファイルで `await` を含むが ConfigureAwait(false) 未適用のもの:
- `Services/DashboardService.cs`
- `Services/LedgerMergeService.cs`
- `Services/LedgerSplitService.cs`
- `Services/PrintService.cs`
- `Services/SummaryGenerator.cs`
- `Services/LedgerConsistencyChecker.cs`
- `Services/StationMasterService.cs`
- `Services/OperationLogExcelExportService.cs`
- その他 Services/ 配下で `await` を含むもの

## 対象外（UI 依存サービスは個別検討）
- `Services/DialogService.cs`（MessageBox/UI）
- `Services/StaffAuthService.cs`（MessageBox/UI）
- `Services/IToastNotificationService.cs` 実装

## 規約
`.claude/rules/async-configureawait.md` 参照。

## 完了条件
- [ ] 残り Service 層全ファイルで `ConfigureAwait(false)` 適用
- [ ] 既存テスト全 pass
- [ ] CHANGELOG 更新
EOF
)" --label "enhancement,area: service,priority: medium"
```

Expected: Issue URL

- [ ] **Step 4: CHANGELOG 更新**

`ICCardManager/CHANGELOG.md` の [Unreleased] リファクタリング内に追加:

```markdown
- Service 層の async メソッドに `ConfigureAwait(false)` を一貫適用（Phase 1: 11 ファイル、約 130 箇所）。WPF UI スレッドへの不要な継続 dispatch を排除し、性能向上とデッドロック予防を図る。対象: `DbContext`, `LendingService`, `OperationLogger`, `CsvImportService` 全 partial, `BackupService`, `ReportService`, `ReportDataBuilder`。`.editorconfig` で `CA2007` を Service 層のみ suggestion レベルに有効化（ViewModels/Views/tests は無効化）。残りの Service は別 Issue で Phase 2 対応予定（#1287）
```

- [ ] **Step 5: コミット + push + PR 作成**

```bash
git add ICCardManager/CHANGELOG.md docs/superpowers/plans/2026-04-19-issue-1287-configure-await-services.md
git commit -m "docs: CHANGELOG と実装計画を Issue #1287 で更新

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"

git push -u origin feat/issue-1287-configure-await-services

gh pr create --title "refactor: ConfigureAwait(false) を Service 層に一貫適用 Phase 1 (Issue #1287)" --body "$(cat <<'EOF'
## Summary
- 主要 Service 層 11 ファイルの全 `await` に `.ConfigureAwait(false)` を適用（~130 箇所）
- `.editorconfig` で CA2007 を Service 層のみ suggestion レベルに有効化
- `.claude/rules/async-configureawait.md` に規約文書化
- 残り Service 層は Phase 2 の follow-up Issue に分離

## Related
- Partially addresses #1287（Phase 1）
- Follow-up: Issue #XXXX（Phase 2 作成済み）

## 対象ファイル（11 ファイル、~130 await）
| ファイル | await 数 |
|---------|---------|
| `Data/DbContext.cs` | 4 |
| `Services/LendingService.cs` | 53 |
| `Services/OperationLogger.cs` | 13 |
| `Services/CsvImportService.cs` + 5 partial | 51 |
| `Services/ReportService.cs` | 6 |
| `Services/ReportDataBuilder.cs` | 7 |
| `Services/BackupService.cs` | 3 |

## 適用しないスコープ（本 PR）
- UI 依存サービス（DialogService, StaffAuthService）
- Services/ 配下の残りファイル（Phase 2）
- ViewModels / Views（設計上 UI 文脈を維持）
- テストコード（regression detector としての純粋性保持）

## Test plan
- [x] 既存 3017 件のテスト全 pass
- [x] ビルド 0 error
- [ ] 手動テスト: アプリ起動・貸出・返却・CSV インポート・帳票生成で挙動変化がないこと

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL

---

## 手動テスト依頼

1. **アプリ起動・終了**: 正常動作
2. **貸出・返却**: 通常フロー確認
3. **CSV インポート**: Ledger / Detail / Card / Staff インポート
4. **月次帳票生成**: エラーなし
5. **バックアップ/リストア**: 正常動作
6. **共有モード**: UNC パスでも動作

## リスクと対策

| リスク | 対策 |
|-------|-----|
| ConfigureAwait(false) 後に UI スレッド前提のコードがある | 対象は全て DB I/O のみで UI 依存なし（事前確認済み） |
| 付け忘れ | Task 7 Step 2 で適用件数確認 |
| `.editorconfig` の CA2007 設定効果 | ビルドログに CA2007 suggestion が出ることを確認。出なければ設定不備 |
