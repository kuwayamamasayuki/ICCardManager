#!/usr/bin/env python3
"""07_テスト設計書.md の件数表を dotnet test --list-tests と比較するスクリプト。

検証対象は 4 つ:

- §1.1a の総件数表（単体 / UI / 合計） … Issue #1546
- §2 のクラス別件数表（``| `Namespace/ClassTests` | 件数 | 観点 |``） … Issue #1889
- 見出しの ID（``#### UT-049: …``）と節番号（``### 2.47a …``）が文書全体で重複しないこと … Issue #2391
- 先頭の列が ``No`` の表で、番号に飛び・重複が無いこと … Issue #2420
  （下 2 つは件数の照合ではないが、同じ 07 を同じ CI で検査するのでここに置く。dotnet を使わない。
  No 列の許可の書き方（範囲・枝番・続きの表・欠番の注記）は ``find_no_column_problems`` と
  ``.claude/rules/testing.md`` を参照）

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
H2_PREFIX = r"^ {0,3}##[ \t]+"
H3_OR_DEEPER_PREFIX = r"^ {0,3}#{3,6}[ \t]+"
# 番号・ID の直後に来てよい文字: 空白・行末・ASCII のコロン・ASCII 以外（全角の括弧・コロン・ダッシュ・和文）。
# ASCII の英数字や記号が続く見出し（``2.2-x`` 等）は番号として扱わない。
TOKEN_END = r"(?=[\s:]|[^\x00-\x7F]|$)"
# 見出しの先頭に置く ID（``#### UT-049: …`` / ``### UT-083`` / ``#### UT-058c-FlaUI: …``）。
# 接頭辞は英大文字 2〜4 文字（UT / IT / ST / PT / SEC / UAT）。ID は ASCII の英数字とハイフンだけを取るので、
# ``:`` ・全角コロン・全角括弧・空白の手前で切れる。節番号と同じく直後に TOKEN_END を要求するので、
# ``#### UT-049.1`` ・ ``#### UT-049_b`` のように ASCII の記号が続く見出しは ID として数えない（UT-049 と
# 数えると、別の見出しを重複と誤検出する）。
ID_TOKEN = r"[A-Z]{2,4}-[A-Za-z0-9-]+" + TOKEN_END
HEADING_ID_RE = re.compile(HEADING_PREFIX + r"(" + ID_TOKEN + r")")
# ID の範囲見出し（``#### UT-067〜UT-071: …``）の終端。末尾の数字だけが違う範囲（同じ桁数）は展開して
# 各 ID を占有済みとして扱う。展開できない範囲（``UT-058b〜UT-058c-FlaUI`` 等）は両端だけを占有として扱う。
ID_RANGE_TAIL_RE = re.compile(r"\s*[〜～]\s*(" + ID_TOKEN + r")")
# 見出しの先頭に置く節番号。
# - ``###`` 以下は点を含む番号（``### 2.47a`` / ``#### 1.6.1`` / ``#### 5.11a-2`` / ``### 7a.1``）に限る。
#   ``#### 1. 準備`` ・ ``#### 30秒ルール`` のような、数字で始まるだけの見出しは節番号ではない
# - ``##`` は章番号（``## 2.`` / ``## 2a.`` / ``## 10.``）も許す
# 番号の文字（ASCII の英数字と ``.``・``-数字`` の枝番）だけを取る。空白まで取ると ``2.47a（…）`` ・ ``2.47a：…`` が
# 別の番号になり、重複を見逃す。``2.20a`` と ``2.20a2``、``1.6`` と ``1.6.1`` は別の番号として数える。
DOTTED_SECTION_NUMBER = r"\d+[A-Za-z]?(?:\.\d+[A-Za-z0-9]*)+(?:-\d+(?![.\d]))?\.?"
CHAPTER_NUMBER = r"\d+[A-Za-z]?(?:\.\d+[A-Za-z0-9]*)*\.?"
H3_SECTION_RE = re.compile(H3_OR_DEEPER_PREFIX + r"(" + DOTTED_SECTION_NUMBER + r")" + TOKEN_END)
H2_SECTION_RE = re.compile(H2_PREFIX + r"(" + CHAPTER_NUMBER + r")" + TOKEN_END)
# 範囲表記（``### 2.31〜2.33 — 欠番``）の終端。範囲の番号はすべて占有済みとして扱う
# （後から ``### 2.32`` を足したら重複として検出する）。範囲の記号は 〜（波ダッシュ）・～（全角チルダ）だけ。
# ASCII のハイフン・全角ハイフン・ダッシュ（``### 2.2 - 2.1系の補足`` / ``2.31－2.33``）は範囲とみなさず、
# 先頭の番号だけを数える。
SECTION_RANGE_TAIL_RE = re.compile(r"\s*[〜～]\s*(" + DOTTED_SECTION_NUMBER + r")" + TOKEN_END)
# 範囲を展開する上限。書き損じ（``2.1〜2.1000``）で巨大な集合を作らないため。
MAX_SECTION_RANGE = 100
# コードブロックの開始（CommonMark: インデント 0〜3、同じ記号 3 文字以上）。閉じるのは開いた記号と同じ種類・
# 同じ長さ以上の記号だけの行。種類・長さを見ないと、```` の中の ``` で開閉が反転する。
# バッククォートのフェンスの info 文字列にバッククォートは書けない（CommonMark）ので、``` ```foo``` は例 ``` の
# ような段落（インラインコード）はフェンスの開始ではない。
FENCE_OPEN_RE = re.compile(r"^ {0,3}(`{3,}|~{3,})(.*)$")
# HTML コメント（ブロック）の開始は行頭（インデント 0〜3）の ``<!--`` に限る。本文中のインラインコード
# （``<!-- 検索条件 -->`` コメント以降）や文中の ``<!--`` で開くと、以降の見出しを数えずに「重複なし」になる。
HTML_COMMENT_OPEN_RE = re.compile(r"^ {0,3}<!--")
HTML_COMMENT_CLOSE = "-->"

# Issue #2420: 表の「No」列の飛び・重複。
# 見出し（節の区切り）。続きの表・欠番の注記は、同じ見出しの中だけで結び付ける。
ATX_HEADING_RE = re.compile(r"^ {0,3}#{1,6}(?:[ \t]|$)")
# 表の区切り行（``|---|:--:|``）。少なくとも 1 つの ``|`` を含む。
TABLE_SEPARATOR_RE = re.compile(r"^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)+\|?\s*$")
# 検査の対象にする表: 先頭の列の見出しが ``No`` / ``No.``。
NO_HEADER_RE = re.compile(r"^No\.?$")
# No のセルの書き方。範囲の記号は 〜（波ダッシュ）・～（全角チルダ）・~・-（表記の揺れは許す）。
# 数字は半角だけを読む（re.ASCII）。Python の ``\d`` は既定で全角の ``２`` にも一致するが、07 の No のセルと注記に
# 全角数字は無く（2026-11 時点で確認）、全角は書き損じとして「読めないセル」の案内に回す。
NO_RANGE_MARK = r"[〜～~]"
NO_INT_RE = re.compile(r"^(\d+)$", re.ASCII)
NO_BRANCH_RE = re.compile(r"^(\d+)([a-z])$", re.ASCII)
NO_WAVE_RANGE_RE = re.compile(r"^(\d+)\s*" + NO_RANGE_MARK + r"\s*(\d+)$", re.ASCII)
NO_HYPHEN_RANGE_RE = re.compile(r"^(\d+)\s*-\s*(\d+)$", re.ASCII)
# ハイフンを枝番に使った書き方（``1-1`` ・ ``3a-2`` ・ ``1-7a``）。これが 1 つでもある表では ``N-M`` を範囲と読まない。
NO_HYPHEN_LIKE_RE = re.compile(r"^\d+[a-z]?\s*-\s*\d+[a-z]?$", re.ASCII)
NO_BRANCH_RANGE_RE = re.compile(r"^(\d+)([a-z])\s*" + NO_RANGE_MARK + r"\s*(\d+)([a-z])$", re.ASCII)
NO_LIST_RE = re.compile(r"^\d+(?:\s*[・、,]\s*\d+)+$", re.ASCII)
# 接頭辞付きの系列（``E1`` ・ ``T2b``）。番号を 0 で埋めた ``TC002`` は行の番号ではなくテストケースの ID
# （表をまたいで飛び飛びに引用される）なので対象外にする。
NO_PREFIXED_RE = re.compile(r"^([A-Z]{1,3})([1-9]\d*)([a-z]?)$", re.ASCII)
NO_PREFIXED_RANGE_RE = re.compile(
    r"^([A-Z]{1,3})([1-9]\d*)\s*(?:" + NO_RANGE_MARK + r"|-)\s*([A-Z]{1,3})([1-9]\d*)$",
    re.ASCII,
)
# 意図した欠番の注記（``No.8・9 は欠番`` / ``No.4〜6 は欠番`` / ``No.E3 は欠番``）。番号の後に「は欠番」が続く形だけを読む
# （「No.8 を参照。…欠番」のように、同じ行にあるだけの番号を許可に数えないため）。列挙の要素ごとに ``No.`` を
# 付けた形（``No.2、No.3 は欠番``）も読む。
# 「は欠番」の直後は許可形に限る。空白を挟んでもよいが、その後ろは句読点・閉じ括弧・表の区切り ``|``・行末・
# 「です」「とする」（その後ろも句読点・空白・行末）か、開き括弧に続く平仮名でない語（``（Issue #1）`` ・ ``（旧 …）``）
# だけ。否定の語を並べて除く方式では、「欠番にしない」「欠番とはしない」「欠番扱いにせず」「欠番 にしない」
# 「欠番（ではない）」のような否定を取りこぼし、許可として読んでしまう（極性の反転）。
# インラインコード（`` `No.8 は欠番` ``）の中は、書き方の例なので読まない。
_GAP_NOTE_TOKEN = r"(?:No\.?\s*)?[A-Z]{0,3}\d+"
_GAP_NOTE_END = r"(?=[\s。、，,．.）)」』\]】|]|$)"
GAP_NOTE_RE = re.compile(
    r"No\.?\s*(" + _GAP_NOTE_TOKEN + r"(?:\s*(?:[・、,，]|" + NO_RANGE_MARK + r"|-)\s*" + _GAP_NOTE_TOKEN + r")*)"
    r"\s*は\s*欠番"
    r"(?=\s*(?:[。、，,．.）)」』\]】|]|$|(?:です|とする)" + _GAP_NOTE_END + r"|[（(](?!\s*[ぁ-ん])))",
    re.ASCII,
)
INLINE_CODE_RE = re.compile(r"`+[^`]*`+")
# 範囲を展開する上限。書き損じ（``1〜1000``）で巨大な集合を作らないため。
MAX_NO_RANGE = 200
# 番号のつもりで書いたが読めないセル（``13.`` ・ ``5〜3`` ・ ``1〜300`` ・全角の ``２``。全角を読めないのはセルの
# 正規表現に re.ASCII を付けているため）。07 には既にあるので
# それだけでは赤にしないが、抜けの位置にあれば案内に添える（原因が行の消失ではなく書式の誤りかもしれないため）。
UNREADABLE_NO_CELL_RE = re.compile(r"^[0-9０-９]")
# 見出しをまたいで続く表の、続ける先の候補にする直近の系列の数。07 では、続きの表の間に 1 から始め直す
# 節（``UT-REPORT-EXPORT-STATUS-001a``）が 1 つ挟まる形があるため 2 以上が要る。
CROSS_HEADING_LOOKBACK = 3


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
    # ファイルの終わりで開いたままのコードブロック・HTML コメント（"コードブロック（123 行目から）" 等）。
    # 閉じ忘れると以降の見出しをすべて数えずに「重複なし」になるため、検査を fail にする。
    unclosed: Optional[str] = None


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


_TRAILING_DIGITS_RE = re.compile(r"^(.*?)(\d+)$")


def expand_id_range(start: str, end: str) -> List[str]:
    """``UT-067`` 〜 ``UT-071`` を ``[UT-067, …, UT-071]`` に展開する。

    末尾の数字だけが違い、桁数が同じ範囲に限って展開する（``UT-067`` の 0 埋めを保つ）。それ以外は
    両端だけを占有として返す。
    """
    ms, me = _TRAILING_DIGITS_RE.match(start), _TRAILING_DIGITS_RE.match(end)
    if ms and me and ms.group(1) == me.group(1) and len(ms.group(2)) == len(me.group(2)):
        first, last = int(ms.group(2)), int(me.group(2))
        if first <= last and last - first < MAX_SECTION_RANGE:
            width = len(ms.group(2))
            return [f"{ms.group(1)}{n:0{width}d}" for n in range(first, last + 1)]
    return [start, end]


def mask_blocks(lines: List[str]) -> Tuple[List[Optional[str]], Optional[str]]:
    """コードブロック（CommonMark のフェンス）と HTML コメント（行頭の ``<!--`` から ``-->`` まで）の行を
    ``None`` に置き換えた行の一覧と、ファイルの終わりで開いたままのブロックの説明（無ければ ``None``）を返す。

    見出しの検査（Issue #2391）と No 列の検査（Issue #2420）が同じ判定を使う（片方だけ直して食い違わないため）。
    """
    masked: List[Optional[str]] = []
    fence = None  # 開いているフェンスの記号（例: "````"）。None ならブロックの外
    in_comment = False
    opened_at = 0  # 開いているコードブロック・HTML コメントの開始行
    for lineno, line in enumerate(lines, start=1):
        if fence is not None:
            stripped = line.strip()
            if (
                len(line) - len(line.lstrip(" ")) <= 3
                and stripped
                and set(stripped) == {fence[0]}
                and len(stripped) >= len(fence)
            ):
                fence = None
            masked.append(None)
            continue
        if in_comment:
            if HTML_COMMENT_CLOSE in line:
                in_comment = False
            masked.append(None)
            continue
        m = FENCE_OPEN_RE.match(line)
        if m and not (m.group(1)[0] == "`" and "`" in m.group(2)):
            fence = m.group(1)
            opened_at = lineno
            masked.append(None)
            continue
        m = HTML_COMMENT_OPEN_RE.match(line)
        if m:
            if HTML_COMMENT_CLOSE not in line[m.end():]:
                in_comment = True
                opened_at = lineno
            masked.append(None)
            continue
        masked.append(line)
    unclosed = None
    if fence is not None:
        unclosed = f"コードブロック（{opened_at} 行目の {fence}）"
    elif in_comment:
        unclosed = f"HTML コメント（{opened_at} 行目の <!--）"
    return masked, unclosed


def _read_lines(md_path: str) -> List[str]:
    with open(md_path, encoding="utf-8-sig") as f:
        return f.read().splitlines()


def parse_headings(md_path: str) -> HeadingScan:
    """文書全体の見出しから ID と節番号を集める。

    コードブロック（CommonMark のフェンス）と HTML コメント（行頭の ``<!--`` から ``-->`` まで）の中は
    見出しとして数えない。
    """
    ids = []
    sections = []
    masked, unclosed = mask_blocks(_read_lines(md_path))
    for lineno, line in enumerate(masked, start=1):
        if line is None:
            continue
        m = HEADING_ID_RE.match(line)
        if m:
            tail = ID_RANGE_TAIL_RE.match(line, m.end())
            if tail:
                for i in expand_id_range(m.group(1), tail.group(1)):
                    ids.append((lineno, i))
            else:
                ids.append((lineno, m.group(1)))
            continue
        m = H3_SECTION_RE.match(line) or H2_SECTION_RE.match(line)
        if m:
            number = m.group(1).rstrip(".")
            tail = SECTION_RANGE_TAIL_RE.match(line, m.end())
            if tail:
                for n in expand_section_range(number, tail.group(1).rstrip(".")):
                    sections.append((lineno, n))
            else:
                sections.append((lineno, number))
    return HeadingScan(ids, sections, unclosed)


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
    if scan.unclosed:
        return False, (
            f"❌ ファイルの終わりまで {scan.unclosed} が閉じていません\n"
            "  閉じていないと、そこから後の見出しを 1 つも数えずに「重複なし」になります。"
            "閉じの記号（開いた記号と同じ種類・同じ長さ以上の ``` / ~~~、または -->）を書き足してください。"
        )
    if not scan.ids or not scan.sections:
        return False, (
            "⚠ 見出しの ID・節番号を読み取れませんでした（"
            f"ID {len(scan.ids)} 件・節番号 {len(scan.sections)} 件）\n"
            "  見出しの書式が変わったか、本スクリプトの HEADING_ID_RE / H3_SECTION_RE / H2_SECTION_RE が"
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


class NoEntry(NamedTuple):
    """No のセルが占める 1 つの番号（Issue #2420）。

    Attributes:
        series: 系列の接頭辞（``E1`` なら ``E``。数字だけの系列は ``""``）
        base: 飛びの判定に使う番号（枝番 ``8a`` は 8）
        key: 重複の判定に使う表記（``8`` と ``8a`` は別、範囲 ``7〜9`` は ``7``・``8``・``9``）
    """

    series: str
    base: int
    key: str


class NoRow(NamedTuple):
    lineno: int
    cell: str
    entries: List[NoEntry]


class NoTable(NamedTuple):
    """先頭の列が ``No`` の表（Issue #2420）。

    Attributes:
        lineno: 表の見出し行（``| No | … |``）の行番号
        section_lineno: 表が属する見出し（``#`` 〜 ``######``）の行番号。見出しより前なら 0
        rows: 表の行
        notes: この表に結び付いた注記「No.8・9 は欠番」が許す (系列, 番号) の集合。結び付くのは、
            同じ見出しの中で表の直前（前の表・見出しから表まで）と直後（表から次の表・見出しまで）の本文と、
            表のセルに書いた注記だけ（同じ見出しの離れた表の欠番を隠さないため）
    """

    lineno: int
    section_lineno: int
    rows: List[NoRow]
    notes: frozenset = frozenset()


class NoColumnScan(NamedTuple):
    """先頭の列が ``No`` の表の走査結果（Issue #2420）。

    Attributes:
        tables: 文書順の表
        unclosed: ファイルの終わりで開いたままのコードブロック・HTML コメント
    """

    tables: List[NoTable]
    unclosed: Optional[str] = None


def _first_cell(line: str) -> str:
    s = line.strip()
    if s.startswith("|"):
        s = s[1:]
    return s.split("|", 1)[0].strip().replace("**", "").strip()


def _is_table_line(line: Optional[str]) -> bool:
    return line is not None and line.lstrip().startswith("|")


def _is_ascending_hyphen_range(cell: str) -> bool:
    m = NO_HYPHEN_RANGE_RE.match(cell)
    return bool(m) and int(m.group(1)) < int(m.group(2))


def _expand(series: str, first: int, last: int) -> List[NoEntry]:
    if first < last <= first + MAX_NO_RANGE:
        return [NoEntry(series, n, f"{series}{n}") for n in range(first, last + 1)]
    return []


def parse_no_cell(cell: str, hyphen_is_range: bool = True) -> List[NoEntry]:
    """No のセル 1 つを、占める番号の一覧に直す。検査の対象外の書き方なら空の一覧を返す。

    - ``8`` → 8 ／ ``8a`` → 基の番号 8 を占める枝番（重複の判定は ``8a`` の表記で行う）
    - ``7〜9`` ・ ``7～9`` ・ ``7~9`` ・ ``7-9`` → 7・8・9（範囲）／ ``20d〜20g`` → 枝番の範囲
    - ``9・10`` → 9・10（列挙）
    - ``E1`` ・ ``M3b`` → 接頭辞（英大文字 1〜3 文字）ごとの別の系列
    - それ以外（``UT-063-1`` のような ID 付きの枝番・``1-7a`` 等）は対象外

    ``hyphen_is_range`` が偽のとき（表が ``1-1`` ・ ``3a-2`` のようにハイフンを枝番に使っている）は、
    ``N-M`` を範囲として展開せず対象外にする（``1-2`` を 1・2 と読むと、続く ``2`` を重複と誤検出する）。
    """
    m = NO_INT_RE.match(cell)
    if m:
        n = int(m.group(1))
        return [NoEntry("", n, str(n))]
    m = NO_BRANCH_RE.match(cell)
    if m:
        return [NoEntry("", int(m.group(1)), cell)]
    m = NO_WAVE_RANGE_RE.match(cell)
    if m:
        return _expand("", int(m.group(1)), int(m.group(2)))
    m = NO_HYPHEN_RANGE_RE.match(cell)
    if m:
        return _expand("", int(m.group(1)), int(m.group(2))) if hyphen_is_range else []
    m = NO_BRANCH_RANGE_RE.match(cell)
    if m and m.group(1) == m.group(3) and m.group(2) < m.group(4):
        n = int(m.group(1))
        return [
            NoEntry("", n, f"{n}{chr(c)}")
            for c in range(ord(m.group(2)), ord(m.group(4)) + 1)
        ]
    if NO_LIST_RE.match(cell):
        return [NoEntry("", int(n), n) for n in re.findall(r"\d+", cell)]
    m = NO_PREFIXED_RE.match(cell)
    if m:
        return [NoEntry(m.group(1), int(m.group(2)), cell)]
    m = NO_PREFIXED_RANGE_RE.match(cell)
    if m and m.group(1) == m.group(3):
        return _expand(m.group(1), int(m.group(2)), int(m.group(4)))
    return []


def parse_gap_note(line: str) -> set:
    """本文の注記「No.8・9 は欠番」「No.4〜6 は欠番」「No.E3 は欠番」から (系列, 番号) の集合を返す。"""
    allowed = set()
    if "欠番" not in line:
        return allowed
    for m in GAP_NOTE_RE.finditer(INLINE_CODE_RE.sub("", line)):
        for part in re.split(r"\s*[・、,，]\s*", m.group(1)):
            tokens = [(p, int(n)) for p, n in re.findall(r"([A-Z]{0,3})([0-9]+)", part)]
            if len(tokens) == 1:
                allowed.update(tokens)
            elif len(tokens) == 2 and tokens[0][0] == tokens[1][0] and tokens[0][1] < tokens[1][1]:
                allowed.update((tokens[0][0], n) for n in range(tokens[0][1], tokens[1][1] + 1))
            # それ以外（接頭辞が食い違う範囲 ``E3〜5``・逆順 ``5〜3``）は読めない注記なので、何も許さない。
            # 両端を単独の番号として許すと、``No.E3〜5 は欠番`` が無印の 5 の欠けまで許してしまう
    return allowed


def parse_no_columns(md_path: str) -> NoColumnScan:
    """先頭の列が ``No`` の表と、それぞれの表に結び付いた欠番の注記を集める（Issue #2420）。

    コードブロック・HTML コメントの中の表と注記は数えない（見出しの検査と同じ ``mask_blocks``）。
    注記は、同じ見出しの中で直前・直後の表にだけ結び付ける（``NoTable.notes``）。
    """
    masked, unclosed = mask_blocks(_read_lines(md_path))
    tables: List[NoTable] = []
    section = 0
    pending: set = set()  # 直前の表（または見出し）より後に書かれ、次の表へ結び付ける注記
    previous: Optional[int] = None  # 同じ見出しの中で直前の表（tables の添字）。直後の注記を結び付ける先
    i = 0
    while i < len(masked):
        line = masked[i]
        if line is None:
            i += 1
            continue
        if ATX_HEADING_RE.match(line):
            section = i + 1
            pending = set()
            previous = None
            i += 1
            continue
        if (
            _is_table_line(line)
            and i + 1 < len(masked)
            and masked[i + 1] is not None
            and TABLE_SEPARATOR_RE.match(masked[i + 1])
        ):
            j = i + 2
            while j < len(masked) and _is_table_line(masked[j]):
                j += 1
            if NO_HEADER_RE.match(_first_cell(line)):
                cells = [(k + 1, _first_cell(masked[k])) for k in range(i + 2, j)]
                hyphen_is_range = not any(
                    NO_HYPHEN_LIKE_RE.match(c) and not _is_ascending_hyphen_range(c)
                    for _, c in cells
                )
                rows = [
                    NoRow(lineno, c, parse_no_cell(c, hyphen_is_range)) for lineno, c in cells
                ]
                # 表のセルに書いた注記（``| 11 | … | …。No.3 は欠番 |``）も数える
                notes = set(pending)
                for k in range(i, j):
                    notes.update(parse_gap_note(masked[k]))
                tables.append(NoTable(i + 1, section, rows, frozenset(notes)))
                previous = len(tables) - 1
                pending = set()
            i = j
            continue
        found = parse_gap_note(line)
        if found:
            pending.update(found)
            if previous is not None:
                tables[previous] = tables[previous]._replace(notes=tables[previous].notes | found)
        i += 1
    return NoColumnScan(tables, unclosed)


def _format_numbers(series: str, numbers: List[int]) -> str:
    """[8, 9, 12] → ``No.8〜9・12``。"""
    parts = []
    start = prev = None
    for n in sorted(numbers) + [None]:
        if start is not None and (n is None or n != prev + 1):
            parts.append(f"{series}{start}" if start == prev else f"{series}{start}〜{series}{prev}")
            start = None
        if n is not None and start is None:
            start = n
        prev = n
    return "No." + "・".join(parts)


def find_no_column_problems(scan: NoColumnScan) -> List[Tuple[int, str]]:
    """表の No 列の飛び・重複を (行番号, 説明) の一覧で返す。

    - 系列は表ごと・接頭辞ごと。1（または 0）から始まらない表は、次の順で続ける先を探して 1 つの系列にまとめる
      1. 同じ見出しの中の直前の表（同じ接頭辞）。間の飛びも検査する
      2. 見出しをまたいだ直近の系列（最後に行を足した順に ``CROSS_HEADING_LOOKBACK`` 個まで）のうち、最大の番号が
         ちょうど「この表の最小の番号 − 1」のもの（近いものから）。番号がつながっていることを結び付けの根拠に
         するので、境目の行が消えていれば結び付かず赤になる。遠くの無関係な表と偶然つながらないよう、候補は直近に限る
      3. どちらも無ければ 1 から始まる系列として数える（先頭の行が消えた表は、欠番の注記が無ければ赤）

    **注記は、その注記が結び付いた表の受け持ちの欠けにだけ効く**。表の受け持ちは、系列のそれまでの最大の番号の次
    （系列の最初の表なら系列の始まり）から、その表の最大の番号まで。続きとして束ねた系列でも、後ろの表の注記が前の表の
    欠けを許すことは無い（先頭を欠番と注記した表が、偶然つながった前の表の続きになった場合も同じ）。

    **偶然つながる形は見逃す**: 前の表の最大の番号が、後ろの表から消えた先頭の行の数とちょうど等しいとき
    （1〜3 の表の後で、続きの表に 4〜8 だけが残った等）、後ろの表は前の表の続きに見えて緑になる（同じ見出しでも、
    見出しをまたいでも同じ）。
    - 飛び: 系列の始まりから最大の番号までの間で、どの行も占めていない番号。その番号を受け持つ表に結び付いた注記
      「No.8 は欠番」で許した番号は除く。抜けの位置に番号として読めないセルの行があれば、案内に添える
    - 重複: 同じ系列の中で同じ表記（``8`` / ``8a``）を占める行が 2 つ以上（範囲 ``7〜9`` と ``8`` も重複）

    **系列の終わりは最大の番号**なので、表の末尾の行が消えたことは原理的に検出できない（基準が無い）。
    """
    groups: List[dict] = []
    last: Dict[Tuple[int, str], dict] = {}
    recent: List[dict] = []  # 最後に行を足した順の系列（見出しをまたぐ続きの候補）
    for table in scan.tables:
        by_series: Dict[str, List[Tuple[int, NoEntry]]] = {}
        for row in table.rows:
            for entry in row.entries:
                by_series.setdefault(entry.series, []).append((row.lineno, entry))
        for series, items in by_series.items():
            smallest = min(e.base for _, e in items)
            group = None
            if smallest > 1:
                group = last.get((table.section_lineno, series))
                if group is None:
                    group = next(
                        (
                            g for g in recent[-CROSS_HEADING_LOOKBACK:][::-1]
                            if g["series"] == series
                            and max(e.base for _, e in g["items"]) == smallest - 1
                        ),
                        None,
                    )
            if group is None:
                group = {
                    "series": series,
                    "start": min(smallest, 1),
                    "table_lineno": table.lineno,
                    "items": [],
                    # 表ごとの番号の受け持ち（lo〜hi）と、その表に結び付いた注記。注記は受け持ちの中の欠けだけを許す
                    "segments": [],
                    "unreadable": [],
                    # 1 から始まらないのに続ける先が見つからなかった表の最小の番号（案内を変えるため）
                    "orphan_from": smallest if smallest > 1 else None,
                }
                groups.append(group)
            # この表が受け持つ番号: 系列のそれまでの最大の番号の次（最初の表なら系列の始まり）から、この表の最大の番号まで
            lo = max(e.base for _, e in group["items"]) + 1 if group["items"] else group["start"]
            group["segments"].append((lo, max(e.base for _, e in items), table.notes))
            group["items"].extend(items)
            group["unreadable"].extend(
                (row.lineno, row.cell) for row in table.rows
                if not row.entries and UNREADABLE_NO_CELL_RE.match(row.cell)
            )
            last[(table.section_lineno, series)] = group
            if group in recent:
                recent.remove(group)
            recent.append(group)

    problems: List[Tuple[int, str]] = []
    for group in groups:
        series, start, items = group["series"], group["start"], group["items"]
        where = f"{group['table_lineno']} 行目の表"
        lines_by_key: Dict[str, List[int]] = {}
        for lineno, entry in items:
            lines_by_key.setdefault(entry.key, []).append(lineno)
        for key, linenos in lines_by_key.items():
            if len(linenos) > 1:
                problems.append((
                    linenos[1],
                    f"{where}: No.{key} が重複しています（{', '.join(str(n) for n in linenos)} 行目）",
                ))
        bases = {e.base for _, e in items}

        def allowed(n: int) -> bool:
            return any(
                lo <= n <= hi and (series, n) in notes for lo, hi, notes in group["segments"]
            )

        missing = [n for n in range(start, max(bases) + 1) if n not in bases and not allowed(n)]
        # 連続する欠けを 1 件にまとめ、その直後の番号を持つ行の行番号を示す
        runs: List[List[int]] = []
        for n in missing:
            if runs and runs[-1][-1] == n - 1:
                runs[-1].append(n)
            else:
                runs.append([n])
        for run in runs:
            after = min(((e.base, lineno) for lineno, e in items if e.base > run[-1]))
            text = (
                f"{where}: {_format_numbers(series, run)} が抜けています"
                f"（{after[1]} 行目の No.{series}{after[0]} の前）"
            )
            # 抜けの位置に番号として読めないセルの行があれば、行の消失ではなく書式の誤りかもしれない
            before_line = max(
                (lineno for lineno, e in items if e.base < run[0]), default=group["table_lineno"]
            )
            suspects = [
                (lineno, cell) for lineno, cell in group["unreadable"]
                if before_line < lineno < after[1]
            ]
            if suspects:
                listed = "、".join(f"{lineno} 行目の「{cell}」" for lineno, cell in suspects)
                text += f"。{listed} を番号として読めません。番号の書式（例: 13・7〜9・8a）を確かめてください"
            orphan_from = group["orphan_from"]
            if orphan_from is not None and run[0] == 1 and not suspects:
                # 先頭の欠けは、行の消失のほか「前の表の続きのつもりが、続ける先が見つからなかった」こともある
                text += (
                    f"。前の表の続きなら、続ける先が見つかりませんでした（直前の表の最大の番号が"
                    f" No.{series}{orphan_from - 1} でない、または見出しをまたいで間に別の No の表が"
                    f" {CROSS_HEADING_LOOKBACK} つ以上ある）。番号をつなげるか、表を近くへ移してください"
                )
            problems.append((after[1], text))
    return sorted(problems)


def check_no_columns(scan: NoColumnScan) -> Tuple[bool, str]:
    """07 の表の No 列に飛び・重複が無いことを確かめる（Issue #2420）。

    #2321 では 07 の 66 か所（計 152 行）から No.13〜15 の行が一斉に消えたが、CI は緑のまま通った。
    表を 1 つも読めなかったときは、正規表現が文書の書式から外れた空振りとして fail する。
    """
    if scan.unclosed:
        return False, (
            f"❌ ファイルの終わりまで {scan.unclosed} が閉じていないため、表の No 列を検査できません\n"
            "  閉じの記号（開いた記号と同じ種類・同じ長さ以上の ``` / ~~~、または -->）を書き足してください。"
        )
    if not scan.tables:
        return False, (
            "⚠ 先頭の列が「No」の表を 1 つも読み取れませんでした\n"
            "  表の書式が変わったか、本スクリプトの NO_HEADER_RE / TABLE_SEPARATOR_RE が文書に合っていません。"
            "正規表現を文書の書式に合わせて更新してください。"
        )
    problems = find_no_column_problems(scan)
    if not problems:
        return True, f"✅ 表の No 列に飛び・重複なし（{len(scan.tables)} 表）"
    lines = ["❌ 07_テスト設計書の表の No 列に飛び・重複があります"]
    lines += [f"  {lineno} 行目 — {text}" for lineno, text in problems]
    lines += [
        "  行を消した・番号を書き損じたのなら、行を戻すか番号を直してください。",
        "  削除したテストの番号を詰めずに残すのが意図なら、表の直前か直後（同じ見出しの中）に",
        "  「No.8・9 は欠番」のように注記してください（範囲は「No.4〜6 は欠番」）。重複は注記で許せません。",
    ]
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

    # 見出しの重複検査（Issue #2391）と表の No 列の検査（Issue #2420）は dotnet も §1.1a の表も使わないので、
    # 最初に結果を出す。後ろの検査が早期に終了しても（§1.1a の不一致・一覧の取得失敗）、両者の結果は必ずログに残る。
    # 終了コードは、早期に終了したときはその値（いずれも非 0）、最後まで進んだときは全検査の合成。
    structure_ok = True
    for passed, text in (
        check_heading_uniqueness(parse_headings(args.doc)),
        check_no_columns(parse_no_columns(args.doc)),
    ):
        stream = sys.stdout if passed else sys.stderr
        print(text, file=stream)
        stream.flush()
        structure_ok = structure_ok and passed

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
    # 見出しの検査のレポートは冒頭で出し済み。
    # stdout はブロックバッファリングされるため、flush しないと CI ログで 2 つのレポートの
    # 前後が入れ替わって読みにくくなる。
    for passed, text in ((ok, report), (class_ok, class_report)):
        stream = sys.stdout if passed else sys.stderr
        print(text, file=stream)
        stream.flush()

    return 0 if ok and class_ok and structure_ok else 1


if __name__ == "__main__":
    sys.exit(main())
