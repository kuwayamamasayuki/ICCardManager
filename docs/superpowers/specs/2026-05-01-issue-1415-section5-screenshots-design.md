# Issue #1415: 管理者マニュアル §5.3/§5.5 交通系ICカード編集・払い戻しダイアログのスクリーンショット追加

## 背景

管理者マニュアル §5「交通系ICカード管理」では §5.1（カード管理画面 = `card.png`）と §5.2（カード登録方法選択 = `card_registration_mode.png`）にしかスクリーンショットが配置されておらず、§5.3「交通系ICカード情報の編集」および §5.5「交通系ICカードの払い戻し」のダイアログ画像が欠落している。

特に §5.5 の払い戻しは、残高を払出として記録しカードを「払戻済」状態に遷移させる不可逆操作であり、実行前の確認 UI を視覚的に提示することは管理者の誤操作防止に直結する。

該当箇所: `ICCardManager/docs/manual/管理者マニュアル.md` §5.3 / §5.5

## 実装側の状態（事前調査）

### §5.3 編集 UI

専用ダイアログは存在せず、`CardManageDialog.xaml`（左=カード一覧 / 右=編集フォームの 2 列レイアウト、900×600）の **右ペインに `IsEditing=true` で表示される編集フォーム**（350px 幅）で実現されている。撮影対象は「行を選択し『編集』ボタンを押した直後の CardManageDialog 全体」。

### §5.5 払い戻し確認ダイアログ

`CardManageViewModel.RefundAsync()`（`CardManageViewModel.cs:696`）が `_dialogService.ShowWarningConfirmation(message, "払い戻し確認")` を呼び、`DialogService.cs:29` で `MessageBox.Show(..., MessageBoxButton.YesNo, MessageBoxImage.Warning)` を実行する。すなわち撮影対象は **System.Windows.MessageBox の標準確認ダイアログ**（黄色三角警告アイコン + Yes/No）。メッセージ本文は `カード「<種別> <番号>」を払い戻しますか？\n\n現在の残高: ¥<金額>\n\n※払い戻し後、このカードは「払戻済」となり、貸出対象外になります。\n　帳票の作成には引き続き使用できます。` と動的に組み立てられる。

### TakeScreenshots.ps1 の事前準備状況

PR #1427「TakeScreenshots.ps1 を Issue #1409-#1418 用に拡張」により、本 Issue 用エントリは **既に追加済み**:

| 行 | Name | 現 Instructions |
|---|---|---|
| 476 | `card_edit_dialog.png` | `F3キーでカード管理画面を開き、行を選択して「編集」、カード情報編集ダイアログが表示されたら` |
| 482 | `card_refund_dialog.png` | `カード管理画面で「払い戻し」を実行し、残高表示と論理削除警告が含まれた確認ダイアログが表示されたら` |

つまり ps1 への新規エントリ追加は不要であり、既存 Instructions の精緻化のみで足りる。

## スコープ判断

§5.5 マニュアル原文の「カードは論理削除されます（手元にないため）」は実装と乖離している。実装は Issue #530（`Migration_006_AddRefundedStatus`）で「払戻済」状態（`IsRefunded` フラグ、`IsDeleted` の論理削除とは別概念）として保持する設計に変更されており、ps1 行 484 の「論理削除警告」表記も同根の問題を抱えている。

本設計では、Issue #1415 のスコープを **「画像参照追加」と「`card_edit_dialog` Instructions の精緻化」に限定** し、§5.5 本文と ps1 行 484 の「論理削除」表記訂正は **新規 Issue（A2-1）として切り出して別 PR で対応** する方針を採用する。直前の PR #1436 で得た「スコープを絞らないと diff が肥大化しレビューが困難になる」教訓と整合する。

## 修正対象

### `ICCardManager/docs/manual/管理者マニュアル.md`

| 場所 | 修正 |
|---|---|
| §5.3 step 4 の直後 | `![交通系ICカード情報編集（カード管理画面右ペインの編集フォーム）](../screenshots/card_edit_dialog.png){width=80%}` を追加 |
| §5.5 step 3 の直後・「重要」quote の前 | `![払い戻し確認ダイアログ](../screenshots/card_refund_dialog.png){width=70%}` を追加 |

幅指定根拠:
- §5.3: CardManageDialog は 900px 幅 → width=80% で十分視認可能。PR #1436 で `staff_register_*` を 80% に揃えた前例と一貫
- §5.5: MessageBox は CardManageDialog より小さい標準ダイアログ → width=70% で本文との視覚バランスが取れる

### `ICCardManager/tools/TakeScreenshots.ps1`

行 478 の `card_edit_dialog` Instructions を以下に変更:

```
F3キーでカード管理画面を開き、行を選択して「編集」、右側の編集フォームに種別／管理番号／備考の編集欄が表示された状態（CardManageDialog の右ペイン編集モード）で。マニュアル §5.3 で参照
```

PR #1436 で `staff_edit_dialog` Instructions を「右側の編集フォームに氏名・職員番号・備考・IDm 欄が表示された状態」と精緻化した前例に揃える。

行 484 の `card_refund_dialog` Instructions（「論理削除警告」表記）は **本 PR では触れない**（A2-1 の新規 Issue で対応）。

### `ICCardManager/CHANGELOG.md`

`## [Unreleased]` セクションの「変更」配下に以下 3 行を追加:

- 管理者マニュアル §5.3「交通系ICカード情報の編集」/ §5.5「交通系ICカードの払い戻し」にスクリーンショット参照（`card_edit_dialog.png` / `card_refund_dialog.png`）を追加 (Issue #1415)
- `tools/TakeScreenshots.ps1` の `card_edit_dialog` エントリ Instructions を精緻化（右側編集フォームの可視欄を明示） (Issue #1415)
- follow-up: §5.5 本文および ps1 `card_refund_dialog` Instructions の「論理削除」表記訂正は別 Issue（#XXXX）で対応予定

### 新規 Issue（A2-1）作成

タイトル: `docs: 管理者マニュアル §5.5 / TakeScreenshots.ps1 の「論理削除」表記を「払戻済」状態（Issue #530）に整合`

ラベル: `priority: low`, `type: documentation`

本文骨子:
- §5.5 本文「カードは論理削除されます」と ps1 行 484「論理削除警告」が、Issue #530（Migration_006_AddRefundedStatus）の「払戻済」状態（`IsRefunded`）と乖離
- 実装側 MessageBox メッセージは「払戻済となり、貸出対象外」「帳票の作成には引き続き使用できます」と表現
- 修正案を §5.5 / ps1 の両方について併記
- 発見元: Issue #1415 のスクリーンショット追加作業中

## 採用理由（アプローチ比較）

| | Approach 1（採用） | Approach 2 |
|---|---|---|
| ps1 `card_edit_dialog` Instructions 精緻化 | ✅ する | ❌ しない |
| トレードオフ | 撮影者が右ペイン編集モードであることを Instructions だけで把握可能。PR #1436 の `staff_edit_dialog` 精緻化と一貫 | 変更最小だが Card 側だけ非対称・撮影者が混乱しやすい |

Approach 1 を採用する理由:
1. PR #1436 で `staff_edit_dialog` Instructions を精緻化した前例があり、Card 側だけ放置するのは保守一貫性を欠く
2. ps1 Instructions の精緻化は **誤記訂正ではなく不足情報の補完** であり、選択した「スコープ厳守」方針と矛盾しない
3. Instructions が曖昧だと撮影者が間違った状態を撮る可能性 → 後でやり直しが発生

## 作業順序

1. `git fetch origin && git checkout main && git pull`
2. ブランチ作成: `docs/issue-1415-section5-card-edit-refund-screenshot`
3. 設計書（本ファイル）をコミット
4. 新規 Issue（A2-1）を `gh issue create` で作成 → 番号取得
5. CHANGELOG の「follow-up: ... 別 Issue（#XXXX）」を実 Issue 番号に置換
6. マニュアル §5.3 / §5.5 編集
7. ps1 Instructions 精緻化（`card_edit_dialog` のみ）
8. CHANGELOG 編集 → コミット
9. push → PR 作成（A2-1 Issue 番号を Follow-up セクションで参照）

## テスト方針

本 PR の変更は **Markdown 文書 + PowerShell コメント文字列** のみでコードロジック変更がないため、単体テスト対象外。

ユーザーへの動作確認依頼:

1. 当ブランチを checkout 後、PowerShell で:
   ```powershell
   .\tools\TakeScreenshots.ps1 -Only card_edit_dialog,card_refund_dialog
   ```
2. CardManageDialog で行を選択 → 「編集」 → 右ペインに編集フォームが表示された状態で Enter（→ `card_edit_dialog.png` が保存される）
3. 同画面で行を選択 → 「払い戻し」 → 警告 MessageBox（黄色三角アイコン + Yes/No）が表示された状態で Enter（→ `card_refund_dialog.png` が保存される）
4. `ICCardManager/docs/screenshots/` に保存された 2 枚を Markdown プレビューで参照確認
5. 撮影画像の追加コミットは別途実施（前 PR #1436 と同パターン）

## スコープ外

- §5.5 マニュアル本文「論理削除されます」表記の訂正（A2-1 の新規 Issue で対応）
- ps1 `card_refund_dialog` Instructions「論理削除警告」表記の訂正（同上）
- 画像本体ファイル（`card_edit_dialog.png` / `card_refund_dialog.png`）のコミット（撮影後にユーザーが追加コミット）
- §5.4「交通系ICカードの削除」のスクリーンショット追加（Issue #1415 では言及なし）
