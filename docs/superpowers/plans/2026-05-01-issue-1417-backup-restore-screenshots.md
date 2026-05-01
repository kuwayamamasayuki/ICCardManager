# Issue #1417 §6.1/§6.2 バックアップ完了通知・リストア一覧スクリーンショット追加 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 管理者マニュアル §6.1「手動バックアップ」/ §6.2「リストア（復元）」に、バックアップ完了ステータス・リストア用バックアップ一覧・ファイル選択ダイアログのスクリーンショット参照を追加し、§6.1 step 3 の文言を実装(ステータスバー表示)に整合させる。

**Architecture:** Markdown 文書 (`管理者マニュアル.md`) + CHANGELOG への 1 行追記のみ。コードロジック・ps1・スクリーンショット撮影スクリプトは変更なし (PR #1427 で既設)。画像本体ファイルはユーザーが別コミットで追加 (Issue #1415 / #1416 と同パターン)。

**Tech Stack:** Markdown / Git。設計書: `docs/superpowers/specs/2026-05-01-issue-1417-backup-restore-screenshots-design.md`。

---

## File Structure

| ファイル | 役割 | 変更 |
|---|---|---|
| `ICCardManager/docs/manual/管理者マニュアル.md` | 管理者向けマニュアル本体。§6.1/§6.2 を編集 | Modify |
| `ICCardManager/CHANGELOG.md` | 変更履歴の Single Source of Truth | Modify (Unreleased セクションに 1 行追記) |
| `ICCardManager/tools/TakeScreenshots.ps1` | 撮影スクリプト | **変更なし** (PR #1427 で既設) |
| `ICCardManager/docs/screenshots/backup_completed_status.png` | バックアップ完了ステータス画像 | 別コミット (ユーザーが撮影後に add) |
| `ICCardManager/docs/screenshots/restore_list.png` | リストア一覧画像 | 別コミット |
| `ICCardManager/docs/screenshots/restore_file_dialog.png` | ファイル選択ダイアログ画像 | 別コミット |

---

## Task 1: §6.1「手動バックアップ」step 3 文言修正と画像追加

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md` (§6.1 手動バックアップ、行 897〜905 付近)

- [ ] **Step 1: 該当箇所を Read で確認**

Read `ICCardManager/docs/manual/管理者マニュアル.md` の行 895〜910 付近を読み、編集対象の現行テキストを確認。具体的には以下のリストブロックが対象:

```markdown
1. 「システム管理」画面（F6）を開きます
2. 「手動バックアップ」セクションの「バックアップを作成」ボタンをクリックします
3. バックアップが設定済みの保存先に作成されます

バックアップファイル: `backup_YYYYMMDD_HHMMSS.db`
```

- [ ] **Step 2: Edit で step 3 の文言修正と画像参照追加**

Edit ツールで以下を置換:

`old_string`:
```
3. バックアップが設定済みの保存先に作成されます

バックアップファイル: `backup_YYYYMMDD_HHMMSS.db`
```

`new_string`:
```
3. バックアップが設定済みの保存先に作成され、ステータスバーに「バックアップを作成しました: <ファイル名>」と表示されます

   ![手動バックアップ完了時のステータス表示（ステータスバーに完了メッセージ）](../screenshots/backup_completed_status.png){width=50%}

バックアップファイル: `backup_YYYYMMDD_HHMMSS.db`
```

- [ ] **Step 3: 編集後の Markdown を Read で再確認**

Read で行 895〜915 付近を再読し、以下を確認:
- step 3 の文言が「ステータスバーに『バックアップを作成しました: <ファイル名>』と表示されます」に変わっている
- step 3 の直後に画像 `backup_completed_status.png` 参照が追加されている (`width=50%`)
- 既存の `バックアップファイル: backup_YYYYMMDD_HHMMSS.db` 行は残っている

---

## Task 2: §6.2「リストア（復元）」に画像 2 点追加

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md` (§6.2 リストア、行 915〜926 付近)

- [ ] **Step 1: 該当箇所を Read で確認**

Read `ICCardManager/docs/manual/管理者マニュアル.md` の行 915〜930 付近を読む。編集対象は以下:

```markdown
### 6.2 リストア（復元）

1. 「システム管理」画面（F6）を開きます
2. 「リストア（データ復元）」セクションのバックアップ一覧から復元したいファイルを選択します
3. 「選択したバックアップからリストア」ボタンをクリックします
4. 確認ダイアログで「はい」をクリックします

外部のバックアップファイルから復元する場合は、「ファイルを指定してリストア」ボタンをクリックし、ファイルを選択します。

> **警告**: リストアを実行すると、現在のデータは上書きされます。
```

- [ ] **Step 2: Edit で step 2 直後に restore_list.png を追加**

Edit ツールで以下を置換:

`old_string`:
```
2. 「リストア（データ復元）」セクションのバックアップ一覧から復元したいファイルを選択します
3. 「選択したバックアップからリストア」ボタンをクリックします
```

`new_string`:
```
2. 「リストア（データ復元）」セクションのバックアップ一覧から復元したいファイルを選択します

   ![リストア用バックアップ一覧（ファイル名・タイムスタンプ・選択状態）](../screenshots/restore_list.png){width=50%}

3. 「選択したバックアップからリストア」ボタンをクリックします
```

- [ ] **Step 3: Edit で「ファイルを指定してリストア」段落直後に restore_file_dialog.png を追加**

Edit ツールで以下を置換:

`old_string`:
```
外部のバックアップファイルから復元する場合は、「ファイルを指定してリストア」ボタンをクリックし、ファイルを選択します。

> **警告**: リストアを実行すると、現在のデータは上書きされます。
```

`new_string`:
```
外部のバックアップファイルから復元する場合は、「ファイルを指定してリストア」ボタンをクリックし、ファイルを選択します。

![「ファイルを指定してリストア」のファイル選択ダイアログ](../screenshots/restore_file_dialog.png){width=60%}

> **警告**: リストアを実行すると、現在のデータは上書きされます。
```

- [ ] **Step 4: 編集後の Markdown を Read で再確認**

Read で行 915〜935 付近を再読し、以下を確認:
- step 2 直後に `restore_list.png` 参照 (width=50%) がある
- step 3 / step 4 / 「外部のバックアップファイル」段落の文言は無変更
- 「ファイルを指定してリストア」段落直後に `restore_file_dialog.png` 参照 (width=60%) がある
- `> **警告**: リストアを実行すると、現在のデータは上書きされます。` 行は無変更

---

## Task 3: CHANGELOG に変更履歴 1 行追加

**Files:**
- Modify: `ICCardManager/CHANGELOG.md` (`## [Unreleased]` セクション「変更」配下)

- [ ] **Step 1: CHANGELOG.md の `[Unreleased]` セクションを Read で確認**

Read `ICCardManager/CHANGELOG.md` の冒頭〜50 行付近を読み、`## [Unreleased]` 直下の「変更」(または `### 変更`) サブセクションに既存エントリ (Issue #1416 等) があることを確認。

- [ ] **Step 2: Edit で 1 行追加**

`### 変更` セクションの末尾 (または既存 Issue #1416 行の直後) に以下を追加:

```markdown
- 管理者マニュアル §6.1「手動バックアップ」/ §6.2「リストア（復元）」にバックアップ完了ステータス・リストア用バックアップ一覧・ファイル選択ダイアログのスクリーンショット参照（`backup_completed_status.png` / `restore_list.png` / `restore_file_dialog.png`）を追加。あわせて §6.1 step 3 の文言をステータスバー表示の現行実装に整合 (Issue #1417)
```

具体的な `old_string` / `new_string` は Step 1 で読み取った内容に合わせて決定する (既存末尾行の直後に挿入する形)。`replace_all` は使わない (一意性保証)。

- [ ] **Step 3: 編集後の CHANGELOG.md を Read で再確認**

Read で `## [Unreleased]` セクション全体を再読し、新規 1 行が「変更」配下に正しく挿入されていることを確認。フォーマット (ハイフン+半角スペース、Issue 番号は末尾) が他エントリと一致していること。

---

## Task 4: 変更内容を確認しコミット

**Files:**
- なし (git 操作のみ)

- [ ] **Step 1: `git status` で変更ファイル一覧を確認**

Run: `git status --short`

Expected: 以下 2 ファイルが modified として表示される:
```
 M ICCardManager/CHANGELOG.md
 M ICCardManager/docs/manual/管理者マニュアル.md
```

予期しないファイルがある場合は中断して原因を調査。

- [ ] **Step 2: `git diff` で変更内容を確認**

Run: `git diff ICCardManager/CHANGELOG.md ICCardManager/docs/manual/管理者マニュアル.md`

Expected: Task 1〜3 で意図した編集のみが diff に現れる:
- 管理者マニュアル.md §6.1 step 3 の文言修正 + `backup_completed_status.png` 参照追加
- 管理者マニュアル.md §6.2 step 2 直後に `restore_list.png` 参照追加
- 管理者マニュアル.md §6.2 「ファイルを指定してリストア」段落直後に `restore_file_dialog.png` 参照追加
- CHANGELOG.md `[Unreleased]` 「変更」に 1 行追記

意図しない変更があれば中断して修正。

- [ ] **Step 3: 個別ファイル指定でステージング**

Run:
```bash
git add ICCardManager/docs/manual/管理者マニュアル.md ICCardManager/CHANGELOG.md
```

(`.claude/rules/git-workflow.md` 規約により `git add -A` / `git add .` は使用禁止。個別指定する。)

- [ ] **Step 4: コミット**

Run:
```bash
git commit -m "$(cat <<'EOF'
docs: 管理者マニュアル §6.1/§6.2 バックアップ完了ステータス・リストア一覧スクリーンショット追加 (Issue #1417)

- §6.1 step 3 の文言をステータスバー表示の現行実装に整合 (「完了通知ダイアログ」表現は実装と乖離するため不採用)
- §6.1 step 3 直後に backup_completed_status.png を追加
- §6.2 step 2 直後に restore_list.png を追加
- §6.2 「ファイルを指定してリストア」段落直後に restore_file_dialog.png を追加
- 画像本体ファイルは別コミット (Issue #1415/#1416 と同パターン)
EOF
)"
```

- [ ] **Step 5: コミット結果を確認**

Run: `git log --oneline -2`

Expected: 直近に上記 docs コミットが、その前に Task 0 (設計書コミット `d711a32`) がある状態。

---

## Task 5: push と PR 作成

**Files:**
- なし (gh CLI 操作のみ)

- [ ] **Step 1: リモートブランチに push**

Run:
```bash
git push -u origin docs/issue-1417-backup-restore-screenshots
```

Expected: 新規ブランチが GitHub に push され、tracking 設定される。

- [ ] **Step 2: PR 作成**

Run:
```bash
gh pr create --title "docs: 管理者マニュアル §6.1/§6.2 バックアップ・リストア手順のスクリーンショット追加 (Issue #1417)" --body "$(cat <<'EOF'
## Summary

- 管理者マニュアル §6.1「手動バックアップ」/ §6.2「リストア（復元）」にスクリーンショット参照を 3 点追加 (Issue #1417)
- §6.1 step 3 の文言をステータスバー表示の現行実装に整合させ、文書と実装の乖離を解消
- 画像本体は別コミット (撮影後に追加)。本 PR には設計書 + マニュアル本文 + CHANGELOG のみ

## 追加する画像参照

| 章 | 画像 | width | 用途 |
|---|---|---|---|
| §6.1 step 3 | `backup_completed_status.png` | 50% | バックアップ完了時のステータスバー表示 |
| §6.2 step 2 | `restore_list.png` | 50% | リストア用バックアップ一覧 |
| §6.2 「ファイルを指定してリストア」段落 | `restore_file_dialog.png` | 60% | OS 標準ファイル選択ダイアログ |

## 文言調整 (§6.1 のみ)

`バックアップが設定済みの保存先に作成されます` → `バックアップが設定済みの保存先に作成され、ステータスバーに「バックアップを作成しました: <ファイル名>」と表示されます`

実装側 (`SystemManageViewModel.SetStatus(...)`) は完了通知をダイアログではなくステータスバーで行うため、文書を実装に整合させた。

## スコープ外

- 画像本体ファイルのコミット (撮影後にユーザーが別コミットで追加)
- 完了通知ダイアログの新規実装 (別 Issue で議論)
- ps1 スクリプト変更 (PR #1427 で既設エントリを流用)

## Test plan

本 PR は Markdown 文書のみのためコードロジック単体テストは対象外。動作確認手順:

- [ ] ブランチを checkout
- [ ] `.\tools\TakeScreenshots.ps1 -Only backup_completed_status,restore_list,restore_file_dialog` で 3 画像を撮影
- [ ] `ICCardManager/docs/screenshots/` に 3 ファイルが保存されることを確認
- [ ] Markdown プレビューで以下を確認:
  - [ ] §6.1 step 3 直後に `backup_completed_status.png` が表示される
  - [ ] §6.2 step 2 直後に `restore_list.png` が表示される
  - [ ] §6.2「ファイルを指定してリストア」段落直後に `restore_file_dialog.png` が表示される
- [ ] 撮影画像を別コミットで追加 (Issue #1415/#1416 と同パターン)

## 関連

- 設計書: `docs/superpowers/specs/2026-05-01-issue-1417-backup-restore-screenshots-design.md`
- 関連 Issue: #1415 / #1416 (同系統のスクリーンショット追加 PR)
- 関連 PR: #1427 (TakeScreenshots.ps1 拡張、本 PR の画像エントリを既設)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 3: PR URL を確認しユーザーに共有**

Run: `gh pr view --json url,number -q '.url'`

Expected: 作成された PR の URL が出力される。これをユーザーに報告し、撮影と画像コミットの動作確認依頼を伝える。

---

## Self-Review チェックリスト (実行者向け)

実装完了後、以下を自己確認:

1. **Spec coverage:** 設計書 §「修正対象」の各項目が Task 1〜3 に対応しているか
   - §6.1 step 3 文言修正 + 画像追加 → Task 1
   - §6.2 step 2 直後 + 「ファイルを指定」直後の 2 画像追加 → Task 2
   - CHANGELOG 1 行追記 → Task 3
2. **Placeholder scan:** 編集後のマニュアル / CHANGELOG に「TBD」「TODO」「<挿入位置>」等の残骸がないか
3. **Image alt text 一貫性:** alt text に「ステータスバー」「リストア用バックアップ一覧」「ファイル選択ダイアログ」等の文脈情報が含まれているか (アクセシビリティ)
4. **画像幅:** §6.1 = 50%, §6.2 step 2 = 50%, §6.2 ファイル選択 = 60% で設計書と一致しているか
5. **`> **警告**` 等の引用ブロック構造が崩れていないか** (画像追加で隣接する Markdown ブロックの構造を破壊していないこと)
