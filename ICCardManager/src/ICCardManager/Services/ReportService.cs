using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using ClosedXML.Excel;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ICCardManager.Services
{
/// <summary>
    /// 帳票作成の結末（Issue #2042）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「作成した」「作成対象外だった」「失敗した」の 3 つは互いに排他なので 1 つの値で表す。
    /// 旧実装は <c>Success</c> と <c>Skipped</c> の 2 つの bool で持っており、スキップが
    /// <c>Success = true, Skipped = true</c> という<b>両方が立った状態</b>になっていた。
    /// <c>Success</c> だけを見る呼び出し側（<c>ReportViewModel</c> の一括作成ループ）は
    /// これを「作成した」と数え、<b>存在しないファイルのパス</b>を作成ファイル一覧へ並べていた。
    /// </para>
    /// <para>
    /// 既定値（0）を <see cref="Failed"/> にしているのは、結末を設定し忘れた結果が
    /// 「成功」に見えないようにするため。
    /// </para>
    /// </remarks>
    public enum ReportGenerationOutcome
    {
        /// <summary>失敗した</summary>
        Failed = 0,

        /// <summary>帳票ファイルを作成した</summary>
        Created = 1,

        /// <summary>作成対象外のため作成しなかった（新規購入より前の月など）</summary>
        Skipped = 2
    }

    /// <summary>
    /// 帳票作成結果
    /// </summary>
    public class ReportGenerationResult
    {
        /// <summary>
        /// 結末（作成した／対象外／失敗した）
        /// </summary>
        public ReportGenerationOutcome Outcome { get; private set; }

        /// <summary>
        /// エラーではない（作成した、または作成対象外だった）
        /// </summary>
        /// <remarks>
        /// <b>「作成した件数」を数える用途に使ってはならない</b>（Issue #2042）。作成対象外の月は
        /// エラーではないためここも true になる。ファイルを作ったかどうかは <see cref="Created"/> で判定する。
        /// </remarks>
        public bool Success => Outcome != ReportGenerationOutcome.Failed;

        /// <summary>
        /// 作成対象外だった（新規購入より前の月など）
        /// </summary>
        public bool Skipped => Outcome == ReportGenerationOutcome.Skipped;

        /// <summary>
        /// 帳票ファイルを実際に作成した（Issue #2042）
        /// </summary>
        public bool Created => Outcome == ReportGenerationOutcome.Created;

        /// <summary>
        /// 全カードに共通する原因による失敗（Issue #2042）
        /// </summary>
        /// <remarks>
        /// テンプレートが見つからない・組織設定のヘッダー列番号が範囲外、のように
        /// <b>対象カードに依らず必ず同じ結果になる</b>失敗。一括作成はこれを受け取ったら中断する
        /// （続けても同じ失敗が選択枚数だけ並ぶだけで、職員には何も分からない）。
        /// 判定を文言の部分一致（<c>ErrorMessage.Contains("テンプレート")</c>）で代用しない —
        /// 文言に「テンプレート」を含まない共通エラーは中断されず、カード固有の失敗文言に
        /// たまたま「テンプレート」が含まれると一括作成全体が止まる。
        /// </remarks>
        public bool IsCommonFailure { get; private set; }

        /// <summary>
        /// エラーメッセージ（失敗時）／作成対象外とした理由（スキップ時）
        /// </summary>
        public string ErrorMessage { get; private set; }

        /// <summary>
        /// 詳細エラーメッセージ（失敗時）
        /// </summary>
        public string DetailedErrorMessage { get; private set; }

        /// <summary>
        /// 出力ファイルパス（作成時）
        /// </summary>
        public string OutputPath { get; private set; }

        /// <summary>
        /// 作成結果を作成
        /// </summary>
        public static ReportGenerationResult SuccessResult(string outputPath) => new()
        {
            Outcome = ReportGenerationOutcome.Created,
            OutputPath = outputPath
        };

        /// <summary>
        /// 失敗結果を作成（そのカードに固有の失敗）
        /// </summary>
        public static ReportGenerationResult FailureResult(string message, string detailedMessage = null) => new()
        {
            Outcome = ReportGenerationOutcome.Failed,
            ErrorMessage = message,
            DetailedErrorMessage = detailedMessage
        };

        /// <summary>
        /// 全カードに共通する原因による失敗結果を作成（Issue #2042）
        /// </summary>
        /// <remarks>
        /// 一括作成を中断させる。<see cref="IsCommonFailure"/> を参照。
        /// </remarks>
        public static ReportGenerationResult CommonFailureResult(string message, string detailedMessage = null) => new()
        {
            Outcome = ReportGenerationOutcome.Failed,
            IsCommonFailure = true,
            ErrorMessage = message,
            DetailedErrorMessage = detailedMessage
        };

        /// <summary>
        /// スキップ結果を作成（新規購入より前の月など）
        /// </summary>
        public static ReportGenerationResult SkippedResult(string reason) => new()
        {
            Outcome = ReportGenerationOutcome.Skipped,
            ErrorMessage = reason
        };
    }

    /// <summary>
    /// 一括帳票作成結果
    /// </summary>
    public class BatchReportGenerationResult
    {
        /// <summary>
        /// 個別の作成結果
        /// </summary>
        public IReadOnlyList<(string CardIdm, string CardName, ReportGenerationResult Result)> Results { get; }

        /// <summary>
        /// テンプレートエラーメッセージ（テンプレートが見つからない場合）
        /// </summary>
        public string TemplateErrorMessage { get; set; }

        /// <summary>
        /// ディレクトリエラーメッセージ（出力先フォルダの作成に失敗した場合）
        /// </summary>
        public string DirectoryErrorMessage { get; set; }

        /// <summary>
        /// テンプレートが見つからなかった
        /// </summary>
        public bool IsTemplateError => TemplateErrorMessage != null;

        /// <summary>
        /// ディレクトリ作成エラーがあった
        /// </summary>
        public bool IsDirectoryError => DirectoryErrorMessage != null;

        /// <summary>
        /// 成功した件数（スキップを除く）
        /// </summary>
        public int SuccessCount => Results.Count(r => r.Result.Created);

        /// <summary>
        /// 失敗した件数
        /// </summary>
        public int FailureCount => Results.Count(r => !r.Result.Success);

        /// <summary>
        /// スキップした件数（新規購入より前の月など）
        /// </summary>
        public int SkippedCount => Results.Count(r => r.Result.Skipped);

        /// <summary>
        /// 全件成功したか
        /// </summary>
        public bool AllSuccess => !IsTemplateError && !IsDirectoryError && Results.All(r => r.Result.Success);

        /// <summary>
        /// 全カード共通の原因による失敗で中断したか（Issue #2042）
        /// </summary>
        public bool IsAborted => Results.Any(r => r.Result.IsCommonFailure);

        /// <summary>
        /// 中断の原因（Issue #2042。中断していなければ <c>null</c>）
        /// </summary>
        /// <remarks>
        /// ステータス欄のように幅の限られた表示へ出す簡潔な見出し。「なぜ／どうすれば」は
        /// <see cref="AbortDetail"/> をダイアログで示す（`.claude/rules/error-messages.md` #1688）。
        /// </remarks>
        public string AbortReason => AbortedResult?.ErrorMessage;

        /// <summary>
        /// 中断の原因の詳細（Issue #2042。中断していなければ <c>null</c>）
        /// </summary>
        public string AbortDetail => AbortedResult is ReportGenerationResult aborted
            ? aborted.DetailedErrorMessage ?? aborted.ErrorMessage
            : null;

        /// <summary>
        /// 中断の原因となった結果（Issue #2042。中断していなければ <c>null</c>）
        /// </summary>
        private ReportGenerationResult AbortedResult => Results
            .Where(r => r.Result.IsCommonFailure)
            .Select(r => r.Result)
            .FirstOrDefault();

        /// <summary>
        /// 作成したファイルパスの一覧
        /// </summary>
        /// <remarks>
        /// Issue #2042: 判定は <see cref="ReportGenerationResult.Created"/> ただ 1 つに寄せる。
        /// スキップは <c>OutputPath</c> を持たないため旧来の <c>Success</c> 判定でも結果は同じだったが、
        /// 「作成した」の数え方が 2 通りあると、片方だけが直される日が来る。
        /// </remarks>
        public IReadOnlyList<string> SuccessfulFiles => Results
            .Where(r => r.Result.Created && r.Result.OutputPath != null)
            .Select(r => r.Result.OutputPath!)
            .ToList()
            .AsReadOnly();

        public BatchReportGenerationResult(IEnumerable<(string CardIdm, string CardName, ReportGenerationResult Result)> results)
        {
            Results = results.ToList().AsReadOnly();
        }

        private BatchReportGenerationResult()
        {
            Results = Array.Empty<(string, string, ReportGenerationResult)>();
        }

        /// <summary>
        /// テンプレートが見つからない場合の結果を作成
        /// </summary>
        public static BatchReportGenerationResult TemplateNotFound(string detailedMessage) => new()
        {
            TemplateErrorMessage = detailedMessage
        };

        /// <summary>
        /// 出力先フォルダの作成に失敗した場合の結果を作成
        /// </summary>
        public static BatchReportGenerationResult DirectoryCreationFailed(string detailedMessage) => new()
        {
            DirectoryErrorMessage = detailedMessage
        };

        /// <summary>
        /// 結果サマリーを取得
        /// </summary>
        public string GetSummary()
        {
            if (IsTemplateError)
            {
                return $"テンプレートエラー: {TemplateErrorMessage}";
            }

            if (IsDirectoryError)
            {
                return $"フォルダエラー: {DirectoryErrorMessage}";
            }

            if (IsAborted)
            {
                // Issue #2042: 全カード共通の原因なので、残りを試しても同じ失敗が並ぶだけ
                return $"帳票作成を中断しました: {AbortReason}";
            }

            if (AllSuccess)
            {
                return SkippedCount > 0
                    ? $"{SuccessCount}件の帳票を作成しました。（対象期間外 {SkippedCount}件）"
                    : $"{SuccessCount}件の帳票を作成しました。";
            }

            return $"{SuccessCount}件成功、{FailureCount}件失敗しました。";
        }
    }

    /// <summary>
    /// 月次帳票作成サービス
    /// </summary>
    public class ReportService
    {
        private readonly ICardRepository _cardRepository;
        private readonly ILedgerRepository _ledgerRepository;
        private readonly ISettingsRepository _settingsRepository;
        private readonly IReportDataBuilder _reportDataBuilder;
        private readonly OrganizationOptions _orgOptions;
        private readonly IReportFileNameFactory _fileNameFactory;
        private readonly ILogger<ReportService> _logger;

        /// <summary>
        /// 書き込み途中の年度ファイルに付ける一時ファイルの拡張子（Issue #2040）
        /// </summary>
        /// <remarks>
        /// 年度ファイル（<c>*.xlsx</c>）と取り違えられないことが要件。<see cref="ReportExportStatusService"/> は
        /// 最終名のファイルを開いて「出力済み」を判定するため、書き込み途中の内容に最終名を与えると
        /// 壊れたファイルが出力済みとして表示される。
        /// </remarks>
        internal const string ReportTempFileExtension = ".tmp";

        /// <summary>
        /// 一時ファイル名に含めるランダム識別子の文字数（Issue #2040）
        /// </summary>
        /// <remarks>
        /// 共有フォルダーで複数台が同じ年度ファイルを同時に作成しても一時ファイルが衝突しない長さ。
        /// GUID 全体（32 文字）にしないのはパス長上限（260 文字）へ早く到達するため（#1748 と同じ判断）。
        /// </remarks>
        internal const int TempFileTokenLength = 8;

        public ReportService(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository,
            ISettingsRepository settingsRepository,
            IReportDataBuilder reportDataBuilder,
            ILogger<ReportService> logger,
            IOptions<OrganizationOptions> orgOptions = null,
            IReportFileNameFactory fileNameFactory = null)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
            _settingsRepository = settingsRepository;
            _reportDataBuilder = reportDataBuilder;
            // Issue #2040: 一時ファイルの回収・残置の痕跡を残すため必須引数で受ける（#1820 / #1956）
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _orgOptions = orgOptions?.Value ?? new OrganizationOptions();
            // Issue #1820: 同じ組織設定から導出する。DI 未指定でも本サービスの _orgOptions と
            // 同じ設定値を使うため、書式が経路によって食い違うことはない。
            _fileNameFactory = fileNameFactory ?? new ReportFileNameFactory(orgOptions);
        }

        /// <summary>
        /// 月次帳票を作成（年度ファイルの該当月シートを作成/更新）
        /// </summary>
        /// <param name="cardIdm">対象カードIDm</param>
        /// <param name="year">年</param>
        /// <param name="month">月</param>
        /// <param name="outputPath">出力先パス（年度ファイル）</param>
        /// <returns>作成結果（成功/失敗とエラーメッセージ）</returns>
        /// <remarks>
        /// Issue #477対応: 年度ごとに1つのExcelファイルを作成し、月ごとにワークシートを分ける。
        /// 上書き時は当該月のワークシートのみ修正する。
        /// </remarks>
        /// <remarks>
        /// Issue #1949: 一括作成ループ（<c>ReportViewModel.CreateReportAsync</c>）の回帰テストで
        /// 差し替えるための継ぎ目として virtual にしている。実ファイル生成（テンプレート解決・Excel 出力）
        /// を伴うため、ここを差し替えられないとループそのものを 1 件も検査できない。
        /// </remarks>
        public virtual async Task<ReportGenerationResult> CreateMonthlyReportAsync(string cardIdm, int year, int month, string outputPath)
        {
            string templatePath = null;

            try
            {
                // Issue #1956: ヘッダー列の設定が帳票幅（A〜L）に収まっているか先に確かめる。
                // 収まっていない列は書き込めても印刷されないため、帳票を作る前に弾く。
                var headerColumnError = ValidateHeaderColumns();
                if (headerColumnError != null)
                {
                    // Issue #2042: 組織設定は全カードに共通なので、一括作成では中断させる
                    return ReportGenerationResult.CommonFailureResult(
                        "組織設定のヘッダー列番号が正しくありません",
                        headerColumnError);
                }

                // テンプレートパスを解決
                try
                {
                    // Issue #1281: 非同期版を使い UI スレッドブロックを回避
                    var settings = await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false);
                    templatePath = TemplateResolver.ResolveTemplatePath(settings.DepartmentType);
                }
                catch (TemplateNotFoundException ex)
                {
                    // Issue #2042: テンプレートは全カードで共有するので、一括作成では中断させる
                    return ReportGenerationResult.CommonFailureResult(
                        "テンプレートファイルが見つかりません",
                        ex.GetDetailedMessage());
                }

                // Issue #501: 導入（カード登録時の新規購入・繰越）より前の月はスキップ
                // Issue #2046: 導入行の判定は Ledger.IsInitialRecordSummary（3 種）。「前年度より繰越」も含む
                var purchaseDate = await _ledgerRepository.GetPurchaseDateAsync(cardIdm).ConfigureAwait(false);
                if (purchaseDate.HasValue)
                {
                    var requestedMonth = new DateTime(year, month, 1);
                    var purchaseMonth = new DateTime(purchaseDate.Value.Year, purchaseDate.Value.Month, 1);
                    if (requestedMonth < purchaseMonth)
                    {
                        return ReportGenerationResult.SkippedResult(
                            $"カードの導入（新規購入・繰越の登録、{purchaseDate.Value.ToString("yyyy/MM", CultureInfo.InvariantCulture)}）より前の月です");
                    }
                }

                // Issue #841: データ準備を共通化されたReportDataBuilderに委譲
                var data = await _reportDataBuilder.BuildAsync(cardIdm, year, month).ConfigureAwait(false);
                if (data == null)
                {
                    return ReportGenerationResult.FailureResult(
                        "カード情報が見つかりません",
                        $"指定されたカード（IDm: {cardIdm}）は登録されていません。");
                }

                var card = data.Card;

                // Issue #2040: 過去に中断した保存の一時ファイルを回収する（例外は投げない）
                CleanupStaleTempFiles(outputPath);

                // Issue #477: 既存ファイルがあれば開く、なければテンプレートから新規作成
                XLWorkbook workbook;
                bool isExistingFile = File.Exists(outputPath);

                if (isExistingFile)
                {
                    try
                    {
                        workbook = OpenExistingWorkbook(outputPath);
                    }
                    catch (Exception ex) when (IsCorruptedPackageException(ex))
                    {
                        // Issue #2040: 修正前の版は年度ファイルへ直接上書き保存していたため、保存の中断で
                        // zip が途中で切れたファイルが最終名のまま残り得る。汎用の「帳票の作成に失敗しました」では
                        // 原因（ファイルが壊れている）も回復手段も分からず、作成が失敗し続ける。
                        // 破損と判定するのは zip／パッケージ／XML の読み取り失敗だけに限る。それ以外
                        // （ロック・権限・ClosedXML が対応しない要素を含む正常なファイル等）を「壊れている」と案内すると、
                        // 正常なファイルを退避させて全月を作り直させる誘導になる。
                        // 黙って作り直さないのは、壊れたファイルでも他の月の内容を取り出せる可能性があるため
                        // （ReportExportStatusService が壊れたファイルを「不明」と表示し上書きしないのと同じ判断）。
                        _logger.LogWarning(ex, "既存の帳票ファイルを開けませんでした: {Path}", outputPath);
                        return ReportGenerationResult.FailureResult(
                            "既存の帳票ファイルを開けません",
                            BuildUnreadableExistingFileMessage(Path.GetFileName(outputPath)));
                    }
                }
                else
                {
                    workbook = new XLWorkbook(templatePath);
                }

                string tempPath;
                using (workbook)
                {
                    // シート名を決定（月名）
                    var sheetName = GetMonthSheetName(month);

                    // Issue #477: 該当月のシートを取得または作成
                    IXLWorksheet worksheet;

                    if (workbook.Worksheets.TryGetWorksheet(sheetName, out worksheet))
                    {
                        // 既存シートがある場合はデータ部分をクリア
                        ClearWorksheetData(worksheet);

                        // Issue #531: 既存シートにもテンプレートのページ設定を適用
                        using var templateWorkbook = new XLWorkbook(templatePath);
                        var templateSheet = templateWorkbook.Worksheets.First();
                        CopyPageSetup(templateSheet, worksheet);

                        // Issue #637: 備考欄（17-22行目）をテンプレートから復元する
                        // ClearWorksheetDataで5行目以降がクリアされるため、備考欄のセルデータも消える
                        CopyNotesSection(templateSheet, worksheet);
                    }
                    else if (isExistingFile)
                    {
                        // 既存ファイルに新しいシートを追加（テンプレートからコピー）
                        using var templateWorkbook = new XLWorkbook(templatePath);
                        var templateSheet = templateWorkbook.Worksheets.First();

                        // テンプレートシートをコピーして追加
                        worksheet = workbook.Worksheets.Add(sheetName);
                        CopyWorksheetFormat(templateSheet, worksheet);
                    }
                    else
                    {
                        // 新規ファイルの場合、テンプレートの最初のシートをリネーム
                        worksheet = workbook.Worksheets.First();
                        worksheet.Name = sheetName;
                    }

                    // シートを月順に並び替え
                    ReorderWorksheetsByMonth(workbook);

                    // Issue #809: 前月シートの最終ページ番号を考慮してページ番号を決定
                    var currentPageNumber = GetStartingPageNumberForMonth(
                        workbook, card, month, _orgOptions.TemplateMapping.PageNumberColumn);

                    // ヘッダ情報を設定（Issue #510: ページ番号も設定）
                    SetHeaderInfo(worksheet, card, currentPageNumber);

                    // Issue #457: ページ設定（印刷時に1-4行目をヘッダーとして各ページに繰り返す）
                    ConfigurePageSetup(worksheet);

                    // Issue #858: 表示倍率を100%に統一
                    // テンプレートの最初のシートを直接使う場合、テンプレートの表示倍率が引き継がれるため
                    worksheet.SheetView.ZoomScale = 100;

                    // Issue #457: データ出力（5～16行に内容を記載、それを超える場合は改ページ）
                    // Issue #1820: この 2 つは組織設定ではなくローカル const のままにしている。
                    // 明細行のレイアウトは行番号・列番号だけでは決まらず、結合セル（摘要 B～D 列・
                    // 備考 I～L 列）・罫線・印刷範囲（ExcelStyleFormatter）・改ページ時のヘッダー／
                    // 備考欄コピー（1-4行 / 17-22行）と一体でテンプレートファイルに埋め込まれている。
                    // 行数だけを設定可能にすると「行位置は動くが結合・罫線は元のまま」という
                    // 半端な状態になるため、TemplateMappingOptions からは同名の項目を削除した。
                    const int DataStartRow = 5;      // データ開始行
                    const int RowsPerPage = 12;      // 1ページあたりの最大データ行数（5～16行目）
                    var currentRow = DataStartRow;
                    var rowsOnCurrentPage = 0;

                    // Issue #1023: MonthlyReportData → 行データの変換を ReportRowBuilder に委譲
                    var rowSet = ReportRowBuilder.Build(data);

                    // 繰越行 + 各履歴行を出力
                    foreach (var row in rowSet.DataRows)
                    {
                        (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber, _orgOptions.TemplateMapping.PageNumberColumn);
                        currentRow = WriteReportRow(worksheet, currentRow, row);
                        rowsOnCurrentPage++;
                    }

                    // 月計行
                    (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber, _orgOptions.TemplateMapping.PageNumberColumn);
                    currentRow = WriteMonthlyTotalRow(worksheet, currentRow,
                        rowSet.MonthlyTotal);
                    rowsOnCurrentPage++;

                    // 累計行
                    if (rowSet.CumulativeTotal != null)
                    {
                        (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber, _orgOptions.TemplateMapping.PageNumberColumn);
                        currentRow = WriteCumulativeRow(worksheet, currentRow,
                            rowSet.CumulativeTotal);
                        rowsOnCurrentPage++;
                    }

                    // 3月の場合は次年度繰越を追加
                    if (rowSet.CarryoverToNextYear.HasValue)
                    {
                        (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber, _orgOptions.TemplateMapping.PageNumberColumn);
                        WriteCarryoverToNextYearRow(worksheet, currentRow, rowSet.CarryoverToNextYear.Value);
                        currentRow++;
                        rowsOnCurrentPage++;
                    }

                    // Issue #457: 最終ページの空白行に罫線を引く
                    FillEmptyRowsWithBorders(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);

                    // Issue #457: 印刷範囲を設定（全データを含む）
                    SetPrintArea(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);

                    // Issue #752: ドキュメントプロパティの日時を更新
                    // ClosedXMLはSaveAs時にCreated/Modifiedを自動更新しないため、明示的に設定する
                    var now = DateTime.Now;
                    if (!isExistingFile)
                    {
                        workbook.Properties.Created = now;
                    }
                    workbook.Properties.Modified = now;

                    // Issue #2040: 最終名へ直接保存しない。一時ファイルへ書き切ってから差し替える
                    tempPath = WriteWorkbookToTempFile(workbook, outputPath);
                }

                // ワークブックを閉じてから差し替える（読み込み元のハンドルが残っていると置換できない環境があるため）
                CommitTempFile(tempPath, outputPath);

                return ReportGenerationResult.SuccessResult(outputPath);
            }
            catch (ReportContentRetainedException ex)
            {
                // Issue #2040: 差し替えに失敗し年度ファイルが無い状態。作成した内容（これまでの月を含む）を
                // 別名で残したので、その名前を示す。IOException の派生なので、この分岐は IOException より前に置く。
                // 痕跡は CommitTempFile が _logger へ記録済み。
                return ReportGenerationResult.FailureResult(
                    "ファイルの保存に失敗しました",
                    BuildRetainedContentMessage(Path.GetFileName(outputPath), Path.GetFileName(ex.RetainedPath)));
            }
            catch (UnauthorizedAccessException ex)
            {
                // #1614: 生の ex.Message は職員へ出さず、技術的詳細はログへ残す（#1817「UI 文言とログを対で数える」）。
                // これらの分岐は ILogger 注入（#2040）より前から ErrorDialogHelper.LogException（エラーログ）へ記録しており、
                // 障害調査の起点を変えないためそのまま残している。
                // 例外種別が確定している分岐では ToReason を「詳細:」として併記しない。
                // ToReason は種別ごとの定型文なので、直前の文と同じことを別の言い方で繰り返すだけになり
                // （DirectoryNotFoundException は IOException 分岐に落ちるため「読み書き中に問題」と
                // headline の「フォルダが見つかりません」が食い違う）、「詳細」という語が空手形になる。
                // 実際の詳細（パス・原文）はログにあるので、そこへ誘導する。
                // Issue #2042: これはカード固有の失敗として扱う（共通失敗にしない）。フォルダーの権限だけでなく、
                // 特定の年度ファイルの読み取り専用属性・そのファイルだけに付いた ACL でも起き得るため、
                // 「残りのカードも必ず同じ結果になる」とは言えない。1 枚だけ失敗する形が実在する。
                ErrorDialogHelper.LogException(ex, "帳票ファイルの保存");
                return ReportGenerationResult.FailureResult(
                    "ファイルの保存に失敗しました",
                    "出力先フォルダへのアクセス権限がありません。別のフォルダを指定するか、管理者に連絡してください。\n\n詳細はログファイルを確認してください。");
            }
            catch (DirectoryNotFoundException ex)
            {
                // Issue #2042: 出力先フォルダは一括作成の全カードで共有するため、見つからなければ
                // 残りのカードも必ず同じ結果になる（一括作成は出力先を先に作らない）。中断させる。
                ErrorDialogHelper.LogException(ex, "帳票ファイルの保存");
                return ReportGenerationResult.CommonFailureResult(
                    "ファイルの保存に失敗しました",
                    "出力先フォルダが見つかりません。フォルダのパスを確認してください。\n\n詳細はログファイルを確認してください。");
            }
            catch (IOException ex)
            {
                ErrorDialogHelper.LogException(ex, "帳票ファイルの保存");
                return ReportGenerationResult.FailureResult(
                    "ファイルの保存に失敗しました",
                    "出力先ファイルに書き込めません。ファイルが他のアプリケーションで開かれている可能性があります。\n\n詳細はログファイルを確認してください。");
            }
            catch (Exception ex)
            {
                // 種別が不明な分岐では ToReason が「なぜ」を名指しする（SQLite の Busy/Locked 等）ので併記する。
                ErrorDialogHelper.LogException(ex, "帳票の作成");
                return ReportGenerationResult.FailureResult(
                    "帳票の作成に失敗しました",
                    $"{ExceptionMessageFormatter.ToReason(ex)}\n\n詳細はログファイルを確認してください。");
            }
        }

        /// <summary>
        /// 既存の年度ファイルが壊れていて開けないときの案内文言を組み立てる（Issue #2040）
        /// </summary>
        /// <param name="fileName">年度ファイルのファイル名（フォルダーを含まない）</param>
        internal static string BuildUnreadableExistingFileMessage(string fileName)
        {
            return $"出力先の年度ファイル「{fileName}」が壊れているため開けません（以前の保存が途中で中断された可能性があります）。" +
                "このファイルを別のフォルダーへ移動するか名前を変えてから、もう一度作成してください。" +
                "移動したファイルに含まれていた他の月の帳票も、あらためて作成してください。\n\n詳細はログファイルを確認してください。";
        }

        /// <summary>
        /// 年度ファイルの一時ファイルパスを組み立てる（Issue #2040）
        /// </summary>
        /// <remarks>
        /// <c>{年度ファイル名}.{8 桁の識別子}.tmp</c>。回収処理が「この年度ファイルの一時ファイル」だけを
        /// 選べるよう、最終名を接頭辞として含める。
        /// </remarks>
        internal static string BuildTempFilePath(string outputPath, string token)
        {
            return $"{outputPath}.{token}{ReportTempFileExtension}";
        }

        /// <summary>
        /// ファイル名が指定した年度ファイルの一時ファイルか（Issue #2040）
        /// </summary>
        /// <remarks>
        /// 出力先フォルダーは職員が選ぶ任意のフォルダー（ドキュメント等）であり、他のアプリケーションの
        /// <c>.tmp</c> も置かれ得る。Win32 のワイルドカード照合は 8.3 短縮名にも一致するため、
        /// 列挙結果をこの厳密な形で絞ってから削除する。
        /// </remarks>
        internal static bool IsTempFileNameOf(string candidateFileName, string outputFileName)
        {
            if (string.IsNullOrEmpty(candidateFileName) || string.IsNullOrEmpty(outputFileName))
            {
                return false;
            }

            var pattern = "^" + Regex.Escape(outputFileName) +
                @"\.[0-9a-f]{" + TempFileTokenLength.ToString(CultureInfo.InvariantCulture) + "}" +
                Regex.Escape(ReportTempFileExtension) + "$";
            return Regex.IsMatch(candidateFileName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// ワークブックを一時ファイルへ書き切り、そのパスを返す（Issue #2040）
        /// </summary>
        /// <remarks>
        /// 書き込みに失敗したら一時ファイルを削除して例外を再スローする。最終名のファイルには一切触れないため、
        /// 既存の年度ファイル（それまでの月のシート）は保存前の状態のまま残る。
        /// </remarks>
        private string WriteWorkbookToTempFile(XLWorkbook workbook, string outputPath)
        {
            var token = Guid.NewGuid().ToString("N").Substring(0, TempFileTokenLength);
            var tempPath = BuildTempFilePath(outputPath, token);

            try
            {
                WriteWorkbookTo(workbook, tempPath);
            }
            catch
            {
                // 後始末の失敗で本来の失敗要因を置き換えない（db-write-conventions.md「catch の中の後始末」）
                TryDeleteFile(tempPath, "書き込みを中断した帳票の一時ファイル");
                throw;
            }

            return tempPath;
        }

        /// <summary>
        /// ワークブックを指定パスへ書き出す（Issue #2040）
        /// </summary>
        /// <remarks>
        /// 保存途中の中断（共有フォルダーの切断・ディスク満杯）は実機でしか起きないため、
        /// 単体テストが差し替えて再現できるよう <c>internal virtual</c> にしている。
        /// 拡張子で形式を決める <c>SaveAs(string)</c> ではなくストリームへ保存するのは、一時ファイルの拡張子が
        /// <c>.xlsx</c> ではないため。<c>FileMode.CreateNew</c> は識別子が衝突したときに他 PC のファイルを
        /// 上書きしないため。
        /// </remarks>
        internal virtual void WriteWorkbookTo(XLWorkbook workbook, string path)
        {
            // ClosedXML（Open XML SDK）はパッケージを組み立てる際に読み戻すため ReadWrite で開く
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            workbook.SaveAs(stream);
            stream.Flush(flushToDisk: true);
        }

        /// <summary>
        /// 書き切った一時ファイルを最終名へ差し替える（Issue #2040）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 既存ファイルがあるときは削除＋移動ではなく <see cref="File.Replace(string, string, string, bool)"/> を使う。
        /// バックアップ（#1748）と違い、年度ファイルは<b>それまでの月のシートを積み上げた状態そのもの</b>であり、
        /// 削除と移動の間で失敗すると次回はテンプレートから作り直されて過去の月が消える。
        /// <c>File.Replace</c> は失敗しても置換先を元の名前のまま残す。
        /// </para>
        /// <para>
        /// 差し替えに失敗したとき、最終名のファイルが残っていれば一時ファイルは消す。中身は作り直せる出力に
        /// すぎず、Excel で年度ファイルを開いたまま作成する（＝置換がロックで失敗する）のは日常的な操作なので、
        /// 残すと出力先フォルダーに一時ファイルが溜まる。
        /// </para>
        /// <para>
        /// 最終名が無いとき（新規ファイルの移動に失敗した／<c>File.Replace</c> が ERROR_UNABLE_TO_MOVE_REPLACEMENT(_2)
        /// で置換先を別名へ退避したまま失敗した）は、一時ファイルが既存の月を含む<b>唯一の完全な内容</b>である。
        /// 一時ファイルのまま残すだけでは守れない — 次回の作成は「年度ファイルが無い」と判断してテンプレートから
        /// 作り直し（過去の月が消えた版が成功として保存される）、24 時間後には回収処理が一時ファイルを消す。
        /// そこで ①まず最終名へ戻す（戻せれば保存は成立している） ②戻せなければ回収対象にならない復旧用の名前
        /// （<see cref="BuildRecoveryFilePath"/>）へ移し、その名前を利用者へ示す。
        /// </para>
        /// </remarks>
        private void CommitTempFile(string tempPath, string outputPath)
        {
            try
            {
                ReplaceTempFile(tempPath, outputPath);
                return;
            }
            catch (Exception ex)
            {
                // 置換非対応の共有先などを切り分けられるよう Win32 のエラーコードを残す
                _logger.LogWarning(ex,
                    "帳票の年度ファイルへの差し替えに失敗しました: {TempPath} → {OutputPath}, HResult=0x{HResult}",
                    tempPath,
                    outputPath,
                    ex.HResult.ToString("X8", CultureInfo.InvariantCulture));

                if (OutputFileExists(outputPath))
                {
                    TryDeleteFile(tempPath, "差し替えられなかった帳票の一時ファイル");
                    throw;
                }

                try
                {
                    MoveTempFile(tempPath, outputPath);
                    _logger.LogWarning(
                        "差し替えに失敗した年度ファイルを、作成した内容で復元しました: {OutputPath}",
                        outputPath);
                    return;
                }
                catch (Exception restoreEx)
                {
                    _logger.LogWarning(restoreEx, "年度ファイルの復元にも失敗しました: {OutputPath}", outputPath);
                }

                var retainedPath = RetainAsRecoveryFile(tempPath, outputPath);
                throw new ReportContentRetainedException(retainedPath, ex);
            }
        }

        /// <summary>
        /// 一時ファイルで最終名のファイルを置き換える（無ければ移動する）
        /// </summary>
        /// <remarks>
        /// <c>File.Replace</c> の失敗モード（置換先を消したまま失敗する等）は実機でしか起きないため、
        /// 単体テストが差し替えて再現できるよう <c>internal virtual</c> にしている。
        /// </remarks>
        internal virtual void ReplaceTempFile(string tempPath, string outputPath)
        {
            if (File.Exists(outputPath))
            {
                File.Replace(tempPath, outputPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                MoveTempFile(tempPath, outputPath);
            }
        }

        /// <summary>
        /// 一時ファイルを最終名へ移動する（テストの継ぎ目。<see cref="ReplaceTempFile"/> を参照）
        /// </summary>
        internal virtual void MoveTempFile(string tempPath, string outputPath)
        {
            File.Move(tempPath, outputPath);
        }

        /// <summary>
        /// 既存の年度ファイルを開く（テストの継ぎ目。ClosedXML が読めない正常なファイルを再現するため）
        /// </summary>
        internal virtual XLWorkbook OpenExistingWorkbook(string path)
        {
            return new XLWorkbook(path);
        }

        /// <summary>
        /// 例外が「ファイルが壊れている」ことを示すか（Issue #2040）
        /// </summary>
        /// <remarks>
        /// zip の破損（<see cref="InvalidDataException"/>）、パッケージ構造の破損（<c>FileFormatException</c> /
        /// <c>OpenXmlPackageException</c>）、XML の破損（<see cref="System.Xml.XmlException"/>）に限る。
        /// 前 2 者は参照アセンブリの差異を避けるため型名で照合する。ラップされた例外も内側まで辿る。
        /// </remarks>
        internal static bool IsCorruptedPackageException(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is InvalidDataException || current is System.Xml.XmlException)
                {
                    return true;
                }

                var typeName = current.GetType().FullName;
                if (typeName == "System.IO.FileFormatException" ||
                    typeName == "DocumentFormat.OpenXml.Packaging.OpenXmlPackageException")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 差し替えに失敗し年度ファイルが無いときに、作成した内容を残す復旧用ファイルのパス（Issue #2040）
        /// </summary>
        /// <remarks>
        /// <c>{年度ファイル名（拡張子なし）}_復旧_{8 桁}.xlsx</c>。回収処理（<c>.tmp</c> で終わる名前）の対象外で、
        /// 出力済み判定（最終名の完全一致）にも現れない。拡張子を <c>.xlsx</c> にして、利用者が Excel で開いて
        /// 中身を確かめられるようにする。
        /// </remarks>
        internal static string BuildRecoveryFilePath(string outputPath, string token)
        {
            var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(outputPath);
            var extension = Path.GetExtension(outputPath);
            return Path.Combine(directory, $"{name}_復旧_{token}{extension}");
        }

        /// <summary>
        /// 一時ファイルを復旧用の名前へ移し、残したファイルのパスを返す（移せなければ一時ファイルのパス）
        /// </summary>
        private string RetainAsRecoveryFile(string tempPath, string outputPath)
        {
            var token = Guid.NewGuid().ToString("N").Substring(0, TempFileTokenLength);
            var recoveryPath = BuildRecoveryFilePath(outputPath, token);
            try
            {
                File.Move(tempPath, recoveryPath);
                _logger.LogWarning(
                    "年度ファイルへ差し替えられなかったため、作成した内容を復旧用ファイルに残しました: {RecoveryPath}（本来の名前: {OutputPath}）",
                    recoveryPath,
                    outputPath);
                return recoveryPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "作成した内容を復旧用ファイルへ移せなかったため、一時ファイルのまま残しました: {TempPath}（本来の名前: {OutputPath}）",
                    tempPath,
                    outputPath);
                return tempPath;
            }
        }

        /// <summary>
        /// 差し替えに失敗し作成した内容を別名で残したときの案内文言（Issue #2040）
        /// </summary>
        internal static string BuildRetainedContentMessage(string outputFileName, string retainedFileName)
        {
            return $"年度ファイル「{outputFileName}」を保存できなかったため、作成した内容（これまでの月を含む）を「{retainedFileName}」に残しました。" +
                $"出力先フォルダーでこのファイルの名前を「{outputFileName}」に変えてから、もう一度作成してください。" +
                "\n\n詳細はログファイルを確認してください。";
        }

        private static bool OutputFileExists(string outputPath)
        {
            try
            {
                return File.Exists(outputPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 差し替えに失敗し、作成した内容を別名で残したことを伝える例外（Issue #2040）
        /// </summary>
        /// <remarks>
        /// <see cref="IOException"/> の派生にして、専用の分岐が無い経路でも従来の保存失敗として扱われるようにする。
        /// </remarks>
        private sealed class ReportContentRetainedException : IOException
        {
            public ReportContentRetainedException(string retainedPath, Exception innerException)
                : base("Report content was retained under a different name.", innerException)
            {
                RetainedPath = retainedPath;
            }

            public string RetainedPath { get; }
        }

        /// <summary>
        /// 保存を中断した一時ファイルのうち十分に古いものを削除する（Issue #2040）
        /// </summary>
        /// <remarks>
        /// 対象はこの年度ファイルの一時ファイルに限る（<see cref="IsTempFileNameOf"/>）。
        /// 最善努力であり<b>例外を投げない</b> — 回収の失敗で帳票の作成を止めない。
        /// </remarks>
        private void CleanupStaleTempFiles(string outputPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(outputPath);
                var outputFileName = Path.GetFileName(outputPath);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(outputFileName) || !Directory.Exists(directory))
                {
                    return;
                }

                var threshold = DateTime.Now.AddHours(-AppConstants.ReportTempFileStaleHours);
                var candidates = new DirectoryInfo(directory)
                    .GetFiles(outputFileName + ".*" + ReportTempFileExtension)
                    .Where(f => IsTempFileNameOf(f.Name, outputFileName) && f.LastWriteTime < threshold);

                foreach (var file in candidates)
                {
                    // 一時ファイルの残存は前回の保存が完走しなかった痕跡なので Information で残す（logging.md）
                    _logger.LogInformation(
                        "保存を中断した帳票の一時ファイルを削除します: {Path}, LastWrite={LastWrite}",
                        file.FullName,
                        file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    TryDeleteFile(file.FullName, "保存を中断した帳票の一時ファイル");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存を中断した帳票の一時ファイルの回収に失敗しました: {Path}", outputPath);
            }
        }

        /// <summary>
        /// ファイルを削除する。失敗しても例外を投げず Warning を残す
        /// </summary>
        private void TryDeleteFile(string path, string description)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Description}の削除に失敗しました: {Path}", description, path);
            }
        }

        /// <summary>
        /// 月に対応するシート名を取得
        /// </summary>
        /// <remarks>
        /// Issue #1691: <see cref="ReportExportStatusService"/> が「対象月のシートが存在するか」で
        /// 出力済み判定を行うため internal に公開する。シート名の書式をここ以外で組み立てると、
        /// 命名規則を変えたときに「出力したのに未出力と表示される」形で静かに乖離する。
        /// </remarks>
        internal static string GetMonthSheetName(int month)
        {
            return $"{month}月";
        }

        /// <summary>
        /// ワークシートのデータ部分をクリア（5行目以降）し、前回生成時の改ページを撤去する
        /// </summary>
        /// <remarks>
        /// Issue #1810: セル値のクリアだけでは <see cref="CheckAndInsertPageBreak"/> が
        /// 前回の生成で挿入した改ページ（PageSetup.RowBreaks）が残留する。
        /// 帳票は毎月同一ファイルへ上書きされる（Issue #477）ため、行数が減る再生成
        /// （Issue #635 の履歴個別削除、Issue #1458 の履歴統合）で旧改ページが残ると、
        /// <see cref="GetLastPageNumberFromWorksheet"/>（L2 + RowBreaks.Count）が過大になって
        /// 翌月シートの開始ページ番号が飛び、空白領域の改ページで印刷時に白紙ページが出る。
        /// 改ページはデータ出力時に毎回作り直されるため、ここで全撤去してよい。
        /// </remarks>
        private static void ClearWorksheetData(IXLWorksheet worksheet)
        {
            // 前回生成時の改ページを撤去（データ量に応じて再挿入される）
            worksheet.PageSetup.RowBreaks.Clear();

            // 5行目から使用されている最終行までをクリア
            var lastRowUsed = worksheet.LastRowUsed()?.RowNumber() ?? 4;
            if (lastRowUsed >= 5)
            {
                var rangeToDelete = worksheet.Range(5, 1, lastRowUsed, TemplateLastColumn);
                rangeToDelete.Clear();
            }
        }

        /// <summary>
        /// テンプレートシートのフォーマットを新しいシートにコピー
        /// </summary>
        private static void CopyWorksheetFormat(IXLWorksheet source, IXLWorksheet target)
        {
            // 1〜4行目（ヘッダ部分）をコピー
            var headerRange = source.Range(1, 1, 4, 12);
            headerRange.CopyTo(target.Cell(1, 1));

            // 17〜22行目（備考欄/フッタ部分）をコピー
            CopyNotesSection(source, target);

            // 列幅をコピー
            for (int col = 1; col <= 12; col++)
            {
                target.Column(col).Width = source.Column(col).Width;
            }

            // 行の高さをコピー（1〜4行目）
            for (int row = 1; row <= 4; row++)
            {
                target.Row(row).Height = source.Row(row).Height;
            }

            // Issue #531: ページ設定（フッター/ヘッダー含む）をコピー
            CopyPageSetup(source, target);
        }

        /// <summary>
        /// Issue #531: ページ設定（フッター/ヘッダー含む）をコピー
        /// </summary>
        /// <remarks>
        /// テンプレートに設定されているフッター、ヘッダー、その他のページ設定を
        /// 新しいワークシートにコピーします。
        /// </remarks>
        private static void CopyPageSetup(IXLWorksheet source, IXLWorksheet target)
        {
            var sourceSetup = source.PageSetup;
            var targetSetup = target.PageSetup;

            // 基本ページ設定
            targetSetup.PaperSize = sourceSetup.PaperSize;
            targetSetup.PageOrientation = sourceSetup.PageOrientation;

            // マージン
            targetSetup.Margins.Top = sourceSetup.Margins.Top;
            targetSetup.Margins.Bottom = sourceSetup.Margins.Bottom;
            targetSetup.Margins.Left = sourceSetup.Margins.Left;
            targetSetup.Margins.Right = sourceSetup.Margins.Right;
            targetSetup.Margins.Header = sourceSetup.Margins.Header;
            targetSetup.Margins.Footer = sourceSetup.Margins.Footer;

            // フッター設定をコピー（全ページ共通の設定をコピー）
            CopyHeaderFooterItem(sourceSetup.Footer.Left, targetSetup.Footer.Left);
            CopyHeaderFooterItem(sourceSetup.Footer.Center, targetSetup.Footer.Center);
            CopyHeaderFooterItem(sourceSetup.Footer.Right, targetSetup.Footer.Right);

            // ヘッダー設定をコピー
            CopyHeaderFooterItem(sourceSetup.Header.Left, targetSetup.Header.Left);
            CopyHeaderFooterItem(sourceSetup.Header.Center, targetSetup.Header.Center);
            CopyHeaderFooterItem(sourceSetup.Header.Right, targetSetup.Header.Right);

            // その他のページ設定
            targetSetup.CenterHorizontally = sourceSetup.CenterHorizontally;
            targetSetup.CenterVertically = sourceSetup.CenterVertically;
            targetSetup.BlackAndWhite = sourceSetup.BlackAndWhite;
            targetSetup.DraftQuality = sourceSetup.DraftQuality;
            targetSetup.ShowGridlines = sourceSetup.ShowGridlines;
            targetSetup.ShowRowAndColumnHeadings = sourceSetup.ShowRowAndColumnHeadings;
            targetSetup.FirstPageNumber = sourceSetup.FirstPageNumber;
            targetSetup.HorizontalDpi = sourceSetup.HorizontalDpi;
            targetSetup.VerticalDpi = sourceSetup.VerticalDpi;
            targetSetup.PageOrder = sourceSetup.PageOrder;

            // スケール設定
            if (sourceSetup.PagesWide > 0 || sourceSetup.PagesTall > 0)
            {
                targetSetup.PagesWide = sourceSetup.PagesWide;
                targetSetup.PagesTall = sourceSetup.PagesTall;
            }
            else
            {
                targetSetup.Scale = sourceSetup.Scale;
            }
        }

        /// <summary>
        /// Issue #637: テンプレートから備考欄（17-22行目）を復元する
        /// </summary>
        /// <remarks>
        /// ClearWorksheetDataは5行目以降をすべてクリアするため、
        /// 備考欄（17-22行目）のセルデータも消えてしまう。
        /// テンプレートから備考欄を復元する必要がある。
        /// また、改ページ処理（CopyNotesToNewPage）はこの17-22行目を
        /// コピー元として使用するため、ここが空だと2ページ目以降の備考欄も消える。
        /// </remarks>
        private static void CopyNotesSection(IXLWorksheet source, IXLWorksheet target)
        {
            var notesRange = source.Range(17, 1, 22, 12);
            notesRange.CopyTo(target.Cell(17, 1));

            // 行の高さもコピー
            for (int row = 17; row <= 22; row++)
            {
                target.Row(row).Height = source.Row(row).Height;
            }
        }

        /// <summary>
        /// Issue #531: ヘッダー/フッターアイテムをコピー
        /// </summary>
        private static void CopyHeaderFooterItem(IXLHFItem source, IXLHFItem target)
        {
            target.Clear();

            // 各ページ種別の設定をコピー
            var occurrences = new[] { XLHFOccurrence.AllPages, XLHFOccurrence.FirstPage, XLHFOccurrence.OddPages, XLHFOccurrence.EvenPages };

            foreach (var occurrence in occurrences)
            {
                var text = source.GetText(occurrence);
                if (!string.IsNullOrEmpty(text))
                {
                    target.AddText(text, occurrence);
                }
            }
        }

        /// <summary>
        /// ワークシートを月順（4月〜3月）に並び替え
        /// </summary>
        private static void ReorderWorksheetsByMonth(XLWorkbook workbook)
        {
            // 月の順序（4月が最初、3月が最後）
            var monthOrder = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };

            var sheets = workbook.Worksheets.ToList();
            var position = 1;

            foreach (var month in monthOrder)
            {
                var sheetName = GetMonthSheetName(month);
                var sheet = sheets.FirstOrDefault(s => s.Name == sheetName);
                if (sheet != null)
                {
                    sheet.Position = position;
                    position++;
                }
            }
        }

        /// <summary>
        /// 複数カードの月次帳票を一括作成
        /// </summary>
        /// <param name="cardIdms">対象カードIDmのリスト</param>
        /// <param name="year">年</param>
        /// <param name="month">月</param>
        /// <param name="outputFolder">出力先フォルダ</param>
        /// <returns>一括作成結果</returns>
        public async Task<BatchReportGenerationResult> CreateMonthlyReportsAsync(
            IEnumerable<string> cardIdms, int year, int month, string outputFolder)
        {
            var results = new List<(string CardIdm, string CardName, ReportGenerationResult Result)>();

            // テンプレートの存在確認を先に行う
            // Issue #1281: 非同期版を使い UI スレッドブロックを回避
            var batchSettings = await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false);
            if (!TemplateResolver.TemplateExists(batchSettings.DepartmentType))
            {
                try
                {
                    TemplateResolver.ResolveTemplatePath(batchSettings.DepartmentType);
                }
                catch (TemplateNotFoundException ex)
                {
                    return BatchReportGenerationResult.TemplateNotFound(ex.GetDetailedMessage());
                }
            }

            try
            {
                Directory.CreateDirectory(outputFolder);
            }
            catch (UnauthorizedAccessException ex)
            {
                // #1614 / #1817: 生の ex.Message は出さず、ログへ残す（上の CreateMonthlyReportAsync と同じ形）
                ErrorDialogHelper.LogException(ex, "帳票の出力先フォルダの作成");
                return BatchReportGenerationResult.DirectoryCreationFailed(
                    "出力先フォルダへのアクセス権限がありません。別のフォルダを指定するか、管理者に連絡してください。\n\n詳細はログファイルを確認してください。");
            }
            catch (IOException ex)
            {
                ErrorDialogHelper.LogException(ex, "帳票の出力先フォルダの作成");
                return BatchReportGenerationResult.DirectoryCreationFailed(
                    "出力先フォルダの作成に失敗しました。パスを確認してください。\n\n詳細はログファイルを確認してください。");
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.LogException(ex, "帳票の出力先フォルダの作成");
                return BatchReportGenerationResult.DirectoryCreationFailed(
                    $"出力先フォルダを作成できませんでした。{ExceptionMessageFormatter.ToReason(ex)}\n\n詳細はログファイルを確認してください。");
            }

            foreach (var cardIdm in cardIdms)
            {
                var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true).ConfigureAwait(false);
                if (card == null)
                {
                    results.Add((cardIdm, null, ReportGenerationResult.FailureResult(
                        "カード情報が見つかりません",
                        $"指定されたカード（IDm: {cardIdm}）は登録されていません。")));
                    continue;
                }

                var cardName = $"{card.CardType} {card.CardNumber}";
                // Issue #477: 年度ファイル名に変更
                var fiscalYear = GetFiscalYear(year, month);
                var fileName = GetFiscalYearFileName(card.CardType, card.CardNumber, fiscalYear);
                var outputPath = Path.Combine(outputFolder, fileName);

                var result = await CreateMonthlyReportAsync(cardIdm, year, month, outputPath).ConfigureAwait(false);
                results.Add((cardIdm, cardName, result));

                // Issue #2042: 全カードに共通する原因（テンプレート・組織設定）の失敗は、続けても
                // 同じ失敗が選択枚数だけ並ぶだけなので中断する。ViewModel 側の一括ループ
                // （ReportViewModel.CreateReportAsync）と判断を揃えておく。
                if (result.IsCommonFailure)
                {
                    break;
                }
            }

            return new BatchReportGenerationResult(results);
        }

        /// <summary>
        /// 年度を計算（4月〜翌3月が同一年度）
        /// </summary>
        /// <param name="year">西暦年</param>
        /// <param name="month">月</param>
        /// <returns>年度（例: 2024年4月〜2025年3月 → 2024）</returns>
        public static int GetFiscalYear(int year, int month)
        {
            return FiscalYearHelper.GetFiscalYear(year, month);
        }

        /// <summary>
        /// 年度ファイル名を生成
        /// </summary>
        /// <param name="cardType">カード種別</param>
        /// <param name="cardNumber">カード番号</param>
        /// <param name="fiscalYear">年度</param>
        /// <returns>ファイル名（既定書式の例: 物品出納簿_はやかけん_H001_2024年度.xlsx）</returns>
        /// <remarks>
        /// Issue #1820: 以前は <c>static</c> だったため注入済みの <see cref="OrganizationOptions"/> を
        /// 参照できず、組織設定 <c>ReportLayout.FileNameFormat</c> が無視されていた。
        /// 生成は <see cref="IReportFileNameFactory"/> に集約している。
        /// </remarks>
        public string GetFiscalYearFileName(string cardType, string cardNumber, int fiscalYear)
            => _fileNameFactory.GetFiscalYearFileName(cardType, cardNumber, fiscalYear);

        /// <summary>
        /// 物品出納簿テンプレートの帳票幅（L 列 = 12）。
        /// </summary>
        /// <remarks>
        /// 罫線・結合セル・印刷範囲（<c>ExcelStyleFormatter</c> の <c>PrintAreas.Add(1, 1, lastRow, 12)</c>）・
        /// 継続ページへのヘッダーコピー（<see cref="CopyHeaderToNewPage"/>）がいずれもこの幅を前提にしており、
        /// テンプレートファイル自体に埋め込まれているため<b>設定では変えられない</b>（Issue #1820 が
        /// 明細行について述べているのと同じ理由）。ヘッダー各列はこの幅の内側でのみ移動できる。
        /// </remarks>
        internal const int TemplateLastColumn = 12;

        /// <summary>
        /// 組織設定のヘッダー列番号が帳票幅（1〜<see cref="TemplateLastColumn"/>）に収まっているか検証する（Issue #1956）。
        /// </summary>
        /// <returns>問題がなければ <c>null</c>。問題があれば「何が・なぜ・どうすれば」を含む文言。</returns>
        /// <remarks>
        /// <para>
        /// 範囲外の列は書き込み自体は成功する（ClosedXML は 16,384 列まで受け付ける）が、
        /// <b>印刷範囲の外なので物品出納簿に現れず</b>、継続ページにもコピーされない。
        /// 「頁の欄が空のまま帳票が出来上がる」形で静かに壊れるため、帳票を作る前に弾く。
        /// </para>
        /// <para>
        /// 既定値へ倒さないのは、倒すと<b>帳票の中身が管理者の意図と違う場所に出る</b>ため。
        /// Issue #1820 が不正な <c>FileNameFormat</c> を既定書式へ倒したのは「ファイル名が既定になるだけで
        /// 帳票の中身は正しい」からで、判断の分かれ目はそこにある（`.claude/rules/development-conventions.md` #1812
        /// 「定義域外の入力を黙って別の値に丸めない」）。
        /// </para>
        /// <para>
        /// 検証は 5 列すべてに掛ける。ページ番号列だけを検証すると「列だけ可変で他が固定」という
        /// 半端な状態がヘッダー行の中に残る（#1820「設定が効く範囲は、その設定だけで完結する単位で切る」）。
        /// </para>
        /// </remarks>
        internal string ValidateHeaderColumns()
        {
            var mapping = _orgOptions.TemplateMapping;
            var invalid = new List<string>();

            void Check(string settingName, int value)
            {
                if (value < 1 || value > TemplateLastColumn)
                {
                    invalid.Add($"{settingName}={value}");
                }
            }

            Check(nameof(mapping.ClassificationColumn), mapping.ClassificationColumn);
            Check(nameof(mapping.CardTypeColumn), mapping.CardTypeColumn);
            Check(nameof(mapping.CardNumberColumn), mapping.CardNumberColumn);
            Check(nameof(mapping.UnitColumn), mapping.UnitColumn);
            Check(nameof(mapping.PageNumberColumn), mapping.PageNumberColumn);

            if (invalid.Count == 0)
            {
                return null;
            }

            return $"組織設定のヘッダー列番号が帳票の範囲外です（{string.Join("、", invalid)}）。" +
                   $"物品出納簿のテンプレートは A〜L 列（1〜{TemplateLastColumn}）で構成されているため、" +
                   "この範囲を超える列に書き込んでも印刷されません。" +
                   $"appsettings.json の OrganizationOptions:TemplateMapping で 1〜{TemplateLastColumn} の値に修正してください。";
        }

        /// <summary>
        /// ヘッダ情報を設定
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="card">カード情報</param>
        /// <param name="pageNumber">ページ番号（省略時は設定しない）</param>
        /// <param name="headerStartRow">ヘッダーの開始行（デフォルトは1）</param>
        private void SetHeaderInfo(IXLWorksheet worksheet, IcCard card, int? pageNumber = null, int headerStartRow = 1)
        {
            // ヘッダ情報を設定（指定された開始行からの相対位置）
            var row2 = headerStartRow + 1;  // 2行目（テンプレートでは2行目にヘッダー情報）
            worksheet.Cell(row2, _orgOptions.TemplateMapping.ClassificationColumn).Value = _orgOptions.ReportLayout.ClassificationText;
            // Issue #1267: CardType / CardNumber はユーザー入力（CSV/UI登録）由来のため式インジェクション対策を施す
            worksheet.Cell(row2, _orgOptions.TemplateMapping.CardTypeColumn).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(card.CardType);
            worksheet.Cell(row2, _orgOptions.TemplateMapping.CardNumberColumn).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(card.CardNumber);
            worksheet.Cell(row2, _orgOptions.TemplateMapping.UnitColumn).Value = _orgOptions.ReportLayout.UnitText;

            // Issue #510: ページ番号を設定
            if (pageNumber.HasValue)
            {
                worksheet.Cell(row2, _orgOptions.TemplateMapping.PageNumberColumn).Value = pageNumber.Value;
            }

            // ヘッダ行のフォントサイズを調整して1行に収める（1ページ目のみ）
            if (headerStartRow == 1)
            {
                AdjustHeaderRowFontSize(worksheet);
            }
        }

        /// <summary>
        /// ページ番号を設定（Issue #510）
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="headerStartRow">ヘッダーの開始行</param>
        /// <param name="pageNumber">ページ番号</param>
        /// <param name="pageNumberColumn">ページ番号の列番号（<see cref="TemplateMappingOptions.PageNumberColumn"/>）</param>
        /// <remarks>
        /// Issue #1956: 以前は列 12（L 列）を直書きしており、<see cref="SetHeaderInfo"/> だけが
        /// 組織設定を読む「一部だけ設定化」の状態だった。設定を変えると 1 ページ目と継続ページで
        /// 書き込み先が食い違い、<see cref="GetLastPageNumberFromWorksheet"/> も読めなくなるため、
        /// 列は既定値を持たない引数で貫通させる（既定値付きの引数は設定の注入を静かに無効化する。#1955）。
        /// </remarks>
        private static void SetPageNumber(IXLWorksheet worksheet, int headerStartRow, int pageNumber, int pageNumberColumn)
        {
            var row2 = headerStartRow + 1;  // ヘッダー情報は開始行+1
            worksheet.Cell(row2, pageNumberColumn).Value = pageNumber;  // 頁の値（既定: L列）
        }

        /// <summary>
        /// ワークシート内の最終ページ番号を取得（Issue #809）
        /// </summary>
        /// <remarks>
        /// <para>
        /// CheckAndInsertPageBreak は改ページごとに AddHorizontalPageBreak と SetPageNumber を呼ぶため、
        /// 「1ページ目のページ番号 + 改ページ数 = 最終ページ番号」が成り立つ。
        /// </para>
        /// <para>
        /// ページ番号セル（1ページ目のページ番号。既定では L2）が空または整数として読めない場合は
        /// <c>0</c> を返す。
        /// 0 は「ページ情報を持たない（無効な）シート」を示すセンチネル値であり、呼び出し側
        /// （<see cref="FindNearestPreviousMonthLastPage"/>）はこの 0 を見て当該シートをスキップする。
        /// </para>
        /// </remarks>
        /// <param name="worksheet">対象ワークシート</param>
        /// <param name="pageNumberColumn">
        /// ページ番号の列番号（<see cref="TemplateMappingOptions.PageNumberColumn"/>）。
        /// Issue #1956: 以前は列 12 を直書きしており、設定を変えると常に 0 を返して
        /// 毎月ページ番号が振り出しに戻っていた。
        /// </param>
        internal static int GetLastPageNumberFromWorksheet(IXLWorksheet worksheet, int pageNumberColumn)
        {
            var firstPageCell = worksheet.Cell(2, pageNumberColumn);  // 1ページ目のページ番号（既定: L2）
            if (firstPageCell.IsEmpty())
                return 0;

            if (!firstPageCell.TryGetValue<int>(out var firstPageNumber))
                return 0;

            var pageBreakCount = worksheet.PageSetup.RowBreaks.Count;
            return firstPageNumber + pageBreakCount;
        }

        /// <summary>
        /// 月の開始ページ番号を算出（Issue #809）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 直近の前月シートが存在し、かつそのシートが有効なページ番号情報を持つ場合は
        /// その最終ページ番号+1 を返す。それ以外（前月シートなし、もしくはあってもページ番号セルが
        /// 空/非整数）の場合は <see cref="IcCard.StartingPageNumber"/> を使用する。
        /// </para>
        /// <para>
        /// 「直近の前月シートを探す」処理は <see cref="FindNearestPreviousMonthLastPage"/> に
        /// 委譲しており、ページ番号セルが空/非整数のシートはスキップしてさらに過去のシートを探索する
        /// 振る舞いはそちらに集約されている。
        /// </para>
        /// </remarks>
        /// <param name="workbook">対象ワークブック</param>
        /// <param name="card">対象カード</param>
        /// <param name="month">対象月</param>
        /// <param name="pageNumberColumn">ページ番号の列番号（<see cref="TemplateMappingOptions.PageNumberColumn"/>、Issue #1956）</param>
        internal static int GetStartingPageNumberForMonth(XLWorkbook workbook, IcCard card, int month, int pageNumberColumn)
        {
            // 年度内の月順序（4月=先頭, 3月=末尾）
            var fiscalMonthOrder = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };
            var currentIndex = Array.IndexOf(fiscalMonthOrder, month);

            // 4月（年度最初の月）または不正な月 → StartingPageNumber をそのまま使用
            if (currentIndex <= 0)
                return card.StartingPageNumber;

            var nearestPreviousLastPage = FindNearestPreviousMonthLastPage(
                workbook, fiscalMonthOrder, currentIndex, pageNumberColumn);
            return nearestPreviousLastPage > 0
                ? nearestPreviousLastPage + 1
                : card.StartingPageNumber;
        }

        /// <summary>
        /// 直近で「有効なページ情報を持つ前月シート」を逆順検索し、その最終ページ番号を返す。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 年度内の月順序（4月→5月→…→3月）を基準に、現在月の1つ前から先頭（4月）に向かって
        /// シートを探索する。シートが存在しても <see cref="GetLastPageNumberFromWorksheet"/> が
        /// 0 を返す場合（ページ番号セルが空または非整数の異常状態）はそのシートをスキップしてさらに過去の
        /// シートを探索する。
        /// </para>
        /// <para>
        /// この「ページ番号セルが空のシートのスキップ」は本メソッドの中核的な責務であり、メソッド名と本コメントで
        /// 明示している。Issue #1197: 以前は <see cref="GetStartingPageNumberForMonth"/> 内に
        /// 直接ループが書かれており、そのスキップ動作が暗黙の前提として埋もれていた。
        /// </para>
        /// </remarks>
        /// <param name="workbook">対象ワークブック</param>
        /// <param name="fiscalMonthOrder">年度内の月順序配列（4月始まり3月終わり）</param>
        /// <param name="currentIndex">現在月の <paramref name="fiscalMonthOrder"/> 内インデックス</param>
        /// <param name="pageNumberColumn">ページ番号の列番号（<see cref="TemplateMappingOptions.PageNumberColumn"/>、Issue #1956）</param>
        /// <returns>
        /// 直近の有効な前月シートの最終ページ番号。
        /// どの前月シートも存在しないか、すべてページ番号セルが空/非整数の場合は 0。
        /// </returns>
        internal static int FindNearestPreviousMonthLastPage(
            XLWorkbook workbook, int[] fiscalMonthOrder, int currentIndex, int pageNumberColumn)
        {
            for (int i = currentIndex - 1; i >= 0; i--)
            {
                var prevMonthName = $"{fiscalMonthOrder[i]}月";
                if (workbook.Worksheets.TryGetWorksheet(prevMonthName, out var prevSheet))
                {
                    var lastPage = GetLastPageNumberFromWorksheet(prevSheet, pageNumberColumn);
                    if (lastPage > 0)
                        return lastPage;
                }
            }
            return 0;
        }

        /// <summary>
        /// ヘッダ行のフォントサイズを調整
        /// </summary>
        /// <remarks>
        /// 物品分類～単位：円までのヘッダ部分（2行目）を1行に収めるため、
        /// フォントサイズを小さくして調整します。
        /// </remarks>
        private void AdjustHeaderRowFontSize(IXLWorksheet worksheet)
        {
            // 2行目（物品分類～単位：円）のフォントサイズを9ptに設定
            const double headerFontSize = 9;

            // A2～L2（テンプレートの帳票幅）の範囲のフォントサイズを調整する。
            // Issue #1956: ヘッダー各列は組織設定（TemplateMapping）で変更できるが、
            // 帳票幅そのものは可変ではない（ValidateHeaderColumns で 1〜TemplateLastColumn に限る）ため
            // 調整範囲は固定でよい。
            var headerRange = worksheet.Range(2, 1, 2, TemplateLastColumn);
            headerRange.Style.Font.FontSize = headerFontSize;
        }

        /// <summary>
        /// ReportRow（繰越行・データ行）を出力
        /// </summary>
        /// <remarks>
        /// Issue #1023: WriteFiscalYearCarryoverRow, WriteMonthlyCarryoverRow, WriteDataRow を統合。
        /// 行種別（繰越/データ）に関わらず、ReportRow の値をそのまま出力する。
        /// データ差異（4月の受入金額あり/なし等）は ReportRowBuilder が解決済み。
        /// </remarks>
        private int WriteReportRow(IXLWorksheet worksheet, int row, ReportRow reportRow)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            // Issue #1267: 式インジェクション対策として、ユーザー入力由来のテキスト列は
            // FormulaInjectionSanitizer.Sanitize で先頭危険文字に ' プレフィクスを付与する。
            worksheet.Cell(row, 1).Value = reportRow.DateDisplay;  // 出納年月日 (A列)
            worksheet.Cell(row, 2).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(reportRow.Summary);  // 摘要 (B-D列)

            // 受入金額 (E列)
            if (reportRow.Income.HasValue)
            {
                worksheet.Cell(row, 5).Value = reportRow.Income.Value;
                worksheet.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
            }
            else
            {
                worksheet.Cell(row, 5).Value = "";
            }

            // 払出金額 (F列)
            if (reportRow.Expense.HasValue)
            {
                worksheet.Cell(row, 6).Value = reportRow.Expense.Value;
                worksheet.Cell(row, 6).Style.NumberFormat.Format = "#,##0";
            }
            else
            {
                worksheet.Cell(row, 6).Value = "";
            }

            // 残額 (G列)
            if (reportRow.Balance.HasValue)
            {
                worksheet.Cell(row, 7).Value = reportRow.Balance.Value;
                worksheet.Cell(row, 7).Style.NumberFormat.Format = "#,##0";
            }
            else
            {
                worksheet.Cell(row, 7).Value = "";
            }

            // 氏名 (H列) / 備考 (I-L列)
            // Issue #1267: 式インジェクション対策
            worksheet.Cell(row, 8).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(reportRow.StaffName);
            worksheet.Cell(row, 9).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(reportRow.Note);

            // 罫線を適用
            ApplyDataRowBorder(worksheet, row);

            // 繰越行: 摘要を中央揃え
            if (reportRow.RowType == ReportRowType.Carryover)
            {
                var summaryCell = worksheet.Cell(row, 2);
                summaryCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                summaryCell.Style.Font.FontSize = 14;
            }
            else
            {
                // Issue #946: 摘要欄のフォントサイズを文字列長に応じて調整
                // ApplyDataRowBorder後に設定して14ptの一括設定を上書きする
                var summaryCell = worksheet.Cell(row, 2);
                summaryCell.Style.Font.FontSize = GetSummaryFontSize(reportRow.Summary);

                // Issue #980: 備考欄のフォントサイズを文字列長に応じて調整
                var noteCell = worksheet.Cell(row, 9);
                noteCell.Style.Font.FontSize = GetNoteFontSize(reportRow.Note);
            }

            return row + 1;
        }

        /// <summary>
        /// 摘要欄のフォントサイズを文字列の長さに応じて決定
        /// </summary>
        /// <remarks>
        /// Issue #946: B-D列結合セルの幅に収まるよう、文字数が多いほどフォントサイズを小さくする。
        /// </remarks>
        /// <param name="summary">摘要文字列</param>
        /// <returns>適切なフォントサイズ（ポイント）</returns>
        internal static double GetSummaryFontSize(string summary)
        {
            var length = summary?.Length ?? 0;

            if (length < 15)  return 14;
            if (length < 32)  return 12;
            if (length < 38)  return 10;
            if (length < 93)  return 8;
            if (length < 108) return 7;
            return 6;
        }

        /// <summary>
        /// 備考欄のフォントサイズを文字列の長さに応じて決定
        /// </summary>
        /// <remarks>
        /// Issue #980: I-L列結合セル（4列、摘要のB-D列3列より約1.33倍広い）の幅に収まるよう、
        /// 文字数が多いほどフォントサイズを小さくする。
        /// しきい値は摘要欄（GetSummaryFontSize）の約1.33倍。
        /// </remarks>
        /// <param name="note">備考文字列</param>
        /// <returns>適切なフォントサイズ（ポイント）</returns>
        internal static double GetNoteFontSize(string note)
        {
            var length = note?.Length ?? 0;

            if (length < 20)  return 14;
            if (length < 43)  return 12;
            if (length < 51)  return 10;
            if (length < 124) return 8;
            if (length < 144) return 7;
            return 6;
        }

        /// <summary>
        /// 月計行を出力
        /// </summary>
        /// <remarks>
        /// Issue #451対応: 受入金額・払出金額は0も表示（空欄にしない）
        /// Issue #813: 4月のみ累計行省略のため残額を表示
        /// Issue #1023: ReportTotal を受け取るように変更
        /// </remarks>
        private int WriteMonthlyTotalRow(IXLWorksheet worksheet, int row, ReportTotal total)
        {
            WriteTotalRowCore(worksheet, row, total);

            // 罫線を適用（月計行は上下を太線に）
            ApplySummaryRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// 累計行を出力
        /// </summary>
        /// <remarks>
        /// Issue #451対応: 受入金額・払出金額は0も表示（空欄にしない）
        /// Issue #1023: ReportTotal を受け取るように変更
        /// </remarks>
        private int WriteCumulativeRow(IXLWorksheet worksheet, int row, ReportTotal total)
        {
            WriteTotalRowCore(worksheet, row, total);

            // 罫線を適用（累計行は上下を太線に）
            ApplySummaryRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// 月計行・累計行の共通出力処理
        /// </summary>
        private void WriteTotalRowCore(IXLWorksheet worksheet, int row, ReportTotal total)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            worksheet.Cell(row, 1).Value = "";           // 出納年月日（空欄）(A列)
            worksheet.Cell(row, 2).Value = total.Label;  // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = total.Income;  // 受入金額 (E列) - 0も表示
            worksheet.Cell(row, 6).Value = total.Expense; // 払出金額 (F列) - 0も表示

            // Issue #813: 残額が設定されている場合のみ表示（4月の月計、すべての累計）
            if (total.Balance.HasValue)
            {
                worksheet.Cell(row, 7).Value = total.Balance.Value;
                worksheet.Cell(row, 7).Style.NumberFormat.Format = "#,##0";
            }
            else
            {
                worksheet.Cell(row, 7).Value = "";  // 残額（空欄）(G列)
            }

            // Issue #509: 金額セルの表示形式を明示的に数値に設定
            worksheet.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
            worksheet.Cell(row, 6).Style.NumberFormat.Format = "#,##0";

            // 合計行にスタイルを適用
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Font.Bold = true;

            // ラベル（B列）を中央揃え・14ptに設定
            var summaryCell = worksheet.Cell(row, 2);
            summaryCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            summaryCell.Style.Font.FontSize = 14;
        }

        /// <summary>
        /// 次年度繰越行を出力
        /// </summary>
        private int WriteCarryoverToNextYearRow(IXLWorksheet worksheet, int row, int balance)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            worksheet.Cell(row, 1).Value = "";  // 出納年月日（空欄）(A列)
            worksheet.Cell(row, 2).Value = SummaryGenerator.GetCarryoverToNextYearSummary(); // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = "";  // 受入金額 (E列)
            worksheet.Cell(row, 6).Value = balance; // 払出金額 (F列)
            worksheet.Cell(row, 7).Value = 0;   // 残額 (G列)

            // Issue #509: 金額セルの表示形式を明示的に数値に設定
            var expenseCell = worksheet.Cell(row, 6);
            var balanceCell = worksheet.Cell(row, 7);
            expenseCell.Style.NumberFormat.Format = "#,##0";  // 会計形式（3桁カンマ区切り）
            balanceCell.Style.NumberFormat.Format = "#,##0";

            // 罫線を適用（ApplyDataRowBorderでBold=falseにリセットされるため、先に呼ぶ）
            ApplyDataRowBorder(worksheet, row);

            // 繰越行にスタイルを適用（ApplyDataRowBorder後に設定して上書き）
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Font.Bold = true;

            return row + 1;
        }

        /// <summary>
        /// データ行に罫線を適用し、セルを結合
        /// </summary>
        private void ApplyDataRowBorder(IXLWorksheet worksheet, int row)
            => ExcelStyleFormatter.ApplyDataRowBorder(worksheet, row);

        /// <summary>
        /// 月計・累計行に罫線を適用し、セルを結合
        /// </summary>
        /// <remarks>
        /// Issue #451対応:
        /// 月計・累計行の上下罫線を太線（Medium）にして視覚的に区切りを明確化。
        /// 会計マニュアルの「月計・累計欄の上下線は朱線又は太線を用いること」に対応。
        /// </remarks>
        private void ApplySummaryRowBorder(IXLWorksheet worksheet, int row)
            => ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, row);

        /// <summary>
        /// Issue #457: 改ページが必要かチェックし、必要なら挿入する
        /// Issue #510: ページ番号のトラッキングを追加
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="currentRow">現在の行番号</param>
        /// <param name="rowsOnCurrentPage">現在のページに書かれた行数</param>
        /// <param name="rowsPerPage">1ページあたりの最大行数</param>
        /// <param name="currentPageNumber">現在のページ番号（省略時はページ番号を設定しない）</param>
        /// <param name="pageNumberColumn">ページ番号の列番号（<see cref="TemplateMappingOptions.PageNumberColumn"/>、Issue #1956）</param>
        /// <returns>更新された（currentRow, rowsOnCurrentPage, newPageNumber）のタプル</returns>
        /// <remarks>
        /// テンプレート構造（1ページ = 22行）:
        /// - 1-4行: ヘッダー（4行）
        /// - 5-16行: データエリア（12行）
        /// - 17-22行: 備考欄（6行）
        ///
        /// 12行を超えるデータがある場合:
        /// - 新しいページ（23行目～）にヘッダーと備考欄をコピー
        /// - データは新しいページのデータエリア（ヘッダーの後）に書き込む
        /// </remarks>
        private static (int currentRow, int rowsOnCurrentPage, int pageNumber) CheckAndInsertPageBreak(
            IXLWorksheet worksheet, int currentRow, int rowsOnCurrentPage, int rowsPerPage, int currentPageNumber,
            int pageNumberColumn)
        {
            const int HeaderRows = 4;   // ヘッダーの行数（1-4行目）
            const int NotesRows = 6;    // 備考欄の行数（17-22行目）

            if (rowsOnCurrentPage >= rowsPerPage)
            {
                // 新しいページの開始行 = 現在の行 + 備考欄の行数
                // 例: currentRow=17 → newPageStartRow=23
                //     currentRow=39 → newPageStartRow=45
                var newPageStartRow = currentRow + NotesRows;

                // ヘッダー（1-4行目）を新しいページにコピー
                CopyHeaderToNewPage(worksheet, newPageStartRow);

                // 備考欄（17-22行目）を新しいページにコピー
                // 備考欄の開始行 = 新しいページの開始行 + ヘッダー + データエリア
                var notesTargetRow = newPageStartRow + HeaderRows + rowsPerPage;
                CopyNotesToNewPage(worksheet, notesTargetRow);

                // 新しいページの改ページを挿入
                // AddHorizontalPageBreak(row) は row の直前に改ページを挿入
                // 前のページの最終行（備考欄の最終行）の直後に改ページを入れるため、newPageStartRowを指定
                worksheet.PageSetup.AddHorizontalPageBreak(newPageStartRow - 1);

                // Issue #510: 新しいページにページ番号を設定
                var newPageNumber = currentPageNumber + 1;
                SetPageNumber(worksheet, newPageStartRow, newPageNumber, pageNumberColumn);

                // データの開始行（ヘッダーの後）
                var newDataStartRow = newPageStartRow + HeaderRows;

                return (newDataStartRow, 0, newPageNumber);
            }
            return (currentRow, rowsOnCurrentPage, currentPageNumber);
        }

        /// <summary>
        /// Issue #457: ヘッダー（1-4行目）を新しいページにコピー
        /// </summary>
        private static void CopyHeaderToNewPage(IXLWorksheet worksheet, int targetStartRow)
        {
            // 1-4行目の内容を新しいページにコピー
            var sourceRange = worksheet.Range(1, 1, 4, TemplateLastColumn);
            sourceRange.CopyTo(worksheet.Cell(targetStartRow, 1));

            // 行の高さもコピー
            for (int i = 0; i < 4; i++)
            {
                worksheet.Row(targetStartRow + i).Height = worksheet.Row(1 + i).Height;
            }
        }

        /// <summary>
        /// Issue #457: 備考欄（17-22行目）を新しいページにコピー
        /// </summary>
        private static void CopyNotesToNewPage(IXLWorksheet worksheet, int targetStartRow)
        {
            // 17-22行目の内容を新しいページにコピー
            var sourceRange = worksheet.Range(17, 1, 22, 12);
            sourceRange.CopyTo(worksheet.Cell(targetStartRow, 1));

            // 行の高さもコピー
            for (int i = 0; i < 6; i++)
            {
                worksheet.Row(targetStartRow + i).Height = worksheet.Row(17 + i).Height;
            }
        }

        /// <summary>
        /// Issue #457: 最終ページの空白行に罫線を引く
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="currentRow">現在の行番号（最後に書いた行の次）</param>
        /// <param name="rowsOnCurrentPage">現在のページに書かれた行数</param>
        /// <param name="rowsPerPage">1ページあたりの最大行数</param>
        private static void FillEmptyRowsWithBorders(IXLWorksheet worksheet, int currentRow, int rowsOnCurrentPage, int rowsPerPage)
        {
            // 最終ページに空白行がある場合、罫線を引く
            if (rowsOnCurrentPage > 0 && rowsOnCurrentPage < rowsPerPage)
            {
                var emptyRowsCount = rowsPerPage - rowsOnCurrentPage;
                // Issue #1480: 連続する空白行を 1 度の範囲適用で塗ることで
                // ClosedXML のスタイル操作回数を削減（per-row ループからの脱却）
                ExcelStyleFormatter.ApplyEmptyRowBordersToRange(
                    worksheet, currentRow, currentRow + emptyRowsCount - 1);
            }
        }

        /// <summary>
        /// Issue #457: 空白行に罫線を適用
        /// </summary>
        private static void ApplyEmptyRowBorder(IXLWorksheet worksheet, int row)
            => ExcelStyleFormatter.ApplyEmptyRowBorder(worksheet, row);

        /// <summary>
        /// Issue #457: ワークシートの印刷設定を行う
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        private static void ConfigurePageSetup(IXLWorksheet worksheet)
            => ExcelStyleFormatter.ConfigurePageSetup(worksheet);

        /// <summary>
        /// Issue #457: 印刷範囲を設定
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="currentRow">現在の行番号（最後に書いた行の次）</param>
        /// <param name="rowsOnCurrentPage">現在のページに書かれた行数</param>
        /// <param name="rowsPerPage">1ページあたりの最大行数</param>
        private static void SetPrintArea(IXLWorksheet worksheet, int currentRow, int rowsOnCurrentPage, int rowsPerPage)
            => ExcelStyleFormatter.SetPrintArea(worksheet, currentRow, rowsOnCurrentPage, rowsPerPage);
    }
}
