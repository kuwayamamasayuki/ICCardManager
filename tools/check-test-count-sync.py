#!/usr/bin/env python3
"""07_テスト設計書.md §1.1a の件数表を dotnet test --list-tests と比較するスクリプト。

Issue #1546: CI 自動検証
Spec: ICCardManager/docs/superpowers/specs/2026-05-18-issue-1546-test-count-ci-check-design.md
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from typing import Dict, Optional, Tuple

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
