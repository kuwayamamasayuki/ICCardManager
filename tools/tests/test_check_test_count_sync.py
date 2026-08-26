"""tools/check-test-count-sync.py の単体テスト。"""
import importlib.util
import pathlib
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


if __name__ == "__main__":
    unittest.main()
