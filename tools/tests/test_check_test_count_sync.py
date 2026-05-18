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


if __name__ == "__main__":
    unittest.main()
