# Issue #1283: LendingService 分割リファクタ 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `LendAsync` (121行) / `ReturnAsync` (182行) を private ヘルパーへ分割し、各メソッドを 50〜60 行程度の orchestration 層にする。public API は変更しない。

**Architecture:** 外側 `LendAsync`/`ReturnAsync` は「lock → 検証 → DB 操作 → 後処理 → 状態更新」という共通骨格だけを残し、各ステップを private `Async` ヘルパーに委譲する。検証系ヘルパーはタプル `(Entity?, ..., string? ErrorMessage)` を返し、呼び出し側は `ErrorMessage` 非 null で早期 return する。

**Tech Stack:** C# 10 / .NET Framework 4.8 / xUnit / FluentAssertions / Moq

---

## 事前確認

- 作業ブランチ: `refactor/issue-1283-lending-service-split` (main から分岐済み)
- 対象ファイル: `ICCardManager/src/ICCardManager/Services/LendingService.cs`
- 既存テスト: `ICCardManager/tests/ICCardManager.Tests/Services/LendingService*.cs` (合計 ~5800 行、141 テスト pass)
- Build/Test コマンド: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService"`
- `[InternalsVisibleTo("ICCardManager.Tests")]` は `ICCardManager/src/ICCardManager/Properties/AssemblyInfo.cs` に設定済み

---

## File Structure

### 変更するファイル

| パス | 変更内容 |
|-----|--------|
| `ICCardManager/src/ICCardManager/Services/LendingService.cs` | 8 つの private/internal ヘルパーを追加し、LendAsync/ReturnAsync を orchestration に再構成 |
| `ICCardManager/CHANGELOG.md` | 変更点を [Unreleased] セクションに追加 |

### 新規作成するファイル

| パス | 役割 |
|-----|-----|
| `ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceHelperTests.cs` | 抽出した internal ヘルパーのユニットテスト |

### 既存で使える設備

- `ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceTests.cs` 内の `CreateLendingService()` / `_mockCardRepo` / `_mockStaffRepo` / `_mockLedgerRepo` パターン（既存のテストを参考にする）

---

## Task 1: Baseline 確認とブランチ準備

**Files:**
- （参照のみ） `ICCardManager/src/ICCardManager/Services/LendingService.cs`

- [ ] **Step 1: ブランチが正しいことを確認**

```bash
git branch --show-current
```

Expected: `refactor/issue-1283-lending-service-split`

- [ ] **Step 2: 既存 LendingService テストが全件 pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `成功!   -失敗:     0、合格:   141`（件数は変動可）

---

## Task 2: `ValidateLendPreconditionsAsync` を抽出

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs:315-336` (LendAsync 内の検証ブロック)

**責務:** カード存在 / 未貸出 / 職員存在の 3 連続検証。タプル `(Card Card, Staff Staff, string ErrorMessage)` を返す。

- [ ] **Step 1: ヘルパーメソッドを LendAsync の直下に追加する**

`LendingService.cs` の `LendAsync` メソッド終了後（L417 付近）、かつ `ReturnAsync` 開始前に挿入する。

```csharp
/// <summary>
/// 貸出処理の事前検証。カード・貸出状態・職員の存在を順次チェックする。
/// </summary>
/// <returns>(Card, Staff, ErrorMessage)。ErrorMessage が非 null の場合は検証失敗。</returns>
internal async Task<(Card Card, Staff Staff, string ErrorMessage)> ValidateLendPreconditionsAsync(
    string staffIdm, string cardIdm)
{
    var card = await _cardRepository.GetByIdmAsync(cardIdm);
    if (card == null)
    {
        return (null, null, "カードが登録されていません。");
    }

    if (card.IsLent)
    {
        return (card, null, "このカードは既に貸出中です。");
    }

    var staff = await _staffRepository.GetByIdmAsync(staffIdm);
    if (staff == null)
    {
        return (card, null, "職員証が登録されていません。");
    }

    return (card, staff, null);
}
```

- [ ] **Step 2: LendAsync 内の対応ブロックをヘルパー呼び出しに置き換える**

`LendingService.cs` L315-336 の以下のブロック:

```csharp
// カードを取得
var card = await _cardRepository.GetByIdmAsync(cardIdm);
if (card == null)
{
    result.ErrorMessage = "カードが登録されていません。";
    return result;
}

// 貸出中チェック
if (card.IsLent)
{
    result.ErrorMessage = "このカードは既に貸出中です。";
    return result;
}

// 職員を取得
var staff = await _staffRepository.GetByIdmAsync(staffIdm);
if (staff == null)
{
    result.ErrorMessage = "職員証が登録されていません。";
    return result;
}
```

を以下に置き換える:

```csharp
var (card, staff, validationError) = await ValidateLendPreconditionsAsync(staffIdm, cardIdm);
if (validationError != null)
{
    result.ErrorMessage = validationError;
    return result;
}
```

- [ ] **Step 3: 既存テストが pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs
git commit -m "$(cat <<'EOF'
refactor: ValidateLendPreconditionsAsync を抽出 (Issue #1283)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `ResolveInitialBalanceAsync` を抽出

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs:340-352` (LendAsync 内の残高 fallback)

**責務:** `balance` が null の場合に直近 ledger から残高を取得（Issue #656）。

- [ ] **Step 1: ヘルパーメソッドを追加**

`ValidateLendPreconditionsAsync` の直下に追加。

```csharp
/// <summary>
/// Issue #656: カードから残高を読み取れなかった場合、直近の ledger 残高を fallback として使用。
/// </summary>
internal async Task<int> ResolveInitialBalanceAsync(string cardIdm, int? balance)
{
    if (balance.HasValue)
    {
        return balance.Value;
    }

    var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(cardIdm);
    if (latestLedger != null)
    {
        _logger.LogInformation(
            "LendAsync: カード残高を読み取れなかったため、直近の履歴残高を使用: {Balance}円", latestLedger.Balance);
        return latestLedger.Balance;
    }

    return 0;
}
```

- [ ] **Step 2: LendAsync 内の対応ブロックをヘルパー呼び出しに置き換える**

L340-352 の:

```csharp
// Issue #656: カードから残高を読み取れなかった場合、直近の履歴から残高を取得
// READ操作はリトライ範囲の外で実行（不要な再クエリを防止）
var currentBalance = balance ?? 0;
if (!balance.HasValue)
{
    var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(cardIdm);
    if (latestLedger != null)
    {
        currentBalance = latestLedger.Balance;
        _logger.LogInformation(
            "LendAsync: カード残高を読み取れなかったため、直近の履歴残高を使用: {Balance}円", currentBalance);
    }
}
```

を以下に置き換える:

```csharp
// Issue #656: カードから残高を読み取れなかった場合、直近の履歴から残高を取得
// READ操作はリトライ範囲の外で実行（不要な再クエリを防止）
var currentBalance = await ResolveInitialBalanceAsync(cardIdm, balance);
```

- [ ] **Step 3: 既存テストが pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs
git commit -m "$(cat <<'EOF'
refactor: ResolveInitialBalanceAsync を抽出 (Issue #1283)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `InsertLendLedgerAsync` を抽出

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs:354-390` (LendAsync 内のトランザクション)

**責務:** トランザクション内で貸出 ledger 挿入 + カード状態更新。作成された Ledger インスタンスを返す。

- [ ] **Step 1: ヘルパーメソッドを追加**

`ResolveInitialBalanceAsync` の直下に追加。

```csharp
/// <summary>
/// 貸出ledgerレコードを作成し、カードの貸出状態を更新する。
/// 共有モード時のSQLITE_BUSY対策として ExecuteWithRetryAsync でラップ。
/// </summary>
internal async Task<Ledger> InsertLendLedgerAsync(
    string cardIdm, string staffIdm, string staffName, int balance, DateTime now)
{
    Ledger createdLedger = null;

    await _dbContext.ExecuteWithRetryAsync(async () =>
    {
        using var scope = await _dbContext.BeginTransactionAsync();

        try
        {
            var ledger = new Ledger
            {
                CardIdm = cardIdm,
                LenderIdm = staffIdm,
                Date = now,
                Summary = SummaryGenerator.GetLendingSummary(),
                Income = 0,
                Expense = 0,
                Balance = balance,
                StaffName = staffName,
                LentAt = now,
                IsLentRecord = true
            };

            var ledgerId = await _ledgerRepository.InsertAsync(ledger);
            ledger.Id = ledgerId;

            await _cardRepository.UpdateLentStatusAsync(cardIdm, true, now, staffIdm);

            scope.Commit();
            createdLedger = ledger;
        }
        catch
        {
            scope.Rollback();
            throw;
        }
    });

    return createdLedger;
}
```

- [ ] **Step 2: LendAsync 内の対応ブロックをヘルパー呼び出しに置き換える**

L354-390 の `await _dbContext.ExecuteWithRetryAsync(...)` ブロックを以下に置き換える:

```csharp
// トランザクション内で貸出ledger作成 + カード状態更新
// 共有モード時のSQLITE_BUSY対策としてリトライでラップ（WRITE操作のみ）
var ledger = await InsertLendLedgerAsync(cardIdm, staffIdm, staff.Name, currentBalance, now);
result.CreatedLedgers.Add(ledger);
```

- [ ] **Step 3: 既存テストが pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs
git commit -m "$(cat <<'EOF'
refactor: InsertLendLedgerAsync を抽出 (Issue #1283)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `ValidateReturnPreconditionsAsync` を抽出

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs:461-482` (ReturnAsync 内の検証)

**責務:** カード存在 / 貸出中 / 職員存在の 3 連続検証。

- [ ] **Step 1: ヘルパーメソッドを追加**

`InsertLendLedgerAsync` の直下に追加。

```csharp
/// <summary>
/// 返却処理の事前検証。カード・貸出状態・職員の存在を順次チェックする。
/// </summary>
/// <returns>(Card, Returner, ErrorMessage)。ErrorMessage が非 null の場合は検証失敗。</returns>
internal async Task<(Card Card, Staff Returner, string ErrorMessage)> ValidateReturnPreconditionsAsync(
    string staffIdm, string cardIdm)
{
    var card = await _cardRepository.GetByIdmAsync(cardIdm);
    if (card == null)
    {
        return (null, null, "カードが登録されていません。");
    }

    if (!card.IsLent)
    {
        return (card, null, "このカードは貸出されていません。");
    }

    var returner = await _staffRepository.GetByIdmAsync(staffIdm);
    if (returner == null)
    {
        return (card, null, "職員証が登録されていません。");
    }

    return (card, returner, null);
}
```

- [ ] **Step 2: ReturnAsync 内の対応ブロックをヘルパー呼び出しに置き換える**

L461-482 の:

```csharp
// カードを取得
var card = await _cardRepository.GetByIdmAsync(cardIdm);
if (card == null)
{
    result.ErrorMessage = "カードが登録されていません。";
    return result;
}

// 貸出中チェック
if (!card.IsLent)
{
    result.ErrorMessage = "このカードは貸出されていません。";
    return result;
}

// 返却者を取得
var returner = await _staffRepository.GetByIdmAsync(staffIdm);
if (returner == null)
{
    result.ErrorMessage = "職員証が登録されていません。";
    return result;
}
```

を以下に置き換える:

```csharp
var (card, returner, validationError) = await ValidateReturnPreconditionsAsync(staffIdm, cardIdm);
if (validationError != null)
{
    result.ErrorMessage = validationError;
    return result;
}
```

- [ ] **Step 3: 既存テストが pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs
git commit -m "$(cat <<'EOF'
refactor: ValidateReturnPreconditionsAsync を抽出 (Issue #1283)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `ResolveLentRecordAsync` を抽出

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs:484-490`

**責務:** 貸出レコード取得 + null チェック。

- [ ] **Step 1: ヘルパーメソッドを追加**

`ValidateReturnPreconditionsAsync` の直下に追加。

```csharp
/// <summary>
/// 貸出レコードを取得。見つからない場合はエラーメッセージを返す。
/// </summary>
/// <returns>(LentRecord, ErrorMessage)。ErrorMessage が非 null の場合は失敗。</returns>
internal async Task<(Ledger LentRecord, string ErrorMessage)> ResolveLentRecordAsync(string cardIdm)
{
    var lentRecord = await _ledgerRepository.GetLentRecordAsync(cardIdm);
    if (lentRecord == null)
    {
        return (null, "貸出レコードが見つかりません。");
    }
    return (lentRecord, null);
}
```

- [ ] **Step 2: ReturnAsync 内の対応ブロックを置き換える**

L484-490 の:

```csharp
// 貸出レコードを取得
var lentRecord = await _ledgerRepository.GetLentRecordAsync(cardIdm);
if (lentRecord == null)
{
    result.ErrorMessage = "貸出レコードが見つかりません。";
    return result;
}
```

を以下に置き換える:

```csharp
var (lentRecord, lentRecordError) = await ResolveLentRecordAsync(cardIdm);
if (lentRecordError != null)
{
    result.ErrorMessage = lentRecordError;
    return result;
}
```

- [ ] **Step 3: 既存テストが pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs
git commit -m "$(cat <<'EOF'
refactor: ResolveLentRecordAsync を抽出 (Issue #1283)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: `FilterUsageSinceLent` を抽出

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs:497-509`

**責務:** 貸出時刻以降の履歴フィルタリング（貸出タッチ忘れに備えて 7 日前まで遡る）。

- [ ] **Step 1: ヘルパーメソッドを追加**

`ResolveLentRecordAsync` の直下に追加。

```csharp
/// <summary>
/// 貸出日以降の履歴を抽出する。貸出タッチ忘れに備え貸出日の1週間前から遡る。
/// 注意: FeliCa履歴の日付は時刻を含まないため、日付部分のみで比較する。
/// </summary>
internal static List<LedgerDetail> FilterUsageSinceLent(
    List<LedgerDetail> detailList, Ledger lentRecord, DateTime now)
{
    var lentAt = lentRecord.LentAt ?? now.AddDays(-1);
    var lentDate = lentAt.Date;
    var filterStartDate = lentDate.AddDays(-7);
    return detailList
        .Where(d => d.UseDate == null || d.UseDate.Value.Date >= filterStartDate)
        .ToList();
}
```

- [ ] **Step 2: ReturnAsync 内の対応ブロックを置き換える**

L497-509 の:

```csharp
// 貸出タッチを忘れた場合でも履歴が正しく記録されるよう、日付フィルタを緩和
// 重複チェックは CreateUsageLedgersAsync 内の既存履歴照合（Issue #326）で行う
// 注意: FeliCa履歴の日付は時刻を含まないため、日付部分のみで比較する
var lentAt = lentRecord.LentAt ?? now.AddDays(-1);
var lentDate = lentAt.Date;  // 時刻を切り捨てて日付のみにする
// 貸出日の1週間前までの履歴を対象とする（貸出タッチ忘れへの対応）
var filterStartDate = lentDate.AddDays(-7);
var usageSinceLent = detailList
    .Where(d => d.UseDate == null || d.UseDate.Value.Date >= filterStartDate)
    .ToList();

_logger.LogDebug("LendingService: 貸出時刻={LentAt}, フィルタ開始日={FilterStart}, 抽出後の履歴件数={Count}",
    lentAt.ToString("yyyy-MM-dd HH:mm:ss"), filterStartDate.ToString("yyyy-MM-dd"), usageSinceLent.Count);
```

を以下に置き換える:

```csharp
// 貸出タッチを忘れた場合でも履歴が正しく記録されるよう、日付フィルタを緩和
// 重複チェックは CreateUsageLedgersAsync 内の既存履歴照合（Issue #326）で行う
var usageSinceLent = FilterUsageSinceLent(detailList, lentRecord, now);

var lentAt = lentRecord.LentAt ?? now.AddDays(-1);
_logger.LogDebug("LendingService: 貸出時刻={LentAt}, フィルタ開始日={FilterStart}, 抽出後の履歴件数={Count}",
    lentAt.ToString("yyyy-MM-dd HH:mm:ss"), lentAt.Date.AddDays(-7).ToString("yyyy-MM-dd"), usageSinceLent.Count);
```

- [ ] **Step 3: 既存テストが pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs
git commit -m "$(cat <<'EOF'
refactor: FilterUsageSinceLent を抽出 (Issue #1283)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: `ResolveReturnBalanceAsync` を抽出

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs:557-587`

**責務:** 返却時の残高解決（カード読取値 → 作成 ledger → DB 直近 ledger の順でフォールバック）。

- [ ] **Step 1: ヘルパーメソッドを追加**

`FilterUsageSinceLent` の直下に追加。

```csharp
/// <summary>
/// 返却時の残高解決カスケード。
/// 優先順位: (1)カード直接読取値 > (2)作成ledger末尾 > (3)DB 直近 ledger(Issue #1139)。
/// </summary>
internal async Task<int> ResolveReturnBalanceAsync(
    List<LedgerDetail> detailList, List<Ledger> createdLedgers, string cardIdm)
{
    // (1) カードから直接読み取った残高を優先
    var cardBalance = detailList.FirstOrDefault()?.Balance;
    if (cardBalance.HasValue && cardBalance.Value > 0)
    {
        _logger.LogDebug("LendingService: カードから直接読み取った残高を使用: {Balance}円", cardBalance.Value);
        return cardBalance.Value;
    }

    // (2) 作成した ledger の末尾残高
    var latestCreatedLedger = createdLedgers.LastOrDefault();
    if (latestCreatedLedger != null)
    {
        _logger.LogDebug("LendingService: ledgerレコードの残高を使用: {Balance}円", latestCreatedLedger.Balance);
        return latestCreatedLedger.Balance;
    }

    // (3) Issue #1139: DB の直近 ledger 残高にフォールバック
    var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(cardIdm);
    if (latestLedger != null)
    {
        _logger.LogInformation(
            "ReturnAsync: カード残高を読み取れなかったため、直近の履歴残高を使用: {Balance}円", latestLedger.Balance);
        return latestLedger.Balance;
    }

    return 0;
}
```

- [ ] **Step 2: ReturnAsync 内の対応ブロックを置き換える**

L557-587 の大きな if/else ブロックを以下に置き換える:

```csharp
// 残額チェック（トランザクション外）
// カードから直接読み取った残高を優先（履歴の先頭が最新）
// FelicaCardReaderで読み取った場合、各LedgerDetail.Balanceには実際の残高が設定されている
result.Balance = await ResolveReturnBalanceAsync(detailList, result.CreatedLedgers, cardIdm);
```

- [ ] **Step 3: 既存テストが pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs
git commit -m "$(cat <<'EOF'
refactor: ResolveReturnBalanceAsync を抽出 (Issue #1283)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: `CalculateBalanceWarningAsync` を抽出

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs:589-592`

**責務:** `AppSettings.WarningBalance` を取得して result にセット。

- [ ] **Step 1: ヘルパーメソッドを追加**

`ResolveReturnBalanceAsync` の直下に追加。

```csharp
/// <summary>
/// 低残高警告情報を result にセットする。
/// </summary>
internal async Task ApplyBalanceWarningAsync(LendingResult result)
{
    var settings = await _settingsRepository.GetAppSettingsAsync();
    result.WarningBalance = settings.WarningBalance;
    result.IsLowBalance = result.Balance < settings.WarningBalance;
}
```

- [ ] **Step 2: ReturnAsync 内の対応ブロックを置き換える**

L589-592 の:

```csharp
// 低残高チェック
var settings = await _settingsRepository.GetAppSettingsAsync();
result.WarningBalance = settings.WarningBalance;
result.IsLowBalance = result.Balance < settings.WarningBalance;
```

を以下に置き換える:

```csharp
await ApplyBalanceWarningAsync(result);
```

- [ ] **Step 3: 既存テストが pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs
git commit -m "$(cat <<'EOF'
refactor: ApplyBalanceWarningAsync を抽出 (Issue #1283)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: 新規テストファイル `LendingServiceHelperTests.cs` を作成

**Files:**
- Create: `ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceHelperTests.cs`

抽出したヘルパーに対するユニットテストを追加する。既存 `LendingServiceTests.cs` の `CreateLendingService()` ヘルパーと同じモック構築パターンに合わせる。

- [ ] **Step 1: 既存 LendingServiceTests.cs の CreateLendingService パターンを確認**

以下コマンドで既存のモック構築コードを参照:

```bash
head -120 ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceTests.cs
```

Expected: `_mockCardRepo`, `_mockStaffRepo`, `_mockLedgerRepo`, `_mockSettingsRepo`, `SummaryGenerator`, `CardLockManager`, `AppOptions`, `ILogger` が揃った setup コードが見られる

- [ ] **Step 2: 新規テストファイルを作成**

`ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceHelperTests.cs` を以下の内容で作成する。

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services
{
    /// <summary>
    /// Issue #1283: LendAsync/ReturnAsync から抽出した private/internal ヘルパーの単体テスト。
    /// </summary>
    public class LendingServiceHelperTests : IDisposable
    {
        private readonly Mock<ICardRepository> _mockCardRepo = new();
        private readonly Mock<IStaffRepository> _mockStaffRepo = new();
        private readonly Mock<ILedgerRepository> _mockLedgerRepo = new();
        private readonly Mock<ISettingsRepository> _mockSettingsRepo = new();

        private readonly DbContext _dbContext;
        private readonly CardLockManager _lockManager;

        public LendingServiceHelperTests()
        {
            _dbContext = new DbContext(":memory:");
            _dbContext.InitializeDatabase();
            _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        }

        private LendingService CreateService()
        {
            return new LendingService(
                _dbContext,
                _mockCardRepo.Object,
                _mockStaffRepo.Object,
                _mockLedgerRepo.Object,
                _mockSettingsRepo.Object,
                new SummaryGenerator(),
                _lockManager,
                Options.Create(new AppOptions()),
                NullLogger<LendingService>.Instance);
        }

        public void Dispose()
        {
            _lockManager.Dispose();
            _dbContext.Dispose();
            GC.SuppressFinalize(this);
        }

        // ============================================================
        // ValidateLendPreconditionsAsync
        // ============================================================

        [Fact]
        public async Task ValidateLendPreconditionsAsync_CardNotFound_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync((Card)null);

            var service = CreateService();
            var (card, staff, error) = await service.ValidateLendPreconditionsAsync("STAFF01", "CARD01");

            card.Should().BeNull();
            staff.Should().BeNull();
            error.Should().Be("カードが登録されていません。");
        }

        [Fact]
        public async Task ValidateLendPreconditionsAsync_CardAlreadyLent_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new Card { CardIdm = "CARD01", IsLent = true });

            var service = CreateService();
            var (card, staff, error) = await service.ValidateLendPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            staff.Should().BeNull();
            error.Should().Be("このカードは既に貸出中です。");
        }

        [Fact]
        public async Task ValidateLendPreconditionsAsync_StaffNotFound_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new Card { CardIdm = "CARD01", IsLent = false });
            _mockStaffRepo.Setup(r => r.GetByIdmAsync("STAFF01", false))
                .ReturnsAsync((Staff)null);

            var service = CreateService();
            var (card, staff, error) = await service.ValidateLendPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            staff.Should().BeNull();
            error.Should().Be("職員証が登録されていません。");
        }

        [Fact]
        public async Task ValidateLendPreconditionsAsync_AllValid_ReturnsNullError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new Card { CardIdm = "CARD01", IsLent = false });
            _mockStaffRepo.Setup(r => r.GetByIdmAsync("STAFF01", false))
                .ReturnsAsync(new Staff { StaffIdm = "STAFF01", Name = "テスト職員" });

            var service = CreateService();
            var (card, staff, error) = await service.ValidateLendPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            staff.Should().NotBeNull();
            staff.Name.Should().Be("テスト職員");
            error.Should().BeNull();
        }

        // ============================================================
        // ValidateReturnPreconditionsAsync
        // ============================================================

        [Fact]
        public async Task ValidateReturnPreconditionsAsync_CardNotFound_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync((Card)null);

            var service = CreateService();
            var (card, returner, error) = await service.ValidateReturnPreconditionsAsync("STAFF01", "CARD01");

            card.Should().BeNull();
            returner.Should().BeNull();
            error.Should().Be("カードが登録されていません。");
        }

        [Fact]
        public async Task ValidateReturnPreconditionsAsync_NotLent_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new Card { CardIdm = "CARD01", IsLent = false });

            var service = CreateService();
            var (card, returner, error) = await service.ValidateReturnPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            returner.Should().BeNull();
            error.Should().Be("このカードは貸出されていません。");
        }

        [Fact]
        public async Task ValidateReturnPreconditionsAsync_StaffNotFound_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new Card { CardIdm = "CARD01", IsLent = true });
            _mockStaffRepo.Setup(r => r.GetByIdmAsync("STAFF01", false))
                .ReturnsAsync((Staff)null);

            var service = CreateService();
            var (card, returner, error) = await service.ValidateReturnPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            returner.Should().BeNull();
            error.Should().Be("職員証が登録されていません。");
        }

        [Fact]
        public async Task ValidateReturnPreconditionsAsync_AllValid_ReturnsNullError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new Card { CardIdm = "CARD01", IsLent = true });
            _mockStaffRepo.Setup(r => r.GetByIdmAsync("STAFF01", false))
                .ReturnsAsync(new Staff { StaffIdm = "STAFF01", Name = "返却者" });

            var service = CreateService();
            var (card, returner, error) = await service.ValidateReturnPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            returner.Should().NotBeNull();
            returner.Name.Should().Be("返却者");
            error.Should().BeNull();
        }

        // ============================================================
        // ResolveLentRecordAsync
        // ============================================================

        [Fact]
        public async Task ResolveLentRecordAsync_NotFound_ReturnsError()
        {
            _mockLedgerRepo.Setup(r => r.GetLentRecordAsync("CARD01"))
                .ReturnsAsync((Ledger)null);

            var service = CreateService();
            var (lentRecord, error) = await service.ResolveLentRecordAsync("CARD01");

            lentRecord.Should().BeNull();
            error.Should().Be("貸出レコードが見つかりません。");
        }

        [Fact]
        public async Task ResolveLentRecordAsync_Found_ReturnsRecord()
        {
            var record = new Ledger { CardIdm = "CARD01", IsLentRecord = true, StaffName = "職員A" };
            _mockLedgerRepo.Setup(r => r.GetLentRecordAsync("CARD01"))
                .ReturnsAsync(record);

            var service = CreateService();
            var (lentRecord, error) = await service.ResolveLentRecordAsync("CARD01");

            lentRecord.Should().NotBeNull();
            lentRecord.StaffName.Should().Be("職員A");
            error.Should().BeNull();
        }

        // ============================================================
        // ResolveInitialBalanceAsync
        // ============================================================

        [Fact]
        public async Task ResolveInitialBalanceAsync_BalanceProvided_ReturnsGivenValue()
        {
            var service = CreateService();
            var result = await service.ResolveInitialBalanceAsync("CARD01", 1500);
            result.Should().Be(1500);
        }

        [Fact]
        public async Task ResolveInitialBalanceAsync_NullWithLedger_ReturnsLedgerBalance()
        {
            _mockLedgerRepo.Setup(r => r.GetLatestLedgerAsync("CARD01"))
                .ReturnsAsync(new Ledger { Balance = 880 });

            var service = CreateService();
            var result = await service.ResolveInitialBalanceAsync("CARD01", null);
            result.Should().Be(880);
        }

        [Fact]
        public async Task ResolveInitialBalanceAsync_NullWithoutLedger_ReturnsZero()
        {
            _mockLedgerRepo.Setup(r => r.GetLatestLedgerAsync("CARD01"))
                .ReturnsAsync((Ledger)null);

            var service = CreateService();
            var result = await service.ResolveInitialBalanceAsync("CARD01", null);
            result.Should().Be(0);
        }

        // ============================================================
        // FilterUsageSinceLent
        // ============================================================

        [Fact]
        public void FilterUsageSinceLent_DetailsBeforeSevenDays_Excluded()
        {
            var now = new DateTime(2026, 4, 19, 10, 0, 0);
            var lentRecord = new Ledger { LentAt = new DateTime(2026, 4, 15) };
            // フィルタ開始日 = 2026-04-15 - 7日 = 2026-04-08
            var details = new List<LedgerDetail>
            {
                new() { UseDate = new DateTime(2026, 4, 7) },   // 除外
                new() { UseDate = new DateTime(2026, 4, 8) },   // 含まれる（境界値）
                new() { UseDate = new DateTime(2026, 4, 15) },  // 含まれる
                new() { UseDate = new DateTime(2026, 4, 18) },  // 含まれる
            };

            var result = LendingService.FilterUsageSinceLent(details, lentRecord, now);

            result.Should().HaveCount(3);
            result.Should().NotContain(d => d.UseDate == new DateTime(2026, 4, 7));
        }

        [Fact]
        public void FilterUsageSinceLent_NullUseDate_Included()
        {
            var now = new DateTime(2026, 4, 19);
            var lentRecord = new Ledger { LentAt = new DateTime(2026, 4, 15) };
            var details = new List<LedgerDetail>
            {
                new() { UseDate = null },
                new() { UseDate = new DateTime(2026, 4, 1) },  // 除外
            };

            var result = LendingService.FilterUsageSinceLent(details, lentRecord, now);

            result.Should().HaveCount(1);
            result[0].UseDate.Should().BeNull();
        }

        [Fact]
        public void FilterUsageSinceLent_LentAtNull_UsesYesterday()
        {
            var now = new DateTime(2026, 4, 19);
            var lentRecord = new Ledger { LentAt = null };  // fallback: now - 1 day = 2026-04-18
            // フィルタ開始日 = 2026-04-18 - 7 = 2026-04-11
            var details = new List<LedgerDetail>
            {
                new() { UseDate = new DateTime(2026, 4, 10) },  // 除外
                new() { UseDate = new DateTime(2026, 4, 11) },  // 境界値（含む）
            };

            var result = LendingService.FilterUsageSinceLent(details, lentRecord, now);

            result.Should().HaveCount(1);
            result[0].UseDate.Should().Be(new DateTime(2026, 4, 11));
        }

        // ============================================================
        // ResolveReturnBalanceAsync
        // ============================================================

        [Fact]
        public async Task ResolveReturnBalanceAsync_CardBalancePresent_ReturnsCardBalance()
        {
            var details = new List<LedgerDetail>
            {
                new() { Balance = 2500 },  // 先頭=最新
                new() { Balance = 3000 },
            };
            var createdLedgers = new List<Ledger> { new() { Balance = 999 } };

            var service = CreateService();
            var result = await service.ResolveReturnBalanceAsync(details, createdLedgers, "CARD01");

            result.Should().Be(2500);
        }

        [Fact]
        public async Task ResolveReturnBalanceAsync_NoCardBalance_ReturnsLedgerBalance()
        {
            var details = new List<LedgerDetail>
            {
                new() { Balance = null }
            };
            var createdLedgers = new List<Ledger>
            {
                new() { Balance = 100 },
                new() { Balance = 200 }  // 末尾
            };

            var service = CreateService();
            var result = await service.ResolveReturnBalanceAsync(details, createdLedgers, "CARD01");

            result.Should().Be(200);
        }

        [Fact]
        public async Task ResolveReturnBalanceAsync_NoDetailNoLedger_UsesDbFallback()
        {
            _mockLedgerRepo.Setup(r => r.GetLatestLedgerAsync("CARD01"))
                .ReturnsAsync(new Ledger { Balance = 777 });

            var service = CreateService();
            var result = await service.ResolveReturnBalanceAsync(
                new List<LedgerDetail>(), new List<Ledger>(), "CARD01");

            result.Should().Be(777);
        }

        [Fact]
        public async Task ResolveReturnBalanceAsync_NoDetailNoLedgerNoDb_ReturnsZero()
        {
            _mockLedgerRepo.Setup(r => r.GetLatestLedgerAsync("CARD01"))
                .ReturnsAsync((Ledger)null);

            var service = CreateService();
            var result = await service.ResolveReturnBalanceAsync(
                new List<LedgerDetail>(), new List<Ledger>(), "CARD01");

            result.Should().Be(0);
        }

        // ============================================================
        // ApplyBalanceWarningAsync
        // ============================================================

        [Fact]
        public async Task ApplyBalanceWarningAsync_BalanceBelowThreshold_SetsIsLowBalance()
        {
            _mockSettingsRepo.Setup(r => r.GetAppSettingsAsync())
                .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

            var service = CreateService();
            var result = new LendingResult { Balance = 500 };
            await service.ApplyBalanceWarningAsync(result);

            result.WarningBalance.Should().Be(1000);
            result.IsLowBalance.Should().BeTrue();
        }

        [Fact]
        public async Task ApplyBalanceWarningAsync_BalanceAboveThreshold_NotLow()
        {
            _mockSettingsRepo.Setup(r => r.GetAppSettingsAsync())
                .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

            var service = CreateService();
            var result = new LendingResult { Balance = 1500 };
            await service.ApplyBalanceWarningAsync(result);

            result.IsLowBalance.Should().BeFalse();
        }

        [Fact]
        public async Task ApplyBalanceWarningAsync_BalanceEqualThreshold_NotLow()
        {
            _mockSettingsRepo.Setup(r => r.GetAppSettingsAsync())
                .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

            var service = CreateService();
            var result = new LendingResult { Balance = 1000 };
            await service.ApplyBalanceWarningAsync(result);

            result.IsLowBalance.Should().BeFalse();
        }
    }
}
```

- [ ] **Step 3: 新規テストが全件 pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingServiceHelperTests" --nologo --verbosity minimal
```

Expected: `失敗: 0`、追加した 20 件前後のテストが pass

- [ ] **Step 4: 全 LendingService 系テストが pass することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 5: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceHelperTests.cs
git commit -m "$(cat <<'EOF'
test: LendingService ヘルパーメソッドのユニットテストを追加 (Issue #1283)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: 行数目標の検証と全テスト実行

**Files:**
- （確認のみ）

- [ ] **Step 1: LendAsync / ReturnAsync の行数を確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project ICCardManager/tools/...  # 使えない
```

のかわりに Grep で位置を特定:

```bash
grep -n "public async Task<LendingResult> LendAsync" ICCardManager/src/ICCardManager/Services/LendingService.cs
grep -n "public async Task<LendingResult> ReturnAsync" ICCardManager/src/ICCardManager/Services/LendingService.cs
```

Expected: 両メソッドが ~50〜80 行に収まっている（メソッド定義 + 閉じ波括弧の行番号の差で確認）

- [ ] **Step 2: ソリューション全体ビルド**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal
```

Expected: `ビルドに成功しました。` エラー 0

- [ ] **Step 3: ソリューション全体テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --nologo --verbosity minimal
```

Expected: `失敗: 0`

- [ ] **Step 4: 問題なければ次タスクへ。失敗すれば内容を精査して修正**

---

## Task 12: CHANGELOG 更新

**Files:**
- Modify: `ICCardManager/CHANGELOG.md` の [Unreleased] セクション

- [ ] **Step 1: 現在の CHANGELOG の [Unreleased] セクション位置を確認**

```bash
grep -n "## \[Unreleased\]" ICCardManager/CHANGELOG.md
```

- [ ] **Step 2: [Unreleased] 下の `### Changed` カテゴリに 1 行追加**

既存の `### Changed` セクションがあればその下、なければ新規に `### Changed` を作成し、以下を追加する:

```markdown
- `LendingService.LendAsync` / `ReturnAsync` を private ヘルパーメソッドに責務分割（Issue #1283）。public API は不変。
```

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/CHANGELOG.md
git commit -m "$(cat <<'EOF'
docs: CHANGELOG に Issue #1283 リファクタを記載

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 13: Push と PR 作成

**Files:**
- （git 操作のみ）

- [ ] **Step 1: ブランチを push**

```bash
git push -u origin refactor/issue-1283-lending-service-split
```

- [ ] **Step 2: PR 作成**

```bash
gh pr create --title "refactor: LendingService の LendAsync/ReturnAsync を責務分割 (Issue #1283)" --body "$(cat <<'EOF'
## Summary
- `LendAsync` (121行) と `ReturnAsync` (182行) を 8 つの private/internal ヘルパーに分割
- public API は一切変更せず、外部からの呼び出しに影響なし
- 抽出したヘルパーに対して `LendingServiceHelperTests` を追加

## Related
- Closes #1283

## Test plan
- [ ] `LendingService*Tests` が全件 pass
- [ ] 新規 `LendingServiceHelperTests` が pass
- [ ] ソリューション全体ビルド成功
- [ ] 手動テスト: 実機で貸出 → 返却の golden path が動作すること（画面操作）

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL が出力される

---

## 手動テスト依頼（コード変更完了後、ユーザーへ依頼）

以下は単体テストでカバーしきれない統合動作の確認のため、実機またはローカル起動で確認してほしい項目:

1. **貸出 golden path**: アプリ起動 → 職員証タッチ → 未貸出カードタッチ → 「貸出完了」表示 + 薄いオレンジ背景 + 「ピッ」
2. **返却 golden path**: 職員証タッチ → 貸出中カードタッチ → 「返却完了」表示 + 薄い水色背景 + 「ピピッ」 + 残額表示
3. **エラーケース**:
   - 未登録カード → 「カードが登録されていません。」
   - 未登録職員証 → 「職員証が登録されていません。」
   - 貸出中のカードを再貸出 → 「このカードは既に貸出中です。」
4. **30 秒ルール**: 貸出直後に同じカードを再タッチ → 返却処理に切り替わる
5. **残高警告**: 設定画面で警告残額を調整し、返却時に警告 UI が出るか
6. **共有モード**: UNC パス指定で 2 台並行で貸出しても衝突しない

---

## リスクと対策

| リスク | 対策 |
|-------|-----|
| ValidateXxx がシグネチャ変更で呼び出し側を壊す | public API は変更しないので発生しない。internal 抽出のみ |
| ヘルパーテストが DbContext 初期化で失敗 | `DbContext(":memory:") + InitializeDatabase()` を既存 `LendingServiceTests` と同じ方式で使用 |
| 既存テスト件数が減る | Task 11 Step 3 で全件数を確認し、ベースラインから大きく減っていないか目視 |
