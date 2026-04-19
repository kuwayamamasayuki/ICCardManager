# Issue #1287: ConfigureAwait(false) の Service 層適用（Phase 1）設計書

作成日: 2026-04-19
対象 Issue: [#1287](https://github.com/kuwayamamasayuki/ICCardManager/issues/1287)

## 背景と問題

現状、Service 層の async メソッドは `ConfigureAwait(false)` をほぼ付けておらず、各 await の後で WPF UI スレッドへ継続がディスパッチされる。以下の悪影響がある:

- 不要な UI スレッド経由による性能劣化（特に DB I/O 集中処理）
- UI スレッドで同期待ちしている箇所があるとデッドロックの原因
- Microsoft 公式のライブラリ/サービス層実装のベストプラクティスから外れる

Issue の推奨:
- Service 層全 async メソッドに `ConfigureAwait(false)` を付与
- ViewModel 層は意図的に付けない（UI 文脈維持）
- Roslyn アナライザ `CA2007` を有効化
- **段階的に適用**（サービスごとに PR を分割）

## スコープ

本 PR = Phase 1。主要な高頻度サービスに一括適用し、基盤（analyzer + 規約文書）を整備する。残りのサービスは Phase 2 以降の follow-up Issue で対応する。

### 含む（Phase 1）

1. **規約文書**: `.claude/rules/async-configureawait.md` 新設
2. **アナライザ有効化**: `.editorconfig` で `CA2007` を `suggestion` レベルで有効化、`ViewModels/` と `Views/` フォルダでは無効化
3. **対象ファイル 11 個** に `ConfigureAwait(false)` を適用（合計 ~130 await 箇所）:

| ファイル | await 数 |
|--------|---------|
| `Data/DbContext.cs` | 4 (既存 1 除く) |
| `Services/LendingService.cs` | 53 |
| `Services/OperationLogger.cs` | 13 |
| `Services/CsvImportService.cs` | 3 |
| `Services/Import/CsvImportService.Card.cs` | 11 |
| `Services/Import/CsvImportService.Staff.cs` | 11 |
| `Services/Import/CsvImportService.Ledger.cs` | 12 |
| `Services/Import/CsvImportService.Detail.cs` | 13 |
| `Services/Import/CsvImportService.LedgerValidation.cs` | 1 |
| `Services/ReportService.cs` | 6 |
| `Services/ReportDataBuilder.cs` | 7 |
| `Services/BackupService.cs` | 3 |

### 含まない（Phase 2+）

- UI 依存サービス（`DialogService`, `StaffAuthService`）は UI context が必要なため本 PR 対象外
- その他の Service 層ファイル（~15 個）は follow-up Issue で対応
- ViewModel 層・View 層は対象外（意図的に UI 文脈を維持）
- 本格的な runtime 挙動検証（既存テストの pass をもって担保）

## 設計

### ConfigureAwait(false) の適用パターン

**Before:**
```csharp
public async Task<List<Card>> GetAllAsync()
{
    return await _cardRepository.GetAllAsync();
}
```

**After:**
```csharp
public async Task<List<Card>> GetAllAsync()
{
    return await _cardRepository.GetAllAsync().ConfigureAwait(false);
}
```

using 宣言内の `await using` は通常の `await` と同様に ConfigureAwait を付けられる:
```csharp
using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
```

### 除外ルール（付けない場所）

- **Service 層内であっても** UI Thread に戻る必要がある場合（本 PR の対象サービスには該当なし）
- **ViewModels/** フォルダ全体
- **Views/** フォルダ全体
- **テストコード**（regression 検出を最大化するため付けない）

### `.editorconfig` 追加

プロジェクトルートの `.editorconfig`（存在しない場合は新規作成）に以下を追加:

```editorconfig
# Issue #1287: Service 層では ConfigureAwait(false) を推奨
[ICCardManager/src/ICCardManager/**/*.cs]
dotnet_diagnostic.CA2007.severity = suggestion

# ViewModels/Views は UI 文脈を維持するため CA2007 を無効化
[ICCardManager/src/ICCardManager/ViewModels/**/*.cs]
dotnet_diagnostic.CA2007.severity = none

[ICCardManager/src/ICCardManager/Views/**/*.cs]
dotnet_diagnostic.CA2007.severity = none

# テストコードでも無効化
[ICCardManager/tests/**/*.cs]
dotnet_diagnostic.CA2007.severity = none
```

severity は `suggestion`（IDE でヒント表示のみ）。将来的に `warning` に昇格することで強制可能。

### 既存テスト戦略

- **3017 件の既存テスト**が regression detector として機能
- ConfigureAwait(false) は副作用を持たない最適化（契約は不変）
- 特定のテスト追加は不要（`ConfigureAwait(false)` を足す単純な変更に対するテストは冗長）

### Phase 2 以降の follow-up

以下を扱う follow-up Issue を本 PR の merge 前に作成する:
- `DashboardService` / `LedgerMergeService` / `LedgerSplitService` / `PrintService` / `SummaryGenerator` / `LedgerConsistencyChecker` / `StationMasterService` 等、残り ~10 ファイル
- `DialogService` / `StaffAuthService` は UI 依存のため個別判断

## リスクと対策

| リスク | 対策 |
|-------|-----|
| 付け忘れによる UI スレッド dispatch 残存 | Phase 1 スコープ内は手作業で全件確認。Phase 2 で analyzer の警告を基に網羅 |
| ConfigureAwait(false) 後に UI スレッドを前提とする処理があった場合のバグ | 対象サービスは全て DB / I/O 処理のみで UI 依存なし（事前検査済み: MessageBox/Dispatcher/Application.Current を grep） |
| `.editorconfig` の効果が CI まで届かない | GitHub Actions の build ログで `CA2007` の suggestion が出ることを確認 |
| 既存テストで UI スレッド前提の assertion | 既存 3017 件が通ることを maintain |

## 非対象（別 Issue 候補）

- Phase 2: 残り Service 層の ConfigureAwait 適用
- `DialogService` / `StaffAuthService` の UI 文脈要検査
- async void の根絶（別課題）
- Task.Run で UI スレッドから分離しているコードの見直し
