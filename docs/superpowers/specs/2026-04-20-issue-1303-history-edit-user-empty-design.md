# Issue #1303: 履歴の「変更」で利用者欄が空欄になる問題

## 背景

履歴画面で「変更」ボタンを押して開いた「履歴の修正」ダイアログにおいて、本来選択されているはずの利用者（職員）が空欄表示される。

ユーザー報告: 2026/4/17 の鉄道往復データ（実機タッチで作成された履歴）でも症状が再現。利用者として記録されている職員は論理削除されていない。

## 根本原因

### 1. 利用 Ledger 生成時に `LenderIdm` が設定されていない

`LendingService.CreateUsageLedgersAsync(string cardIdm, string staffName, ...)` のシグネチャが `staffIdm` を受け取らないため、生成される Ledger に `LenderIdm` を設定する手段がない。

該当コード:

| 場所 | 内容 |
|---|---|
| `LendingService.cs:751-752` | `CreateUsageLedgersAsync` シグネチャに `staffIdm` 引数なし |
| `LendingService.cs:850-860` | 残高不足マージ Ledger: `StaffName` のみ、`LenderIdm` なし |
| `LendingService.cs:1107-1116` | 通常の利用 Ledger: `StaffName` のみ、`LenderIdm` なし |
| `LendingService.cs:1059-1062` | 既存利用 Ledger 統合時に `StaffName` のみ補正、`LenderIdm` なし |

結果: 返却タッチで作成される利用 Ledger（鉄道、バス、残高不足統合等）は DB 上 `lender_idm = NULL` & `staff_name = "..."` という不整合状態になる。

なお、貸出時の Ledger（`InsertLendLedgerAsync` line 398）は両方を正しく設定している。

### 2. 編集ダイアログが LenderIdm でしか職員を照合しない

`LedgerRowEditViewModel.InitializeForEditAsync` (line 283):

```csharp
SelectedStaff = StaffList.FirstOrDefault(s => s.StaffIdm == ledger.LenderIdm);
```

`ledger.LenderIdm` が null の場合、どの職員ともマッチせず `SelectedStaff = null` となり利用者欄が空欄に。

### 3. 保存時にスナップショットの `StaffName` も失われる

`SaveEditAsync` (line 649-658):

```csharp
if (SelectedStaff != null) { ... }
else
{
    ledger.LenderIdm = null;
    ledger.StaffName = null;  // ← 元々あったスナップショットも消える
}
```

ユーザーが備考だけ修正して保存しても、`SelectedStaff = null` のため `StaffName` まで null で上書きされる。

## 修正方針

### Part 1: 根本原因修正（`LendingService`）

`CreateUsageLedgersAsync` が `staffIdm` を受け取り、生成する全 Ledger の `LenderIdm` に設定する。

**変更内容**:

1. `CreateUsageLedgersAsync` のシグネチャに `string staffIdm` を追加
   - 追加位置: `staffName` の前（型の自然順、IDm が主キー）
2. 内部で生成する Ledger に `LenderIdm = staffIdm` を追加:
   - 残高不足マージ Ledger（line 850-860）
   - 通常の利用 Ledger（line 1107-1116）
   - 既存利用 Ledger との統合時に `LenderIdm` も補正（line 1059 周辺）
3. 呼び出し元の更新:
   - `PersistReturnAsync` (line 492): `lentRecord.LenderIdm` を渡す
   - `RegisterCardWithUsageAsync` (line 1256): `null` を渡す（カード登録時は利用者情報がないため既存挙動維持）

注: チャージ Ledger（line 931-940）と払戻し・ポイント還元 Ledger は機械操作扱いで `StaffName = null` のままなので `LenderIdm` も null のまま（既存設計どおり）。

### Part 2: 既存データ救済（`LedgerRowEditViewModel`）

すでに DB に保存されてしまっている `LenderIdm = null` 行への対処。さらに、論理削除等で IDm が一致しないケースも氏名でフォールバックする。

**変更内容**: `InitializeForEditAsync` (line 280-283) のロジック拡張:

```csharp
SelectedStaff = StaffList.FirstOrDefault(s => s.StaffIdm == ledger.LenderIdm);

// LenderIdm で見つからない場合、StaffName（スナップショット）で照合する
// - 同名別職員を選んでしまう可能性があるが、物品出納簿上は氏名表示のみで区別不可のため許容
// - LenderIdm が null（旧バグ由来）と、削除済み等で IDm 不一致の両ケースを救済
if (SelectedStaff == null && !string.IsNullOrEmpty(ledger.StaffName))
{
    SelectedStaff = StaffList.FirstOrDefault(s => s.Name == ledger.StaffName);
}
```

### Part 3: 単体テスト

| 対象 | テストケース | 期待 |
|---|---|---|
| `LedgerRowEditViewModelTests` | Edit 初期化: `LenderIdm`=null, `StaffName`="田中太郎"（職員リスト在籍） | `SelectedStaff` が「田中太郎」 |
| `LedgerRowEditViewModelTests` | Edit 初期化: `LenderIdm`=null, `StaffName`=null | `SelectedStaff` null |
| `LedgerRowEditViewModelTests` | Edit 初期化: `LenderIdm`="存在しないIdm", `StaffName`="田中太郎" | `SelectedStaff` が「田中太郎」（フォールバック） |
| `LedgerRowEditViewModelTests` | Edit 初期化: `LenderIdm`=null, `StaffName`=リストに無い氏名 | `SelectedStaff` null |
| `LendingServiceTests`（既存に追加） | `ReturnAsync` 経由で利用 Ledger 作成 → `LenderIdm` が貸出者の IDm と一致 | Pass |
| `LendingServiceTests`（既存に追加） | `ReturnAsync` 経由で残高不足マージ Ledger 作成 → `LenderIdm` が貸出者の IDm と一致 | Pass |

## 影響範囲

### コード

- `ICCardManager/src/ICCardManager/Services/LendingService.cs`（シグネチャ追加・呼び出し元更新）
- `ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs`（フォールバック追加）

### テスト

- `ICCardManager/tests/ICCardManager.Tests/ViewModels/LedgerRowEditViewModelTests.cs`（4 ケース追加）
- `ICCardManager/tests/ICCardManager.Tests/Services/LendingServiceTests.cs`（2 ケース追加）

### ドキュメント

- `docs/design/02_DB設計書.md`: ledger テーブル `lender_idm` 列の説明に「**返却時の利用 Ledger でも必須**」を追記
- `docs/design/07_テスト設計書.md`: 上記テストケース追加を反映
- `ICCardManager/CHANGELOG.md`: 次バージョンに Issue #1303 修正を記載

### マイグレーション

不要（既存データの `lender_idm = NULL` は Part 2 のフォールバックで運用上は問題なくなる。一括バックフィルは同名衝突リスクがあるため見送り）。

## 非変更範囲

- カード登録時の取込み（`RegisterCardWithUsageAsync` 経由）は引き続き `staffIdm = null`。これは登録時には誰がカードを使ったか確定できないため、現状仕様どおり。
- チャージ、ポイント還元、払戻しの Ledger は機械操作として `StaffName = null` を維持（既存設計）。
