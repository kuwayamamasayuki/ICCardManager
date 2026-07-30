using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace ICCardManager.Common
{
    /// <summary>
    /// xlsx ファイルのワークシート名だけを読み取るユーティリティ（Issue #1691）
    /// </summary>
    /// <remarks>
    /// 帳票の「出力済み / 未出力」判定は、カード枚数（最大20枚程度）ぶんの年度ファイルを
    /// 画面を開くたびに調べる。ClosedXML の <c>XLWorkbook</c> でブックを開くとセル値・書式まで
    /// 解析されるため、シート構成を知りたいだけの用途には重すぎる。
    ///
    /// xlsx は OPC（ZIP）パッケージなので、<c>xl/workbook.xml</c> の
    /// <c>&lt;sheet name="..."/&gt;</c> だけを読めばシート構成が分かる。
    /// また <see cref="FileShare.ReadWrite"/> で開くため、対象のブックを Excel で開いたままでも
    /// 判定できる（月次作業中に前月分を開きっぱなしにする運用を想定）。
    /// </remarks>
    public static class XlsxSheetNameReader
    {
        /// <summary>
        /// xlsx パッケージ内でブック構造を保持するパート
        /// </summary>
        private const string WorkbookPartPath = "xl/workbook.xml";

        /// <summary>
        /// SpreadsheetML の名前空間
        /// </summary>
        private static readonly XNamespace SpreadsheetMlNamespace =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        /// <summary>
        /// xlsx ファイルからワークシート名の一覧を読み取る
        /// </summary>
        /// <param name="filePath">xlsx ファイルのパス</param>
        /// <param name="sheetNames">読み取れたシート名（失敗時は空）</param>
        /// <returns>読み取れた場合true。ファイルが無い/壊れている/読めない場合はfalse</returns>
        public static bool TryReadSheetNames(string filePath, out IReadOnlyList<string> sheetNames)
        {
            sheetNames = new List<string>();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                using (var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var workbookEntry = archive.GetEntry(WorkbookPartPath);
                    if (workbookEntry == null)
                    {
                        // xlsx ではない ZIP（xls を拡張子だけ変えた等）
                        return false;
                    }

                    using (var entryStream = workbookEntry.Open())
                    {
                        var document = XDocument.Load(entryStream);
                        sheetNames = document
                            .Descendants(SpreadsheetMlNamespace + "sheet")
                            .Select(element => (string)element.Attribute("name"))
                            .Where(name => !string.IsNullOrEmpty(name))
                            .ToList();
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // 破損ファイル・排他ロック・権限不足などはすべて「判定不能」として扱う。
                // 呼び出し側（ReportExportStatusService）が Unknown 状態としてユーザーに提示する。
                sheetNames = new List<string>();
                return false;
            }
        }

        /// <summary>
        /// xlsx ファイルに指定名のワークシートが含まれるかを判定する
        /// </summary>
        /// <param name="filePath">xlsx ファイルのパス</param>
        /// <param name="sheetName">探すシート名</param>
        /// <param name="contains">シートが含まれる場合true</param>
        /// <returns>判定できた場合true。ファイルを読めず判定不能な場合はfalse</returns>
        public static bool TryContainsSheet(string filePath, string sheetName, out bool contains)
        {
            contains = false;

            if (!TryReadSheetNames(filePath, out var sheetNames))
            {
                return false;
            }

            contains = sheetNames.Any(name => string.Equals(name, sheetName, StringComparison.Ordinal));
            return true;
        }
    }
}
