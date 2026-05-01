# Issue #1416: §5.6.5/§6.4 CSVインポートプレビュー画面のスクリーンショット追加 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 管理者マニュアル §5.6.5「月途中からの履歴入力（CSVインポート）」と §6.4「データインポート」に CSV インポートプレビュー画面（`import_preview.png`）の参照を追加し、機能の便利さを視覚的に伝える。

**Architecture:** 1 枚の画像（`import_preview.png`）を 2 箇所（§5.6.5 step 3 末尾 / §6.4 step 4 直後）から共通参照する Pandoc Markdown 編集。`tools/TakeScreenshots.ps1` の既存エントリ（行 330）と「流用宣言コメント」（行 487）は変更しない。Issue #1415 の `card_edit_dialog.png` / `card_refund_dialog.png` 追加と同じパターン。

**Tech Stack:** Pandoc Markdown (画像参照 + `{width=N%}` 拡張記法)、Git、GitHub CLI (`gh`)

**Spec:** `docs/superpowers/specs/2026-05-01-issue-1416-csv-import-preview-screenshot-design.md`

---

## File Structure

| ファイル | 役割 | 変更種別 |
|---|---|---|
| `ICCardManager/docs/manual/管理者マニュアル.md` | 管理者向け運用マニュアル本体。§5.6.5 と §6.4 の 2 箇所を編集 | Modify |
| `ICCardManager/CHANGELOG.md` | バージョン履歴の Single Source of Truth。`### Unreleased` の該当カテゴリに 1 行追記 | Modify |
| `ICCardManager/tools/TakeScreenshots.ps1` | スクリーンショット撮影スクリプト。**変更なし**（行 330 のエントリを流用） | (no change) |
| `ICCardManager/docs/screenshots/import_preview.png` | プレビュー画面の実画像。**本 PR では追加しない**（撮影後に別コミットで追加） | (out of scope) |

---

## Task 1: 管理者マニュアル §5.6.5 step 3 末尾に画像参照を追加

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md` (周辺: line 803〜806)

- [ ] **Step 1: 該当箇所の現在の状態を確認**

Run: `sed -n '800,810p' ICCardManager/docs/manual/管理者マニュアル.md`

期待される出力（参照用）:
```
3. **「プレビュー」** ボタンをクリック
   - ファイル選択ダイアログでCSVファイルを選択します
   - 変更点プレビューが表示されます

4. プレビュー結果を確認
```

行番号がずれていた場合は次の Step の Edit 対象を実情に合わせる（編集対象文字列はユニークなので問題ないが念のため確認）。

- [ ] **Step 2: 画像参照を挿入**

Edit ツールで以下を適用:

```
old_string:
3. **「プレビュー」** ボタンをクリック
   - ファイル選択ダイアログでCSVファイルを選択します
   - 変更点プレビューが表示されます

4. プレビュー結果を確認

new_string:
3. **「プレビュー」** ボタンをクリック
   - ファイル選択ダイアログでCSVファイルを選択します
   - 変更点プレビューが表示されます

   ![CSVインポートのプレビュー画面（追加=緑/修正=オレンジ/スキップ=灰/復元=青のアクション色分けと「プレビュー」「インポート実行」「直接インポート」ボタン）](../screenshots/import_preview.png){width=85%}

4. プレビュー結果を確認
```

注意:
- インデントはリスト内の継続要素として 3 スペース（list item の本文と揃える）
- alt text の中身は xaml の grep で確認した 3 ボタン名のみに留める（「キャンセル」など未確認のボタンは含めない）
- 参照パス `../screenshots/import_preview.png` は `docs/manual/` から `docs/screenshots/` への相対パス

- [ ] **Step 3: 編集結果を確認**

Run: `sed -n '800,815p' ICCardManager/docs/manual/管理者マニュアル.md`

期待される出力:
```
3. **「プレビュー」** ボタンをクリック
   - ファイル選択ダイアログでCSVファイルを選択します
   - 変更点プレビューが表示されます

   ![CSVインポートのプレビュー画面（追加=緑/修正=オレンジ/スキップ=灰/復元=青のアクション色分けと「プレビュー」「インポート実行」「直接インポート」ボタン）](../screenshots/import_preview.png){width=85%}

4. プレビュー結果を確認
```

---

## Task 2: 管理者マニュアル §6.4 step 4 直後に画像参照を追加

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md` (周辺: line 962〜970)

- [ ] **Step 1: 該当箇所の現在の状態を確認**

Run: `sed -n '961,972p' ICCardManager/docs/manual/管理者マニュアル.md`

期待される出力（参照用）:
```
#### インポートの手順

1. データ種別を選択
2. 「利用履歴」の場合は、インポート先のカードを指定（CSVにカードIDmが含まれている場合は不要）
3. **「プレビュー」** ボタンでCSVファイルを選択し、変更内容を確認
4. 問題がなければ **「インポート実行」** ボタンで取り込み

プレビューなしでインポートしたい場合は、**「直接インポート」** ボタンからCSVファイルを選択して即座に取り込むこともできます。
```

- [ ] **Step 2: 画像参照を挿入**

Edit ツールで以下を適用:

```
old_string:
3. **「プレビュー」** ボタンでCSVファイルを選択し、変更内容を確認
4. 問題がなければ **「インポート実行」** ボタンで取り込み

プレビューなしでインポートしたい場合は、**「直接インポート」** ボタンからCSVファイルを選択して即座に取り込むこともできます。

new_string:
3. **「プレビュー」** ボタンでCSVファイルを選択し、変更内容を確認
4. 問題がなければ **「インポート実行」** ボタンで取り込み

![CSVインポートのプレビュー画面（変更点が色分けで表示される）](../screenshots/import_preview.png){width=85%}

プレビューなしでインポートしたい場合は、**「直接インポート」** ボタンからCSVファイルを選択して即座に取り込むこともできます。
```

注意:
- §6.4 の手順リストはインデント無しの番号付きリスト（§5.6.5 の入れ子リストと違う）。よって画像参照もトップレベルに配置（行頭からの記述、インデント無し）
- alt text は §5.6.5 のものより簡潔に（§6.4 は §5.6.5 への参照リンクで詳細委譲する構造のため、ここでは詳細解説不要）
- 同じファイル `import_preview.png` を 2 箇所から参照する形

- [ ] **Step 3: 編集結果を確認**

Run: `sed -n '961,973p' ICCardManager/docs/manual/管理者マニュアル.md`

期待される出力:
```
#### インポートの手順

1. データ種別を選択
2. 「利用履歴」の場合は、インポート先のカードを指定（CSVにカードIDmが含まれている場合は不要）
3. **「プレビュー」** ボタンでCSVファイルを選択し、変更内容を確認
4. 問題がなければ **「インポート実行」** ボタンで取り込み

![CSVインポートのプレビュー画面（変更点が色分けで表示される）](../screenshots/import_preview.png){width=85%}

プレビューなしでインポートしたい場合は、**「直接インポート」** ボタンからCSVファイルを選択して即座に取り込むこともできます。
```

---

## Task 3: CHANGELOG.md に変更を追記

**Files:**
- Modify: `ICCardManager/CHANGELOG.md` (`### Unreleased` セクション内の適切なカテゴリ)

- [ ] **Step 1: `### Unreleased` セクションのカテゴリ構成を確認**

Run: `grep -n '^###\|^\*\*' ICCardManager/CHANGELOG.md | head -30`

期待される出力（参考）:
```
1:### Unreleased
N:**セキュリティ修正**
M:**開発基盤**
...
```

ドキュメント追加に該当するカテゴリ（**ドキュメント** / **改善** / **変更** 等）を見つける。Issue #1415 の追記がどのカテゴリ配下にあるかを参考にする:

Run: `grep -n -B1 -A1 'Issue #1415' ICCardManager/CHANGELOG.md`

これで Issue #1415 と同じカテゴリに追記すべき位置が分かる。

- [ ] **Step 2: Issue #1415 の追記カテゴリの直後に Issue #1416 行を追加**

Edit ツールで Issue #1415 の追記の **直後**（同じカテゴリ内、近い位置）に以下を追加:

```
- 管理者マニュアル §5.6.5「月途中からの履歴入力（CSVインポート）」/ §6.4「データインポート」にCSVインポートのプレビュー画面のスクリーンショット参照（`import_preview.png`）を追加 (Issue #1416)
```

具体的な編集例（Issue #1415 行が次のような形だった場合）:

```
old_string:
- 管理者マニュアル §5.3「交通系ICカード情報の編集」/ §5.5「交通系ICカードの払い戻し」にスクリーンショット参照（`card_edit_dialog.png` / `card_refund_dialog.png`）を追加 (Issue #1415)

new_string:
- 管理者マニュアル §5.3「交通系ICカード情報の編集」/ §5.5「交通系ICカードの払い戻し」にスクリーンショット参照（`card_edit_dialog.png` / `card_refund_dialog.png`）を追加 (Issue #1415)
- 管理者マニュアル §5.6.5「月途中からの履歴入力（CSVインポート）」/ §6.4「データインポート」にCSVインポートのプレビュー画面のスクリーンショット参照（`import_preview.png`）を追加 (Issue #1416)
```

注: Issue #1415 行は実際の CHANGELOG 内容に合わせて完全一致させること。文言が微妙に異なる場合は実物を `grep -n 'Issue #1415' ICCardManager/CHANGELOG.md` で確認してから貼り付ける。

- [ ] **Step 3: 追記結果を確認**

Run: `grep -n 'Issue #141[56]' ICCardManager/CHANGELOG.md`

期待される出力:
```
N: ... Issue #1415 ...
N+1: ... Issue #1416 ...
```

両エントリが連続して出現すれば OK。

---

## Task 4: ビルドと差分確認

**Files:** (no file changes — verification only)

- [ ] **Step 1: 全変更ファイルを diff で確認**

Run: `git diff --stat`

期待される出力:
```
 ICCardManager/CHANGELOG.md                       |  1 +
 ICCardManager/docs/manual/管理者マニュアル.md     |  4 ++++
 2 files changed, 5 insertions(+)
```

- [ ] **Step 2: 内容差分を確認**

Run: `git diff ICCardManager/docs/manual/管理者マニュアル.md`

確認ポイント:
- §5.6.5 と §6.4 の両方に画像参照行が追加されている
- alt text の内容が設計書通り（「キャンセル」が含まれていない）
- パスは `../screenshots/import_preview.png`
- `{width=85%}` が両方に付いている

Run: `git diff ICCardManager/CHANGELOG.md`

確認ポイント:
- Issue #1416 の 1 行のみが追加されている
- `### Unreleased` セクション内に位置している
- Issue #1415 の文体と整合する書き方になっている

- [ ] **Step 3: 単体テスト（該当なし）**

本 PR の変更は **Markdown 文書のみ** でコードロジック変更なし → 単体テスト対象外（設計書「テスト方針」セクション参照）。

ビルド確認も任意。実施するなら:
```
"/mnt/c/Program Files/dotnet/dotnet.exe" build
```
（成功するはず。本 PR がビルドに影響しないことの確認）

---

## Task 5: コミット

**Files:** (no file changes — git operations only)

- [ ] **Step 1: 変更ファイルをステージング**

`git add -A` は禁止（CLAUDE.md / `.claude/rules/git-workflow.md` 参照）。個別ファイル指定で:

```bash
git add ICCardManager/docs/manual/管理者マニュアル.md ICCardManager/CHANGELOG.md
```

- [ ] **Step 2: コミットメッセージを HEREDOC で作成しコミット**

```bash
git commit -m "$(cat <<'EOF'
docs: 管理者マニュアル §5.6.5/§6.4 CSVインポートプレビュー画面のスクリーンショット参照追加 (Issue #1416)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

メッセージのフォーマット根拠: 直近 Issue #1415 のコミット（`e696b88`）が「`docs: 管理者マニュアル §5.3/§5.5 交通系ICカード編集・払い戻しダイアログのスクリーンショット参照追加 (Issue #1415)`」と同形式のため踏襲。

- [ ] **Step 3: コミットが成功したか確認**

Run: `git log --oneline -3`

期待される出力:
```
<新コミットhash> docs: 管理者マニュアル §5.6.5/§6.4 CSVインポートプレビュー画面のスクリーンショット参照追加 (Issue #1416)
037c263 docs(spec): Issue #1416 §5.6.5/§6.4 CSVインポートプレビュー画面のスクリーンショット追加の設計書
4eb2ec7 <main の最新コミット>
```

---

## Task 6: push + PR 作成

**Files:** (no file changes — git/gh operations only)

- [ ] **Step 1: リモートに push**

Run:
```bash
git push -u origin docs/issue-1416-csv-import-preview-screenshot
```

期待される出力: `* [new branch]      docs/issue-1416-csv-import-preview-screenshot -> docs/issue-1416-csv-import-preview-screenshot`

- [ ] **Step 2: PR を作成**

Run:
```bash
gh pr create --title "docs: 管理者マニュアル §5.6.5/§6.4 CSVインポートプレビュー画面のスクリーンショット参照追加 (Issue #1416)" --body "$(cat <<'EOF'
## Summary

- 管理者マニュアル §5.6.5「月途中からの履歴入力（CSVインポート）」と §6.4「データインポート」に CSV インポートのプレビュー画面のスクリーンショット参照を追加
- 1 枚の画像（`import_preview.png`）を 2 箇所から共通参照する構成（プレビュー UI は両節で同一を指すため）
- `tools/TakeScreenshots.ps1` は変更なし（行 330 の既存エントリを流用、行 487 の「流用宣言コメント」と整合）
- 設計書: `docs/superpowers/specs/2026-05-01-issue-1416-csv-import-preview-screenshot-design.md`

Closes #1416

## Test plan

本 PR は Markdown 文書のみの変更でコードロジック変更なし → 単体テスト対象外。

ユーザーへの動作確認依頼:

- [ ] 当ブランチを checkout 後、テスト用 CSV を準備（4 アクション全種が出現する状態が望ましい：追加 / 修正 / スキップ / 復元）
- [ ] PowerShell で `.\tools\TakeScreenshots.ps1 -Only import_preview` を実行
- [ ] 「データ入出力」画面（F4） → CSV ファイル選択 → 「プレビュー」ボタン → プレビュー結果 DataGrid が色分け付きで表示された状態で Enter（→ `import_preview.png` が保存される）
- [ ] `ICCardManager/docs/screenshots/import_preview.png` を Markdown プレビューで参照確認:
  - §5.6.5 step 3 直後に表示されるか
  - §6.4 step 4 直後に表示されるか
- [ ] 撮影画像の追加コミットは別途実施（Issue #1415 と同パターン）

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 3: PR URL を取得し報告**

Run: `gh pr view --json url -q .url`

PR URL をユーザーに報告し、画像撮影を依頼。

---

## Self-Review Checklist (実装エンジニア向け)

実装完了後、以下を自己点検:

- [ ] §5.6.5 / §6.4 両方に画像参照が追加されている
- [ ] alt text に未確認のボタン名（「キャンセル」など）が含まれていない
- [ ] 画像幅は両方とも `{width=85%}`
- [ ] 参照パスは `../screenshots/import_preview.png`
- [ ] CHANGELOG に Issue #1416 の 1 行追記
- [ ] `tools/TakeScreenshots.ps1` を**触っていない**（流用方針）
- [ ] 画像実体ファイルを**コミットしていない**（撮影後に別コミット）
- [ ] コミットメッセージは Issue #1415 と同形式
- [ ] PR の Test plan に撮影手順が含まれる
