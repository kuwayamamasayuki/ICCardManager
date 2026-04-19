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

## アナライザ

`.editorconfig` で `CA2007` を `severity=suggestion` として設定。Service 層のみ対象、ViewModels / Views / tests は `none` で無効化。

## 例外: UI 依存サービス

一部のサービス（`DialogService`, `StaffAuthService` など）は内部で `MessageBox.Show` など UI API を呼ぶため、ConfigureAwait(false) を付けると問題になる箇所がある。これらは個別判断。

## 参考

- 設計書: `docs/superpowers/specs/2026-04-19-issue-1287-configure-await-services-design.md`
- Issue #1287
