using ClosedXML.Excel;

namespace ICCardManager.Services
{
    /// <summary>
    /// Excel帳票の書式設定ユーティリティ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ReportService"/> から抽出された、Excelワークシートの罫線・書式・
    /// 印刷設定を行う純粋関数群を提供します。
    /// </para>
    /// <para>
    /// 主な機能:
    /// </para>
    /// <list type="bullet">
    /// <item><description>データ行の罫線・書式適用（<see cref="ApplyDataRowBorder"/>）</description></item>
    /// <item><description>月計・累計行の罫線・書式適用（<see cref="ApplySummaryRowBorder"/>）</description></item>
    /// <item><description>空白行の罫線適用（<see cref="ApplyEmptyRowBorder"/>）</description></item>
    /// <item><description>印刷設定（<see cref="ConfigurePageSetup"/>）</description></item>
    /// <item><description>印刷範囲の設定（<see cref="SetPrintArea"/>）</description></item>
    /// </list>
    /// </remarks>
    internal static class ExcelStyleFormatter
    {
        /// <summary>
        /// データ行に罫線・書式を適用します。
        /// </summary>
        /// <remarks>
        /// <para>行の高さ30、B-D列結合（摘要）、I-L列結合（備考）、</para>
        /// <para>A列中央寄せ+縮小表示、H列中央寄せ、金額列16pt、その他14pt。</para>
        /// <para>Issue #591: 既存ファイル上書き時に前回の太字書式が残る場合のリセット。</para>
        /// <para>Issue #858: 全列のフォントサイズを14ptに明示的に設定。</para>
        /// <para>Issue #947: 金額列（受入E・払出F・残額G）のフォントサイズを16ptに設定。</para>
        /// </remarks>
        /// <param name="worksheet">対象のワークシート</param>
        /// <param name="row">適用する行番号</param>
        internal static void ApplyDataRowBorder(IXLWorksheet worksheet, int row)
        {
            // 行の高さを30に設定
            worksheet.Row(row).Height = 30;

            // Issue #1480: 行全体の Range を 1 回だけ取得して再利用（重複生成を削減）
            var fullRange = worksheet.Range(row, 1, row, 12);

            // Issue #591: 既存ファイル上書き時に前回の太字書式が残る場合があるため、
            // データ行では太字を明示的にリセットする
            fullRange.Style.Font.Bold = false;

            // Issue #858: 全列のフォントサイズを14ptに明示的に設定
            // テンプレートの最初のシートを直接使う場合とAdd()で新規作成する場合で
            // デフォルトフォントサイズが異なるため、明示的に統一する
            fullRange.Style.Font.FontSize = 14;

            // Issue #947: 金額列（受入E・払出F・残額G）のフォントサイズを16ptに設定
            var amountRange = worksheet.Range(row, 5, row, 7);
            amountRange.Style.Font.FontSize = 16;

            // B列からD列を結合（摘要）
            var summaryRange = worksheet.Range(row, 2, row, 4);
            summaryRange.Merge();
            summaryRange.Style.Alignment.WrapText = true; // 折り返して全体を表示

            // I列からL列を結合（備考）
            var noteRange = worksheet.Range(row, 9, row, 12);
            noteRange.Merge();
            noteRange.Style.Alignment.WrapText = true; // 折り返して全体を表示

            // A列（出納年月日）を中央寄せ、縮小して全体を表示する
            var dateCell = worksheet.Cell(row, 1);
            dateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            dateCell.Style.Alignment.ShrinkToFit = true;

            // H列（氏名）を中央寄せ
            worksheet.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // A列からL列まで罫線を適用
            fullRange.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            fullRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            fullRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            // 両端（A列左側、L列右側）は太線で表示
            worksheet.Cell(row, 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            worksheet.Cell(row, 12).Style.Border.RightBorder = XLBorderStyleValues.Medium;

            // 行全体を上下中央揃えに設定
            fullRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        /// <summary>
        /// 月計・累計行に罫線を適用し、セルを結合します。
        /// </summary>
        /// <remarks>
        /// Issue #451対応:
        /// 月計・累計行の上下罫線を太線（Medium）にして視覚的に区切りを明確化。
        /// 会計マニュアルの「月計・累計欄の上下線は朱線又は太線を用いること」に対応。
        /// </remarks>
        /// <param name="worksheet">対象のワークシート</param>
        /// <param name="row">適用する行番号</param>
        internal static void ApplySummaryRowBorder(IXLWorksheet worksheet, int row)
        {
            // 行の高さを30に設定
            worksheet.Row(row).Height = 30;

            // Issue #1480: 行全体の Range を 1 回だけ取得して再利用（重複生成を削減）
            // Issue #858: 全列のフォントサイズを14ptに明示的に設定
            // テンプレートの最初のシートを直接使う場合とAdd()で新規作成する場合で
            // デフォルトフォントサイズが異なるため、明示的に統一する
            var fullRange = worksheet.Range(row, 1, row, 12);
            fullRange.Style.Font.FontSize = 14;

            // Issue #947: 金額列（受入E・払出F・残額G）のフォントサイズを16ptに設定
            var amountRange = worksheet.Range(row, 5, row, 7);
            amountRange.Style.Font.FontSize = 16;

            // Issue #1071: 金額列（受入E・払出F・残額G）は6桁以上になりうるため、
            // 縮小して全体を表示する
            amountRange.Style.Alignment.ShrinkToFit = true;

            // B列からD列を結合（摘要）
            var summaryRange = worksheet.Range(row, 2, row, 4);
            summaryRange.Merge();
            summaryRange.Style.Alignment.WrapText = true;

            // I列からL列を結合（備考）
            var noteRange = worksheet.Range(row, 9, row, 12);
            noteRange.Merge();
            noteRange.Style.Alignment.WrapText = true;

            // A列（出納年月日）を中央寄せ、縮小して全体を表示する
            var dateCell = worksheet.Cell(row, 1);
            dateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            dateCell.Style.Alignment.ShrinkToFit = true;

            // H列（氏名）を中央寄せ
            worksheet.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // A列からL列まで罫線を適用（内側は細線）
            fullRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            // 月計・累計行は上下を太線に
            fullRange.Style.Border.TopBorder = XLBorderStyleValues.Medium;
            fullRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;

            // 両端（A列左側、L列右側）は太線で表示
            worksheet.Cell(row, 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            worksheet.Cell(row, 12).Style.Border.RightBorder = XLBorderStyleValues.Medium;

            // 行全体を上下中央揃えに設定
            fullRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        /// <summary>
        /// 空白行に罫線を適用します。
        /// </summary>
        /// <remarks>
        /// Issue #457: 最終ページの空白行に罫線を引く際に使用。
        /// Issue #591: 既存ファイル上書き時に残った太字書式をリセット。
        /// </remarks>
        /// <param name="worksheet">対象のワークシート</param>
        /// <param name="row">適用する行番号</param>
        internal static void ApplyEmptyRowBorder(IXLWorksheet worksheet, int row)
        {
            ApplyEmptyRowBordersToRange(worksheet, row, row);
        }

        /// <summary>
        /// 連続する複数の空白行に罫線を一括適用します（性能最適化版）。
        /// </summary>
        /// <remarks>
        /// <para>Issue #1480: 連続する空白行に対して per-row ループで個別 Range を生成する代わりに、
        /// 1 つの複数行 Range に対してまとめて罫線を適用することで ClosedXML のスタイル操作回数を削減。</para>
        /// <para>視覚的結果は <see cref="ApplyEmptyRowBorder"/> を <paramref name="firstRow"/> から
        /// <paramref name="lastRow"/> までループ呼出した場合と同等。</para>
        /// <para>セル結合（B-D 列、I-L 列）は複数行をまとめると 1 つの結合セルになってしまうため、
        /// 行単位で個別に実行する。</para>
        /// <para><paramref name="firstRow"/> &gt; <paramref name="lastRow"/> の場合は何もしない。</para>
        /// </remarks>
        /// <param name="worksheet">対象のワークシート</param>
        /// <param name="firstRow">開始行番号（1 始まり、含む）</param>
        /// <param name="lastRow">終了行番号（1 始まり、含む）</param>
        internal static void ApplyEmptyRowBordersToRange(IXLWorksheet worksheet, int firstRow, int lastRow)
        {
            if (firstRow > lastRow)
            {
                return;
            }

            // 行の高さは行単位で設定する必要がある
            for (int row = firstRow; row <= lastRow; row++)
            {
                worksheet.Row(row).Height = 30;
            }

            // 複数行範囲を 1 度だけ取得し、罫線・太字リセットを一括適用
            var fullRange = worksheet.Range(firstRow, 1, lastRow, 12);

            // Issue #591: 既存ファイル上書き時に残った太字書式をリセット
            fullRange.Style.Font.Bold = false;

            // A列からL列まで罫線を適用
            // 複数行範囲では TopBorder/BottomBorder は各セルにカスケード適用され、
            // InsideBorder が内側（行間・列間）の境界に Thin を適用するため、
            // 結果として per-row 適用と同等の見た目になる
            fullRange.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            fullRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            fullRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            // 両端（A列左側、L列右側）は太線で表示（範囲一括適用）
            worksheet.Range(firstRow, 1, lastRow, 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            worksheet.Range(firstRow, 12, lastRow, 12).Style.Border.RightBorder = XLBorderStyleValues.Medium;

            // セル結合は行単位で実行（複数行をまとめると 1 つの結合セルになるため）
            for (int row = firstRow; row <= lastRow; row++)
            {
                worksheet.Range(row, 2, row, 4).Merge();   // B-D 列（摘要）
                worksheet.Range(row, 9, row, 12).Merge();  // I-L 列（備考）
            }
        }

        /// <summary>
        /// ワークシートの印刷設定（用紙サイズ・向き・マージン）を行います。
        /// </summary>
        /// <remarks>
        /// Issue #457: A4横向き、マージン0.5インチ。
        /// 印刷タイトル（SetRowsToRepeatAtTop）は使用しない（各ページにヘッダーをコピーするため不要）。
        /// </remarks>
        /// <param name="worksheet">対象のワークシート</param>
        internal static void ConfigurePageSetup(IXLWorksheet worksheet)
        {
            // 用紙サイズ: A4
            worksheet.PageSetup.PaperSize = XLPaperSize.A4Paper;

            // 印刷の向き: 横
            worksheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;

            // マージンの設定（単位: インチ）
            worksheet.PageSetup.Margins.Top = 0.5;
            worksheet.PageSetup.Margins.Bottom = 0.5;
            worksheet.PageSetup.Margins.Left = 0.5;
            worksheet.PageSetup.Margins.Right = 0.5;

            // 注: 印刷タイトル（SetRowsToRepeatAtTop）は使用しない
            // 各ページにヘッダーをコピーするため不要
        }

        /// <summary>
        /// 印刷範囲を設定します。
        /// </summary>
        /// <remarks>
        /// Issue #457: 最終ページの備考欄を含む範囲を印刷範囲として設定。
        /// 1ページ目のみの場合は22行目まで。
        /// </remarks>
        /// <param name="worksheet">対象のワークシート</param>
        /// <param name="currentRow">現在の行番号（最後に書いた行の次）</param>
        /// <param name="rowsOnCurrentPage">現在のページに書かれた行数</param>
        /// <param name="rowsPerPage">1ページあたりの最大行数</param>
        internal static void SetPrintArea(IXLWorksheet worksheet, int currentRow, int rowsOnCurrentPage, int rowsPerPage)
        {
            const int NotesRows = 6;    // 備考欄の行数
            const int RowsPerPageTotal = 22;  // 1ページの総行数

            // 最終行を計算
            int lastRow;
            if (rowsOnCurrentPage == 0)
            {
                // 改ページ直後（データがまだ書かれていない）
                // 前のページの備考欄の最終行
                lastRow = currentRow - 1;
            }
            else
            {
                // 現在のページにデータがある場合
                // 現在のページの備考欄の最終行を計算
                // データエリアの最終行 = currentRow - 1
                // 備考欄の最終行 = データエリアの最終行 + NotesRows + (rowsPerPage - rowsOnCurrentPage)
                var dataAreaEndRow = currentRow - 1;
                var remainingDataRows = rowsPerPage - rowsOnCurrentPage;
                lastRow = dataAreaEndRow + remainingDataRows + NotesRows;
            }

            // 1ページ目のみの場合は22行目まで
            if (lastRow < RowsPerPageTotal)
            {
                lastRow = RowsPerPageTotal;
            }

            // 印刷範囲を設定（A1からL列の最終行まで）
            worksheet.PageSetup.PrintAreas.Clear();
            worksheet.PageSetup.PrintAreas.Add(1, 1, lastRow, 12);
        }
    }
}
