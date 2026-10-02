# Issue #1546 テスト件数表 CI 自動検証 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `07_テスト設計書.md` §1.1a のテスト件数表と `dotnet test --list-tests` の実測値が乖離した際に CI を fail させる自動検証を導入する。

**Architecture:** 専用 GitHub Actions workflow (`.github/workflows/test-count-sync-check.yml`) が Python スクリプト (`tools/check-test-count-sync.py`) を呼び出し、Markdown パース・件数取得・差分比較を行う。Python スクリプトの純粋関数（パース・比較）は `unittest` で TDD する。

**Tech Stack:** Python 3.x（標準ライブラリのみ）、GitHub Actions (windows-latest)、dotnet CLI、unittest

**Spec:** `ICCardManager/docs/superpowers/specs/2026-05-18-issue-1546-test-count-ci-check-design.md`

**Branch:** `feat/issue-1546-test-count-ci-check`（作成済み）

---

## File Structure

| ファイル | 区分 | 責務 |
|---------|------|------|
| `tools/check-test-count-sync.py` | 新規 | パーサ・件数取得・比較・CLI |
| `tools/tests/test_check_test_count_sync.py` | 新規 | 純粋関数の unittest |
| `.github/workflows/test-count-sync-check.yml` | 新規 | CI ワークフロー |
| `ICCardManager/docs/design/07_テスト設計書.md` | 修正 | §1.1a 注記更新 |
| `.claude/rules/testing.md` | 修正 | CI 自動検証への参照追記 |
| `ICCardManager/CHANGELOG.md` | 修正 | `[Unreleased] ### Added` 追加 |

設計書 (`spec`) は既にコミット済み (`8616a98`)。

---

### Task 1: パーサ関数 `parse_doc_counts` を TDD で実装

**Files:**
- Create: `tools/check-test-count-sync.py`
- Create: `tools/tests/test_check_test_count_sync.py`

- [ ] **Step 1.1: 失敗するテストを書く（正常系）**

ファイル `tools/tests/test_check_test_count_sync.py` を新規作成：

```python
"""tools/check-test-count-sync.py の単体テスト。"""
import sys
import pathlib
import textwrap
import tempfile
import unittest

# tools/ を import path に追加
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

# モジュール名はハイフン入りで import できないため、ファイル名を _ に揃える前提
# （Task 1 step 1.3 でファイル名を確定）
import importlib.util
SPEC_PATH = pathlib.Path(__file__).resolve().parent.parent / "check_test_count_sync.py"
_spec = importlib.util.spec_from_file_location("check_test_count_sync", SPEC_PATH)
_mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_mod)
parse_doc_counts = _mod.parse_doc_counts
compare = _mod.compare


def _write_md(content: str) -> str:
    tmp = tempfile.NamedTemporaryFile(
        mode="w", suffix=".md", delete=False, encoding="utf-8"
    )
    tmp.write(content)
    tmp.close()
    return tmp.name


SAMPLE_DOC_OK = textwrap.dedent("""\
    ### 1.1a テスト規模（現状）

    | 種別 | テスト数 | 備考 |
    |------|---------|------|
    | 単体テスト（ICCardManager.Tests） | 3,266件 | xUnit + FluentAssertions + Moq |
    | UIテスト（ICCardManager.UITests） | 26件 | Issue #1263 |
    | **合計** | **3,292件** | 全件パス（最終同期: ...） |
    """)


class ParseDocCountsTest(unittest.TestCase):
    def test_extracts_unit_ui_total_from_well_formed_table(self):
        path = _write_md(SAMPLE_DOC_OK)
        result = parse_doc_counts(path)
        self.assertEqual(result, {"unit": 3266, "ui": 26, "total": 3292})


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 1.2: テストを実行して fail を確認**

Run: `python -m unittest tools.tests.test_check_test_count_sync` （from repo root）
Expected: `ModuleNotFoundError` または `FileNotFoundError`（`check_test_count_sync.py` 未作成）

WSL2: `python3 -m unittest discover -s tools/tests -t .`

- [ ] **Step 1.3: 最小実装を書く**

ファイル `tools/check-test-count-sync.py` を新規作成（先頭のみ、後段で追記）：

```python
#!/usr/bin/env python3
"""07_テスト設計書.md §1.1a の件数表を dotnet test --list-tests と比較するスクリプト。

Issue #1546: CI 自動検証
Spec: ICCardManager/docs/superpowers/specs/2026-05-18-issue-1546-test-count-ci-check-design.md
"""
from __future__ import annotations

import re
import sys
import argparse
import subprocess
from typing import Optional, Dict, Tuple

UNIT_RE = re.compile(r"^\|\s*単体テスト[^|]*\|\s*([\d,]+)\s*件\s*\|")
UI_RE = re.compile(r"^\|\s*UI\s*テスト[^|]*\|\s*([\d,]+)\s*件\s*\|")
TOTAL_RE = re.compile(r"^\|\s*\*\*合計\*\*\s*\|\s*\*\*([\d,]+)\s*件\*\*\s*\|")


def parse_doc_counts(md_path: str) -> Optional[Dict[str, int]]:
    """§1.1a の表から (unit, ui, total) を抽出する。

    Returns:
        {"unit": int, "ui": int, "total": int} on success.
        None if any of the three values cannot be parsed.
    """
    unit = ui = total = None
    with open(md_path, encoding="utf-8") as f:
        for line in f:
            if unit is None:
                m = UNIT_RE.match(line)
                if m:
                    unit = int(m.group(1).replace(",", ""))
                    continue
            if ui is None:
                m = UI_RE.match(line)
                if m:
                    ui = int(m.group(1).replace(",", ""))
                    continue
            if total is None:
                m = TOTAL_RE.match(line)
                if m:
                    total = int(m.group(1).replace(",", ""))
                    continue
            if unit is not None and ui is not None and total is not None:
                break

    if unit is None or ui is None or total is None:
        return None
    return {"unit": unit, "ui": ui, "total": total}


# placeholder — compare は Task 2 で実装
def compare(expected: Dict[str, int], actual: Dict[str, int]) -> Tuple[bool, str]:
    raise NotImplementedError
```

- [ ] **Step 1.4: テストが pass することを確認**

Run: `python3 -m unittest discover -s tools/tests -t . -v` （from repo root）
Expected: `test_extracts_unit_ui_total_from_well_formed_table ... ok` で 1 test pass

- [ ] **Step 1.5: 異常系テストを追加**

`tools/tests/test_check_test_count_sync.py` の `ParseDocCountsTest` に追加：

```python
    def test_returns_none_when_table_is_missing(self):
        path = _write_md("# No table here\n\nJust text.\n")
        result = parse_doc_counts(path)
        self.assertIsNone(result)

    def test_returns_none_when_unit_row_is_broken(self):
        broken = SAMPLE_DOC_OK.replace("単体テスト", "Unit Tests")
        path = _write_md(broken)
        result = parse_doc_counts(path)
        self.assertIsNone(result)

    def test_handles_count_without_comma_separator(self):
        no_comma = SAMPLE_DOC_OK.replace("3,266", "3266").replace("3,292", "3292")
        path = _write_md(no_comma)
        result = parse_doc_counts(path)
        self.assertEqual(result, {"unit": 3266, "ui": 26, "total": 3292})
```

- [ ] **Step 1.6: 異常系テストが pass することを確認**

Run: `python3 -m unittest discover -s tools/tests -t . -v`
Expected: 4 tests pass（既存実装は既に正しく動作する想定）

- [ ] **Step 1.7: 実行権限と shebang を確認**

```bash
chmod +x tools/check-test-count-sync.py
```

- [ ] **Step 1.8: コミット**

```bash
git add tools/check-test-count-sync.py tools/tests/test_check_test_count_sync.py
git commit -m "$(cat <<'EOF'
feat: パーサ parse_doc_counts を TDD で実装 (Issue #1546)

07_テスト設計書.md §1.1a の表から単体・UI・合計の件数を抽出する
純粋関数を実装。unittest 4 件で正常系・異常系・桁区切りなしを検証。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 比較関数 `compare` を TDD で実装

**Files:**
- Modify: `tools/check-test-count-sync.py`（`compare` を本実装に置換）
- Modify: `tools/tests/test_check_test_count_sync.py`（CompareTest 追加）

- [ ] **Step 2.1: 失敗するテストを書く**

`tools/tests/test_check_test_count_sync.py` の末尾（`if __name__` の前）に追加：

```python
class CompareTest(unittest.TestCase):
    def test_all_match_returns_ok_true(self):
        expected = {"unit": 3266, "ui": 26, "total": 3292}
        actual = {"unit": 3266, "ui": 26, "total": 3292}
        ok, _ = compare(expected, actual)
        self.assertTrue(ok)

    def test_unit_only_diff_reports_unit_row(self):
        expected = {"unit": 3266, "ui": 26, "total": 3292}
        actual = {"unit": 3270, "ui": 26, "total": 3296}
        ok, report = compare(expected, actual)
        self.assertFalse(ok)
        self.assertIn("3,266", report)
        self.assertIn("3,270", report)
        self.assertIn("+4", report)

    def test_report_contains_recovery_instruction(self):
        expected = {"unit": 3266, "ui": 26, "total": 3292}
        actual = {"unit": 3270, "ui": 26, "total": 3296}
        _, report = compare(expected, actual)
        self.assertIn("§1.1a", report)
        self.assertIn("更新してください", report)
```

- [ ] **Step 2.2: テスト fail を確認**

Run: `python3 -m unittest discover -s tools/tests -t . -v`
Expected: 3 件の Compare テストが `NotImplementedError` で error

- [ ] **Step 2.3: `compare` を実装**

`tools/check-test-count-sync.py` の `compare` 関数を以下に置換：

```python
def compare(expected: Dict[str, int], actual: Dict[str, int]) -> Tuple[bool, str]:
    """記載値 (expected) と実測値 (actual) を比較し、差分レポートを返す。

    Returns:
        (True,  "✅ ...")  全一致
        (False, "❌ ...")  乖離あり
    """
    keys = (("unit", "単体"), ("ui", "UI  "), ("total", "合計"))
    diffs = [(k, expected[k], actual[k]) for k, _ in keys if expected[k] != actual[k]]

    if not diffs:
        lines = ["✅ テスト件数表 §1.1a と実測値が一致しています"]
        for k, label in keys:
            lines.append(f"  {label}: {expected[k]:,} 件")
        return True, "\n".join(lines)

    lines = [
        "❌ テスト件数表 §1.1a が実測値と乖離しています",
        "",
        "| 種別 | 記載値 | 実測値 | 差分 |",
        "|------|-------|-------|------|",
    ]
    for k, label in keys:
        exp = expected[k]
        act = actual[k]
        diff = act - exp
        sign = "+" if diff > 0 else ""
        lines.append(
            f"| {label.strip()} | {exp:,} | {act:,} | {sign}{diff} |"
        )
    lines += [
        "",
        "修正方法:",
        "  ICCardManager/docs/design/07_テスト設計書.md §1.1a の表を実測値で",
        "  更新してください（Issue #1475 の同期手順を参照）。",
    ]
    return False, "\n".join(lines)
```

- [ ] **Step 2.4: テストが pass することを確認**

Run: `python3 -m unittest discover -s tools/tests -t . -v`
Expected: 7 tests pass（Parse 4 + Compare 3）

- [ ] **Step 2.5: コミット**

```bash
git add tools/check-test-count-sync.py tools/tests/test_check_test_count_sync.py
git commit -m "$(cat <<'EOF'
feat: 比較関数 compare を TDD で実装 (Issue #1546)

記載値と実測値を比較し、Markdown 形式の差分テーブルと修正手順を
含む文字列を返す。一致時は ok=True、乖離時は ok=False。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 件数取得 `count_tests` と CLI main を実装

**Files:**
- Modify: `tools/check-test-count-sync.py`（末尾に追記）

`count_tests` は subprocess を呼ぶため unittest 化が困難。本タスクではローカル/CI の end-to-end 実行（Task 4）で検証する。

- [ ] **Step 3.1: `count_tests` を実装**

`tools/check-test-count-sync.py` の末尾（`if __name__` より前）に追加：

```python
def count_tests(csproj_path: str, prefix: str) -> int:
    """dotnet test --list-tests を実行し、ICCardManager.<prefix>.* のテスト数を返す。

    Raises:
        RuntimeError: dotnet が非ゼロ終了したとき。
    """
    cmd = [
        "dotnet", "test", csproj_path,
        "--list-tests",
        "--nologo",
        "--verbosity", "quiet",
        "--no-build",
        "--configuration", "Release",
    ]
    proc = subprocess.run(
        cmd, capture_output=True, text=True, encoding="utf-8"
    )
    if proc.returncode != 0:
        raise RuntimeError(
            f"dotnet test failed for {csproj_path} (exit {proc.returncode}):\n"
            f"{proc.stderr}"
        )
    pattern = re.compile(rf"^\s+ICCardManager\.{re.escape(prefix)}\.")
    return sum(1 for line in proc.stdout.splitlines() if pattern.match(line))
```

- [ ] **Step 3.2: `main` を実装**

`tools/check-test-count-sync.py` の末尾に追加：

```python
def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Verify §1.1a test counts in 07_テスト設計書.md against actual dotnet test counts."
    )
    parser.add_argument("--doc", required=True, help="Path to 07_テスト設計書.md")
    parser.add_argument("--unit-csproj", required=True, help="Path to ICCardManager.Tests.csproj")
    parser.add_argument("--ui-csproj", required=True, help="Path to ICCardManager.UITests.csproj")
    args = parser.parse_args(argv)

    expected = parse_doc_counts(args.doc)
    if expected is None:
        print(
            "⚠ テスト件数表 §1.1a の形式が認識できません",
            f"  ファイル: {args.doc}",
            "  期待する形式は spec §4.1 を参照。表を破壊している場合は元に戻すか、",
            "  本スクリプト (tools/check-test-count-sync.py) の正規表現を更新してください。",
            sep="\n",
            file=sys.stderr,
        )
        return 2

    if expected["unit"] + expected["ui"] != expected["total"]:
        print(
            "❌ §1.1a の記載値の合計が単体+UI と一致しません",
            f"  単体 {expected['unit']:,} + UI {expected['ui']:,} = {expected['unit']+expected['ui']:,}",
            f"  記載合計: {expected['total']:,}",
            "  §1.1a の表の足し算を修正してください。",
            sep="\n",
            file=sys.stderr,
        )
        return 1

    try:
        unit_actual = count_tests(args.unit_csproj, "Tests")
        ui_actual = count_tests(args.ui_csproj, "UITests")
    except RuntimeError as e:
        print(f"⚠ {e}", file=sys.stderr)
        return 2

    if unit_actual == 0 or ui_actual == 0:
        print(
            "⚠ テスト件数が 0 件として検出されました",
            f"  単体実測: {unit_actual}, UI 実測: {ui_actual}",
            "  csproj パスまたは prefix の不一致が疑われます。ビルドが完了しているか、",
            "  プロジェクト名 (ICCardManager.Tests / ICCardManager.UITests) が変わっていないか確認してください。",
            sep="\n",
            file=sys.stderr,
        )
        return 2

    actual = {
        "unit": unit_actual,
        "ui": ui_actual,
        "total": unit_actual + ui_actual,
    }
    ok, report = compare(expected, actual)
    if ok:
        print(report)
        return 0
    else:
        print(report, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 3.3: 既存テストが引き続き pass することを確認**

Run: `python3 -m unittest discover -s tools/tests -t . -v`
Expected: 7 tests pass（main 追加で既存テストは影響なし）

- [ ] **Step 3.4: コミット**

```bash
git add tools/check-test-count-sync.py
git commit -m "$(cat <<'EOF'
feat: count_tests と main(CLI) を実装 (Issue #1546)

dotnet test --list-tests の結果から ICCardManager.<prefix>.* の
テスト数をカウント。main は --doc / --unit-csproj / --ui-csproj
を受け、形式異常は exit 2、件数乖離は exit 1、一致は exit 0。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: ローカル end-to-end 検証

**Files:** （変更なし、実行のみ）

WSL2 の dotnet は `"/mnt/c/Program Files/dotnet/dotnet.exe"`。Python は `python3`。

- [ ] **Step 4.1: dotnet build を実行**

Run:
```bash
cd /mnt/d/OneDrive/交通系/src/ICCardManager
"/mnt/c/Program Files/dotnet/dotnet.exe" build --configuration Release 2>&1 | tail -5
```
Expected: `Build succeeded` または warning のみ

- [ ] **Step 4.2: PATH エイリアス確認**

WSL2 で `dotnet` がそのまま使えるよう PATH を確認：

```bash
which dotnet || alias dotnet="/mnt/c/Program Files/dotnet/dotnet.exe"
```

`tools/check-test-count-sync.py` 内で `subprocess.run(["dotnet", ...])` を呼んでいるため、PATH に通っている必要がある。WSL では `/mnt/c/Program Files/dotnet/dotnet.exe` を `dotnet` として PATH に置くか、本スクリプトを Windows 側で実行する。

Note: CI (windows-latest) では `dotnet` が PATH に通っているため問題なし。WSL ローカル検証は省略可（後段 PR で CI が動くことを最終確認）。

- [ ] **Step 4.3: スクリプトを実行（pass を確認）**

Run（WSL/Windows どちらか）:
```bash
python3 tools/check-test-count-sync.py \
  --doc ICCardManager/docs/design/07_テスト設計書.md \
  --unit-csproj ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj \
  --ui-csproj ICCardManager/tests/ICCardManager.UITests/ICCardManager.UITests.csproj
echo "exit=$?"
```
Expected: `✅ テスト件数表 §1.1a と実測値が一致しています` + `exit=0`

ローカルで `dotnet` が呼べない場合は本ステップをスキップし、Task 7 で CI 上の動作確認に委ねる旨を PR コメントに残す。

- [ ] **Step 4.4: 意図的に件数を 1 ずらして fail 確認（破壊的ローカルテスト）**

```bash
# 一時的に単体テスト件数 +1
python3 -c "
import pathlib, re
p = pathlib.Path('ICCardManager/docs/design/07_テスト設計書.md')
s = p.read_text(encoding='utf-8')
s2 = re.sub(r'3,266件', '3,267件', s, count=1)
p.write_text(s2, encoding='utf-8')
"
python3 tools/check-test-count-sync.py \
  --doc ICCardManager/docs/design/07_テスト設計書.md \
  --unit-csproj ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj \
  --ui-csproj ICCardManager/tests/ICCardManager.UITests/ICCardManager.UITests.csproj
echo "exit=$?"
# 元に戻す
python3 -c "
import pathlib, re
p = pathlib.Path('ICCardManager/docs/design/07_テスト設計書.md')
s = p.read_text(encoding='utf-8')
s2 = re.sub(r'3,267件', '3,266件', s, count=1)
p.write_text(s2, encoding='utf-8')
"
```
Expected: 1 回目で「合計の検算が一致しない」エラーで exit 1。または件数乖離で exit 1。
（記載合計 3,292 vs unit+ui=3,267+26=3,293 のため、まず検算エラーが先に出る）

- [ ] **Step 4.5: ローカル変更がないことを確認**

Run: `git status --porcelain | grep -v '^??'`
Expected: 出力なし（一時変更が元に戻っている）

このタスクではコミットなし（検証のみ）。

---

### Task 5: GitHub Actions workflow ファイル作成

**Files:**
- Create: `.github/workflows/test-count-sync-check.yml`

- [ ] **Step 5.1: workflow ファイルを作成**

ファイル `.github/workflows/test-count-sync-check.yml` を新規作成：

```yaml
name: Test Count Sync Check

on:
  push:
    branches: [ main, develop ]
    paths:
      - 'ICCardManager/tests/**/*.cs'
      - 'ICCardManager/tests/**/*.csproj'
      - 'ICCardManager/docs/design/07_テスト設計書.md'
      - 'tools/check-test-count-sync.py'
      - 'tools/tests/test_check_test_count_sync.py'
      - '.github/workflows/test-count-sync-check.yml'
  pull_request:
    branches: [ main ]
    paths:
      - 'ICCardManager/tests/**/*.cs'
      - 'ICCardManager/tests/**/*.csproj'
      - 'ICCardManager/docs/design/07_テスト設計書.md'
      - 'tools/check-test-count-sync.py'
      - 'tools/tests/test_check_test_count_sync.py'
      - '.github/workflows/test-count-sync-check.yml'

env:
  DOTNET_VERSION: '8.0.x'
  DOTNET_NOLOGO: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true

jobs:
  test-count-sync:
    runs-on: windows-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v6

      - name: Setup .NET
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Setup Python
        uses: actions/setup-python@v5
        with:
          python-version: '3.x'

      - name: Run Python unit tests for the check script
        shell: bash
        run: python -m unittest discover -s tools/tests -t .

      - name: Restore .NET dependencies
        working-directory: ICCardManager
        run: dotnet restore

      - name: Build .NET solution (Release)
        working-directory: ICCardManager
        run: dotnet build --no-restore --configuration Release

      - name: Verify test count sync (§1.1a vs --list-tests)
        shell: bash
        run: |
          python tools/check-test-count-sync.py \
            --doc ICCardManager/docs/design/07_テスト設計書.md \
            --unit-csproj ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj \
            --ui-csproj ICCardManager/tests/ICCardManager.UITests/ICCardManager.UITests.csproj
```

- [ ] **Step 5.2: YAML 構文チェック（任意）**

Run: `python3 -c "import yaml; yaml.safe_load(open('.github/workflows/test-count-sync-check.yml'))" && echo OK`
Expected: `OK`

`yaml` モジュールが入っていなければスキップ可。

- [ ] **Step 5.3: コミット**

```bash
git add .github/workflows/test-count-sync-check.yml
git commit -m "$(cat <<'EOF'
ci: テスト件数表自動検証 workflow を追加 (Issue #1546)

windows-latest で dotnet build → Python スクリプトによる
件数比較を実行。trigger paths に 07_テスト設計書.md を含めることで、
ドキュメントのみ更新する PR でも検証が走るようにした。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: 関連ドキュメント・ルールを更新

**Files:**
- Modify: `ICCardManager/docs/design/07_テスト設計書.md`（§1.1a 末尾注記）
- Modify: `.claude/rules/testing.md`（テスト追加・修正時セクション）
- Modify: `ICCardManager/CHANGELOG.md`（`[Unreleased] ### Added`）

- [ ] **Step 6.1: 07_テスト設計書.md §1.1a 注記の最終段落を置換**

`ICCardManager/docs/design/07_テスト設計書.md` の以下の段落を：

```
> **CI による自動検証は将来検討（別 Issue）**: 本書 §1.1a を `dotnet test --list-tests` の集計値と CI で比較し、乖離時に CI を失敗させる仕組みは、Issue #1475 提案 2 として将来別 Issue で長期検討する。
```

以下に置換：

```
> **CI による自動検証（Issue #1546 で実装済み）**: 本書 §1.1a の記載値は `.github/workflows/test-count-sync-check.yml` で自動検証されている。表とテストプロジェクトの実測値（`dotnet test --list-tests`）が乖離すると CI が fail する。検証スクリプトは `tools/check-test-count-sync.py`。設計は `ICCardManager/docs/superpowers/specs/2026-05-18-issue-1546-test-count-ci-check-design.md` を参照。
```

- [ ] **Step 6.2: .claude/rules/testing.md に 1 行追記**

`.claude/rules/testing.md` の `## テスト追加・修正時` セクション末尾に追加：

```
- 件数表 §1.1a の同期は CI で自動検証される（`.github/workflows/test-count-sync-check.yml`、Issue #1546）。乖離があると PR がブロックされる。
```

- [ ] **Step 6.3: CHANGELOG.md `[Unreleased]` セクションに `### Added` を追加**

`ICCardManager/CHANGELOG.md` の `[Unreleased]` セクション内の適切な位置（既存の `### Added` があればそこに行追加、なければ新設）に：

```markdown
### Added

- CI: テスト件数表（`07_テスト設計書.md` §1.1a）と `dotnet test --list-tests` 実測値の乖離を自動検出する workflow (`.github/workflows/test-count-sync-check.yml`) と検証スクリプト (`tools/check-test-count-sync.py`) を追加 (Issue #1546)。乖離時は CI が fail し、修正手順を表示する。
```

既存 `[Unreleased]` セクションの構造（既存の `### Added` の有無、項目順）を読んで適切な箇所に挿入する。

- [ ] **Step 6.4: コミット**

```bash
git add ICCardManager/docs/design/07_テスト設計書.md .claude/rules/testing.md ICCardManager/CHANGELOG.md
git commit -m "$(cat <<'EOF'
docs: テスト件数 CI 自動検証の運用反映 (Issue #1546)

- 07_テスト設計書.md §1.1a 末尾注記を「将来検討」→「実装済み」に更新
- .claude/rules/testing.md に CI 自動検証への参照を追記
- CHANGELOG.md [Unreleased] に Added エントリを追加

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: push & PR 作成 & CI 確認

**Files:** （変更なし、git/gh コマンドのみ）

- [ ] **Step 7.1: ブランチを remote に push**

```bash
git push -u origin feat/issue-1546-test-count-ci-check
```

- [ ] **Step 7.2: PR を作成**

```bash
gh pr create --title "feat: テスト件数表の CI 自動検証 (Issue #1546)" --body "$(cat <<'EOF'
## Summary

- `07_テスト設計書.md` §1.1a のテスト件数表と `dotnet test --list-tests` 実測値の乖離を自動検出する CI を追加 (Issue #1546)
- 専用 workflow `.github/workflows/test-count-sync-check.yml` を新設し、テスト設計書のみ更新する PR でも検証が走るように `paths` を構成
- 検証スクリプト `tools/check-test-count-sync.py` は Markdown パース・件数取得・差分比較を担当。純粋関数は `unittest` で 7 件の単体テストを追加
- 乖離時は exit 1（修正手順を表示）、形式異常時は exit 2（保守者向けメッセージ）と切り分け

## Design

`ICCardManager/docs/superpowers/specs/2026-05-18-issue-1546-test-count-ci-check-design.md` を参照。

## Test plan

- [x] Python 単体テスト 7 件（パーサ 4 + 比較 3）が pass
- [ ] CI（本 workflow）が PR 上で green になること
- [ ] 件数表を意図的に 1 ずらして push すると CI が fail することを動作確認
- [ ] テスト設計書のみ更新する PR でも workflow が発火することを次の PR で確認

## 関連

- Parent: Issue #1546
- 親 Issue: #1475（同期手順の注記化）
- 関連 PR: #1545（注記実装）

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL が表示される

- [ ] **Step 7.3: PR 上で CI が green になることを確認**

```bash
gh pr checks
```

Expected: `Test Count Sync Check / test-count-sync` が pass。fail した場合はログを取得：

```bash
gh run list --workflow=test-count-sync-check.yml --limit 1
gh run view <run-id> --log-failed
```

修正が必要なら commit を追加し再 push。CI 通過まで繰り返す。

- [ ] **Step 7.4: PR URL をユーザーに報告**

PR の URL を出力し、レビュー依頼を促す。

---

## Self-Review チェックリスト

- **Spec coverage**: spec の §1〜10 が Task 1〜7 でカバーされている。§5 異常系は Task 3 の main、§6 テストは Task 1〜2、§7 関連ドキュメント更新は Task 6 が担当。
- **Placeholder scan**: TBD/TODO なし、すべて具体的なコード・コマンドを記載。
- **Type consistency**: `parse_doc_counts` 戻り値型 `Optional[Dict[str, int]]` で Task 1〜3 を通じて一貫。`compare` の戻り値 `Tuple[bool, str]` で Task 2〜3 一貫。`count_tests` 戻り値 `int` で Task 3 一貫。
- **File path consistency**: `tools/check-test-count-sync.py`、`tools/tests/test_check_test_count_sync.py` が全タスクで同一表記。

問題なし。
