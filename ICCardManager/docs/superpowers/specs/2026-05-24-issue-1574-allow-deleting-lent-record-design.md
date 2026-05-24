# Issue #1574: 貸出中状態の履歴行が残ってしまった際に変更ボタンで削除できない

- **Issue**: [#1574 貸出中状態の履歴行が残ってしまった際に変更ボタンで削除できない](https://github.com/kuwayamamasayuki/ICCardManager/issues/1574)
- **作成日**: 2026-05-24
- **種別**: バグ修正（コード変更あり）

## 背景

何らかの原因（アプリ異常終了、DB手動修復、過去バージョンの不具合など）で「（貸出中）」状態の履歴行（`ledger.is_lent_record = 1`）が残ってしまうケースがある。この行を変更ダイアログ（`LedgerRowEditDialog`）で開いても、Issue #750 で導入された安全策により「削除」ボタンが非表示になっており、削除できない。

```csharp
// 現状: LedgerRowEditViewModel.cs:298-299
// Issue #750: 貸出中でなければ削除可能
CanDelete = !ledgerDto.IsLentRecord;
```

`MainViewModel.DeleteLedgerRow` 側にも同様のガードがあり、`is_lent_record=1` の行は削除を拒否する：

```csharp
// 現状: MainViewModel.cs:1644-1651
if (ledger.IsLentRecord)
{
    _navigationService.ShowWarning(
        "貸出中のレコードは削除できません。" +
        "先にメイン画面で交通系ICカードをタッチして返却操作を行ってから、再度削除してください。",
        "削除不可");
    return;
}
```

通常運用では「返却操作」で「（貸出中）」レコードは消滅・更新される。しかし異常状態で残った行は、対応する物理カードが既に手元になかったり既に別運用に回っていたりすると、返却操作で復旧することができず、データ整合性を取り戻す手段がなくなる。

## ゴール

1. 「（貸出中）」状態の履歴行（`is_lent_record=1`）も削除可能にする
2. 削除時に対応する `ic_card.is_lent` フラグも整合性を保ってリセットする
3. 通常運用では誤削除を防ぐため、削除前に「貸出中レコード」専用の警告メッセージを表示する
4. 単体テストで新しい削除パスを保護する

## 非ゴール

- `UpdateLentStatusAsync` のトランザクション一体化（影響範囲拡大のため別 Issue にする）
- `ic_card.is_lent` 整合性チェックの再設計（既存の `CheckAndNotifyConsistencyAsync` を活用するだけ）
- 履歴詳細画面以外の削除パス（カード管理画面・操作ログ画面）の改修

## 設計

### A. 削除ガード解除

| ファイル | 変更内容 |
|---------|---------|
| `LedgerRowEditViewModel.cs:298-299` | `CanDelete = true;` に変更（`is_lent_record` の値によらず削除ボタン表示） |
| `MainViewModel.cs:1644-1651` | `IsLentRecord` 拒否ブロックを削除 |

### B. 削除時の警告メッセージ強化

`LedgerRowEditViewModel.RequestDelete()`（既存メソッド）と `MainViewModel.DeleteLedgerRow`（既存メソッド）の確認ダイアログを以下の条件で分岐:

- **通常レコード**: 既存の確認文言（変更なし）
- **貸出中レコード**: 以下の警告強化文に置換

```
この履歴は「貸出中」状態のレコードです。
削除すると、このカードの貸出中状態も解消されます
（他に貸出中レコードが残っている場合は維持）。

通常は、メイン画面で交通系ICカードをタッチして返却操作を
行うのが正しい復旧方法です。それでも削除しますか？
```

### C. `is_lent` 整合性リセット

削除した行が `is_lent_record=1` だった場合のみ、削除トランザクション完了後に以下を実行:

```csharp
// 疑似コード
if (deletedLedger.IsLentRecord)
{
    var hasOther = await _ledgerRepository.HasOtherLentRecordsAsync(
        deletedLedger.CardIdm, excludeLedgerId: deletedLedger.Id);
    if (!hasOther)
    {
        await _cardRepository.UpdateLentStatusAsync(
            deletedLedger.CardIdm, isLent: false, lentAt: null, staffIdm: null);
    }
}
```

**セーフティロジック**: 同一カードに他の貸出中レコードが残っている場合（多重貸出の異常状態）は `is_lent=true` を維持。次回操作で他のレコードを順次処理することで段階的に復旧できる。

### D. 新規リポジトリメソッド

`ILedgerRepository` / `LedgerRepository` に以下を追加:

```csharp
/// <summary>
/// 指定カードに「貸出中」状態のレコードが、指定 ID 以外に残っているか判定する。
/// 削除後の is_lent 整合性判断に使用する。
/// </summary>
Task<bool> HasOtherLentRecordsAsync(string cardIdm, long excludeLedgerId);
```

SQL:

```sql
SELECT COUNT(*) FROM ledger
WHERE card_idm = @cardIdm
  AND is_lent_record = 1
  AND id <> @excludeLedgerId
```

### E. トランザクション戦略

- ledger DELETE + 監査ログ INSERT: 同一トランザクション（既存通り、Issue #1458）
- `is_lent` リセット: 上記トランザクション完了後に独立して実行（既存の `UpdateLentStatusAsync` はトランザクション引数を持たないため）

**部分失敗時の挙動**: ledger DELETE 成功・`is_lent` リセット失敗の場合、貸出中レコードは消えているが `is_lent=true` が残る。`CheckAndNotifyConsistencyAsync` が警告を出すので運用上は検知可能。再操作で復旧可能。

## 変更対象ファイル一覧

| # | ファイル | 変更内容 |
|---|---------|---------|
| 1 | `ICCardManager/src/ICCardManager/ViewModels/LedgerRowEditViewModel.cs` | `CanDelete = true` 化、`RequestDelete()` の警告メッセージ分岐 |
| 2 | `ICCardManager/src/ICCardManager/ViewModels/MainViewModel.cs` | `DeleteLedgerRow` のガード削除＋警告強化、削除後 `is_lent` リセット呼び出し。`EditLedgerWithAuthAsync` 内の削除分岐にも同じ処理を適用 |
| 3 | `ICCardManager/src/ICCardManager/Data/Repositories/ILedgerRepository.cs` | `HasOtherLentRecordsAsync` シグネチャ追加 |
| 4 | `ICCardManager/src/ICCardManager/Data/Repositories/LedgerRepository.cs` | `HasOtherLentRecordsAsync` 実装 |
| 5 | `ICCardManager/tests/ICCardManager.Tests/ViewModels/LedgerRowEditViewModelTests.cs` | `IsLentRecord=true` でも `CanDelete=true` になることを検証 |
| 6 | `ICCardManager/tests/ICCardManager.Tests/ViewModels/MainViewModelTests.cs`（または該当ファイル）| `DeleteLedgerRow` で貸出中行削除時の `UpdateLentStatusAsync` 呼び出しを検証（呼ばれる/呼ばれないの両ケース） |
| 7 | `ICCardManager/tests/ICCardManager.Tests/Data/Repositories/LedgerRepositoryTests.cs` | `HasOtherLentRecordsAsync` の正常系・除外系を検証 |
| 8 | `ICCardManager/CHANGELOG.md` | `### Unreleased` セクションに fix エントリ追加 |
| 9 | `ICCardManager/docs/design/03_画面設計書.md` 等の関連設計書 | 「貸出中レコードは削除不可」と書いていれば修正 |

## テスト方針

### 単体テスト追加

1. **`LedgerRowEditViewModelTests`**
   - `InitializeForEditAsync` 後、`IsLentRecord=true` のレコードに対して `CanDelete=true` になることを検証
   - 既存の「`IsLentRecord=false` で `CanDelete=true`」テストも維持
2. **`MainViewModel` 関連テスト**
   - 貸出中レコード削除時に `UpdateLentStatusAsync(cardIdm, false, null, null)` が呼ばれる（他に貸出中なし）
   - 同条件で他に貸出中レコードが残っている場合は `UpdateLentStatusAsync` が呼ばれない
   - 通常レコード（`IsLentRecord=false`）削除時は `UpdateLentStatusAsync` が呼ばれない（既存挙動）
3. **`LedgerRepositoryTests`**
   - `HasOtherLentRecordsAsync` が貸出中レコード数を正しく返す
   - `excludeLedgerId` で指定した ID は除外される
   - 同じカードでも `is_lent_record=0` の行はカウントしない
   - 別カードの貸出中レコードはカウントしない

### 手動確認項目（PR 作成後ユーザー実施）

ユニットテストでは UI 操作（ダイアログ表示、確認ボタンクリック）の検証が困難なため、以下を手動確認していただきたい:

- [ ] 「（貸出中）」表示の履歴行をダブルクリックまたは「変更」ボタンで開く
- [ ] 変更ダイアログに「削除」ボタンが表示されている
- [ ] 「削除」ボタンを押すと、貸出中専用の警告メッセージが表示される
- [ ] 警告で「はい」を選ぶと該当行が消え、`is_lent` も解消される（メイン画面の「貸出中カード」一覧から消える）
- [ ] 別カードや別の貸出中レコードに影響がない
- [ ] 通常レコード（`is_lent_record=0`）の削除時は従来通りの確認メッセージ

## 影響範囲・リスク

- **業務影響**: 異常状態からの復旧手段が新たに提供される（プラスの影響）。通常運用では強化された警告メッセージにより誤操作の可能性は低い
- **互換性**: 既存の `ic_card` / `ledger` テーブル構造は変更なし。マイグレーション不要
- **コード品質**: 既存テストへの影響は最小限。新規テストで品質確保
- **リスク**: 部分失敗（ledger DELETE 成功・`is_lent` リセット失敗）時に半端な状態が残るが、既存の整合性チェック機構で検知可能。発生確率は極めて低い

## 関連 Issue

- Issue #750: 履歴行の追加/削除/変更機能の導入（本 Issue で緩和する削除ガードの初出）
- Issue #1109: ICカード削除時の `is_lent` チェック手順の刷新
- Issue #1458: ledger 変更時のトランザクション一体化
- Issue #1575/#1576: 利用履歴を含む返却処理のデッドロック修正（直近の関連修正）
