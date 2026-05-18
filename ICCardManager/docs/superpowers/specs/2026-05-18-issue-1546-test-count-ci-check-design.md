# Issue #1546 設計書 — テスト件数表の CI 自動検証

- **Issue**: [#1546](https://github.com/kuwayamamasayuki/ICCardManager/issues/1546)
- **親 Issue**: #1475（提案 2 の切り出し）
- **関連 PR**: #1545（同期手順を注記として明文化）
- **作成日**: 2026-05-18
- **ブランチ**: `feat/issue-1546-test-count-ci-check`

## 0. 概要

`ICCardManager/docs/design/07_テスト設計書.md` §1.1a に記載されているテスト件数表（単体 / UI / 合計）が、`dotnet test --list-tests` の実測値と乖離していないかを CI で自動検証する。Issue #1475（PR #1545）で運用ルール（注記）の整備までは完了したが、検証は依然として PR 作業者の自己点検に依存していた。本タスクはその検証を機械化する。

## 1. ゴールと非ゴール

### ゴール

- §1.1a の「単体テスト」「UI テスト」「合計」3 値と、`dotnet test --list-tests` の実測値が **0 件差で一致** することを CI で自動検証する。
- 乖離があれば CI を fail させ、修正手順を明示するエラーメッセージを出力する。
- テスト設計書「だけ」を変更する PR でも検証が走るようにする（既存 `ci.yml` の `paths-ignore: docs/**` を回避）。

### 非ゴール

- §8.1 の件数記載（テスト規模スナップショット）の検証は対象外（別 Issue 候補）。
- テスト件数表を「動的生成（自動更新）」する仕組みは導入しない。手動同期を前提に、乖離検出のみ自動化する。
- ローカル開発時の事前検証（pre-commit hook など）は本 PR では行わない。

## 2. アーキテクチャ

```
┌──────────────────────────────────────────────────────────────┐
│ .github/workflows/test-count-sync-check.yml （新規）          │
│   - runs-on: windows-latest                                  │
│   - paths trigger:                                           │
│       ICCardManager/tests/**/*.cs                            │
│       ICCardManager/tests/**/*.csproj                        │
│       ICCardManager/docs/design/07_テスト設計書.md           │
│       tools/check-test-count-sync.py                         │
│       .github/workflows/test-count-sync-check.yml            │
│   - steps: checkout → setup-dotnet → restore → build         │
│           → run check script                                 │
└────────────────────────────┬─────────────────────────────────┘
                             │ python tools/check-test-count-sync.py
                             ▼
┌──────────────────────────────────────────────────────────────┐
│ tools/check-test-count-sync.py （新規、Python 3）             │
│                                                              │
│   parse_doc_counts(md_path) -> dict | None                   │
│       → §1.1a の表を正規表現で抽出                            │
│   count_tests(csproj_path, prefix) -> int                    │
│       → dotnet test --list-tests の出力行数をカウント         │
│   compare(expected, actual) -> (ok: bool, report: str)       │
│       → 差分テーブルを生成                                    │
│                                                              │
│   main: 不一致なら exit 1、形式異常なら exit 2、一致なら exit 0│
└──────────────────────────────────────────────────────────────┘
```

### 配置の根拠

- **`.github/workflows/test-count-sync-check.yml`** を独立 workflow とすることで、`ci.yml` の `paths-ignore: docs/**` を壊さずに「テスト設計書のみ更新 PR」でも検証可能にする。
- **`tools/check-test-count-sync.py`** をルート `tools/` 配下に置く（`merge_station_codes.py` と同居）。Python は `windows-latest` 標準搭載・WSL 開発環境でも実行可能。`ICCardManager/tools/` の PowerShell 群は Windows 固有のローカル開発支援用で性格が異なるため分離。

## 3. データフロー

```
[trigger]
  ├─ ICCardManager/tests/**/*.cs            （テスト追加・削除）
  ├─ ICCardManager/tests/**/*.csproj        （テストプロジェクト構成変更）
  ├─ ICCardManager/docs/design/07_テスト設計書.md  （件数表の手動更新）
  ├─ tools/check-test-count-sync.py         （検証ロジック自体の変更）
  └─ .github/workflows/test-count-sync-check.yml    （workflow 自身の変更）
        │
        ▼
[workflow steps]
  1. actions/checkout@v6
  2. actions/setup-dotnet@v5 (8.0.x)
  3. cd ICCardManager && dotnet restore
  4. dotnet build --no-restore --configuration Release
  5. python tools/check-test-count-sync.py \
       --doc ICCardManager/docs/design/07_テスト設計書.md \
       --unit-csproj ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj \
       --ui-csproj   ICCardManager/tests/ICCardManager.UITests/ICCardManager.UITests.csproj
        │
        ▼
[スクリプト内部]
  (a) parse_doc_counts(--doc)
        → {unit: 3266, ui: 26, total: 3292}
  (b) count_tests(--unit-csproj, "Tests")
      count_tests(--ui-csproj,   "UITests")
        → 実測値
  (c) compare(expected, actual)
        一致 → exit 0、stdout に "✅ テスト件数一致"
        乖離 → exit 1、stderr に差分テーブル + 修正手順
        形式異常 → exit 2、stderr に保守者向けメッセージ
```

## 4. 詳細仕様

### 4.1 設計書パーサ

§1.1a の表は PR #1545 で確立した以下の形式を前提とする：

```markdown
### 1.1a テスト規模（現状）

| 種別 | テスト数 | 備考 |
|------|---------|------|
| 単体テスト（ICCardManager.Tests） | 3,266件 | xUnit + FluentAssertions + Moq |
| UIテスト（ICCardManager.UITests） | 26件 | Issue #1263 で... |
| **合計** | **3,292件** | 全件パス（最終同期: ...） |
```

抽出に使う正規表現（行頭 `|` で固定、桁区切りカンマ対応）：

```python
UNIT_RE  = re.compile(r"^\|\s*単体テスト[^|]*\|\s*([\d,]+)\s*件\s*\|")
UI_RE    = re.compile(r"^\|\s*UI\s*テスト[^|]*\|\s*([\d,]+)\s*件\s*\|")
TOTAL_RE = re.compile(r"^\|\s*\*\*合計\*\*\s*\|\s*\*\*([\d,]+)\s*件\*\*\s*\|")
```

抽出された文字列はカンマを除去し int 変換。3 値のいずれかが取れなければ `None` を返し、main で exit 2 として扱う。

### 4.2 テスト件数カウント

```python
def count_tests(csproj_path: str, prefix: str) -> int:
    proc = subprocess.run(
        ["dotnet", "test", csproj_path,
         "--list-tests", "--nologo", "--verbosity", "quiet",
         "--no-build", "--configuration", "Release"],
        capture_output=True, text=True, encoding="utf-8"
    )
    if proc.returncode != 0:
        raise RuntimeError(f"dotnet test failed for {csproj_path}: {proc.stderr}")

    pattern = re.compile(rf"^\s+ICCardManager\.{re.escape(prefix)}\.")
    return sum(1 for line in proc.stdout.splitlines() if pattern.match(line))
```

prefix は `"Tests"`（単体）または `"UITests"`（UI）。

### 4.3 比較・差分レポート

```python
def compare(expected: dict, actual: dict) -> tuple[bool, str]:
    diffs = []
    for key in ("unit", "ui", "total"):
        if expected[key] != actual[key]:
            diffs.append((key, expected[key], actual[key]))
    if not diffs:
        return True, "✅ テスト件数一致"
    # 差分テーブル生成（Markdown 形式、CI ログで読みやすい）
    ...
```

### 4.4 出力フォーマット

**成功時 (stdout)**:

```
✅ テスト件数表 §1.1a と実測値が一致しています
  単体: 3,266 件
  UI:      26 件
  合計: 3,292 件
```

**失敗時 (stderr, exit 1)**:

```
❌ テスト件数表 §1.1a が実測値と乖離しています

| 種別 | 記載値 | 実測値 | 差分 |
|------|-------|-------|------|
| 単体 | 3,266 | 3,270 | +4   |
| UI   |    26 |    26 |   0  |
| 合計 | 3,292 | 3,296 | +4   |

修正方法:
  ICCardManager/docs/design/07_テスト設計書.md §1.1a の表を実測値で更新してください
  (Issue #1475 の同期手順を参照)
```

**形式異常時 (stderr, exit 2)**:

```
⚠ テスト件数表 §1.1a の形式が認識できません
  ファイル: ICCardManager/docs/design/07_テスト設計書.md
  期待する形式は本書 §4.1 を参照。表を破壊している場合は元に戻すか、
  本スクリプトの正規表現 (tools/check-test-count-sync.py) を更新してください。
```

### 4.5 エラーメッセージ品質

`.claude/rules/error-messages.md` の 3 要素（「何が」「なぜ」「どうすれば」）を満たす：

- **何が**: 「テスト件数表 §1.1a」「単体 3,266 vs 実測 3,270」
- **なぜ**: 「乖離しています」「形式が認識できません」
- **どうすれば**: 「§1.1a の表を実測値で更新してください」「本スクリプトの正規表現を更新してください」

## 5. 異常系・エッジケース

| ケース | 検出方法 | exit code | 表示 |
|--------|----------|-----------|------|
| §1.1a の表自体が存在しない/形式変更 | 3 正規表現のいずれかが unmatch | 2 | 形式異常メッセージ |
| 記載合計 ≠ 単体 + UI（足し算ミス） | パース後に検算 | 1 | 「記載値の合計が単体+UI と一致しません」 |
| `dotnet test --list-tests` 異常終了 | returncode != 0 | 2 | dotnet stderr をそのまま表示 |
| カウント 0 件（prefix 不一致など） | `count == 0` | 2 | 「テストが 0 件検出されました。csproj パスまたは prefix を確認」 |

## 6. テスト

### 6.1 Python スクリプト単体テスト

`tools/tests/test_check_test_count_sync.py` を新規作成し、pytest または `unittest` で以下を検証：

- 正常 markdown から `(3266, 26, 3292)` が抽出される
- 桁区切りなし markdown でも抽出できる（保険）
- §1.1a の表が壊れた markdown で `parse_doc_counts` が `None`
- `compare` の一致／不一致／部分一致の各ケース
- 合計検算（unit + ui != total）の検出

**CI 上では Python テストは回さない**。スクリプト本体が CI で動くこと自体が end-to-end の検証になるため。Python テストは保守者が手動で実行する（`python -m pytest tools/tests/`）。

### 6.2 統合検証（手動）

PR レビュー時に以下を確認：

- [ ] 件数表の値を 1 だけずらして commit → CI が fail する
- [ ] 正規の値に戻して commit → CI が pass する
- [ ] テスト設計書のみ更新する PR でも workflow が発火する（paths trigger が機能している）

### 6.3 テスト設計書 §1.1a 注記の更新

「**CI による自動検証は将来検討（別 Issue）**」を「**CI による自動検証（Issue #1546 で実装済み）**」に置換し、workflow と検証スクリプトへの参照を追加する。

## 7. 関連ドキュメント・ルールの更新

| ファイル | 変更内容 |
|---------|---------|
| `ICCardManager/docs/design/07_テスト設計書.md` | §1.1a 末尾注記を「将来検討」→「実装済み」に更新 |
| `.claude/rules/testing.md` | 「テスト追加・修正時」セクションに CI 自動検証への参照を追記 |
| `ICCardManager/CHANGELOG.md` | `[Unreleased]` の `### Added` に本機能を記載 |

## 8. リスクと対策

| リスク | 影響 | 対策 |
|--------|------|------|
| `dotnet build` の追加で CI 時間 +1〜3 分 | PR レビューのフィードバックループ遅延 | 独立 workflow のため `ci.yml` の体感速度は不変。NuGet キャッシュ (`actions/cache`) を将来検討 |
| 件数表のレイアウト変更で正規表現が unmatch | スクリプト保守負担 | 4.5 の exit 2 メッセージで「正規表現を更新」と明示。設計書 §1.1a 形式自体の変更時は本スクリプトも同 PR で更新する運用 |
| Windows runner 上で Python のエンコーディング問題（cp932） | 日本語 markdown を読めない | `open(path, encoding="utf-8")` で明示。subprocess 出力も `encoding="utf-8"` |
| `--list-tests` 出力フォーマットの将来変更（.NET 10 等） | 突然 CI が壊れる | カウント結果が 0 件のとき exit 2 で形式異常を検出。テスト数 0 はあり得ないため早期検知できる |

## 9. ロールアウト

1. ブランチ `feat/issue-1546-test-count-ci-check` 上で本設計書・スクリプト・workflow・ドキュメント更新をコミット
2. PR を作成し、PR 上で CI（新規 workflow）が green になることを確認
3. PR レビュー後、main にマージ
4. 以降、テストを追加・削除する PR では本 workflow が自動的に動き、件数表との同期が強制される

## 10. 参考

- Issue #1546（本タスク）
- Issue #1475（親 Issue、同期手順の注記化）
- PR #1545（注記実装）
- `.claude/rules/testing.md`（テスト追加・修正時のドキュメント同期）
- `.claude/rules/error-messages.md`（エラーメッセージ品質ガイドライン）
- メモリ `feedback_test_count_snapshot_sync.md`
