---
paths:
  - "ICCardManager/src/ICCardManager/Services/**"
  - "ICCardManager/src/ICCardManager/Data/**"
  - "ICCardManager/src/ICCardManager/Infrastructure/**"
  - "ICCardManager/src/ICCardManager/Common/**"
---

# async / ConfigureAwait(false) 規約

## Service 層（Services/ 配下）

すべての `await` に `.ConfigureAwait(false)` を付与する。

```csharp
// ✅ 推奨
var card = await _cardRepository.GetByIdmAsync(idm).ConfigureAwait(false);

// ❌ 非推奨（UI スレッドへの不要な dispatch が発生）
var card = await _cardRepository.GetByIdmAsync(idm);
```

## ViewModel 層（ViewModels/ 配下）

`ConfigureAwait(false)` を**付けない**。`INotifyPropertyChanged` や WPF バインディングが UI 文脈を要求するため、継続が UI スレッドに戻ることが必要。

## View 層（Views/ 配下）

ViewModel と同じ理由で付けない。

## テストコード

付けない（regression detector としての純粋性を保つ。意図しない UI 依存が既存コードに混入していないかを検出するため）。

### 適用範囲（Issue #1287 / ドリフト監査 ACA-R4-01）

本規約が対象とするのは **テスト本体が SUT（テスト対象）の async API を `await` する箇所** である。次のものは本規約の対象外であり、`ConfigureAwait(false)` が付いていても違反ではない:

- **コメント・docstring 中の言及**: `ConfigureAwait(false)` を解説・参照しているだけの記述（例: `BackupServiceUiThreadGuardTests` / `MainViewModelIntegrationTests` の説明コメント）。
- **テストダブル／フェイクが本番メソッドを `override` する箇所**: 本番側の規約（Service 層は付ける）を踏襲するのが正しい（例: `SettingsRepositorySaveTransactionTests` のテスト用サブクラスが `base.BeginTransactionAsync().ConfigureAwait(false)` を呼ぶ）。
- **フレームワーク基本型の `await`**: `Task.Delay` / `Task.WhenAll` 等、SUT ではなく .NET 基本型を待つテスト基盤コード（例: `DashboardServiceTests` / `LendingServiceTests` の並行性テスト）。これらは UI 文脈非依存で regression detector の純粋性に影響しない。

要は「SUT が UI 文脈を不要に要求していないか」を検出する目的に資する `await` が対象であり、テスト基盤やダブルの内部実装は対象外。

## ガード（Issue #1823）

`ConfigureAwaitConventionTests` が `src/ICCardManager` の `Common` / `Data` / `Dtos` /
`Infrastructure` / `Models` / `Services` 配下を走査し、`.ConfigureAwait(false)` を伴わない
`await` を検出する。走査対象は**ディレクトリから導出**するため、新規ファイルは自動的に検査対象へ入る。

未是正のファイルは同テストの `KnownUnfixedFiles` で明示的に除外している。除外は
**減らす方向にのみ変更する**こと（除外ファイルへ新たな `await` を足しても検出されない）。
除外が是正済みになったらテストが赤くなり、エントリの削除を促す。

> Issue #1287 で規約を定めてからガードが無く、Issue #1823 の時点で
> `CardRepository` 0/51・`StaffRepository` 0/27・`Infrastructure/` 配下 0 件・
> `CsvExportService` の三項演算子 2 か所の付与漏れが蓄積していた。

## アナライザ（Issue #2162）

本体で .NET アナライザーを有効にし（`development-conventions.md`「.NET アナライザー（CA ルール）は本体で有効」）、`ICCardManager/.editorconfig` で CA2007 を次の 3 段で設定している。付け忘れは**ビルド警告**になり、CI の「ビルド警告ゼロ検証」で止まる。

| 節 | 重大度 | 理由 |
|---|---|---|
| `src/ICCardManager/**.cs` | none | ViewModels / Views / `App.xaml.cs` は継続を UI スレッドへ戻す必要がある |
| `src/ICCardManager/{Common,Data,Dtos,Infrastructure,Models,Services}/**.cs` | **warning** | 本規約の対象 |
| `src/ICCardManager/Services/DialogService.cs` | none | 下の「例外: UI 依存サービス」 |

テストプロジェクトではアナライザー自体を有効にしていない（テストは付けない規約）。

> Issue #2162 以前は、リポジトリ直下の `.editorconfig` に「Service 層は suggestion」と書いていたが、`ICCardManager/.editorconfig` が `root = true` のため**一度も読まれておらず**、そもそもアナライザーも動いていなかった。設定が書いてあることは、それが効いていることを意味しない。

**アナライザーと静的検査（`ConfigureAwaitConventionTests`）の関係**: 意味解析で判定するアナライザーを主たる検出手段とし、静的検査は残す。アナライザーはビルド設定（`EnableNETAnalyzers` や重大度の格下げ）で止められるが、静的検査はそれと独立に働くため。2 つの対象範囲（warning のディレクトリ ↔ `TargetDirectories`、none に戻すファイル ↔ `KnownUnfixedFiles`）は同テストの `CA2007の設定がこの検査と同じ範囲を対象にしていること` が一致を固定する。対象ディレクトリや除外を変えるときは両方を同時に直すこと。Issue #2162 で warning にしたのに伴い、除外していた `LedgerRepository` / `SettingsRepository` / `OperationLogRepository` は是正し、除外は `DialogService` だけになった。

## 例外: UI 依存サービス

一部のサービス（`DialogService`, `StaffAuthService` など）は内部で `MessageBox.Show` など UI API を呼ぶため、ConfigureAwait(false) を付けると問題になる箇所がある。これらは個別判断。

## 参考

- 設計書: `ICCardManager/docs/superpowers/specs/2026-04-19-issue-1287-configure-await-services-design.md`
- Issue #1287
