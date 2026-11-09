"""tools/check-test-count-sync.py の単体テスト。"""
import contextlib
import importlib.util
import io
import pathlib
import re
import sys
import tempfile
import textwrap
import unittest

# tools/ を import path に追加
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

# モジュール名にハイフンが入るため importlib で動的ロード
SPEC_PATH = pathlib.Path(__file__).resolve().parent.parent / "check-test-count-sync.py"
_spec = importlib.util.spec_from_file_location("check_test_count_sync", SPEC_PATH)
_mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_mod)
parse_doc_counts = _mod.parse_doc_counts
compare = _mod.compare
parse_class_counts = _mod.parse_class_counts
count_class_tests = _mod.count_class_tests
resolve_class = _mod.resolve_class
compare_class_counts = _mod.compare_class_counts
ClassRowScan = _mod.ClassRowScan
MIN_CLASS_ROWS = _mod.MIN_CLASS_ROWS
parse_headings = _mod.parse_headings
check_heading_uniqueness = _mod.check_heading_uniqueness


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


# --- §2 クラス別件数（Issue #1889） -------------------------------------------------

SAMPLE_SECTION2 = textwrap.dedent("""\
    ## 2. 単体テスト

    #### UT-073: 管理者ダッシュボード

    | テストクラス | 件数 | 主な検証観点 |
    |---|---|---|
    | `Common/Charting/ChartScaleTests` | 47 | 目盛り |
    | `Services/AdminDashboardServiceTests` | 59 | 集計 |
    | `Views/MainWindowKeyBindingTests`（追加 4 件） | +4 | F8 の割り当て |

    #### §1.6 の別書式（クラス別件数表ではない）

    | テスト名 | ファイル | 説明 |
    |---|---|---|
    | `DeleteOrClearFile_通常のファイルの場合_削除されてtrueを返す` | `DeleteOrClearFileTests.cs` | 説明 |
    | `[Fact]` | これ 1 つで 1 ケースのテスト |

    #### クラス名 | テスト名 | 期待結果 の表（件数表ではない）

    | テストクラス | テスト名 | 期待結果 |
    |---|---|---|
    | `MainViewModelTests` | `DeleteLedgerRow_確認の結果に従って削除すること`（2） | 「はい」なら削除 |

    ## 3. 結合テスト

    | テストクラス | 件数 | 主な検証観点 |
    |---|---|---|
    | `Services/OutOfSectionTests` | 99 | §2 の外なので対象外 |
    """)


def _make_rows(count: int, per_class: int = 5):
    """MIN_CLASS_ROWS を満たすだけの (行番号, 表記, 件数) を機械的に作る。"""
    return [(i + 1, f"Common/Sample{i}Tests", per_class) for i in range(count)]


def _make_scan(rows, delta_rows=(), malformed_rows=(), section_found=True):
    return ClassRowScan(list(rows), list(delta_rows), list(malformed_rows), section_found)


def _make_actual(count: int, per_class: int = 5):
    return {
        f"ICCardManager.Tests.Common.Sample{i}Tests": per_class for i in range(count)
    }


class ParseClassCountsTests(unittest.TestCase):
    """§2 のクラス別件数行の抽出（検査ロジックをサンプル入力で固定する）。"""

    def setUp(self):
        self.path = _write_md(SAMPLE_SECTION2)
        self.addCleanup(lambda: pathlib.Path(self.path).unlink(missing_ok=True))

    def test_absolute_rows_are_extracted_with_line_numbers(self):
        scan = parse_class_counts(self.path)
        self.assertTrue(scan.section_found)
        self.assertEqual(
            [(name, count) for _, name, count in scan.rows],
            [
                ("Common/Charting/ChartScaleTests", 47),
                ("Services/AdminDashboardServiceTests", 59),
            ],
        )
        # 行番号は 1 始まりで、実ファイルの位置を指す
        self.assertEqual(scan.rows[0][0], 7)

    def test_delta_row_is_listed_but_not_compared(self):
        """「（追加 4 件）| +4」は絶対値ではないので比較しない。ただし黙って捨てない。"""
        scan = parse_class_counts(self.path)
        self.assertEqual(
            scan.delta_rows, [(9, "Views/MainWindowKeyBindingTests")]
        )
        self.assertNotIn(
            "Views/MainWindowKeyBindingTests", [name for _, name, _ in scan.rows]
        )

    def test_other_table_formats_are_not_picked_up(self):
        """対の表明: 件数表以外の表（§1.6 のテスト名一覧・「クラス｜テスト名」表）を拾わない。"""
        scan = parse_class_counts(self.path)
        names = [name for _, name, _ in scan.rows]
        self.assertNotIn("DeleteOrClearFile_通常のファイルの場合_削除されてtrueを返す", names)
        self.assertEqual(len(names), 2)
        # 2 列目がテスト名（バッククォート付き）の表を「書式違反の件数行」と誤検出しない
        self.assertEqual(scan.malformed_rows, [])

    def test_rows_outside_section2_are_ignored(self):
        """走査範囲は §2 の見出しから次の `##` まで。§3 の同形の表を巻き込まない。"""
        scan = parse_class_counts(self.path)
        self.assertNotIn(
            "Services/OutOfSectionTests", [name for _, name, _ in scan.rows]
        )

    def test_missing_section_heading_is_reported(self):
        """§2 の見出しが無ければ走査は空振り。黙って「差分なし」にしない。"""
        path = _write_md("| `Services/FooTests` | 12 | 観点 |\n")
        self.addCleanup(lambda: pathlib.Path(path).unlink(missing_ok=True))
        scan = parse_class_counts(path)
        self.assertFalse(scan.section_found)
        self.assertEqual(scan.rows, [])

    def test_malformed_count_cell_is_reported_not_skipped(self):
        """件数セルの書式から外れた行を黙って読み飛ばさない（検査対象が静かに縮む経路）。"""
        path = _write_md(textwrap.dedent("""\
            ## 2. 単体テスト

            | テストクラス | 件数 | 主な検証観点 |
            |---|---|---|
            | `Services/WithNoteTests`（Issue #1900 で新設） | 12 | 観点 |
            | `Services/WithUnitTests` | 12 件 | 観点 |
            | `Services/BoldTests` | **12** | 観点 |
            """))
        self.addCleanup(lambda: pathlib.Path(path).unlink(missing_ok=True))
        scan = parse_class_counts(path)
        self.assertEqual(scan.rows, [])
        self.assertEqual(
            [name for _, name, _ in scan.malformed_rows],
            ["Services/WithNoteTests", "Services/WithUnitTests", "Services/BoldTests"],
        )


class CountClassTestsTests(unittest.TestCase):
    """--list-tests の出力からクラス単位の件数を数える。"""

    def test_groups_by_fully_qualified_class_name(self):
        names = [
            "    ICCardManager.Tests.Services.FooTests.Bar",
            "    ICCardManager.Tests.Services.FooTests.Baz",
            "    ICCardManager.Tests.Common.QuxTests.Method",
        ]
        self.assertEqual(
            count_class_tests(names),
            {
                "ICCardManager.Tests.Services.FooTests": 2,
                "ICCardManager.Tests.Common.QuxTests": 1,
            },
        )

    def test_theory_arguments_do_not_split_the_class(self):
        """Theory の表示名は引数に「.」を含み得るため、括弧以降を捨ててから数える。"""
        names = [
            "    ICCardManager.Tests.Common.QuxTests.Method(value: 1.5)",
            "    ICCardManager.Tests.Common.QuxTests.Method(value: 2.5)",
        ]
        self.assertEqual(
            count_class_tests(names), {"ICCardManager.Tests.Common.QuxTests": 2}
        )


class ResolveClassTests(unittest.TestCase):
    """表記から実測クラスの完全修飾名への解決。"""

    ACTUAL = {
        "ICCardManager.Tests.Common.Charting.ChartScaleTests": 47,
        "ICCardManager.Tests.Views.ChartScaleTests": 3,
        "ICCardManager.Tests.Services.AdminDashboardServiceTests": 59,
    }

    def test_path_prefix_disambiguates_same_simple_name(self):
        fqn, reason = resolve_class("Common/Charting/ChartScaleTests", self.ACTUAL)
        self.assertIsNone(reason)
        self.assertEqual(fqn, "ICCardManager.Tests.Common.Charting.ChartScaleTests")

    def test_ambiguous_simple_name_is_reported(self):
        fqn, reason = resolve_class("ChartScaleTests", self.ACTUAL)
        self.assertIsNone(fqn)
        self.assertIn("複数", reason)

    def test_missing_class_is_reported(self):
        fqn, reason = resolve_class("Common/NotExistTests", self.ACTUAL)
        self.assertIsNone(fqn)
        self.assertIn("該当するテストクラスがありません", reason)

    def test_wrong_path_prefix_is_reported(self):
        """名前空間を移したのに表記を直していない場合も検出する。"""
        fqn, reason = resolve_class("Services/ChartScaleTests", self.ACTUAL)
        self.assertIsNone(fqn)
        self.assertIn("該当するテストクラスがありません", reason)


class CompareClassCountsTests(unittest.TestCase):
    """§2 の記載値と実測値の比較。"""

    def test_all_match_returns_ok_true(self):
        scan = _make_scan(_make_rows(MIN_CLASS_ROWS), delta_rows=[(9, "Views/FooTests")])
        ok, report = compare_class_counts(scan, _make_actual(MIN_CLASS_ROWS))
        self.assertTrue(ok, report)
        self.assertIn("§2", report)
        # 読み飛ばした行は成功時も名指しで列挙する（黙って捨てない）
        self.assertIn("9 行 `Views/FooTests`", report)

    def test_mismatch_reports_both_values_and_line_number(self):
        scan = _make_scan(_make_rows(MIN_CLASS_ROWS))
        actual = _make_actual(MIN_CLASS_ROWS)
        actual["ICCardManager.Tests.Common.Sample3Tests"] = 8
        ok, report = compare_class_counts(scan, actual)
        self.assertFalse(ok)
        self.assertIn("Sample3Tests", report)
        self.assertIn("| 4 |", report)  # 行番号
        self.assertIn("+3", report)
        self.assertIn("更新してください", report)

    def test_failure_report_also_lists_skipped_delta_rows(self):
        """対の表明: 失敗時にも読み飛ばした行を伏せない（成功時だけ出す非対称を防ぐ）。"""
        scan = _make_scan(_make_rows(MIN_CLASS_ROWS), delta_rows=[(9, "Views/FooTests")])
        actual = _make_actual(MIN_CLASS_ROWS)
        actual["ICCardManager.Tests.Common.Sample3Tests"] = 8
        _, report = compare_class_counts(scan, actual)
        self.assertIn("9 行 `Views/FooTests`", report)

    def test_unresolvable_row_is_reported_as_a_problem(self):
        scan = _make_scan(_make_rows(MIN_CLASS_ROWS) + [(99, "Common/GoneTests", 5)])
        ok, report = compare_class_counts(scan, _make_actual(MIN_CLASS_ROWS))
        self.assertFalse(ok)
        self.assertIn("GoneTests", report)
        # 照合範囲（単体テストのみ）を明示し、UI テストのクラスを書いた場合に
        # 「改名した？」と誤誘導しないこと
        self.assertIn("ICCardManager.Tests.*", report)

    def test_malformed_row_fails_instead_of_being_skipped(self):
        """書式違反の行は比較できないが、読み飛ばして緑にはしない。"""
        scan = _make_scan(
            _make_rows(MIN_CLASS_ROWS),
            malformed_rows=[(42, "Services/WithUnitTests", "12 件")],
        )
        ok, report = compare_class_counts(scan, _make_actual(MIN_CLASS_ROWS))
        self.assertFalse(ok)
        self.assertIn("WithUnitTests", report)
        self.assertIn("12 件", report)
        self.assertIn("| 42 |", report)

    def test_too_few_rows_fails_instead_of_passing_silently(self):
        """表が壊れて検査対象が縮んだとき、差分ゼロで緑にならないこと。"""
        ok, report = compare_class_counts(
            _make_scan(_make_rows(MIN_CLASS_ROWS - 1)),
            _make_actual(MIN_CLASS_ROWS - 1),
        )
        self.assertFalse(ok)
        self.assertIn("空振り", report)

    def test_missing_section_fails_instead_of_passing_silently(self):
        """§2 の見出しを見失った状態で「差分なし」の緑にならないこと。"""
        ok, report = compare_class_counts(
            _make_scan(_make_rows(MIN_CLASS_ROWS), section_found=False),
            _make_actual(MIN_CLASS_ROWS),
        )
        self.assertFalse(ok)
        self.assertIn("見出し", report)

    def test_report_states_that_missing_rows_are_out_of_scope(self):
        """検出できない範囲（行を足し忘れたクラス）を黙って伏せないこと。"""
        scan = _make_scan(_make_rows(MIN_CLASS_ROWS))
        actual = _make_actual(MIN_CLASS_ROWS)
        actual["ICCardManager.Tests.Common.Sample0Tests"] = 6
        _, report = compare_class_counts(scan, actual)
        self.assertIn("行を足していない場合は検出できない", report)


# Issue #2391: 見出しの ID・節番号の重複検査。検査ロジック自体を既知の見本で固定する
# （実際の 07 に重複が無くなっても、空振りの検出が働き続けるように）。
SAMPLE_HEADINGS_UNIQUE = textwrap.dedent("""\
    ## 1. テスト概要

    ### 1.6 テストコードの読み方

    #### 1.6.1 テストファイルの構造

    ## 2. 単体テスト

    ### 2.20a 帳票出力前プリフライトチェック

    #### UT-024a: DisplayFormatters

    ### 2.20a2 出力済みチェックリスト

    #### UT-024h: 設定ファイルのアトミック保存

    ### 2.31〜2.33 — 欠番

    ### UT-083

    #### UT-058c: レイアウト

    #### UT-058c-FlaUI: 描画

    ## 2a. 単体テスト（続き）
    """)


class HeadingUniquenessTests(unittest.TestCase):
    def _check(self, content):
        path = _write_md(content)
        try:
            return check_heading_uniqueness(parse_headings(path))
        finally:
            pathlib.Path(path).unlink()

    def test_unique_headings_pass(self):
        ok, report = self._check(SAMPLE_HEADINGS_UNIQUE)
        self.assertTrue(ok, report)

    def test_prefix_of_another_number_or_id_is_not_a_duplicate(self):
        # 1.6 と 1.6.1、2.20a と 2.20a2、UT-058c と UT-058c-FlaUI はそれぞれ別の見出し
        path = _write_md(SAMPLE_HEADINGS_UNIQUE)
        try:
            scan = parse_headings(path)
        finally:
            pathlib.Path(path).unlink()
        self.assertIn("1.6", [s for _, s in scan.sections])
        self.assertIn("1.6.1", [s for _, s in scan.sections])
        self.assertIn("2.20a2", [s for _, s in scan.sections])
        self.assertIn("UT-058c-FlaUI", [i for _, i in scan.ids])
        self.assertIn("2a", [s for _, s in scan.sections])

    def test_duplicate_id_fails_with_both_line_numbers(self):
        content = SAMPLE_HEADINGS_UNIQUE + "\n#### UT-024a: 設定ファイル（別の節）\n"
        ok, report = self._check(content)
        self.assertFalse(ok)
        self.assertIn("ID UT-024a", report)
        self.assertIn("ID UT-024a: 11, 27 行目", report)

    def test_duplicate_id_at_different_heading_levels_fails(self):
        content = SAMPLE_HEADINGS_UNIQUE + "\n#### UT-083: 同じ ID の別の節\n"
        ok, report = self._check(content)
        self.assertFalse(ok)
        self.assertIn("ID UT-083", report)

    def test_duplicate_section_number_fails(self):
        content = SAMPLE_HEADINGS_UNIQUE + "\n### 2.20a バージョン混在の検出\n"
        ok, report = self._check(content)
        self.assertFalse(ok)
        self.assertIn("節番号 §2.20a", report)

    def test_headings_inside_code_block_are_ignored(self):
        content = SAMPLE_HEADINGS_UNIQUE + "\n```\n#### UT-024a: コードの中\n### 2.20a\n```\n"
        ok, report = self._check(content)
        self.assertTrue(ok, report)

    def test_document_without_headings_fails_instead_of_passing_silently(self):
        ok, report = self._check("本文だけで見出しが無い\n")
        self.assertFalse(ok)
        self.assertIn("読み取れませんでした", report)

    def _assert_duplicate(self, extra, expected):
        ok, report = self._check(SAMPLE_HEADINGS_UNIQUE + extra)
        self.assertFalse(ok, f"重複を見逃した: {extra!r}")
        self.assertIn(expected, report)

    def test_section_number_followed_by_fullwidth_paren_or_colon_is_a_duplicate(self):
        # 番号の直後に空白を置かずに全角括弧・全角コロンが続いても、同じ番号として数える
        self._assert_duplicate("\n### 2.20a（バージョン混在）\n", "節番号 §2.20a:")
        self._assert_duplicate("\n### 2.20a：バージョン混在\n", "節番号 §2.20a:")

    def test_id_followed_by_fullwidth_paren_or_colon_is_a_duplicate(self):
        self._assert_duplicate("\n#### UT-024a：別の節\n", "ID UT-024a:")
        self._assert_duplicate("\n#### UT-024a（別の節）\n", "ID UT-024a:")

    def test_heading_indented_up_to_three_spaces_is_counted(self):
        self._assert_duplicate("\n   #### UT-024a: インデント付き\n", "ID UT-024a:")

    def test_line_indented_four_spaces_is_not_a_heading(self):
        # 4 文字のインデントは CommonMark ではコードブロック
        ok, report = self._check(SAMPLE_HEADINGS_UNIQUE + "\n    #### UT-024a: コード\n")
        self.assertTrue(ok, report)

    def test_shorter_or_different_fence_inside_a_block_does_not_close_it(self):
        content = SAMPLE_HEADINGS_UNIQUE + textwrap.dedent("""\

            ````markdown
            ```
            #### UT-024a: 4 連のブロックの中
            ~~~
            ### 2.20a
            ````
            """)
        ok, report = self._check(content)
        self.assertTrue(ok, report)

    def test_heading_after_a_closed_fence_is_counted(self):
        self._assert_duplicate("\n~~~\n### 2.1 コード\n~~~\n#### UT-024a: ブロックの後\n", "ID UT-024a:")

    def test_headings_inside_html_comment_are_ignored(self):
        content = SAMPLE_HEADINGS_UNIQUE + "\n<!--\n#### UT-024a: コメントの中\n### 2.20a\n-->\n"
        ok, report = self._check(content)
        self.assertTrue(ok, report)

    def test_heading_after_html_comment_is_counted(self):
        self._assert_duplicate("\n<!-- 1 行のコメント -->\n<!--\n複数行\n-->\n### 2.20a 後\n", "節番号 §2.20a:")

    def test_section_range_occupies_every_number_in_it(self):
        # 2.31〜2.33 の欠番の注記がある所へ 2.32 を足したら重複
        self._assert_duplicate("\n### 2.32 新しい節\n", "節番号 §2.32:")
        path = _write_md(SAMPLE_HEADINGS_UNIQUE)
        try:
            numbers = [s for _, s in parse_headings(path).sections]
        finally:
            pathlib.Path(path).unlink()
        self.assertEqual(["2.31", "2.32", "2.33"], [n for n in numbers if n.startswith("2.3")])

    def test_number_just_outside_a_range_is_not_a_duplicate(self):
        ok, report = self._check(SAMPLE_HEADINGS_UNIQUE + "\n### 2.34 次の節\n")
        self.assertTrue(ok, report)

    def test_branch_number_is_part_of_the_section_number(self):
        # 5.11a-2 は 5.11a と別の番号（範囲表記ではない）
        ok, report = self._check(SAMPLE_HEADINGS_UNIQUE + "\n#### 5.11a 節\n\n#### 5.11a-2 枝番\n")
        self.assertTrue(ok, report)

    # --- レビュー 2 回目: 本文中のインラインコードで開かない・数字で始まるだけの見出しを数えない ---

    def test_inline_html_comment_in_a_paragraph_does_not_hide_later_headings(self):
        # 07 の本文に実在する形（「`<!-- 検索条件 -->` コメント以降」）と、文中の閉じない <!--
        self._assert_duplicate(
            "\n「`<!-- 検索条件 -->` コメント以降最初の行」\n\n#### UT-024a: 後の見出し\n", "ID UT-024a:"
        )
        self._assert_duplicate(
            "\n本文の途中に <!-- と書いた行\n\n#### UT-024a: 後の見出し\n", "ID UT-024a:"
        )

    def test_inline_triple_backtick_code_in_a_paragraph_is_not_a_fence(self):
        # info 文字列にバッククォートを含む行はフェンスの開始ではない（CommonMark）
        self._assert_duplicate("\n```foo``` は例\n\n#### UT-024a: 後の見出し\n", "ID UT-024a:")

    def test_indented_html_comment_block_is_still_ignored(self):
        content = SAMPLE_HEADINGS_UNIQUE + "\n   <!-- 複数行のコメント\n#### UT-024a: 中\n-->\n"
        ok, report = self._check(content)
        self.assertTrue(ok, report)

    def test_numbered_list_like_headings_are_not_section_numbers(self):
        content = SAMPLE_HEADINGS_UNIQUE + textwrap.dedent("""\

            #### 1. 準備

            #### 1. 準備

            #### 30秒ルール

            #### 30秒ルール
            """)
        ok, report = self._check(content)
        self.assertTrue(ok, report)

    def test_ascii_hyphen_is_not_a_range(self):
        # 「2.2 - 2.1系の補足」は範囲ではないので 2.1 を占有しない
        ok, report = self._check(SAMPLE_HEADINGS_UNIQUE + "\n### 2.1 本来の節\n\n### 2.2 - 2.1系の補足\n")
        self.assertTrue(ok, report)

    def test_section_number_followed_by_ascii_colon_is_a_duplicate(self):
        self._assert_duplicate("\n### 2.20a: バージョン混在\n", "節番号 §2.20a:")

    def test_id_range_occupies_every_id_in_it(self):
        content = SAMPLE_HEADINGS_UNIQUE + "\n#### UT-067〜UT-071: まとめた節\n"
        path = _write_md(content)
        try:
            ids = [i for _, i in parse_headings(path).ids]
        finally:
            pathlib.Path(path).unlink()
        self.assertEqual(["UT-067", "UT-068", "UT-069", "UT-070", "UT-071"], [i for i in ids if i.startswith(("UT-06", "UT-07"))])
        self._assert_duplicate("\n#### UT-067〜UT-071: まとめた節\n\n#### UT-069: 後から\n", "ID UT-069:")

    # --- レビュー 3 回目 ---

    def test_id_followed_by_ascii_symbol_is_not_counted_as_that_id(self):
        # UT-024a.1 / UT-024a_b を UT-024a と数えると、別の見出しを重複と誤検出する
        ok, report = self._check(SAMPLE_HEADINGS_UNIQUE + "\n#### UT-024a.1 補足\n\n#### UT-024a_b 補足\n")
        self.assertTrue(ok, report)

    def test_unclosed_fence_at_end_of_file_fails(self):
        ok, report = self._check(SAMPLE_HEADINGS_UNIQUE + "\n```\n閉じ忘れ\n#### UT-024a: 数えられない\n")
        self.assertFalse(ok)
        self.assertIn("コードブロック", report)
        self.assertIn("閉じていません", report)

    def test_unclosed_html_comment_at_end_of_file_fails(self):
        ok, report = self._check(SAMPLE_HEADINGS_UNIQUE + "\n<!--\n閉じ忘れ\n#### UT-024a: 数えられない\n")
        self.assertFalse(ok)
        self.assertIn("HTML コメント", report)
        self.assertIn("閉じていません", report)


class MainReportsHeadingCheckOnEarlyExitTests(unittest.TestCase):
    """レビュー 2 回目: 後ろの検査が早期に終了しても、見出しの検査結果は必ず出す。"""

    DUPLICATED = "\n## 2. 単体テスト\n\n#### UT-001: a\n\n#### UT-001: b\n"

    def _run_main(self, doc):
        path = _write_md(doc)
        err = io.StringIO()
        out = io.StringIO()
        try:
            with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
                code = _mod.main(["--doc", path, "--unit-csproj", "u.csproj", "--ui-csproj", "ui.csproj"])
        finally:
            pathlib.Path(path).unlink()
        return code, out.getvalue() + err.getvalue()

    def test_sum_mismatch_still_reports_heading_duplicates(self):
        doc = SAMPLE_DOC_OK.replace("**3,292件**", "**9,999件**") + self.DUPLICATED + NO_TABLE_GAP
        code, output = self._run_main(doc)
        self.assertEqual(1, code)
        self.assertIn("§1.1a の記載値の合計", output)
        self.assertIn("ID UT-001:", output)
        self.assertIn("No.2 が抜けています", output)  # Issue #2420: No 列の検査の結果も出す

    def test_listing_failure_still_reports_heading_duplicates(self):
        original = _mod.list_test_names

        def fail(*_args, **_kwargs):
            raise RuntimeError("dotnet test --list-tests に失敗")

        _mod.list_test_names = fail
        try:
            code, output = self._run_main(SAMPLE_DOC_OK + self.DUPLICATED + NO_TABLE_GAP)
        finally:
            _mod.list_test_names = original
        self.assertEqual(2, code)
        self.assertIn("ID UT-001:", output)
        self.assertIn("No.2 が抜けています", output)  # Issue #2420: No 列の検査の結果も出す


class MainExitCodeCombinesHeadingCheckTests(unittest.TestCase):
    """レビュー 3 回目: 最後まで進んだとき、終了コードは見出しの検査結果も合成する。"""

    CLASS_COUNT = MIN_CLASS_ROWS

    def _doc(self, headings):
        rows = "\n".join(
            f"| `Services/Sample{i}Tests` | 1 | 見本 |" for i in range(self.CLASS_COUNT)
        )
        return textwrap.dedent(f"""\
            ### 1.1a テスト規模（現状）

            | 種別 | テスト数 | 備考 |
            |------|---------|------|
            | 単体テスト（ICCardManager.Tests） | {self.CLASS_COUNT}件 | 見本 |
            | UIテスト（ICCardManager.UITests） | 1件 | 見本 |
            | **合計** | **{self.CLASS_COUNT + 1}件** | 見本 |

            """) + "## 2. 単体テスト\n\n| クラス | 件数 | 観点 |\n|---|---|---|\n" + rows + "\n" + headings + NO_TABLE_OK

    def _run_main(self, doc):
        def fake_list(_csproj, prefix):
            if prefix == "Tests":
                return [f"    ICCardManager.Tests.Services.Sample{i}Tests.Test" for i in range(self.CLASS_COUNT)]
            return ["    ICCardManager.UITests.Sample.Test"]

        original = _mod.list_test_names
        _mod.list_test_names = fake_list
        path = _write_md(doc)
        try:
            with contextlib.redirect_stderr(io.StringIO()) as err, contextlib.redirect_stdout(io.StringIO()) as out:
                code = _mod.main(["--doc", path, "--unit-csproj", "u.csproj", "--ui-csproj", "ui.csproj"])
        finally:
            _mod.list_test_names = original
            pathlib.Path(path).unlink()
        return code, out.getvalue() + err.getvalue()

    def test_all_checks_pass_returns_zero(self):
        code, output = self._run_main(self._doc("\n### 2.1 節\n\n#### UT-001: a\n"))
        self.assertEqual(0, code, output)

    def test_counts_match_but_headings_duplicate_returns_one(self):
        code, output = self._run_main(self._doc("\n### 2.1 節\n\n#### UT-001: a\n\n#### UT-001: b\n"))
        self.assertEqual(1, code, output)
        self.assertIn("ID UT-001:", output)
        self.assertIn("✅", output)  # 件数の検査は通っている（失敗の原因が見出しだけであることの前提）

    def test_counts_match_but_no_column_has_a_gap_returns_one(self):
        """Issue #2420: No 列の検査の結果も終了コードに合成する。"""
        doc = self._doc("\n### 2.1 節\n\n#### UT-001: a\n") + NO_TABLE_GAP
        code, output = self._run_main(doc)
        self.assertEqual(1, code, output)
        self.assertIn("No.2 が抜けています", output)
        self.assertIn("✅ テスト件数表 §2", output)  # 失敗の原因が No 列だけであることの前提


# ---- Issue #2420: 表の「No」列の飛び・重複 ----

NO_TABLE_OK = "\n#### UT-900: No 列の見本\n\n| No | ケース |\n|----|----|\n| 1 | a |\n| 2 | b |\n"
NO_TABLE_GAP = "\n#### UT-901: 飛びのある表\n\n| No | ケース |\n|----|----|\n| 1 | a |\n| 3 | c |\n"


def _no_problems(doc: str):
    path = _write_md(doc)
    try:
        return _mod.find_no_column_problems(_mod.parse_no_columns(path))
    finally:
        pathlib.Path(path).unlink()


def _no_check(doc: str):
    path = _write_md(doc)
    try:
        return _mod.check_no_columns(_mod.parse_no_columns(path))
    finally:
        pathlib.Path(path).unlink()


def _table(*cells, header="No"):
    rows = "\n".join(f"| {c} | x |" for c in cells)
    return f"| {header} | ケース |\n|----|----|\n{rows}\n"


class ParseNoCellTests(unittest.TestCase):
    def _keys(self, cell, hyphen_is_range=True):
        return [(e.series, e.base, e.key) for e in _mod.parse_no_cell(cell, hyphen_is_range)]

    def test_integer(self):
        self.assertEqual([("", 8, "8")], self._keys("8"))

    def test_every_range_mark_expands(self):
        for cell in ("7〜9", "7～9", "7~9", "7-9", "7 - 9"):
            with self.subTest(cell=cell):
                self.assertEqual([7, 8, 9], [b for _, b, _ in self._keys(cell)])

    def test_branch_occupies_its_base_number(self):
        self.assertEqual([("", 8, "8a")], self._keys("8a"))

    def test_branch_range(self):
        self.assertEqual(["20d", "20e", "20f", "20g"], [k for _, _, k in self._keys("20d〜20g")])

    def test_list(self):
        self.assertEqual([9, 10], [b for _, b, _ in self._keys("9・10")])

    def test_prefixed_series(self):
        self.assertEqual([("E", 1, "E1")], self._keys("E1"))
        self.assertEqual([("T", 2, "T2b")], self._keys("T2b"))

    def test_out_of_scope_forms(self):
        for cell in ("UT-063-1", "TC002", "M-SHARED-1", "TC033B〜D", "1-7a", "", "—"):
            with self.subTest(cell=cell):
                self.assertEqual([], self._keys(cell))

    def test_hyphen_is_not_a_range_when_the_table_uses_it_as_a_branch(self):
        self.assertEqual([], self._keys("1-2", hyphen_is_range=False))


class ParseGapNoteTests(unittest.TestCase):
    def test_list_range_and_prefix(self):
        self.assertEqual({("", 8), ("", 9)}, _mod.parse_gap_note("No.8・9 は欠番。"))
        self.assertEqual({("", 4), ("", 5), ("", 6)}, _mod.parse_gap_note("> No.4〜6 は欠番（削除）"))
        self.assertEqual({("", 4), ("T", 6)}, _mod.parse_gap_note("（No.4・T6 は欠番）"))

    def test_number_not_followed_by_gap_word_is_not_allowed(self):
        self.assertEqual(set(), _mod.parse_gap_note("No.8 を参照。FILE001 は欠番"))
        self.assertEqual(set(), _mod.parse_gap_note("No.8 は削除した"))


class NoColumnGreenSamplesTests(unittest.TestCase):
    """許可の設計どおりの書き方は緑。"""

    def test_sequential(self):
        self.assertEqual([], _no_problems(_table(1, 2, 3)))

    def test_ranges_fill_the_numbers_they_cover(self):
        self.assertEqual([], _no_problems(_table(1, "2-4", "5〜6", "7～8", "9~10", "11・12", 13)))

    def test_branches_fill_their_base_number(self):
        # 枝番だけで占めた番号（4）も飛びにしない。基の番号と枝番の併存（3・3a）は重複にしない
        self.assertEqual([], _no_problems(_table(1, 2, 3, "3a", "3b", "4a", "4b", 5)))

    def test_hyphen_used_as_a_branch_is_not_expanded(self):
        self.assertEqual([], _no_problems(_table(1, "1-1", "1-2", "1-3", 2, 3)))

    def test_prefixed_series_are_checked_separately(self):
        self.assertEqual([], _no_problems(_table(1, 2, "E1", "E2", 3)))

    def test_continuation_table_in_the_same_section(self):
        doc = "#### UT-001\n\n" + _table(1, 2, 3) + "\n本文\n\n" + _table(4, 5)
        self.assertEqual([], _no_problems(doc))

    def test_table_starting_at_one_in_the_same_section_is_a_new_series(self):
        doc = "#### UT-001\n\n" + _table(1, 2) + "\n本文\n\n" + _table(1, 2)
        self.assertEqual([], _no_problems(doc))

    def test_gap_with_a_note_before_or_after_or_in_a_cell(self):
        for note_before, note_after, cells in (
            ("No.3 は欠番。\n\n", "", (1, 2, 4)),
            ("", "\n> 旧 No.3・4 は削除した（No.3・4 は欠番）。\n", (1, 2, 5)),
            ("", "", (1, 2, "4 | No.3 は欠番")),
        ):
            with self.subTest(cells=cells):
                doc = "#### UT-001\n\n" + note_before + _table(*cells) + note_after
                self.assertEqual([], _no_problems(doc))

    def test_tables_inside_code_blocks_are_ignored(self):
        doc = "#### UT-001\n\n" + _table(1, 2) + "\n```markdown\n" + _table(1, 1, 5) + "```\n"
        self.assertEqual([], _no_problems(doc))
        doc = "#### UT-001\n\n" + _table(1, 2) + "\n<!--\n" + _table(1, 1, 5) + "-->\n"
        self.assertEqual([], _no_problems(doc))

    def test_tables_whose_first_column_is_not_no_are_ignored(self):
        doc = _table(1, 2) + "\n" + _table(1, 1, 5, header="ID")
        self.assertEqual([], _no_problems(doc))


class NoColumnRedSamplesTests(unittest.TestCase):
    """注記の無い飛び・重複は赤。行番号を出す。"""

    def test_gap_without_a_note(self):
        doc = "#### UT-001\n\n" + _table(1, 2, 5, 6)
        # 1: 見出し / 3: 表の見出し行 / 5〜8: 行
        self.assertEqual([(7, "3 行目の表: No.3〜4 が抜けています（7 行目の No.5 の前）")], _no_problems(doc))

    def test_issue_2321_shape_rows_13_to_15_vanished(self):
        doc = "#### UT-001\n\n" + _table(*range(1, 13), *range(16, 20))
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems))
        self.assertIn("No.13〜15 が抜けています", problems[0][1])

    def test_duplicate_number(self):
        doc = "#### UT-001\n\n" + _table(1, 2, 2, 3)
        self.assertEqual([(7, "3 行目の表: No.2 が重複しています（6, 7 行目）")], _no_problems(doc))

    def test_range_overlapping_a_number_is_a_duplicate(self):
        problems = _no_problems("#### UT-001\n\n" + _table(1, "2-4", 3, 5))
        self.assertEqual(1, len(problems))
        self.assertIn("No.3 が重複しています", problems[0][1])

    def test_duplicate_branch(self):
        doc = "#### UT-001\n\n" + _table(1, 2, "2a", "2a", 3)
        self.assertEqual([(8, "3 行目の表: No.2a が重複しています（7, 8 行目）")], _no_problems(doc))

    def test_gap_in_a_prefixed_series(self):
        problems = _no_problems("#### UT-001\n\n" + _table(1, 2, "T1", "T3"))
        self.assertEqual(1, len(problems))
        self.assertIn("No.T2 が抜けています", problems[0][1])

    def test_gap_between_a_table_and_its_continuation(self):
        doc = "#### UT-001\n\n" + _table(1, 2, 3) + "\n本文\n\n" + _table(5, 6)
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems))
        self.assertIn("No.4 が抜けています", problems[0][1])

    def test_duplicate_across_a_table_and_its_continuation(self):
        doc = "#### UT-001\n\n" + _table(1, 2, 3) + "\n本文\n\n" + _table(3, 4)
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems))
        self.assertIn("No.3 が重複しています", problems[0][1])

    def test_note_in_another_section_does_not_allow_the_gap(self):
        doc = "#### UT-001\n\nNo.3 は欠番。\n\n#### UT-002\n\n" + _table(1, 2, 4)
        self.assertEqual(1, len(_no_problems(doc)))

    def test_note_does_not_allow_a_duplicate(self):
        doc = "#### UT-001\n\nNo.2 は欠番。\n\n" + _table(1, 2, 2, 3)
        self.assertEqual(1, len(_no_problems(doc)))

    def test_report_lists_line_numbers_and_how_to_fix(self):
        ok, report = _no_check("#### UT-001\n\n" + _table(1, 3))
        self.assertFalse(ok)
        self.assertIn("6 行目 — 3 行目の表: No.2 が抜けています", report)
        self.assertIn("は欠番", report)

    def test_no_table_at_all_fails_as_a_blank_run(self):
        ok, report = _no_check("#### UT-001\n\n" + _table(1, 2, header="ID"))
        self.assertFalse(ok)
        self.assertIn("1 つも読み取れません", report)

    def test_unclosed_fence_fails(self):
        ok, report = _no_check(_table(1, 2) + "\n```\n" + _table(1, 1))
        self.assertFalse(ok)
        self.assertIn("閉じていない", report)


class NoColumnMutationTests(unittest.TestCase):
    """許可の設計の要（範囲の展開・枝番の寄せ・欠番の注記）を壊すと、緑の見本が赤になること。

    要を外した実装でも緑のままになる見本は、その要を固定していない。
    """

    def _with_patch(self, name, replacement, doc):
        original = getattr(_mod, name)
        setattr(_mod, name, replacement)
        try:
            return _no_problems(doc)
        finally:
            setattr(_mod, name, original)

    def test_without_range_expansion_the_range_sample_turns_red(self):
        doc = "#### UT-001\n\n" + _table(1, "2-4", "5〜6", 7)
        self.assertEqual([], _no_problems(doc))
        self.assertNotEqual([], self._with_patch("_expand", lambda s, a, b: [], doc))

    def test_without_branch_folding_the_branch_sample_turns_red(self):
        doc = "#### UT-001\n\n" + _table(1, "2a", "2b", 3)
        self.assertEqual([], _no_problems(doc))
        never = re.compile(r"(?!)")
        self.assertNotEqual([], self._with_patch("NO_BRANCH_RE", never, doc))

    def test_without_gap_notes_the_note_sample_turns_red(self):
        doc = "#### UT-001\n\nNo.3 は欠番。\n\n" + _table(1, 2, 4)
        self.assertEqual([], _no_problems(doc))
        self.assertNotEqual([], self._with_patch("parse_gap_note", lambda line: set(), doc))


class NoColumnNoteScopeTests(unittest.TestCase):
    """レビュー 1 回目: 欠番の注記は直前・直後の表にだけ効く。否定・インラインコードは注記として読まない。"""

    def test_note_does_not_hide_a_gap_in_a_distant_table_of_the_same_section(self):
        doc = (
            "#### UT-001\n\n" + _table(1, 2, 4) + "\n本文\n\n"
            + _table(1, 3) + "\n> No.2 は欠番。\n\n" + _table(1, 2, 3)
        )
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems), problems)
        self.assertIn("3 行目の表: No.3 が抜けています", problems[0][1])

    def test_note_between_two_tables_applies_to_both(self):
        doc = "#### UT-001\n\n" + _table(1, 3) + "\nNo.2 は欠番。\n\n" + _table(1, 2, 4) + "\n"
        self.assertEqual(1, len(_no_problems(doc)))  # 後の表の No.3 には効かない
        doc = "#### UT-001\n\n" + _table(1, 3) + "\nNo.2・3 は欠番。\n\n" + _table(1, 2, 4) + "\n"
        self.assertEqual([], _no_problems(doc))

    def test_note_before_a_table_does_not_reach_past_it(self):
        doc = "#### UT-001\n\nNo.2 は欠番。\n\n" + _table(1, 3) + "\n本文\n\n" + _table(1, 3)
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems), problems)
        self.assertIn("12 行目の表", problems[0][1])  # 後の表だけ

    def test_negated_note_is_not_an_allowance(self):
        """レビュー 3 回目: 否定の語を並べるのではなく、「は欠番」の直後を許可形に限る。"""
        for text in (
            "No.8 は欠番ではない", "No.8 は欠番でない", "No.8 は欠番ではありません",
            "No.8 は欠番にしない", "No.8 は欠番とはしない", "No.8 は欠番扱いにせず詰めた",
            "No.8 は欠番じゃない", "No.8 は欠番候補", "No.8 は欠番にする予定",
            # レビュー 4 回目: 空白・括弧を挟んだ否定
            "No.8 は 欠番 にしない", "No.8 は欠番 ではない", "No.8 は欠番（ではない）", "No.8 は欠番 （ではない）",
            "No.8 は欠番(にしない)", "No.8 は欠番ですらない", "No.8 は欠番とするかは未定",
        ):
            with self.subTest(text=text):
                self.assertEqual(set(), _mod.parse_gap_note(text))

    def test_allowed_forms_after_the_gap_word(self):
        for text in (
            "No.8 は欠番", "No.8 は欠番。", "No.8 は欠番、理由は…", "（No.8 は欠番）", "No.8 は欠番 |",
            "No.8 は欠番（Issue #1）", "No.8 は欠番です。", "No.8 は欠番とする。", "No.8 は欠番.",
            "No.8 は欠番 です。", "No.8 は 欠番。", "No.8 は欠番（旧 No.8 は削除）", "No.8 は欠番 （Issue #1）",
            "No.8 は欠番） |",
        ):
            with self.subTest(text=text):
                self.assertEqual({("", 8)}, _mod.parse_gap_note(text))

    def test_range_with_mismatched_prefixes_allows_nothing(self):
        """レビュー 4 回目: 「No.E3〜5」の両端を単独の番号として許すと、無印の 5 の欠けまで許してしまう。"""
        self.assertEqual(set(), _mod.parse_gap_note("No.E3〜5 は欠番。"))
        self.assertEqual(set(), _mod.parse_gap_note("No.5〜3 は欠番。"))
        doc = "#### UT-001\n\n" + _table(1, 2, 3, 4, 6) + "\nNo.E3〜5 は欠番。\n"
        self.assertEqual(1, len(_no_problems(doc)))

    def test_list_with_no_prefix_on_each_element(self):
        self.assertEqual({("", 2), ("", 3)}, _mod.parse_gap_note("No.2、No.3 は欠番。"))
        self.assertEqual({("", 2), ("", 3)}, _mod.parse_gap_note("No.2・No.3 は欠番。"))
        self.assertEqual({("", 2), ("", 3), ("", 4)}, _mod.parse_gap_note("No.2〜No.4 は欠番。"))

    def test_note_covers_only_the_numbers_its_table_owns_in_the_same_section(self):
        """レビュー 3 回目: 続きとして束ねた系列でも、注記は自分の表の受け持ちの欠けにだけ効く。"""
        doc = "#### UT-001\n\n" + _table(1, 3) + "\n" + _table(4, 5) + "\n" + _table(6, 7) + "\nNo.2 は欠番。\n"
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems), problems)
        self.assertIn("No.2 が抜けています", problems[0][1])

    def test_note_covers_only_the_numbers_its_table_owns_across_headings(self):
        doc = "#### UT-001\n\n" + _table(1, 3, 4, 5) + "\n#### UT-002\n\nNo.2 は欠番。\n\n" + _table(6, 7)
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems), problems)
        self.assertIn("No.2 が抜けています", problems[0][1])

    def test_note_covers_the_gap_just_before_its_continuation_table(self):
        doc = "#### UT-001\n\n" + _table(1, 2, 3) + "\n本文\n\n" + _table(6, 7) + "\nNo.4・5 は欠番。\n"
        self.assertEqual([], _no_problems(doc))

    def test_note_inside_inline_code_is_an_example(self):
        self.assertEqual(set(), _mod.parse_gap_note("注記は `No.8・9 は欠番` の形で書く"))
        doc = "#### UT-001\n\n例: `No.2 は欠番`\n\n" + _table(1, 3)
        self.assertEqual(1, len(_no_problems(doc)))
        # コードの中が許可形で終わっていても（「。」が続く）、例として読まない
        self.assertEqual(set(), _mod.parse_gap_note("例: `No.8 は欠番。` のように書く"))
        self.assertEqual(set(), _mod.parse_gap_note("例: ``No.8 は欠番 ``"))

    def test_note_inside_a_one_line_html_comment_is_ignored(self):
        doc = "#### UT-001\n\n" + _table(1, 3) + "\n<!-- No.2 は欠番。 -->\n"
        self.assertEqual(1, len(_no_problems(doc)))


class NoColumnSeriesStartTests(unittest.TestCase):
    """レビュー 1 回目: 1 から始まらない系列は、続きの表か欠番の注記が無ければ赤。"""

    def test_table_not_starting_at_one_without_continuation_is_red(self):
        problems = _no_problems("#### UT-001\n\n" + _table(4, 5, 6))
        self.assertEqual(1, len(problems), problems)
        self.assertIn("No.1〜3 が抜けています", problems[0][1])

    def test_leading_numbers_with_a_note_are_green(self):
        self.assertEqual([], _no_problems("#### UT-001\n\nNo.1〜3 は欠番。\n\n" + _table(4, 5)))

    def test_zero_based_table_is_green(self):
        self.assertEqual([], _no_problems(_table(0, "0a", 1, 2)))

    def test_continuation_across_headings_when_numbers_connect(self):
        doc = "#### UT-001\n\n" + _table(1, 2, 3) + "\n#### UT-002\n\n" + _table(4, 5)
        self.assertEqual([], _no_problems(doc))

    def test_continuation_across_headings_skips_a_section_restarting_at_one(self):
        doc = (
            "#### UT-001\n\n" + _table(1, 2, 3)
            + "\n#### UT-001a\n\n" + _table(1, 2)
            + "\n#### UT-002\n\n" + _table(4, 5)
        )
        self.assertEqual([], _no_problems(doc))

    def test_lost_first_rows_of_a_cross_heading_continuation_are_red(self):
        doc = "#### UT-001\n\n" + _table(1, 2, 3) + "\n#### UT-002\n\n" + _table(6, 7)
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems), problems)
        self.assertIn("No.1〜5 が抜けています", problems[0][1])

    def test_distant_table_is_not_a_continuation_candidate(self):
        tables = "".join(f"\n#### UT-{i:03d}\n\n" + _table(1, 2) for i in range(5))
        doc = "#### UT-900\n\n" + _table(1, 2, 3, 4, 5, 6) + tables + "\n#### UT-901\n\n" + _table(7, 8)
        self.assertEqual(1, len(_no_problems(doc)))

    def test_lost_last_rows_are_not_detected(self):
        """既知の限界: 系列の終わりは最大の番号なので、末尾の行の消失は原理的に検出できない。"""
        self.assertEqual([], _no_problems("#### UT-001\n\n" + _table(1, 2, 3)))

    def test_coincidental_connection_is_not_detected(self):
        """既知の限界: 前の表の最大の番号が、後ろの表から消えた先頭の行の数と等しいと、続きに見えて緑になる。"""
        doc = "#### UT-001\n\n" + _table(1, 2, 3) + "\n#### UT-002\n\n" + _table(4, 5, 6, 7, 8)
        self.assertEqual([], _no_problems(doc))

    def test_lookback_boundary(self):
        """続ける先の候補は直近 3 系列まで（3 つ目は可・4 つ目は不可）。"""
        others = lambda count: "".join(f"\n#### UT-1{i:02d}\n\n" + _table(1, 2) for i in range(count))
        head = "#### UT-001\n\n" + _table(1, 2, 3)
        tail = "\n#### UT-900\n\n" + _table(4, 5)
        self.assertEqual([], _no_problems(head + others(2) + tail))
        problems = _no_problems(head + others(3) + tail)
        self.assertEqual(1, len(problems), problems)
        self.assertIn("No.1〜3 が抜けています", problems[0][1])
        self.assertIn("続ける先が見つかりませんでした", problems[0][1])  # 欠番の注記だけを促さない

    def test_gap_from_lost_rows_does_not_get_the_continuation_hint(self):
        problems = _no_problems("#### UT-001\n\n" + _table(1, 3))
        self.assertNotIn("続ける先", problems[0][1])

    def test_leading_note_does_not_hide_a_gap_in_a_coincidentally_connected_table(self):
        """レビュー 2 回目: 先頭を欠番と注記した表（UT-013 の形）は、見出しをまたいだ続きとして結び付けない。

        結び付けると、その注記（No.1〜6）が前の表 A の No.3 の消失まで許してしまう。
        """
        doc = (
            "#### UT-A\n\n" + _table(1, 2, 4, 5, 6)
            + "\n#### UT-B\n\n" + _table(1, 2)
            + "\n#### UT-C\n\n" + _table(1, 2)
            + "\n#### UT-013\n\n> No.1〜6・8 は欠番。\n\n" + _table("7a", "7b", 9)
        )
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems), problems)
        self.assertIn("3 行目の表: No.3 が抜けています", problems[0][1])

    def test_note_in_the_next_section_does_not_reach_back_to_the_previous_table(self):
        doc = "#### UT-001\n\n" + _table(1, 3) + "\n#### UT-002\n\nNo.2 は欠番。\n\n" + _table(1, 2)
        problems = _no_problems(doc)
        self.assertEqual(1, len(problems), problems)
        self.assertIn("3 行目の表: No.2 が抜けています", problems[0][1])


class NoColumnUnreadableCellTests(unittest.TestCase):
    """レビュー 3 回目: 番号として読めないセルは赤にしないが、抜けの位置にあれば案内に添える。"""

    def test_unreadable_cell_at_the_gap_is_named(self):
        for cell in ("13.", "13〜13", "15〜13"):
            with self.subTest(cell=cell):
                doc = "#### UT-001\n\n" + _table(*range(1, 13), cell, 14)
                problems = _no_problems(doc)
                self.assertEqual(1, len(problems), problems)
                self.assertIn(f"17 行目の「{cell}」 を番号として読めません", problems[0][1])
                self.assertIn("番号の書式", problems[0][1])

    def test_unreadable_cell_elsewhere_is_not_mentioned(self):
        problems = _no_problems("#### UT-001\n\n" + _table(1, "1.", 2, 4))
        self.assertEqual(1, len(problems), problems)
        self.assertNotIn("読めません", problems[0][1])

    def test_unreadable_cell_without_a_gap_is_green(self):
        self.assertEqual([], _no_problems("#### UT-001\n\n" + _table(1, 2, "2.", 3)))

    def test_range_over_the_limit_is_unreadable_and_named(self):
        problems = _no_problems("#### UT-001\n\n" + _table("1〜300", 301))
        self.assertEqual(1, len(problems), problems)
        self.assertIn("「1〜300」 を番号として読めません", problems[0][1])
        self.assertNotIn("続ける先", problems[0][1])  # 原因に合わない案内を出さない

    def test_fullwidth_digit_is_unreadable_and_named(self):
        """レビュー 4 回目: セルの数字は半角だけを読む（re.ASCII）。全角は書き損じとして案内する。"""
        self.assertEqual([], _mod.parse_no_cell("２"))
        self.assertEqual([], _mod.parse_no_cell("1〜３"))
        problems = _no_problems("#### UT-001\n\n" + _table(1, "２", 3))
        self.assertEqual(1, len(problems), problems)
        self.assertIn("「２」 を番号として読めません", problems[0][1])


class NoRangeLimitTests(unittest.TestCase):
    """範囲の展開の上限（``MAX_NO_RANGE``）。書き損じで巨大な集合を作らない。"""

    def test_range_up_to_the_limit_expands(self):
        limit = _mod.MAX_NO_RANGE
        self.assertEqual(limit + 1, len(_mod.parse_no_cell(f"1〜{1 + limit}")))

    def test_range_over_the_limit_is_not_expanded(self):
        limit = _mod.MAX_NO_RANGE
        self.assertEqual([], _mod.parse_no_cell(f"1〜{2 + limit}"))
        self.assertEqual(200, limit)  # 上限を外す・緩める変更は、ここで意図を問う


class NoColumnRealDocumentTests(unittest.TestCase):
    """実データ: 現在の 07 は緑。#2321 の是正前（No.13〜15 が一斉に消えた版）の抜粋は赤。"""

    DOC = pathlib.Path(__file__).resolve().parents[2] / "ICCardManager" / "docs" / "design" / "07_テスト設計書.md"
    # 0a6dce76^（#2321 の是正 PR #2362 の親）の 07 から 3 節を写した fixture。CI の浅いクローンでも必ず走る
    BEFORE_2321_FIX = pathlib.Path(__file__).resolve().parent / "fixtures" / "07_before_2321_fix_excerpt.md"

    def test_current_document_is_green(self):
        ok, report = _mod.check_no_columns(_mod.parse_no_columns(str(self.DOC)))
        self.assertTrue(ok, report)

    def test_excerpt_before_the_2321_fix_is_red(self):
        problems = _mod.find_no_column_problems(_mod.parse_no_columns(str(self.BEFORE_2321_FIX)))
        vanished = [text for _, text in problems if "No.13〜15 が抜けています" in text]
        self.assertEqual(3, len(vanished), problems)


if __name__ == "__main__":
    unittest.main()
