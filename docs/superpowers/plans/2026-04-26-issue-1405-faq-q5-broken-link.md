# Issue #1405: FAQ Q5 の参照リンク修正 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ユーザーマニュアル §8 FAQ Q5 が参照する存在しない管理者マニュアル §3.5 を、実在する §2.1 と §2.5 への参照に差し替える。

**Architecture:** ドキュメントの 1 行修正のみ。コード変更・単体テストなし。手動確認とリンクテキストの目視チェックで完了。

**Tech Stack:** Markdown（GitHub Flavored Markdown）

---

## File Structure

| ファイル | 変更内容 |
|---|---|
| `ICCardManager/docs/manual/ユーザーマニュアル.md` | §8 Q5 の末尾 1 文を差し替え（行 675 付近） |

---

### Task 1: FAQ Q5 のリンク文字列を差し替える

**Files:**
- Modify: `ICCardManager/docs/manual/ユーザーマニュアル.md`（行 675 付近）

- [ ] **Step 1: 現状確認**

Run: `grep -n '3.5 共有フォルダの設定' ICCardManager/docs/manual/ユーザーマニュアル.md`
Expected: 行番号 675 付近で 1 件マッチ

- [ ] **Step 2: 修正対象テキストの正確な特定**

修正対象の旧文字列：

```
詳しい設定手順は管理者マニュアルの「3.5 共有フォルダの設定」を参照してください。
```

新文字列：

```
詳しい設定手順は管理者マニュアルの「2.1 複数PCで利用する場合の事前準備」および「2.5 共有モードについて」を参照してください。
```

- [ ] **Step 3: Edit ツールで差し替え**

Edit `ICCardManager/docs/manual/ユーザーマニュアル.md`：
- old_string: `詳しい設定手順は管理者マニュアルの「3.5 共有フォルダの設定」を参照してください。`
- new_string: `詳しい設定手順は管理者マニュアルの「2.1 複数PCで利用する場合の事前準備」および「2.5 共有モードについて」を参照してください。`

- [ ] **Step 4: 修正後の検証**

Run: `grep -n '3.5 共有フォルダの設定' ICCardManager/docs/manual/ユーザーマニュアル.md`
Expected: マッチ 0 件（旧参照が完全に消えていること）

Run: `grep -n '2.1 複数PCで利用する場合の事前準備' ICCardManager/docs/manual/ユーザーマニュアル.md`
Expected: 行 675 付近で新参照が 1 件以上マッチ

Run: `grep -n '2.5 共有モードについて' ICCardManager/docs/manual/ユーザーマニュアル.md`
Expected: 行 675 付近で新参照が 1 件以上マッチ

- [ ] **Step 5: 参照先節が管理者マニュアルに実在することの確認**

Run: `grep -n '^### 2\.1 複数PCで利用する場合の事前準備\|^### 2\.5 共有モードについて' ICCardManager/docs/manual/管理者マニュアル.md`
Expected: 2 件マッチ（両節とも実在）

- [ ] **Step 6: コミット**

```bash
git add ICCardManager/docs/manual/ユーザーマニュアル.md
git commit -m "$(cat <<'EOF'
docs: FAQ Q5 の管理者マニュアル参照リンクを実在節に修正 (Issue #1405)

ユーザーマニュアル §8 FAQ Q5 が参照していた管理者マニュアル §3.5
（欠番で実在しない）を、実在する以下 2 節への参照に差し替え:

- 管理者マニュアル §2.1 複数PCで利用する場合の事前準備
- 管理者マニュアル §2.5 共有モードについて

Q5 本文が「インストーラーで指定」「F5 設定画面で変更」の概要を
述べているため、それぞれの詳細手順をカバーする 2 節を案内する。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: PR 作成

**Files:** なし（git/gh 操作のみ）

- [ ] **Step 1: ブランチ push**

```bash
git push -u origin docs/issue-1405-fix-faq-q5-broken-link
```

- [ ] **Step 2: PR 作成**

```bash
gh pr create --title "docs: FAQ Q5 の管理者マニュアル参照リンクを実在節に修正 (Issue #1405)" --body "$(cat <<'EOF'
## Summary

- ユーザーマニュアル §8 FAQ Q5 が参照していた管理者マニュアル「3.5 共有フォルダの設定」は欠番で実在しないため、実在する §2.1 / §2.5 への参照に差し替え
- 設計書: `docs/superpowers/specs/2026-04-26-issue-1405-faq-q5-broken-link-design.md`
- Closes #1405

## Test plan

- [ ] ユーザーマニュアル.md の Q5 を目視確認し、参照節タイトルが管理者マニュアルの実在節と一致すること
- [ ] `grep` で旧参照「3.5 共有フォルダの設定」がドキュメント内に残っていないこと

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Test Strategy

ドキュメントのみの修正のため、単体テスト対象外。

**手動確認項目（PR レビュアー / マージ前）:**

1. `ICCardManager/docs/manual/ユーザーマニュアル.md` Q5 の文章が自然な日本語で読めること
2. リンク先節タイトル（「2.1 複数PCで利用する場合の事前準備」「2.5 共有モードについて」）が管理者マニュアル側の実在節タイトルと完全一致していること（grep で機械的に確認可能）
3. 他に「3.5 共有フォルダの設定」を参照している箇所がドキュメント内に残っていないこと（プラン Step 4 でカバー済み）

## ドキュメント更新

- `CHANGELOG.md`: 軽微なドキュメント参照修正のため追記不要（過去の Issue #1391 / #1392 / #1394 等の同種修正でも CHANGELOG 追記なしで運用されている）
- 設計書 (`docs/design/`) / 規約 (`.claude/rules/`): 該当なし
