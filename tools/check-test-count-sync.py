#!/usr/bin/env python3
"""07_テスト設計書.md の件数表を dotnet test --list-tests と比較するスクリプト。

検証対象は 3 つ:

- §1.1a の総件数表（単体 / UI / 合計） … Issue #1546
- §2 のクラス別件数表（``| `Namespace/ClassTests` | 件数 | 観点 |``） … Issue #1889
- 見出しの ID（``#### UT-049: …``）と節番号（``### 2.47a …``）が文書全体で重複しないこと … Issue #2391
  （件数の照合ではないが、同じ 07 を同じ CI で検査するのでここに置く。dotnet を使わない）

**§2 検証の適用範囲**: 「表に載っている行」が実測と一致することだけを検証する。
テストクラスを新設したのに §2 へ行を足さなかった場合は検出できない（本書に行を持つ
クラスは全 5,500 件超のうち十数クラスに限られ、「全クラスが行を持つこと」は要求できない）。

そのぶん、**検査対象が黙って縮む経路は塞ぐ**:

- 走査範囲は ``## 2.`` 見出しから次の ``##`` 見出しまでに限定する（§2 以外の表を
  巻き込んで誤検出しないため）。見出しが見つからなければ空振りとして fail する
- 件数セルの書式から外れた行（``| 12 件 |`` / ``| **12** |`` / 名前セルに注記が付いた行）は
  黙って読み飛ばさず、書式違反として fail する
- 差分表記（``+4`` / ``-2``）の行は比較できないが、読み飛ばした行をレポートに列挙する
- 表そのものが縮んで検査が空振りする事故は ``MIN_CLASS_ROWS`` の下限で止める

Issue #1546 / #1889: CI 自動検証
Spec: ICCardManager/docs/superpowers/specs/2026-05-18-issue-1546-test-count-ci-check-design.md
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from collections import Counter
from typing import Dict, List, NamedTuple, Optional, Tuple

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

# §2（単体テストの章）の範囲。クラス別件数行の走査をこの範囲に限定する。
# 文書全体を走査すると、他章の「| `AsciiName` | 12 | 説明 |」形の表を勝手に検査対象に
# 引き込んで誤検出し、逆に §2 の表が別章へ移っても気付けない（走査範囲は導出する）。
SECTION2_HEADING_RE = re.compile(r"^##\s*2\.\s")
SECTION_HEADING_RE = re.compile(r"^##\s")

# §2 のクラス別件数行。名前セルはバッククォートで囲まれた識別子のみ（`Common/Charting/FooTests`
# のようなパス接頭辞を許す）、件数セルは十進の絶対値。
CLASS_ROW_RE = re.compile(r"^\|\s*`([A-Za-z0-9_./]+)`\s*\|\s*(\d+)\s*\|")
# 「（追加 4 件）| +4」のような差分行。絶対値ではないため比較対象にせず、
# 「黙って読み飛ばした」ことが分かるようレポートへ行を列挙する。減少（-2）も同じ扱い。
DELTA_ROW_RE = re.compile(r"^\|\s*`([A-Za-z0-9_./]+)`[^|]*\|\s*[+-]\d+\s*\|")
# 件数行のつもりで書かれたが書式から外れた行（`| 12 件 |` / `| **12** |` /
# 名前セルに注記が付いて CLASS_ROW_RE を外れた行）。黙って読み飛ばすと、その行だけが
# 検査対象から静かに消える（MIN_CLASS_ROWS の余裕に吸収されて緑のまま通る）。
# 2 列目にバッククォートを含む行は「クラス | テスト名 | 期待結果」形式の別表なので除外する。
MALFORMED_CLASS_ROW_RE = re.compile(
    r"^\|\s*`([A-Za-z0-9_./]+Tests)`[^|]*\|([^|`]*\d[^|`]*)\|"
)

# §2 のクラス別件数行の下限。表が丸ごと壊れた／消えたときに「差分なし」で緑にならないための歯止め。
MIN_CLASS_ROWS = 10

# Issue #2391: 見出し（CommonMark の ATX 見出し。行頭のインデントは 0〜3 文字まで）。
HEADING_PREFIX = r"^ {0,3}#{2,6}[ \t]+"
# 見出しの先頭に置く ID（``#### UT-049: …`` / ``### UT-083`` / ``#### UT-058c-FlaUI: …``）。
# 接頭辞は英大文字 2〜4 文字（UT / IT / ST / PT / SEC / UAT）。ID は ASCII の英数字とハイフンだけを取るので、
# ``:`` ・全角コロン・全角括弧・空白の手前で切れる。
HEADING_ID_RE = re.compile(HEADING_PREFIX + r"([A-Z]{2,4}-[A-Za-z0-9-]+)")
# 見出しの先頭に置く節番号（``## 2.`` / ``### 2.47a`` / ``#### 1.6.1`` / ``#### 5.11a-2``）。
# 番号の文字（ASCII の英数字と ``.``。``-数字`` の枝番）だけを取る。空白まで取ると ``2.47a（…）`` ・ ``2.47a：…`` が
# 別の番号になり、重複を見逃す。``2.20a`` と ``2.20a2``、``1.6`` と ``1.6.1`` は別の番号として数える。
SECTION_NUMBER = r"\d[0-9A-Za-z.]*(?:-\d+(?![.\d]))?"
HEADING_SECTION_RE = re.compile(HEADING_PREFIX + r"(" + SECTION_NUMBER + r")")
# 範囲表記（``### 2.31〜2.33 — 欠番``）の終端。範囲の番号はすべて占有済みとして扱う
# （後から ``### 2.32`` を足したら重複として検出する）。
SECTION_RANGE_TAIL_RE = re.compile(r"\s*[〜～~-]\s*(" + SECTION_NUMBER + r")")
# 範囲を展開する上限。書き損じ（``2.1〜2.1000``）で巨大な集合を作らないため。
MAX_SECTION_RANGE = 100
# コードブロックの開始（CommonMark: インデント 0〜3、同じ記号 3 文字以上）。閉じるのは開いた記号と同じ種類・
# 同じ長さ以上の記号だけの行。種類・長さを見ないと、```` の中の ``` で開閉が反転する。
FENCE_OPEN_RE = re.compile(r"^ {0,3}(`{3,}|~{3,})")
HTML_COMMENT_OPEN = "<!--"
HTML_COMMENT_CLOSE = "-->"


class ClassRowScan(NamedTuple):
    """§2 のクラス別件数表の走査結果。

    Attributes:
        rows: 比較対象の行 [(行番号, 表記, 記載件数), ...]
        delta_rows: 差分表記のため比較できない行 [(行番号, 表記), ...]
        malformed_rows: 件数セルの書式から外れた行 [(行番号, 表記, セル文字列), ...]
        section_found: §2 の見出しを見つけられたか（見つからなければ走査は空振り）
    """

    rows: List[tuple]
    delta_rows: List[tuple]
    malformed_rows: List[tuple]
    section_found: bool


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
    class_fqns = []
    for raw in test_names:
        name = raw.strip().split("(", 1)[0]
        if "." not in name:
            continue
        class_fqns.append(name.rsplit(".", 1)[0])
    return dict(Counter(class_fqns))


def parse_class_counts(md_path: str) -> ClassRowScan:
    """§2 のクラス別件数表を走査する。

    走査範囲は ``## 2.`` 見出しから次の ``##`` 見出しまで。文書全体を見ると他章の
    「``| `AsciiName` | 12 | 説明 |``」形の表を検査対象へ引き込む（誤検出でガードの寿命が
    縮む）一方、§2 の表が別章へ移っても気付けないため、走査範囲は見出しから導出する。
    """
    rows = []
    delta_rows = []
    malformed_rows = []
    section_found = False
    inside = False
    with open(md_path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            if SECTION2_HEADING_RE.match(line):
                section_found = True
                inside = True
                continue
            if inside and SECTION_HEADING_RE.match(line):
                inside = False
            if not inside:
                continue
            m = CLASS_ROW_RE.match(line)
            if m:
                rows.append((lineno, m.group(1), int(m.group(2))))
                continue
            m = DELTA_ROW_RE.match(line)
            if m:
                delta_rows.append((lineno, m.group(1)))
                continue
            m = MALFORMED_CLASS_ROW_RE.match(line)
            if m:
                malformed_rows.append((lineno, m.group(1), m.group(2).strip()))
    return ClassRowScan(rows, delta_rows, malformed_rows, section_found)


class HeadingScan(NamedTuple):
    """見出しの ID と節番号の走査結果（Issue #2391）。

    Attributes:
        ids: [(行番号, ID), ...]
        sections: [(行番号, 節番号), ...]
    """

    ids: List[tuple]
    sections: List[tuple]


def expand_section_range(start: str, end: str) -> List[str]:
    """``2.31`` 〜 ``2.33`` を ``[2.31, 2.32, 2.33]`` に展開する。

    最後の要素だけが異なる数字の範囲に限って展開し、それ以外（``2.31a``〜``2.33``・前の要素が違う・
    逆順・上限超え）は両端だけを占有として返す（展開できない範囲でも、両端の衝突は検出する）。
    """
    head_s, _, last_s = start.rpartition(".")
    head_e, _, last_e = end.rpartition(".")
    if head_s == head_e and last_s.isdigit() and last_e.isdigit():
        first, last = int(last_s), int(last_e)
        if first <= last and last - first < MAX_SECTION_RANGE:
            prefix = head_s + "." if head_s else ""
            return [f"{prefix}{n}" for n in range(first, last + 1)]
    return [start, end]


def parse_headings(md_path: str) -> HeadingScan:
    """文書全体の見出しから ID と節番号を集める。

    コードブロック（CommonMark のフェンス）と HTML コメント（``<!-- … -->``）の中は見出しとして数えない。
    """
    ids = []
    sections = []
    fence = None  # 開いているフェンスの記号（例: "````"）。None ならブロックの外
    in_comment = False
    with open(md_path, encoding="utf-8-sig") as f:
        for lineno, line in enumerate(f, start=1):
            if fence is not None:
                stripped = line.strip()
                if (
                    len(line) - len(line.lstrip(" ")) <= 3
                    and stripped
                    and set(stripped) == {fence[0]}
                    and len(stripped) >= len(fence)
                ):
                    fence = None
                continue
            if in_comment:
                if HTML_COMMENT_CLOSE in line:
                    in_comment = False
                continue
            m = FENCE_OPEN_RE.match(line)
            if m:
                fence = m.group(1)
                continue
            start = line.find(HTML_COMMENT_OPEN)
            if start >= 0 and line.find(HTML_COMMENT_CLOSE, start + len(HTML_COMMENT_OPEN)) < 0:
                in_comment = True
            m = HEADING_ID_RE.match(line)
            if m:
                ids.append((lineno, m.group(1)))
                continue
            m = HEADING_SECTION_RE.match(line)
            if m:
                number = m.group(1).rstrip(".")
                tail = SECTION_RANGE_TAIL_RE.match(line, m.end())
                if tail:
                    for n in expand_section_range(number, tail.group(1).rstrip(".")):
                        sections.append((lineno, n))
                else:
                    sections.append((lineno, number))
    return HeadingScan(ids, sections)


def find_duplicates(entries) -> Dict[str, List[int]]:
    """[(行番号, 表記), ...] から 2 回以上現れる表記と、その行番号の一覧を返す。"""
    lines_by_key: Dict[str, List[int]] = {}
    for lineno, key in entries:
        lines_by_key.setdefault(key, []).append(lineno)
    return {key: lines for key, lines in lines_by_key.items() if len(lines) > 1}


def check_heading_uniqueness(scan: HeadingScan) -> Tuple[bool, str]:
    """見出しの ID・節番号が重複していないことを確かめる（Issue #2391）。

    同じ ID が 2 つの節にあると、Issue・PR・コードのコメントから ID を引いたときにどちらを
    指すか決まらない。意図した重複は無いので、許可の書き方は設けない（直すときは、引用の少ない
    側・後から足した側を空いている ID へ付け替え、旧 ID を本文に注記する）。
    見出しを 1 つも読めなかったときは、正規表現が文書の書式から外れた空振りとして fail する。
    """
    if not scan.ids or not scan.sections:
        return False, (
            "⚠ 見出しの ID・節番号を読み取れませんでした（"
            f"ID {len(scan.ids)} 件・節番号 {len(scan.sections)} 件）\n"
            "  見出しの書式が変わったか、本スクリプトの HEADING_ID_RE / HEADING_SECTION_RE が"
            "文書に合っていません。正規表現を文書の書式に合わせて更新してください。"
        )

    dup_ids = find_duplicates(scan.ids)
    dup_sections = find_duplicates(scan.sections)
    if not dup_ids and not dup_sections:
        return True, (
            f"✅ 見出しの重複なし（ID {len(scan.ids)} 件・節番号 {len(scan.sections)} 件）"
        )

    lines = ["❌ 07_テスト設計書の見出しに重複があります"]
    for key, linenos in sorted(dup_ids.items()):
        lines.append(f"  ID {key}: {', '.join(str(n) for n in linenos)} 行目")
    for key, linenos in sorted(dup_sections.items()):
        lines.append(f"  節番号 §{key}: {', '.join(str(n) for n in linenos)} 行目")
    lines.append(
        "  後から足した側（または引用の少ない側）を空いている ID・番号へ付け替え、"
        "その ID を引用している箇所も直してください。"
    )
    return False, "\n".join(lines)


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
        return None, (
            "実測に該当するテストクラスがありません"
            "（クラス名の変更・削除・名前空間の移動を疑う。§2 は単体テストの章のため"
            " ICCardManager.Tests.* のみを照合対象にしている）"
        )
    if len(candidates) > 1:
        return None, "同名のテストクラスが複数あります: " + " / ".join(sorted(candidates))
    return candidates[0], None


def _format_skipped_rows(scan: ClassRowScan) -> str:
    """比較対象外にした差分行を「黙って読み飛ばしていない」形で列挙する。"""
    if not scan.delta_rows:
        return "差分表記のため検査対象外の行: なし"
    listed = "、".join(f"{lineno} 行 `{name}`" for lineno, name in scan.delta_rows)
    return f"差分表記のため検査対象外の行: {len(scan.delta_rows)}（{listed}）"


def compare_class_counts(scan: ClassRowScan, actual: Dict[str, int]) -> Tuple[bool, str]:
    """§2 の記載件数と実測件数を比較し、差分レポートを返す。"""
    if not scan.section_found:
        return False, "\n".join([
            "❌ §2（`## 2. 単体テスト`）の見出しが見つからず、検査が空振りしています",
            "  クラス別件数の走査範囲は §2 の見出しから導出しています。章番号・見出し書式を",
            "  変更した場合は SECTION2_HEADING_RE を追随させてください。",
        ])

    if len(scan.rows) < MIN_CLASS_ROWS:
        return False, "\n".join([
            "❌ §2 のクラス別件数行が想定より少なく、検査が空振りしています",
            f"  検出した行数: {len(scan.rows)}（下限 {MIN_CLASS_ROWS}）",
            "  表を壊していないか、CLASS_ROW_RE の書式（| `Namespace/ClassTests` | 件数 | 観点 |）",
            "  から外れていないかを確認してください。",
        ])

    problems = []
    # 書式から外れた行は「読み飛ばして緑」にしない。1 行だけ検査対象から静かに消えると
    # MIN_CLASS_ROWS の余裕に吸収され、§2 のドリフトが再び無言で通る。
    for lineno, doc_name, cell in scan.malformed_rows:
        problems.append((
            lineno, doc_name, "-", None,
            f"件数セル「{cell}」が十進の絶対値ではないため比較できません"
            "（名前セルはバッククォートで囲んだクラス表記のみ、件数セルは 59 のような"
            "十進の絶対値のみにしてください）",
        ))
    for lineno, doc_name, doc_count in scan.rows:
        fqn, reason = resolve_class(doc_name, actual)
        if fqn is None:
            problems.append((lineno, doc_name, doc_count, None, reason))
            continue
        if actual[fqn] != doc_count:
            problems.append((lineno, doc_name, doc_count, actual[fqn], None))

    if not problems:
        return True, (
            f"✅ テスト件数表 §2 と実測値が一致しています（{len(scan.rows)} クラス"
            f"／{_format_skipped_rows(scan)}）"
        )

    lines = [
        "❌ テスト件数表 §2 が実測値と乖離しています",
        "",
        "| 行 | テストクラス | 記載値 | 実測値 | 備考 |",
        "|----|-------------|-------|-------|------|",
    ]
    for lineno, doc_name, doc_count, act, reason in sorted(problems, key=lambda x: x[0]):
        if reason is not None:
            lines.append(f"| {lineno} | `{doc_name}` | {doc_count} | - | {reason} |")
        else:
            diff = act - doc_count
            sign = "+" if diff > 0 else ""
            lines.append(f"| {lineno} | `{doc_name}` | {doc_count} | {act} | {sign}{diff} |")
    lines += [
        "",
        _format_skipped_rows(scan),
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
        description=(
            "Verify the test counts in 07_テスト設計書.md (§1.1a totals and §2 per-class rows) "
            "against actual dotnet test counts."
        )
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

    class_scan = parse_class_counts(args.doc)
    heading_ok, heading_report = check_heading_uniqueness(parse_headings(args.doc))

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
        class_scan, count_class_tests(unit_names)
    )

    # 片方が失敗しても両方のレポートを出す（1 回の CI 実行で両方を直せるようにする）。
    # stdout はブロックバッファリングされるため、flush しないと CI ログで 2 つのレポートの
    # 前後が入れ替わって読みにくくなる。
    for passed, text in ((ok, report), (class_ok, class_report), (heading_ok, heading_report)):
        stream = sys.stdout if passed else sys.stderr
        print(text, file=stream)
        stream.flush()

    return 0 if ok and class_ok and heading_ok else 1


if __name__ == "__main__":
    sys.exit(main())
