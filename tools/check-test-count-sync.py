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
    raise NotImplementedError
