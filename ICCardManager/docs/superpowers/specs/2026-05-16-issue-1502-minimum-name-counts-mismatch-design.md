# Issue #1502 — MinimumNameCounts コメントと閾値の不一致を修正

- 起票: 2026-05-16
- 修正対象: `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs`
- 種別: test (documentation alignment + threshold tightening)
- 関連 PR: #1500（元 PR、Issue #1468）

## 1. 背景

`DialogAutomationPropertiesCoverageTests.MinimumNameCounts` のコメントと閾値が不整合だった。

```csharp
// OperationLogDialog: 期間ボタン3個 + 検索条件4個 + 検索/クリア2個 +
// ページネーション4個 + ページサイズ + DataGrid + Window + エクスポート2個 + 閉じる + 処理中 = 19個以上
{ "OperationLogDialog.xaml", 18 },
```

- コメント末尾: 「= 19個以上」
- 実際の閾値: 18
- 実 XAML の `AutomationProperties.Name` 出現数: **23 個**（`grep -c` で実測）

読み手は「なぜ閾値が 18 / コメントが 19 と食い違うのか」「現状の付与数を 1 件削っても OK なのか」を判断できない。

## 2. 実態調査結果

### 2.1 OperationLogDialog.xaml の付与状況（23 個）

`grep` で全 `AutomationProperties.Name="..."` を抽出した結果:

| グループ | 件数 | 内訳 |
|---|---|---|
| Window | 1 | 「操作ログダイアログ」 |
| 検索条件入力 | 6 | 「開始日」「終了日」「操作種別」「対象テーブル」「対象ID」「操作者名」 |
| 期間クイック | 3 | 「今日の期間に設定」「今月の期間に設定」「先月の期間に設定」 |
| 検索操作 | 2 | 「検索を実行」「検索条件をクリア」 |
| DataGrid | 1 | 「操作ログ一覧」 |
| ページサイズ | 1 | 「1ページあたりの表示件数」 |
| ページネーション | 4 | 「最初のページへ移動」「前のページへ移動」「次のページへ移動」「最後のページへ移動」 |
| エクスポート | 2 | 「Excelファイルにエクスポート」「エクスポートしたファイルを開く」 |
| 閉じる | 1 | 「閉じる」 |
| 処理中 | 2 | 「処理中オーバーレイ」「処理中」 |
| **合計** | **23** | |

### 2.2 StaffAuthDialog.xaml の付与状況（4 個）

| グループ | 件数 | 内訳 |
|---|---|---|
| Window | 1 | 「職員証認証ダイアログ」 |
| アイコン | 1 | 「認証アイコン」 |
| キャンセル | 1 | 「キャンセル」 |
| 仮想タッチ | 1 | 「職員証仮想タッチ（デバッグ用）」 |
| **合計** | **4** | |

注: `Regex.Matches(xaml, @"AutomationProperties\.Name\b")` でカウントすると 5 になるが、これは L48 のコメント中の言及「`AutomationProperties.Name` を設定しない」を拾うため。真の付与数は 4 で、現状の閾値・コメントとも整合している。

## 3. 修正方針

### 3.1 OperationLogDialog: 閾値を 18 → 23 に引き上げ、コメントを実態に合わせて再構築

| 項目 | 修正前 | 修正後 |
|---|---|---|
| 閾値 | 18 | **23** |
| コメント | `期間ボタン3個 + 検索条件4個 + 検索/クリア2個 + ページネーション4個 + ページサイズ + DataGrid + Window + エクスポート2個 + 閉じる + 処理中 = 19個以上` | `Window + 検索条件入力6個 + 期間クイック3個 + 検索/クリア2個 + DataGrid + ページサイズ + ページネーション4個 + エクスポート2個 + 閉じる + 処理中2個 = 23個` |

`MinimumNameCounts` の運用ルールは「値を**増やす**変更は許可（カバレッジ向上）、**減らす**変更は要レビュー（カバレッジ後退）」。閾値を実態の 23 に引き上げる本変更は前者に該当し、運用ルールに沿う。

### 3.2 StaffAuthDialog: 変更なし

- 現状の閾値 `4` は真の付与数と一致
- 現状のコメント「`Window + アイコン + キャンセル + 仮想タッチ = 4個以上`」も実態と整合
- regex がコメントを 1 件拾う件は Issue #1502 のスコープ外（別途検討事項）

## 4. 検証方法

ドキュメント整合性と閾値引き上げのみで、本番コード（XAML / コードビハインド）は変更しない。検証は次の 2 段:

1. **静的確認**: 修正後の閾値 `23` が実 XAML の付与数と一致することを `grep -c "AutomationProperties\.Name\b"` で確認
2. **既存テスト実行**: `Dialog_should_meet_minimum_AutomationProperties_Name_coverage` Theory（2 ケース）が修正後の閾値で pass し続けること、および全テスト 3201 件で回帰がないこと

## 5. テストの取り扱い

- 追加テストは作成しない。本変更は既存テスト `Dialog_should_meet_minimum_AutomationProperties_Name_coverage` の閾値をより厳しい値に引き上げるだけで、新たな検証観点は導入しない
- 「閾値が実 XAML 付与数と一致するか」を検証するメタテストの導入も検討したが、それは Issue #1502 のスコープ外（別の品質ゲート）

## 6. ロールバック

`git revert <commit>` で復元可能。閾値を 18 に戻すと現状の 23 個に対しても pass するため、機能的後退なし。

## 7. スコープ外

- `MinimumHelpTextCounts` の整合性確認（Issue #1502 は `MinimumNameCounts` のみ言及）
- regex `AutomationProperties\.Name\b` のコメント混入問題の解決（別 Issue として検討）
- `OperationLogDialog.xaml` / `StaffAuthDialog.xaml` 本体の変更

## 8. 参考

- Issue #1502
- 元 PR: #1500（Issue #1468）
- 関連既存ファイル: `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs:38-45`
- 実 XAML: `ICCardManager/src/ICCardManager/Views/Dialogs/OperationLogDialog.xaml`, `ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml`
