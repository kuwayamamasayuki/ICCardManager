#!/usr/bin/env bash
# Stop hook: コード変更があれば Claude に「ドキュメント更新は不要か？」と自問させる。
#
# 設計方針:
#   特定のファイル→ドキュメント対応を hook 側で列挙するのではなく、
#   変更ファイル一覧と確認観点リストを Claude に渡して自己判断させる。
#   これにより hook の保守が不要になり、新しいドキュメント種別にも自動対応できる。
#
# 動作:
#   - 変更なし → exit 0（何もしない）
#   - ドキュメント・workflow 設定のみの変更 → exit 0（自問不要）
#   - それ以外（コード変更あり）→ exit 2 + stderr で自問プロンプトを注入
#     → Claude は次ターンで更新要否を判断し、必要なら更新、不要ならその旨述べて stop
#     → stop_hook_active=true で再発火時は即 exit 0（無限ループ防止）
#
# バイパス:
#   - SKIP_DOC_SYNC_CHECK=1 で即 exit 0
#
# ルールの出典:
#   .claude/rules/testing.md（テスト設計書同期）
#   memory/feedback_update_docs_with_code.md（コード変更時は設計書も同じPRで更新）
#   memory/feedback_update_third_party_licenses.md（パッケージ/素材更新時はライセンス更新）

# ---- 早期バイパス ----
[ "${SKIP_DOC_SYNC_CHECK:-}" = "1" ] && exit 0

# ---- 無限ループ防止 ----
INPUT="$(cat || true)"
if printf '%s' "$INPUT" | grep -q '"stop_hook_active"[[:space:]]*:[[:space:]]*true'; then
  exit 0
fi

# ---- git リポジトリでなければ何もしない ----
git rev-parse --git-dir >/dev/null 2>&1 || exit 0

# ---- 変更ファイル集合を取得 ----
# -c core.quotepath=false: 日本語ファイル名を octal escape せず UTF-8 のまま出力
UNCOMMITTED="$(git -c core.quotepath=false status --porcelain 2>/dev/null \
  | sed -e 's/^...//' -e 's/^"//' -e 's/"$//' -e 's/.* -> //')" || UNCOMMITTED=""

BASE="$(git merge-base HEAD main 2>/dev/null || true)"
if [ -n "$BASE" ]; then
  COMMITTED="$(git -c core.quotepath=false log --name-only --pretty=format: "$BASE"..HEAD 2>/dev/null | sort -u)" || COMMITTED=""
else
  COMMITTED=""
fi

CHANGED="$(printf '%s\n%s\n' "$UNCOMMITTED" "$COMMITTED" | sed '/^$/d' | sort -u)"
[ -z "$CHANGED" ] && exit 0

# ---- ドキュメント/meta ファイル以外の変更を抽出 ----
# docs/, ICCardManager/docs/, .claude/, ルート直下の *.md は「それ自体がドキュメント」なので除外
NON_DOC="$(printf '%s\n' "$CHANGED" \
  | grep -Ev '^(docs/|ICCardManager/docs/|\.claude/)' \
  | grep -Ev '^[^/]+\.md$' \
  || true)"
[ -z "$NON_DOC" ] && exit 0

# ---- 自問プロンプトを stderr で注入（exit 2 で stop をブロック） ----
{
  echo "📋 ドキュメント更新の自己確認"
  echo ""
  echo "本セッションで以下のコード/設定ファイルが変更されました:"
  printf '%s\n' "$NON_DOC" | head -30 | sed 's/^/  - /'
  TOTAL="$(printf '%s\n' "$NON_DOC" | wc -l | tr -d ' ')"
  if [ "$TOTAL" -gt 30 ]; then
    echo "  ... 他 $((TOTAL - 30)) 件"
  fi
  echo ""
  echo "応答を終える前に、これらの変更に対応したドキュメント更新が必要か自問してください。"
  echo "確認観点:"
  echo "  - テスト追加/修正 → docs/design/07_テスト設計書.md"
  echo "  - 主要クラスの構造変更 → docs/design/05_クラス設計書.md"
  echo "  - DB スキーマ変更 → docs/design/02_DB設計書.md"
  echo "  - 画面/操作フロー変更 → docs/design/03_画面設計書.md / docs/manual/ユーザーマニュアル.md"
  echo "  - 公開 API / 動作仕様変更 → docs/design/04_機能設計書.md / ICCardManager/CHANGELOG.md"
  echo "  - 依存パッケージ/素材追加 → ICCardManager/docs/THIRD_PARTY_LICENSES.md"
  echo "  - シーケンス変化のある API 変更 → docs/design/06_シーケンス図.md"
  echo ""
  echo "更新不要と判断した場合は理由を一言添えた上で応答を終えてください。"
  echo "(意図的にスキップする場合は SKIP_DOC_SYNC_CHECK=1 で再実行)"
} >&2

exit 2
