# Issue #1407: 管理者マニュアル §9.1 「日次（自動）バックアップ」表記修正 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 管理者マニュアル §9.1 表で「日次（自動）バックアップ」と記載している箇所を、実装挙動（アプリケーション起動時に作成）に合わせて「起動時」に変更し、起動頻度依存の運用上の含意を補足ブロックで明記する。

**Architecture:** 表内 1 セルの文字列置換 + 表直下に補足ブロックを 1 つ追加。表構造（列・他行）は維持。

**Tech Stack:** Markdown（GitHub Flavored Markdown）

---

## File Structure

| ファイル | 変更内容 |
|---|---|
| `ICCardManager/docs/manual/管理者マニュアル.md` | §9.1 表の「日次」→「起動時」（行 1302）、表直下に補足ブロックを新規追加 |

---

### Task 1: §9.1 のバックアップ頻度記述を修正

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md`（行 1300〜1305 付近）

- [ ] **Step 1: 現状確認**

Run: `awk 'NR>=1296 && NR<=1310' ICCardManager/docs/manual/管理者マニュアル.md`
Expected: 行 1300〜1304 に §9.1 表が確認でき、行 1302 が `| 日次 | （自動）バックアップ |` であること。

- [ ] **Step 2: ドキュメント全体で同種の乖離が他に無いか確認**

Run: `grep -rn "日次" ICCardManager/docs/manual/ | grep -i "バックアップ"`
Expected: §9.1 表の 1 行のみがヒットすること（既に他の節は「起動時」記述）。

- [ ] **Step 3: 表セルの「日次」を「起動時」に変更し、表直下に補足ブロックを追加**

Edit `ICCardManager/docs/manual/管理者マニュアル.md`：

```diff
 | 頻度 | 作業内容 |
 |------|----------|
-| 日次 | （自動）バックアップ |
+| 起動時 | （自動）バックアップ |
 | 月次 | 帳票出力、残額確認 |
 | 年次 | 古いデータの確認、ディスク容量確認 |
+
+> **補足**: 自動バックアップはアプリケーション起動時に作成されます（[§3.3 バックアップ設定](#33-バックアップ設定)・[§6.1 バックアップとリストア](#61-バックアップとリストア)参照）。常時 PC を起動したまま運用する場合は実質的に日次バックアップとなりますが、毎日アプリを起動・終了する運用ではその頻度に依存します。長期間アプリを起動しない場合は、システム管理画面（F6）から手動バックアップを取得することを推奨します。
```

- [ ] **Step 4: 修正後の §9.1 を確認**

Run: `awk 'NR>=1296 && NR<=1312' ICCardManager/docs/manual/管理者マニュアル.md`
Expected: 表セルが「起動時」、表直下に `> **補足**:` で始まる補足ブロックが存在すること。

- [ ] **Step 5: 補足ブロック内のリンク先節が実在することを確認**

Run: `grep -nE '^### 3\.3 バックアップ設定$|^### 6\.1 バックアップとリストア$' ICCardManager/docs/manual/管理者マニュアル.md`
Expected: §3.3 と §6.1 が両方ヒットすること（補足ブロックのアンカーリンクが切れていないことを保証）。

- [ ] **Step 6: コミット**

```bash
git add ICCardManager/docs/manual/管理者マニュアル.md \
        docs/superpowers/specs/2026-04-26-issue-1407-backup-frequency-fix-design.md \
        docs/superpowers/plans/2026-04-26-issue-1407-backup-frequency-fix.md
git commit -m "$(cat <<'EOF'
docs: 管理者マニュアル §9.1 自動バックアップ頻度を実装に合わせて修正 (Issue #1407)

§9.1「定期メンテナンス」表で自動バックアップの頻度を「日次」と
記載していたが、実装は「アプリケーション起動時に作成」が正。
他箇所（§3.3, §6.1, ユーザーマニュアル §6.1）は「起動時」と
正しく記載されており、§9.1 のみが乖離していた。

- 表セル「日次」→「起動時」
- 表直下に補足ブロックを追加し、§3.3/§6.1 への参照と、
  常駐起動運用 vs 手動起動運用での頻度差、長期間未起動時の
  手動バックアップ推奨を明記

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: PR 作成

**Files:** なし（git/gh 操作のみ）

- [ ] **Step 1: ブランチ push**

```bash
git push -u origin docs/issue-1407-fix-backup-frequency
```

- [ ] **Step 2: PR 作成**

```bash
gh pr create --title "docs: 管理者マニュアル §9.1 自動バックアップ頻度を実装に合わせて修正 (Issue #1407)" --body "$(cat <<'EOF'
## Summary

- 管理者マニュアル §9.1 表で「日次（自動）バックアップ」と記載していた頻度を、実装挙動に合わせて「起動時」に変更
- 表直下に補足ブロックを追加し、常駐起動運用 vs 手動起動運用での実質頻度差と、長期間未起動時の手動バックアップ推奨を明記
- 設計書: \`docs/superpowers/specs/2026-04-26-issue-1407-backup-frequency-fix-design.md\`
- Closes #1407

## Test plan

- [ ] 修正後の §9.1 を目視確認し、表セルが「起動時」になっていること
- [ ] 補足ブロックの §3.3 / §6.1 へのアンカーリンクが切れていないこと（クリックで該当節に飛べること）
- [ ] \`grep -rn "日次" ICCardManager/docs/manual/ | grep -i バックアップ\` で表外に同種の乖離記述が残っていないこと

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Test Strategy

ドキュメントのみの修正のため、単体テスト対象外。

**手動確認項目（PR レビュアー / マージ前）:**

1. `ICCardManager/docs/manual/管理者マニュアル.md` の §9.1 を目視で読み、表セルが「起動時」、補足ブロックがその直下にあること
2. 補足ブロック内のリンク `[§3.3 バックアップ設定](#33-バックアップ設定)` および `[§6.1 バックアップとリストア](#61-バックアップとリストア)` が GitHub プレビュー上で機能すること
3. ユーザーマニュアル §6.1 や管理者マニュアル §3.3／§6.1 の既存記述と矛盾していないこと

## ドキュメント更新

- `CHANGELOG.md`: 軽微なドキュメント記述修正のため追記不要（Issue #1405 / #1406 等の同種修正と同じ運用）
- 設計書 (`docs/design/`) / 規約 (`.claude/rules/`): 該当なし（バックアップ仕様自体は変更していない）
- ユーザーマニュアル: 該当なし（既に「起動時」と正しく記載されているため変更不要）
