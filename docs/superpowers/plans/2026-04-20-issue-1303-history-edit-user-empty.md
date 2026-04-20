# Issue #1303: 履歴編集で利用者欄が空欄になる問題 — 実装プラン

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 返却時の利用 Ledger 生成で `LenderIdm` を正しく設定し、編集ダイアログでは既存データの `LenderIdm = null` 行も `StaffName` でフォールバック選択できるようにする。

**Architecture:** 2 段構え:
1. `LendingService.CreateUsageLedgersAsync` のシグネチャに `staffIdm` を追加し、生成する全利用 Ledger に `LenderIdm` を設定（根本原因修正）
2. `LedgerRowEditViewModel.InitializeForEditAsync` で `LenderIdm` 一致が無ければ `StaffName` で再照合（既存データ救済）

**Tech Stack:** C# / .NET Framework 4.8 / WPF (MVVM Toolkit) / xUnit + FluentAssertions + Moq / SQLite

**Reference:** 設計書 `docs/superpowers/specs/2026-04-20-issue-1303-history-edit-user-empty-design.md`

**Working dir:** プロジェクトルート (`/mnt/d/OneDrive/交通系/src`)。 dotnet コマンドは `"/mnt/c/Program Files/dotnet/dotnet.exe"` を使用。

---

## File Map

| ファイル | 種別 | 役割 |
|---|---|---|
| `ICCardManager/src/ICCardManager/Services/LendingService.cs` | Modify | `CreateUsageLedgersAsync` シグネチャ変更・3 箇所で `LenderIdm` セット・呼び出し元 2 箇所修正 |
| `ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs` | Modify | `InitializeForEditAsync` に `StaffName` フォールバック追加 |
| `ICCardManager/tests/ICCardManager.Tests/ViewModels/LedgerRowEditViewModelTests.cs` | Modify | 4 ケース追加 |
| `ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceTests.cs` | Modify | 2 ケース追加 |
| `ICCardManager/docs/design/02_DB設計書.md` | Modify | `lender_idm` の説明強化 |
| `ICCardManager/docs/design/07_テスト設計書.md` | Modify | 追加テストケース反映 |
| `ICCardManager/CHANGELOG.md` | Modify | Unreleased セクションに修正記載 |

---

## Task 1: LedgerRowEditViewModel に StaffName フォールバックを追加

**Files:**
- Modify: `ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs:280-283`
- Test: `ICCardManager/tests/ICCardManager.Tests/ViewModels/LedgerRowEditViewModelTests.cs`

このタスクでまずダイアログ側のフォールバックを実装する（既存データへの即効救済）。

- [ ] **Step 1: 失敗するテストを書く**

`ICCardManager/tests/ICCardManager.Tests/ViewModels/LedgerRowEditViewModelTests.cs` の `#region Editモード初期化` セクション末尾（既存 `InitializeForEdit_SetsEditMode` テストの直後）に以下 4 ケースを追加:

```csharp
[Fact]
public async Task InitializeForEdit_LenderIdmNullButStaffNameMatches_SelectsByName()
{
    // Arrange: 旧バグで作成された LenderIdm=null 行（StaffName のみ）
    var ledger = new Ledger
    {
        Id = 1, CardIdm = TestCardIdm,
        Date = new DateTime(2026, 4, 17),
        Summary = "鉄道（薬院～博多 往復）",
        Income = 0, Expense = 420, Balance = 596,
        LenderIdm = null,             // バグで未設定
        StaffName = _staffA.Name,     // スナップショットには残っている
        Note = string.Empty
    };
    _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(ledger);
    var dto = new LedgerDto
    {
        Id = 1, CardIdm = TestCardIdm,
        Date = ledger.Date, DateDisplay = "R8.4.17",
        Summary = ledger.Summary,
        Income = 0, Expense = 420, Balance = 596,
        StaffName = _staffA.Name
    };

    // Act
    await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

    // Assert: 氏名フォールバックで職員 A が選択される
    _viewModel.SelectedStaff.Should().NotBeNull();
    _viewModel.SelectedStaff!.StaffIdm.Should().Be(_staffA.StaffIdm);
}

[Fact]
public async Task InitializeForEdit_LenderIdmNullAndStaffNameNull_LeavesSelectedStaffNull()
{
    // Arrange: チャージ等、利用者情報が無い行
    var ledger = new Ledger
    {
        Id = 2, CardIdm = TestCardIdm,
        Date = new DateTime(2026, 4, 17),
        Summary = "役務費によりチャージ",
        Income = 1000, Expense = 0, Balance = 2000,
        LenderIdm = null,
        StaffName = null,
        Note = string.Empty
    };
    _ledgerRepoMock.Setup(r => r.GetByIdAsync(2)).ReturnsAsync(ledger);
    var dto = new LedgerDto
    {
        Id = 2, CardIdm = TestCardIdm,
        Date = ledger.Date, DateDisplay = "R8.4.17",
        Summary = ledger.Summary,
        Income = 1000, Expense = 0, Balance = 2000
    };

    // Act
    await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

    // Assert
    _viewModel.SelectedStaff.Should().BeNull();
}

[Fact]
public async Task InitializeForEdit_LenderIdmNotInListButStaffNameMatches_FallsBackByName()
{
    // Arrange: 論理削除された職員等、IDm が一致しないケース
    var ledger = new Ledger
    {
        Id = 3, CardIdm = TestCardIdm,
        Date = new DateTime(2026, 4, 17),
        Summary = "鉄道（薬院～博多）",
        Income = 0, Expense = 210, Balance = 800,
        LenderIdm = "DDDD000000000099",  // StaffList に存在しない IDm
        StaffName = _staffA.Name,         // 同名のアクティブ職員 A は存在
        Note = string.Empty
    };
    _ledgerRepoMock.Setup(r => r.GetByIdAsync(3)).ReturnsAsync(ledger);
    var dto = new LedgerDto
    {
        Id = 3, CardIdm = TestCardIdm,
        Date = ledger.Date, DateDisplay = "R8.4.17",
        Summary = ledger.Summary,
        Income = 0, Expense = 210, Balance = 800,
        StaffName = _staffA.Name
    };

    // Act
    await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

    // Assert: 物品出納簿は氏名表示のため同名であれば許容（設計書 Part 2 参照）
    _viewModel.SelectedStaff.Should().NotBeNull();
    _viewModel.SelectedStaff!.StaffIdm.Should().Be(_staffA.StaffIdm);
}

[Fact]
public async Task InitializeForEdit_LenderIdmNullAndStaffNameNotInList_LeavesSelectedStaffNull()
{
    // Arrange: 該当氏名の職員がリストに無い
    var ledger = new Ledger
    {
        Id = 4, CardIdm = TestCardIdm,
        Date = new DateTime(2026, 4, 17),
        Summary = "鉄道（博多～天神）",
        Income = 0, Expense = 210, Balance = 800,
        LenderIdm = null,
        StaffName = "存在しない人物",
        Note = string.Empty
    };
    _ledgerRepoMock.Setup(r => r.GetByIdAsync(4)).ReturnsAsync(ledger);
    var dto = new LedgerDto
    {
        Id = 4, CardIdm = TestCardIdm,
        Date = ledger.Date, DateDisplay = "R8.4.17",
        Summary = ledger.Summary,
        Income = 0, Expense = 210, Balance = 800,
        StaffName = "存在しない人物"
    };

    // Act
    await _viewModel.InitializeForEditAsync(dto, TestOperatorIdm);

    // Assert
    _viewModel.SelectedStaff.Should().BeNull();
}
```

- [ ] **Step 2: テストが失敗することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LedgerRowEditViewModelTests.InitializeForEdit_LenderIdm" -v minimal
```

期待: 4 件中、`LenderIdmNullAndStaffNameNull_LeavesSelectedStaffNull` と `LenderIdmNullAndStaffNameNotInList_LeavesSelectedStaffNull` は PASS（既存挙動で偶然通る）、他 2 件 FAIL（`SelectedStaff` が null になる）。

- [ ] **Step 3: 最小実装**

`ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs:280-283` を以下に置換:

```csharp
            await LoadStaffListAsync();

            // 現在の利用者を選択（Issue #1303）
            // 1) まず LenderIdm 完全一致を試行
            // 2) 一致が無い場合は StaffName でフォールバック
            //    - 旧バグ（LenderIdm 未設定）由来の行を救済
            //    - 論理削除等で LenderIdm が一致しない場合も同名アクティブ職員に紐づける
            //    - 同名別職員を選んでしまう可能性はあるが、物品出納簿は氏名表示のみで区別不可のため許容
            SelectedStaff = StaffList.FirstOrDefault(s => s.StaffIdm == ledger.LenderIdm);
            if (SelectedStaff == null && !string.IsNullOrEmpty(ledger.StaffName))
            {
                SelectedStaff = StaffList.FirstOrDefault(s => s.Name == ledger.StaffName);
            }
```

- [ ] **Step 4: テストが通ることを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LedgerRowEditViewModelTests" -v minimal
```

期待: 全件 PASS。

- [ ] **Step 5: コミット**

```bash
git add ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs ICCardManager/tests/ICCardManager.Tests/ViewModels/LedgerRowEditViewModelTests.cs
git commit -m "fix: 履歴編集ダイアログで LenderIdm 不一致時に StaffName フォールバック (Issue #1303 Part 2)"
```

---

## Task 2: LendingService の利用 Ledger 生成で LenderIdm を設定

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/LendingService.cs:751-1116`
- Test: `ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceTests.cs`

このタスクで根本原因を修正。新規返却から作成される行は `LenderIdm` が常にセットされるようになる。

- [ ] **Step 1: 失敗するテストを書く**

`ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceTests.cs` の `ReturnAsync_WithCharge_CreatesChargeLedger` の直後に以下 2 ケース追加。

まずファイル先頭の using 句に `using System.Collections.Generic;`（既存）と `using System.Linq;`（既存）があることを確認。コンストラクタ部分は既存のまま使用。

```csharp
/// <summary>
/// Issue #1303: 返却時に作成される利用 Ledger に LenderIdm が設定されることを確認
/// </summary>
[Fact]
public async Task ReturnAsync_WithRailwayUsage_SetsLenderIdmOnUsageLedger()
{
    // Arrange
    var card = CreateTestCard(isLent: true);
    var staff = CreateTestStaff();
    var lentRecord = CreateTestLentRecord();
    var usageDetails = new List<LedgerDetail>
    {
        new()
        {
            UseDate = DateTime.Now,
            EntryStation = "0001",
            ExitStation = "0002",
            Amount = 210,
            Balance = 1790,
            IsBus = false,
            IsCharge = false,
            IsPointRedemption = false
        }
    };

    SetupReturnMocks(card, staff, lentRecord);

    // 挿入された Ledger を捕捉
    var insertedLedgers = new List<Ledger>();
    _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
        .ReturnsAsync((Ledger l) => { insertedLedgers.Add(l); return insertedLedgers.Count; });

    // Act
    var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

    // Assert
    result.Success.Should().BeTrue();
    var usageLedger = insertedLedgers.FirstOrDefault(l => l.Expense > 0);
    usageLedger.Should().NotBeNull("利用 Ledger が作成されるはず");
    usageLedger!.LenderIdm.Should().Be(TestStaffIdm, "Issue #1303: 利用 Ledger に LenderIdm が設定されること");
    usageLedger.StaffName.Should().Be(TestStaffName);
}

/// <summary>
/// Issue #1303: 残高不足パターンで作成される統合 Ledger にも LenderIdm が設定されることを確認
/// </summary>
[Fact]
public async Task ReturnAsync_InsufficientBalancePattern_SetsLenderIdmOnMergedLedger()
{
    // Arrange: 残高不足→チャージ→利用 のパターン
    // DetectInsufficientBalancePattern が検出する条件: 同日内・チャージ→利用 が連続・利用額>残高
    var card = CreateTestCard(isLent: true);
    var staff = CreateTestStaff();
    var lentRecord = CreateTestLentRecord();
    var now = DateTime.Now;
    var usageDetails = new List<LedgerDetail>
    {
        new()
        {
            UseDate = now.AddMinutes(-1),
            IsCharge = true,
            Amount = 1000,
            Balance = 1100  // チャージ後の残高
        },
        new()
        {
            UseDate = now,
            EntryStation = "0001",
            ExitStation = "0002",
            Amount = 1500,  // 残高 1100 < 利用 1500 → 残高不足パターン
            Balance = 0,
            IsCharge = false
        }
    };

    SetupReturnMocks(card, staff, lentRecord);
    var insertedLedgers = new List<Ledger>();
    _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
        .ReturnsAsync((Ledger l) => { insertedLedgers.Add(l); return insertedLedgers.Count; });

    // Act
    var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, usageDetails);

    // Assert
    result.Success.Should().BeTrue();
    // 残高不足統合 Ledger は Note が「現金で支払」を含む
    var mergedLedger = insertedLedgers.FirstOrDefault(l => !string.IsNullOrEmpty(l.Note) && l.Note.Contains("現金で支払"));
    mergedLedger.Should().NotBeNull("残高不足統合 Ledger が作成されるはず");
    mergedLedger!.LenderIdm.Should().Be(TestStaffIdm, "Issue #1303: 残高不足統合 Ledger にも LenderIdm が設定されること");
    mergedLedger.StaffName.Should().Be(TestStaffName);
}
```

- [ ] **Step 2: テストが失敗することを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingServiceTests.ReturnAsync_WithRailwayUsage_SetsLenderIdm|FullyQualifiedName~LendingServiceTests.ReturnAsync_InsufficientBalancePattern_SetsLenderIdm" -v minimal
```

期待: 2 件とも FAIL（`LenderIdm` が null）。

- [ ] **Step 3: `CreateUsageLedgersAsync` シグネチャに `staffIdm` を追加**

`ICCardManager/src/ICCardManager/Services/LendingService.cs:751-752` の現状:

```csharp
private async Task<List<Ledger>> CreateUsageLedgersAsync(
    string cardIdm, string staffName, List<LedgerDetail> details, bool skipDuplicateCheck = false)
```

を以下に変更:

```csharp
private async Task<List<Ledger>> CreateUsageLedgersAsync(
    string cardIdm, string staffIdm, string staffName, List<LedgerDetail> details, bool skipDuplicateCheck = false)
```

- [ ] **Step 4: 残高不足マージ Ledger に LenderIdm を追加**

`LendingService.cs:850-860` の `mergedLedger` 初期化を以下に変更:

```csharp
                    var mergedLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = usage.UseDate ?? date,
                        Summary = summary,
                        Income = 0,
                        Expense = expense,   // 運賃 - チャージ額（カードから充当した金額）
                        Balance = usage.Balance ?? 0,  // 利用後の実残高（端数チャージの場合は端数が残る）
                        LenderIdm = staffIdm,  // Issue #1303
                        StaffName = staffName,
                        Note = note
                    };
```

- [ ] **Step 5: 通常の利用 Ledger に LenderIdm を追加**

`LendingService.cs:1107-1116` の `usageLedger` 初期化を以下に変更:

```csharp
                            var usageLedger = new Ledger
                            {
                                CardIdm = cardIdm,
                                Date = usageDetails.FirstOrDefault()?.UseDate ?? date,
                                Summary = summary,
                                Income = 0,
                                Expense = expense,
                                Balance = balance,
                                // Issue #1303: ポイント還元のみの場合は機械操作扱いで LenderIdm/StaffName とも null
                                LenderIdm = usageDetails.All(d => d.IsPointRedemption) ? null : staffIdm,
                                StaffName = usageDetails.All(d => d.IsPointRedemption) ? null : staffName
                            };
```

- [ ] **Step 6: 既存利用 Ledger 統合時の LenderIdm 補正**

`LendingService.cs:1059-1062` の StaffName 補正部分を、LenderIdm も同様に補正するよう変更:

```csharp
                            // 5. 既存レコードを更新
                            fullLedger.Summary = summary;
                            fullLedger.Expense = expense;
                            fullLedger.Balance = balance;
                            // Issue #1303: 既存レコードの利用者情報が欠落していれば現在のタッチ者で補完
                            if (fullLedger.StaffName == null && staffName != null)
                            {
                                fullLedger.StaffName = staffName;
                            }
                            if (string.IsNullOrEmpty(fullLedger.LenderIdm) && !string.IsNullOrEmpty(staffIdm))
                            {
                                fullLedger.LenderIdm = staffIdm;
                            }
                            await _ledgerRepository.UpdateAsync(fullLedger).ConfigureAwait(false);
```

- [ ] **Step 7: 呼び出し元を更新**

`LendingService.cs:492-493` の `PersistReturnAsync` 内呼び出し:

変更前:

```csharp
                    var createdLedgers = await CreateUsageLedgersAsync(
                        cardIdm, lentRecord.StaffName ?? string.Empty, usageSinceLent, skipDuplicateCheck).ConfigureAwait(false);
```

変更後:

```csharp
                    var createdLedgers = await CreateUsageLedgersAsync(
                        cardIdm, lentRecord.LenderIdm, lentRecord.StaffName ?? string.Empty, usageSinceLent, skipDuplicateCheck).ConfigureAwait(false);
```

`LendingService.cs:1255-1256` の `RegisterCardWithUsageAsync` 内呼び出し:

変更前:

```csharp
                    // 既存のCreateUsageLedgersAsyncを利用（staffNameはnull: 登録時には利用者情報がないため）
                    var createdLedgers = await CreateUsageLedgersAsync(cardIdm, null, filtered).ConfigureAwait(false);
```

変更後:

```csharp
                    // 既存のCreateUsageLedgersAsyncを利用（staffIdm/staffNameはnull: 登録時には利用者情報がないため）
                    var createdLedgers = await CreateUsageLedgersAsync(cardIdm, null, null, filtered).ConfigureAwait(false);
```

- [ ] **Step 8: テストが通ることを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~LendingService" -v minimal
```

期待: 既存テスト + 新規 2 件全て PASS。回帰なし。

- [ ] **Step 9: 全体ビルドとテストの確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj -v minimal
```

期待: ビルド成功、テスト全件 PASS。

- [ ] **Step 10: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/LendingService.cs ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceTests.cs
git commit -m "fix: 返却時の利用 Ledger に LenderIdm を設定 (Issue #1303 Part 1)"
```

---

## Task 3: ドキュメント更新

**Files:**
- Modify: `ICCardManager/docs/design/02_DB設計書.md`
- Modify: `ICCardManager/docs/design/07_テスト設計書.md`
- Modify: `ICCardManager/CHANGELOG.md`

- [ ] **Step 1: DB 設計書の `lender_idm` 説明を強化**

`ICCardManager/docs/design/02_DB設計書.md` の line 217 付近の `lender_idm` 行説明を確認し、説明列に「**返却時に作成される利用 Ledger でも必須（Issue #1303）**」を追記。具体的には以下のように変更:

変更前:

```markdown
| lender_idm | TEXT | YES | NULL | 貸出者IDm(FK→staff) |
```

変更後:

```markdown
| lender_idm | TEXT | YES | NULL | 貸出者IDm(FK→staff)。貸出時・返却時の利用 Ledger 双方で設定（Issue #1303）。チャージ・払戻し・ポイント還元等の機械操作 Ledger では NULL |
```

- [ ] **Step 2: テスト設計書を更新**

`ICCardManager/docs/design/07_テスト設計書.md` を開き、`LedgerRowEditViewModelTests` および `LendingServiceTests` のテストケース一覧セクションを探す（`grep -n "LedgerRowEditViewModelTests\|LendingServiceTests" ICCardManager/docs/design/07_テスト設計書.md` で位置を特定）。該当セクションに以下のテストケースを追加（テーブル形式の場合は対応する列で記載）:

- `InitializeForEdit_LenderIdmNullButStaffNameMatches_SelectsByName` — 旧バグ救済: LenderIdm=null & StaffName 一致 → 氏名フォールバックで選択
- `InitializeForEdit_LenderIdmNullAndStaffNameNull_LeavesSelectedStaffNull` — 利用者情報無し → SelectedStaff null
- `InitializeForEdit_LenderIdmNotInListButStaffNameMatches_FallsBackByName` — IDm 不一致 & StaffName 一致 → 氏名フォールバック
- `InitializeForEdit_LenderIdmNullAndStaffNameNotInList_LeavesSelectedStaffNull` — 該当氏名無し → SelectedStaff null
- `ReturnAsync_WithRailwayUsage_SetsLenderIdmOnUsageLedger` — 利用 Ledger に LenderIdm 設定
- `ReturnAsync_InsufficientBalancePattern_SetsLenderIdmOnMergedLedger` — 残高不足統合 Ledger にも LenderIdm 設定

セクションが見つからない場合は、近い構造のセクション（例: 既存の `LedgerRowEditViewModelTests` 関連項目）を参考に整合する形式で追加。

- [ ] **Step 3: CHANGELOG 更新**

`ICCardManager/CHANGELOG.md` の `### Unreleased` 内の `**バグ修正**` セクション末尾（既存 `#1370` の項目の直後）に、以下の項目を追加:

```markdown
- 履歴画面の「変更」ボタンを押した直後の編集ダイアログで利用者欄が空欄になる問題を修正。返却時に作成される利用 Ledger（鉄道・バス・残高不足統合）で `LenderIdm` カラムが設定されておらず（`StaffName` のみ設定）、編集ダイアログが `s.StaffIdm == ledger.LenderIdm` で職員照合していたため一致せず空欄表示となっていた。さらに `SelectedStaff = null` のまま備考等を修正して保存すると `StaffName` まで null で上書きされ、スナップショット情報も失われる二次被害があった。(1) `LendingService.CreateUsageLedgersAsync` のシグネチャに `staffIdm` を追加し、生成する全利用 Ledger（残高不足マージ・通常利用・既存統合）で `LenderIdm` を設定。呼び出し元 `PersistReturnAsync` は `lentRecord.LenderIdm` を、`RegisterCardWithUsageAsync` は `null` を渡す（カード登録時は利用者情報なしの既存仕様維持）。(2) 既存データの `LenderIdm = NULL` 行救済として `LedgerRowEditViewModel.InitializeForEditAsync` に `StaffName` フォールバックを追加。LenderIdm 一致が無い場合は同名アクティブ職員を選択（同名別職員を選ぶリスクはあるが物品出納簿は氏名表示のみで区別不可のため許容）。回帰防止として `LedgerRowEditViewModelTests` に 4 件、`LendingServiceTests` に 2 件のテストを追加（#1303）
```

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/docs/design/02_DB設計書.md ICCardManager/docs/design/07_テスト設計書.md ICCardManager/CHANGELOG.md
git commit -m "docs: Issue #1303 修正に伴うDB設計書・テスト設計書・CHANGELOG更新"
```

---

## Task 4: 最終ビルド・全テスト実行・PR 作成

**Files:** N/A（検証のみ）

- [ ] **Step 1: 全体ビルド**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln 2>&1 | tail -20
```

期待: `Build succeeded` でエラー 0、警告は既存ベースライン以下。

- [ ] **Step 2: 全テスト実行**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj 2>&1 | tail -10
```

期待: `Passed` のみ、`Failed: 0`。

- [ ] **Step 3: PR 作成**

```bash
git push -u origin fix/issue-1303-history-edit-user-empty
gh pr create --title "fix: 履歴編集で利用者欄が空欄になる問題を修正 (Issue #1303)" --body "$(cat <<'EOF'
## Summary
- 返却時に作成される利用 Ledger（鉄道・バス・残高不足統合）で `LenderIdm` が未設定だったため、編集ダイアログを開くと利用者欄が空欄になる問題を修正
- `LendingService.CreateUsageLedgersAsync` のシグネチャに `staffIdm` を追加し、生成する全利用 Ledger に `LenderIdm` を設定
- 既存データ（`lender_idm = NULL` 行）救済として、編集ダイアログ初期化時に `StaffName` フォールバック照合を追加

Closes #1303

## Test plan
- [ ] `LedgerRowEditViewModelTests` 既存 + 新規 4 件すべて PASS
- [ ] `LendingServiceTests` 既存 + 新規 2 件すべて PASS
- [ ] 全テスト Pass
- [ ] 手動確認: 履歴画面で利用者氏名が表示されている行の「変更」ボタンを押し、編集ダイアログで利用者欄が正しく選択されていること
- [ ] 手動確認: 新規貸出→利用→返却タッチ後に作成された行を編集ダイアログで開き、利用者欄が貸出者名で選択されていること
- [ ] 手動確認: 利用者欄を変更せず備考だけ修正して保存し、再度開いた時に利用者欄が保持されていること

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

期待: PR が作成されURL が返る。

---

## Self-Review (実施済み)

- **Spec coverage**: Part 1 (LendingService 修正) → Task 2、Part 2 (Dialog フォールバック) → Task 1、Part 3 (テスト) → Task 1 + Task 2 内、ドキュメント更新 → Task 3、最終確認 → Task 4。全項目カバー。
- **Placeholder scan**: TODO/TBD なし。全コードブロックは具体的な実装内容を記載。
- **Type consistency**: `staffIdm` の型は `string`、`StaffName` は `string`、`LenderIdm` は `string` で一貫。`null` 許容の扱いは `string.IsNullOrEmpty` で統一。
- **テスト設計書の位置**: Step 2 で grep で位置特定する手順を入れることでファイル構造への依存を緩和。
