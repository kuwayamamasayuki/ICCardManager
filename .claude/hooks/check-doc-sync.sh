#!/usr/bin/env bash
# Stop hook: テスト/主要サービス変更時にドキュメント同期漏れを検知
#
# 仕様:
#   - exit 2 + stderr: Claude への警告（作業継続を強制）
#   - exit 0 + stdout: Claude への参考情報（context として注入）
#   - exit 0 (無出力): 何もしない
#
# バイパス方法:
#   - SKIP_DOC_SYNC_CHECK=1 で即 exit 0
#   - DOC_SYNC_STRICT=1 で low confidence も exit 2 に昇格
#
# ルールの出典: .claude/rules/testing.md「テスト追加・修正時はテスト設計書も同期更新する」

# ---- 早期バイパス ----
[ "${SKIP_DOC_SYNC_CHECK:-}" = "1" ] && exit 0

# ---- 無限ループ防止 ----
# stop_hook_active=true のときは Claude が既に hook 応答中。追加アクション禁止。
INPUT="$(cat || true)"
if printf '%s' "$INPUT" | grep -q '"stop_hook_active"[[:space:]]*:[[:space:]]*true'; then
  exit 0
fi

# ---- git リポジトリでなければ何もしない ----
git rev-parse --git-dir >/dev/null 2>&1 || exit 0

# ---- 変更ファイル集合を取得 ----
# -c core.quotepath=false: 日本語ファイル名を octal escape せず UTF-8 のまま出力
# 1) 未コミット変更（staged + unstaged + untracked）
#    porcelain 形式は "XY path" または "XY orig -> new"。path に空白含む場合はダブルクォートされる。
#    先頭 3 文字（XY + スペース）を除去して、外側のダブルクォートも剥がす。
UNCOMMITTED="$(git -c core.quotepath=false status --porcelain 2>/dev/null \
  | sed -e 's/^...//' -e 's/^"//' -e 's/"$//' -e 's/.* -> //')" || UNCOMMITTED=""

# 2) ブランチ上のコミット（main..HEAD）
BASE="$(git merge-base HEAD main 2>/dev/null || true)"
if [ -n "$BASE" ]; then
  COMMITTED="$(git -c core.quotepath=false log --name-only --pretty=format: "$BASE"..HEAD 2>/dev/null | sort -u)" || COMMITTED=""
else
  COMMITTED=""
fi

CHANGED="$(printf '%s\n%s\n' "$UNCOMMITTED" "$COMMITTED" | sed '/^$/d' | sort -u)"
[ -z "$CHANGED" ] && exit 0

# ---- 判定 ----
has_match() { printf '%s\n' "$CHANGED" | grep -E "$1" >/dev/null 2>&1; }

TEST_DOC='ICCardManager/docs/design/07_テスト設計書\.md'
CLASS_DOC='ICCardManager/docs/design/05_クラス設計書\.md'
TEST_SRC='^ICCardManager/tests/.*\.cs$'
# 主要サービスクラス: 05_クラス設計書.md §5 に挙がっているもの
SERVICE_SRC='^ICCardManager/src/ICCardManager/Services/(LendingService|ReportService|BackupService|SummaryGenerator|LedgerMergeService|LedgerSplitService)\.cs$'

MISS_TEST_DOC=0
MISS_CLASS_DOC=0
has_match "$TEST_SRC"    && ! has_match "$TEST_DOC"  && MISS_TEST_DOC=1
has_match "$SERVICE_SRC" && ! has_match "$CLASS_DOC" && MISS_CLASS_DOC=1

# ---- 出力 ----
STRICT="${DOC_SYNC_STRICT:-0}"

if [ "$MISS_TEST_DOC" = "1" ]; then
  {
    echo "⚠ ドキュメント同期漏れ検知 (high confidence)"
    echo "  テストコード (ICCardManager/tests/) を変更しましたが、"
    echo "  ICCardManager/docs/design/07_テスト設計書.md が未更新です。"
    echo "  .claude/rules/testing.md の規約に従い、テスト設計書を同期更新してください。"
    echo "  (意図的にスキップする場合は SKIP_DOC_SYNC_CHECK=1 で再実行)"
  } >&2
  exit 2
fi

if [ "$MISS_CLASS_DOC" = "1" ]; then
  MSG=$'ℹ ドキュメント同期の可能性あり (low confidence)\n  主要サービスクラスを変更しましたが、\n  ICCardManager/docs/design/05_クラス設計書.md が未更新です。\n  設計に影響する変更か確認し、必要なら同期更新してください。'
  if [ "$STRICT" = "1" ]; then
    printf '%s\n' "$MSG" >&2
    exit 2
  else
    printf '%s\n' "$MSG"
    exit 0
  fi
fi

exit 0
