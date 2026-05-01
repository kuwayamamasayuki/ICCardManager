# Issue #1415 §5.3/§5.5 スクリーンショット追加 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 管理者マニュアル §5.3 / §5.5 に交通系ICカード編集・払い戻しダイアログのスクリーンショット参照を追加し、`TakeScreenshots.ps1` の `card_edit_dialog` Instructions を精緻化する。§5.5 本文の「論理削除」表記乖離は同 PR ではスコープ外として新規 follow-up Issue で別 PR 対応。

**Architecture:** ドキュメント編集 + PowerShell コメント文字列編集のみ。コードロジック変更ゼロ。3 ファイル（マニュアル / ps1 / CHANGELOG）を 1 コミットに集約し、画像本体は別途ユーザーが撮影してフォローアップコミット。

**Tech Stack:** Markdown (Pandoc 拡張記法 `{width=NN%}`), PowerShell 5.1, GitHub CLI (`gh`)

**設計書:** `docs/superpowers/specs/2026-05-01-issue-1415-section5-screenshots-design.md`

**ブランチ:** `docs/issue-1415-section5-card-edit-refund-screenshot`（既に作成済み・設計書コミット済み）

---

## Task 1: follow-up Issue (A2-1) を作成

**Files:**
- 外部システム（GitHub）への新規 Issue 作成

**目的:** §5.5 本文「論理削除」表記乖離 + ps1 行 484 同種表記の訂正用 Issue を立て、本 PR の CHANGELOG / PR 本文で参照する番号を取得する。

- [ ] **Step 1: Issue 本文ファイルを一時作成**

`/tmp/issue_a21_body.md` に以下を書き出す:

```markdown
## 概要

`ICCardManager/docs/manual/管理者マニュアル.md` §5.5「交通系ICカードの払い戻し」の重要注記に「カードは論理削除されます（手元にないため）」とある。一方、実装は Issue #530 (`Migration_006_AddRefundedStatus`) で「払戻済」状態（`IsRefunded` フラグ）として保持する設計に変更されており、`IsDeleted` の論理削除とは別概念である。

`CardManageViewModel.RefundAsync` の確認 MessageBox メッセージも以下の通り「払戻済」と表現している:

> ※払い戻し後、このカードは「払戻済」となり、貸出対象外になります。
> 　帳票の作成には引き続き使用できます。

また `ICCardManager/tools/TakeScreenshots.ps1` 行 484 の `card_refund_dialog` Instructions にも同根の「論理削除警告が含まれた確認ダイアログ」表記がある。

## 該当箇所

- `ICCardManager/docs/manual/管理者マニュアル.md` §5.5（重要注記の「論理削除されます」行）
- `ICCardManager/tools/TakeScreenshots.ps1` 行 484（`card_refund_dialog` Instructions の「論理削除警告」）

## 修正案

### マニュアル §5.5 重要注記

| 旧 | 新 |
|---|---|
| カードは論理削除されます（手元にないため） | カードは「払戻済」状態となり、一覧では「払戻済」と表示され貸出対象から除外されます |

### ps1 `card_refund_dialog` Instructions

| 旧 | 新 |
|---|---|
| 残高表示と論理削除警告が含まれた確認ダイアログが表示されたら | 残高表示と「払戻済」状態への遷移注意（黄色三角警告アイコン付き Yes/No）が含まれた確認ダイアログが表示されたら |

## 関連

- 発見元: Issue #1415 のスクリーンショット追加作業中
- 実装根拠: Issue #530, `Migration_006_AddRefundedStatus`
- 同 Issue 作業 PR: TBD（本 Issue を立てた後に作成される PR で参照）
```

- [ ] **Step 2: Issue を作成し番号を取得**

Run:
```bash
gh issue create \
  --title 'docs: 管理者マニュアル §5.5 / TakeScreenshots.ps1 の「論理削除」表記を「払戻済」状態（Issue #530）に整合' \
  --label 'priority: low' \
  --label 'type: documentation' \
  --body-file /tmp/issue_a21_body.md
```

Expected: `https://github.com/<owner>/<repo>/issues/<NEW_NUMBER>` の URL が出力される。`<NEW_NUMBER>` を以降のタスクで使用する変数 `$A21_ISSUE` として記憶。

- [ ] **Step 3: 一時ファイル削除**

```bash
rm /tmp/issue_a21_body.md
```

---

## Task 2: 管理者マニュアル §5.3 に画像参照を追加

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md`（§5.3 step 4 の直後）

- [ ] **Step 1: §5.3 の現状を確認**

Run:
```bash
grep -n -A6 '### 5.3 交通系ICカード情報の編集' /mnt/d/OneDrive/交通系/src/ICCardManager/docs/manual/管理者マニュアル.md
```

Expected: 5 行表示される（タイトル + 番号付きリスト 4 項目）。

- [ ] **Step 2: Edit ツールで step 4 の直後に画像参照を追加**

Edit ツールで以下置換:

old_string:
```
### 5.3 交通系ICカード情報の編集

1. 一覧から編集するカードを選択
2. 「編集」ボタンをクリック
3. 情報を修正
4. 「保存」ボタンをクリック

### 5.4 交通系ICカードの削除
```

new_string:
```
### 5.3 交通系ICカード情報の編集

1. 一覧から編集するカードを選択
2. 「編集」ボタンをクリック
3. 情報を修正
4. 「保存」ボタンをクリック

![交通系ICカード情報編集（カード管理画面右ペインの編集フォーム）](../screenshots/card_edit_dialog.png){width=80%}

### 5.4 交通系ICカードの削除
```

- [ ] **Step 3: 挿入結果を確認**

Run:
```bash
grep -n -A8 '### 5.3 交通系ICカード情報の編集' /mnt/d/OneDrive/交通系/src/ICCardManager/docs/manual/管理者マニュアル.md
```

Expected: 画像参照行 `![交通系ICカード情報編集...](../screenshots/card_edit_dialog.png){width=80%}` が含まれていること。

---

## Task 3: 管理者マニュアル §5.5 に画像参照を追加

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md`（§5.5 step 3 の直後・「重要」quote の前）

- [ ] **Step 1: §5.5 の現状を確認**

Run:
```bash
grep -n -A12 '### 5.5 交通系ICカードの払い戻し' /mnt/d/OneDrive/交通系/src/ICCardManager/docs/manual/管理者マニュアル.md
```

Expected: タイトル + 説明文 + 番号付きリスト 3 項目 + `> **重要**:` quote が表示される。

- [ ] **Step 2: Edit ツールで画像参照を挿入**

Edit ツールで以下置換:

old_string:
```
1. 一覧から払い戻すカードを選択
2. 「払い戻し」ボタンをクリック
3. 確認ダイアログで現在の残高を確認し、「はい」をクリック

> **重要**: 払い戻し処理を行うと、以下の処理が実行されます。
```

new_string:
```
1. 一覧から払い戻すカードを選択
2. 「払い戻し」ボタンをクリック
3. 確認ダイアログで現在の残高を確認し、「はい」をクリック

![払い戻し確認ダイアログ](../screenshots/card_refund_dialog.png){width=70%}

> **重要**: 払い戻し処理を行うと、以下の処理が実行されます。
```

- [ ] **Step 3: 挿入結果を確認**

Run:
```bash
grep -n -A10 '### 5.5 交通系ICカードの払い戻し' /mnt/d/OneDrive/交通系/src/ICCardManager/docs/manual/管理者マニュアル.md
```

Expected: 画像参照行が step 3 と `> **重要**:` の間に挿入されていること。

---

## Task 4: TakeScreenshots.ps1 の `card_edit_dialog` Instructions を精緻化

**Files:**
- Modify: `ICCardManager/tools/TakeScreenshots.ps1:478`

- [ ] **Step 1: 現状の Instructions を確認**

Run:
```bash
sed -n '474,486p' /mnt/d/OneDrive/交通系/src/ICCardManager/tools/TakeScreenshots.ps1
```

Expected:
```powershell
    # Issue #1415: 管理者マニュアル §5.3/§5.5 カード編集・払い戻しダイアログ
    @{
        Name = "card_edit_dialog.png"
        Title = "交通系ICカード情報編集ダイアログ"
        Instructions = "F3キーでカード管理画面を開き、行を選択して「編集」、カード情報編集ダイアログが表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "card_refund_dialog.png"
```

- [ ] **Step 2: Instructions 行を Edit で精緻化**

Edit ツールで以下置換:

old_string:
```
        Name = "card_edit_dialog.png"
        Title = "交通系ICカード情報編集ダイアログ"
        Instructions = "F3キーでカード管理画面を開き、行を選択して「編集」、カード情報編集ダイアログが表示されたら"
```

new_string:
```
        Name = "card_edit_dialog.png"
        Title = "交通系ICカード情報編集ダイアログ"
        Instructions = "F3キーでカード管理画面を開き、行を選択して「編集」、右側の編集フォームに種別／管理番号／備考の編集欄が表示された状態（CardManageDialog の右ペイン編集モード）で。マニュアル §5.3 で参照"
```

- [ ] **Step 3: 変更結果を確認**

Run:
```bash
sed -n '474,486p' /mnt/d/OneDrive/交通系/src/ICCardManager/tools/TakeScreenshots.ps1
```

Expected: 行 478 の Instructions に「右側の編集フォームに種別／管理番号／備考の編集欄」が含まれていること。`card_refund_dialog`（行 484）の Instructions は **未変更**（A2-1 Issue で対処）であることを確認。

- [ ] **Step 4: ps1 構文が壊れていないことを軽量チェック**

Run:
```bash
"/mnt/c/Program Files/PowerShell/7/pwsh.exe" -NoProfile -Command "Get-Command -Syntax /mnt/d/OneDrive/交通系/src/ICCardManager/tools/TakeScreenshots.ps1" 2>&1 | head -5
```

Expected: 構文エラーが出ない（`Get-Command` がパラメータ一覧を表示するか、最低でも構文 parse error が無いこと）。pwsh.exe が無い環境ではこの step はスキップ可（push 後 CI または手動撮影時に検出される）。

---

## Task 5: CHANGELOG.md に変更を追記

**Files:**
- Modify: `ICCardManager/CHANGELOG.md`（`## [Unreleased]` セクション直下、または最上位の `## [Unreleased]` 配下の「変更」サブセクション）

- [ ] **Step 1: CHANGELOG 先頭部の現状確認**

Run:
```bash
head -50 /mnt/d/OneDrive/交通系/src/ICCardManager/CHANGELOG.md
```

Expected: `## [Unreleased]` セクションの形式（`### 変更` / `### 修正` 等のサブヘッダ構成）が確認できる。

- [ ] **Step 2: `## [Unreleased]` 配下の「変更」項目に 3 行追加**

Edit ツールで `## [Unreleased]` 直下の最初の `### 変更` サブヘッダの先頭に 3 項目挿入。エントリは以下フォーマット（`$A21_ISSUE` は Task 1 で取得した番号で置換）:

```markdown
- 管理者マニュアル §5.3「交通系ICカード情報の編集」/ §5.5「交通系ICカードの払い戻し」にスクリーンショット参照（`card_edit_dialog.png` / `card_refund_dialog.png`）を追加 (Issue #1415)
- `tools/TakeScreenshots.ps1` の `card_edit_dialog` エントリ Instructions を精緻化（右側編集フォームの可視欄を明示） (Issue #1415)
- follow-up: §5.5 本文および ps1 `card_refund_dialog` Instructions の「論理削除」→「払戻済」表記訂正は別 Issue (#$A21_ISSUE) で対応予定
```

挿入時の前後コンテキストは Step 1 の grep 結果に基づき、既存 `### 変更` セクションの先頭または末尾に追加（PR #1436 のパターンを踏襲）。

- [ ] **Step 3: 結果を確認**

Run:
```bash
head -30 /mnt/d/OneDrive/交通系/src/ICCardManager/CHANGELOG.md
```

Expected: 3 行のエントリが `## [Unreleased]` 配下に追加され、`#1415` と `#$A21_ISSUE` の両 Issue 番号が含まれていること。

---

## Task 6: 全体検証

**目的:** コミット前に変更全体を一度確認する。

- [ ] **Step 1: git diff で変更全体を確認**

Run:
```bash
git -C /mnt/d/OneDrive/交通系/src diff --stat
```

Expected: 3 ファイル変更:
- `ICCardManager/CHANGELOG.md`（+3 行程度）
- `ICCardManager/docs/manual/管理者マニュアル.md`（+4 行程度: 画像参照 2 つ + 前後の空行）
- `ICCardManager/tools/TakeScreenshots.ps1`（+1 -1 行）

- [ ] **Step 2: 画像参照のパスが既存と一貫していることを確認**

Run:
```bash
grep -E '../screenshots/(card_edit_dialog|card_refund_dialog)\.png' /mnt/d/OneDrive/交通系/src/ICCardManager/docs/manual/管理者マニュアル.md
```

Expected: 2 行（§5.3 と §5.5 の画像参照）が出力される。

- [ ] **Step 3: ps1 内の `card_edit_dialog` と `card_refund_dialog` エントリが両方存在することを確認**

Run:
```bash
grep -n 'Name = "card_edit_dialog\|Name = "card_refund_dialog' /mnt/d/OneDrive/交通系/src/ICCardManager/tools/TakeScreenshots.ps1
```

Expected: 2 行（`card_edit_dialog.png` と `card_refund_dialog.png`）が出力される。

---

## Task 7: コミット

**Files:** Task 2-5 で変更した 3 ファイル

- [ ] **Step 1: 個別ファイルをステージング**

Run:
```bash
git -C /mnt/d/OneDrive/交通系/src add \
  ICCardManager/docs/manual/管理者マニュアル.md \
  ICCardManager/tools/TakeScreenshots.ps1 \
  ICCardManager/CHANGELOG.md
```

- [ ] **Step 2: ステージング内容を確認**

Run:
```bash
git -C /mnt/d/OneDrive/交通系/src diff --cached --stat
```

Expected: 3 ファイルがすべてステージングされている。`docs/screenshots/` 配下の untracked PNG や `mermaid-filter.err` が含まれていないこと。

- [ ] **Step 3: コミット作成**

`$A21_ISSUE` は Task 1 で取得した番号で置換。

```bash
git -C /mnt/d/OneDrive/交通系/src commit -m "$(cat <<'EOF'
docs: 管理者マニュアル §5.3/§5.5 交通系ICカード編集・払い戻しダイアログのスクリーンショット参照追加 (Issue #1415)

§5.3「交通系ICカード情報の編集」/ §5.5「交通系ICカードの払い戻し」用画像参照
（card_edit_dialog.png / card_refund_dialog.png）2 件を追加。
TakeScreenshots.ps1 既存エントリ (PR #1427) のうち card_edit_dialog の
Instructions を「右側の編集フォーム」明記に精緻化（PR #1436 で staff_edit_dialog
に対し実施した精緻化と同パターン）。

§5.5 本文「論理削除」表記の実装乖離（実装は Issue #530 の「払戻済」状態）と
ps1 行 484 の同種表記は本 PR ではスコープ外とし、別 Issue (#$A21_ISSUE) で対応。
画像本体は別コミットで追加予定。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 4: コミット結果確認**

Run:
```bash
git -C /mnt/d/OneDrive/交通系/src log --oneline -3
```

Expected: 直近 2 コミットが本ブランチの設計書（`3229c17`）と本コミット。

---

## Task 8: push と PR 作成

**目的:** リモートに push し、Pull Request を作成。

- [ ] **Step 1: リモートに push**

Run:
```bash
git -C /mnt/d/OneDrive/交通系/src push -u origin docs/issue-1415-section5-card-edit-refund-screenshot
```

Expected: GitHub への push 成功。

- [ ] **Step 2: PR 本文ファイルを一時作成**

`/tmp/pr1415_body.md` に以下を書き出す（`$A21_ISSUE` を実番号に置換）:

```markdown
## Summary

- 管理者マニュアル §5.3 / §5.5 にダイアログのスクリーンショット参照を追加 (Issue #1415)
- `tools/TakeScreenshots.ps1` の `card_edit_dialog` Instructions を「右側の編集フォーム」明記に精緻化（PR #1436 と同パターン）
- 画像本体は `tools\TakeScreenshots.ps1 -Only card_edit_dialog,card_refund_dialog` で別コミット予定

## Follow-up

§5.5 本文「論理削除されます」と ps1 行 484 「論理削除警告」の表記乖離（実装は Issue #530 の「払戻済」状態）は別 Issue #$A21_ISSUE で対応。

## Test plan

- [ ] 当ブランチを checkout 後、`pwsh tools\TakeScreenshots.ps1 -Only card_edit_dialog,card_refund_dialog` を実行
- [ ] CardManageDialog で行選択 → 「編集」 → 右ペインに編集フォーム（種別／管理番号／備考）が表示された状態で Enter
- [ ] 同画面で行選択 → 「払い戻し」 → 警告 MessageBox（黄色三角アイコン + Yes/No）が表示された状態で Enter
- [ ] 撮影された 2 枚を `ICCardManager/docs/screenshots/` に保存し、Markdown プレビューで参照が正しいことを確認

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

- [ ] **Step 3: PR を作成**

Run:
```bash
gh pr create \
  --title 'docs: 管理者マニュアル §5.3/§5.5 交通系ICカード編集・払い戻しダイアログのスクリーンショット追加 (Issue #1415)' \
  --body-file /tmp/pr1415_body.md
```

Expected: `https://github.com/<owner>/<repo>/pull/<PR_NUMBER>` の URL が出力される。

- [ ] **Step 4: 一時ファイル削除**

```bash
rm /tmp/pr1415_body.md
```

- [ ] **Step 5: PR URL をユーザーに報告**

PR URL と、ユーザーへの撮影依頼内容（設計書の「テスト方針」セクション参照）を会話に出力。

---

## 完了条件

- 新規 follow-up Issue (`#$A21_ISSUE`) が作成されている
- ブランチ `docs/issue-1415-section5-card-edit-refund-screenshot` に 2 コミット（設計書 + 本実装）が積まれている
- マニュアル §5.3 / §5.5 に画像参照行が含まれている
- ps1 行 478 の Instructions が「右側の編集フォーム」明記版に置換されている
- CHANGELOG `[Unreleased]` 配下に 3 行のエントリが追加されている
- PR が GitHub に作成されている
- ユーザーに PR URL と撮影依頼が伝達されている

## スコープ外（明示的に行わない）

- 画像本体ファイル（`card_edit_dialog.png` / `card_refund_dialog.png`）のコミット
- §5.5 マニュアル本文「論理削除されます」表記の訂正（A2-1 Issue 対象）
- ps1 行 484 `card_refund_dialog` Instructions の「論理削除警告」訂正（A2-1 Issue 対象）
- §5.4「交通系ICカードの削除」のスクリーンショット追加（Issue #1415 範囲外）
- `docs/screenshots/` 配下の前作業由来の untracked ファイル（`error_no_reader.png` 等）への対処（本 Issue 範囲外）
