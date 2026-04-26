# Issue #1406: 管理者マニュアル §5.6.3 手順番号 10 欠番修正 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 管理者マニュアル §5.6.3 で `9 → 11 → 12` と飛び番になっている手順番号を `9 → 10 → 11` の連番に修正する。

**Architecture:** 番号 2 箇所の数字変更のみ（`11.` → `10.`、`12.` → `11.`）。手順内容、注意ブロックの位置、文章は一切変更しない。

**Tech Stack:** Markdown（GitHub Flavored Markdown）

---

## File Structure

| ファイル | 変更内容 |
|---|---|
| `ICCardManager/docs/manual/管理者マニュアル.md` | §5.6.3 内の `11.` → `10.`、`12.` → `11.`（行 654 / 658 付近） |

---

### Task 1: §5.6.3 の手順番号を詰める

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md`（行 654 / 658 付近）

- [ ] **Step 1: 現状確認**

Run: `awk 'NR>=640 && NR<=665' ICCardManager/docs/manual/管理者マニュアル.md`
Expected: 行 648 に `9. 繰越額の入力欄...`、行 654 に `11. 累計受入...`、行 658 に `12. 「OK」ボタンをクリック...` が確認できる

- [ ] **Step 2: §5.6.3 内に他の番号付き手順がないか確認（誤置換防止）**

Run: `awk '/^#### 5\.6\.3 /,/^#### 5\.6\.4 /' ICCardManager/docs/manual/管理者マニュアル.md | grep -nE '^[0-9]+\. '`
Expected: `1.` 〜 `9.` `11.` `12.` のみ（合計 11 行）。同節内に他の `10.` や `11.`/`12.` が無いことを確認

- [ ] **Step 3: `11.` の手順を `10.` に変更**

Edit `ICCardManager/docs/manual/管理者マニュアル.md`：
- old_string: `11. 累計受入・累計払出に、紙の出納簿の前月末時点での累計金額を入力します（デフォルトは0）`
- new_string: `10. 累計受入・累計払出に、紙の出納簿の前月末時点での累計金額を入力します（デフォルトは0）`

（同行は §5.6.3 内のみに存在する見込み。Step 2 で確認済み）

- [ ] **Step 4: `12.` の手順を `11.` に変更**

Edit `ICCardManager/docs/manual/管理者マニュアル.md`：
- old_string: `12. 「OK」ボタンをクリックすると、入力した繰越額が「5月から繰越」として記録されます`
- new_string: `11. 「OK」ボタンをクリックすると、入力した繰越額が「5月から繰越」として記録されます`

- [ ] **Step 5: 修正後の手順番号並び確認**

Run: `awk '/^#### 5\.6\.3 /,/^#### 5\.6\.4 /' ICCardManager/docs/manual/管理者マニュアル.md | grep -nE '^[0-9]+\. ' | sed 's/.*://' | awk -F. '{print $1}'`
Expected: `1 2 3 4 5 6 7 8 9 10 11`（連番、欠番なし）

- [ ] **Step 6: 同節を §5.6.3 として参照する他箇所がないか確認**

Run: `grep -n '5\.6\.3' ICCardManager/docs/manual/*.md`
Expected: 参照箇所をリストし、「手順 X」のような番号を伴う参照があれば追加修正が必要。番号を伴わない節タイトルのみの参照（例: `[5.6.3](#563-...)`）は影響なし

(注: §5.6.3 内の手順番号を直接参照している箇所はリポジトリ内に存在しない見込みだが、念のため機械的に確認する)

- [ ] **Step 7: コミット**

```bash
git add ICCardManager/docs/manual/管理者マニュアル.md
git commit -m "$(cat <<'EOF'
docs: 管理者マニュアル §5.6.3 の手順番号 10 欠番を修正 (Issue #1406)

§5.6.3「登録手順（紙の出納簿からの繰越の場合）」の手順が
9 → 11 → 12 と飛び番になっていた問題を修正:

- 旧: 9. 繰越額入力 → (注意×2) → 11. 累計入力 → 12. OK クリック
- 新: 9. 繰越額入力 → (注意×2) → 10. 累計入力 → 11. OK クリック

2 つの注意ブロックは手順 9（繰越額入力）の補足説明であり、
独立した手順ではないため、番号を詰めて連番に修正する。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: PR 作成

**Files:** なし（git/gh 操作のみ）

- [ ] **Step 1: ブランチ push**

```bash
git push -u origin docs/issue-1406-fix-step-numbering
```

- [ ] **Step 2: PR 作成**

```bash
gh pr create --title "docs: 管理者マニュアル §5.6.3 の手順番号 10 欠番を修正 (Issue #1406)" --body "$(cat <<'EOF'
## Summary

- 管理者マニュアル §5.6.3「登録手順（紙の出納簿からの繰越の場合）」の手順番号が \`9 → 11 → 12\` と飛び番になっていた問題を、\`9 → 10 → 11\` の連番に修正
- 設計書: \`docs/superpowers/specs/2026-04-26-issue-1406-step-numbering-fix-design.md\`
- Closes #1406

## Test plan

- [ ] 修正後の §5.6.3 を目視確認し、手順 1〜11 が連番で読めること
- [ ] \`grep\` で §5.6.3 内の手順番号並びが \`1 2 3 4 5 6 7 8 9 10 11\` であること

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Test Strategy

ドキュメントのみの修正のため、単体テスト対象外。

**手動確認項目（PR レビュアー / マージ前）:**

1. `ICCardManager/docs/manual/管理者マニュアル.md` の §5.6.3 を目視で読み、手順 1〜11 が連番で表示されること
2. 手順内容、注意ブロックの位置、文章に意図しない変更がないこと（diff で確認）
3. 同節を「手順 10」「手順 11」など番号で参照する他箇所がないこと（プラン Step 6 でカバー済み）

## ドキュメント更新

- `CHANGELOG.md`: 軽微なドキュメント番号修正のため追記不要（Issue #1391 / #1392 等の同種修正と同じ運用）
- 設計書 (`docs/design/`) / 規約 (`.claude/rules/`): 該当なし
