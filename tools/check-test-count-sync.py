#!/usr/bin/env python3
"""07_テスト設計書.md の件数表を dotnet test --list-tests と比較するスクリプト。

検証対象は 2 つ:

- §1.1a の総件数表（単体 / UI / 合計） … Issue #1546
- §2 のクラス別件数表（``| `Namespace/ClassTests` | 件数 | 観点 |``） … Issue #1889

**§2 検証の適用範囲**: 「表に載っている行」が実測と一致することだけを検証する。
テストクラスを新設したのに §2 へ行を足さなかった場合は検出できない（本書に行を持つ
クラスは全 5,500 件超のうち十数クラスに限られ、「全クラスが行を持つこと」は要求できない）。
そのぶん、表そのものが縮んで検査が空振りする事故は ``MIN_CLASS_ROWS`` の下限で止める。

Issue #1546 / #1889: CI 自動検証
Spec: ICCardManager/docs/superpowers/specs/2026-05-18-issue-1546-test-count-ci-check-design.md
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from typing import Dict, Optional, Tuple

# Windows ランナーは stdout/stderr のデフォルトが cp1252 で絵文字 (✅ ❌ ⚠) が
# UnicodeEncodeError を起こすため、UTF-8 に再構成する。Python 3.7+ で動作。
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        try:
            _stream.reconfigure(encoding="utf-8")
        except Exception:
            pass

UNIT_RE = re.compile(r"^\|\s*単体テスト[^|]*\|\s*([\d,]+)\s*件\s*\|")
UI_RE = re.compile(r"^\|\s*UI\s*テスト[^|]*\|\s*([\d,]+)\s*件\s*\|")
TOTAL_RE = re.compile(r"^\|\s*\*\*合計\*\*\s*\|\s*\*\*([\d,]+)\s*件\*\*\s*\|")

# §2 のクラス別件数行。名前セルはバッククォートで囲まれた識別子のみ（`Common/Charting/FooTests`
# のようなパス接頭辞を許す）、件数セルは十進の絶対値。
CLASS_ROW_RE = re.compile(r"^\|\s*`([A-Za-z0-9_./]+)`\s*\|\s*(\d+)\s*\|")
# 「（追加 4 件）| +4」のような差分行。絶対値ではないため比較対象にせず、
# 「黙って読み飛ばした」ことが分かるようレポートへ件数を出す。
DELTA_ROW_RE = re.compile(r"^\|\s*`([A-Za-z0-9_./]+)`[^|]*\|\s*\+\d+\s*\|")

# §2 のクラス別件数行の下限。表が丸ごと壊れた／消えたときに「差分なし」で緑にならないための歯止め。
MIN_CLASS_ROWS = 10


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
            lines.append(f"  {label.strip()}: {expected[k]:,} 件")
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
        lines.append(f"| {label.strip()} | {exp:,} | {act:,} | {sign}{diff} |")
    lines += [
        "",
        "修正方法:",
        "  ICCardManager/docs/design/07_テスト設計書.md §1.1a の表を実測値で",
        "  更新してください（Issue #1475 の同期手順を参照）。",
    ]
    return False, "\n".join(lines)


def list_test_names(csproj_path: str, prefix: str) -> list:
    """dotnet test --list-tests を実行し、ICCardManager.<prefix>.* のテスト名一覧を返す。

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
    return [line for line in proc.stdout.splitlines() if pattern.match(line)]


def count_class_tests(test_names) -> Dict[str, int]:
    """テスト名一覧をテストクラスの完全修飾名ごとに数える。

    ``      ICCardManager.Tests.Services.FooTests.Bar(x: 1)`` のような行から
    ``ICCardManager.Tests.Services.FooTests`` を取り出す（Theory の引数は捨てる）。
    """
    counts: Dict[str, int] = {}
    for raw in test_names:
        name = raw.strip().split("(", 1)[0]
        if "." not in name:
            continue
        class_fqn = name.rsplit(".", 1)[0]
        counts[class_fqn] = counts.get(class_fqn, 0) + 1
    return counts


def parse_class_counts(md_path: str) -> Tuple[list, int]:
    """§2 のクラス別件数表から [(行番号, 表記, 記載件数), ...] と差分行の件数を返す。"""
    rows = []
    delta_rows = 0
    with open(md_path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            m = CLASS_ROW_RE.match(line)
            if m:
                rows.append((lineno, m.group(1), int(m.group(2))))
                continue
            if DELTA_ROW_RE.match(line):
                delta_rows += 1
    return rows, delta_rows


def resolve_class(doc_name: str, actual: Dict[str, int]):
    """表記 (``Common/Charting/FooTests`` 等) を実測クラスの完全修飾名へ解決する。

    Returns:
        (fqn, None) 解決できたとき / (None, 理由) 解決できなかったとき。
    """
    segments = doc_name.split("/")
    simple = segments[-1]
    suffix = "." + ".".join(segments)
    candidates = [fqn for fqn in actual if fqn.split(".")[-1] == simple]
    if len(segments) > 1:
        candidates = [fqn for fqn in candidates if fqn.endswith(suffix)]
    if not candidates:
        return None, "実測に該当するテストクラスがありません（クラス名の変更・削除・名前空間の移動を疑う）"
    if len(candidates) > 1:
        return None, "同名のテストクラスが複数あります: " + " / ".join(sorted(candidates))
    return candidates[0], None


def compare_class_counts(rows, delta_rows: int, actual: Dict[str, int]) -> Tuple[bool, str]:
    """§2 の記載件数と実測件数を比較し、差分レポートを返す。"""
    if len(rows) < MIN_CLASS_ROWS:
        return False, "\n".join([
            "❌ §2 のクラス別件数行が想定より少なく、検査が空振りしています",
            f"  検出した行数: {len(rows)}（下限 {MIN_CLASS_ROWS}）",
            "  表を壊していないか、CLASS_ROW_RE の書式（| `Namespace/ClassTests` | 件数 | 観点 |）",
            "  から外れていないかを確認してください。",
        ])

    problems = []
    for lineno, doc_name, doc_count in rows:
        fqn, reason = resolve_class(doc_name, actual)
        if fqn is None:
            problems.append((lineno, doc_name, doc_count, None, reason))
            continue
        if actual[fqn] != doc_count:
            problems.append((lineno, doc_name, doc_count, actual[fqn], None))

    if not problems:
        return True, (
            f"✅ テスト件数表 §2 と実測値が一致しています（{len(rows)} クラス"
            f"／差分表記のため検査対象外の行: {delta_rows}）"
        )

    lines = [
        "❌ テスト件数表 §2 が実測値と乖離しています",
        "",
        "| 行 | テストクラス | 記載値 | 実測値 | 備考 |",
        "|----|-------------|-------|-------|------|",
    ]
    for lineno, doc_name, doc_count, act, reason in problems:
        if reason is not None:
            lines.append(f"| {lineno} | `{doc_name}` | {doc_count} | - | {reason} |")
        else:
            diff = act - doc_count
            sign = "+" if diff > 0 else ""
            lines.append(f"| {lineno} | `{doc_name}` | {doc_count} | {act} | {sign}{diff} |")
    lines += [
        "",
        "修正方法:",
        "  ICCardManager/docs/design/07_テスト設計書.md §2 の該当行を実測値で",
        "  更新してください（Issue #1475 の同期手順を参照）。",
        "",
        "注意: 本検査は「表に載っている行」しか見ません。テストクラスを新設したのに",
        "  §2 へ行を足していない場合は検出できないため、クラス追加時は手動で行を足してください。",
    ]
    return False, "\n".join(lines)


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

    class_rows, delta_rows = parse_class_counts(args.doc)

    try:
        unit_names = list_test_names(args.unit_csproj, "Tests")
        ui_names = list_test_names(args.ui_csproj, "UITests")
    except RuntimeError as e:
        print(f"⚠ {e}", file=sys.stderr)
        return 2

    unit_actual = len(unit_names)
    ui_actual = len(ui_names)

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
    class_ok, class_report = compare_class_counts(
        class_rows, delta_rows, count_class_tests(unit_names)
    )

    # 片方が失敗しても両方のレポートを出す（1 回の CI 実行で両方を直せるようにする）。
    for passed, text in ((ok, report), (class_ok, class_report)):
        print(text, file=sys.stdout if passed else sys.stderr)

    return 0 if ok and class_ok else 1


if __name__ == "__main__":
    sys.exit(main())
