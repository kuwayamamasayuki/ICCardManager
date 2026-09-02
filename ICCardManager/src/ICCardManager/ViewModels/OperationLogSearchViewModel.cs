using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Win32;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;


namespace ICCardManager.ViewModels;

/// <summary>
/// 操作種別の選択肢
/// </summary>
public class ActionTypeItem
{
    public string Value { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// 対象テーブルの選択肢
/// </summary>
public class TargetTableItem
{
    public string Value { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// 操作ログ表示用DTO
/// </summary>
public class OperationLogDisplayItem
{
    public int Id { get; init; }
    public DateTime Timestamp { get; init; }
    public string TimestampDisplay => DisplayFormatters.FormatTimestamp(Timestamp);
    public string Action { get; init; } = string.Empty;
    public string ActionDisplay => OperationLogDisplayNames.GetActionDisplayName(Action);
    public string TargetTable { get; init; } = string.Empty;
    public string TargetTableDisplay => OperationLogDisplayNames.GetTableDisplayName(TargetTable);
    public string TargetId { get; init; } = string.Empty;
    /// <summary>
    /// 対象の詳細表示名（例: 「田中太郎（001）」「はやかけん 001」「R7.2.6 鉄道（博多～天神）」）
    /// </summary>
    public string TargetDisplayName { get; init; } = string.Empty;
    public string OperatorName { get; init; } = string.Empty;
    public string? BeforeData { get; init; }
    public string? AfterData { get; init; }
    public string DetailSummary { get; init; } = string.Empty;
}

/// <summary>
/// 操作ログ検索画面のViewModel
/// </summary>
public partial class OperationLogSearchViewModel : ViewModelBase
{
    private readonly IOperationLogRepository _operationLogRepository;
    private readonly IDialogService _dialogService;
    private readonly OperationLogExcelExportService _excelExportService;
    private readonly ISafeFileLauncher _safeFileLauncher;
    private readonly OperationLogger _operationLogger;

    // 検索条件
    [ObservableProperty]
    private DateTime _fromDate;

    [ObservableProperty]
    private DateTime _toDate;

    [ObservableProperty]
    private ActionTypeItem? _selectedAction;

    [ObservableProperty]
    private TargetTableItem? _selectedTargetTable;

    [ObservableProperty]
    private string _targetIdFilter = string.Empty;

    [ObservableProperty]
    private string _operatorNameFilter = string.Empty;

    // 検索結果
    [ObservableProperty]
    private ObservableCollection<OperationLogDisplayItem> _logs = new();

    [ObservableProperty]
    private OperationLogDisplayItem? _selectedLog;

    // ページネーション
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageInfo))]
    [NotifyPropertyChangedFor(nameof(PageNumberDisplay))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageNumberDisplay))]
    private int _totalPages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageInfo))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageInfo))]
    private int _pageSize = 50;

    [ObservableProperty]
    private bool _hasPreviousPage;

    [ObservableProperty]
    private bool _hasNextPage;

    // ステータス
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private string _lastExportedFile = string.Empty;

    /// <summary>
    /// 操作種別の選択肢（Issue #1787: 「すべて」＋ SSOT の全操作種別。種別追加時に自動追随する）
    /// </summary>
    public ObservableCollection<ActionTypeItem> ActionTypes { get; } = new(
        new[] { new ActionTypeItem { Value = "", DisplayName = "すべて" } }
            .Concat(OperationLogDisplayNames.ActionEntries
                .Select(e => new ActionTypeItem { Value = e.Key, DisplayName = e.Value })));

    /// <summary>
    /// 対象テーブルの選択肢（Issue #1787: 「すべて」＋ SSOT の全テーブル）
    /// </summary>
    public ObservableCollection<TargetTableItem> TargetTables { get; } = new(
        new[] { new TargetTableItem { Value = "", DisplayName = "すべて" } }
            .Concat(OperationLogDisplayNames.TableEntries
                .Select(e => new TargetTableItem { Value = e.Key, DisplayName = e.Value })));

    /// <summary>
    /// ページサイズの選択肢
    /// </summary>
    public int[] PageSizeOptions { get; } = { 20, 50, 100 };

    /// <summary>
    /// ページ情報の表示テキスト
    /// </summary>
    public string PageInfo => TotalCount > 0
        ? $"{TotalCount}件中 {(CurrentPage - 1) * PageSize + 1}～{Math.Min(CurrentPage * PageSize, TotalCount)}件を表示"
        : "0件";

    // Issue #1548/#1507: CurrentPageNumberText TextBlock 用の単一バインド文字列。
    // 元は XAML 側で <Run Text="{Binding CurrentPage}"/> <Run Text=" / "/> <Run Text="{Binding TotalPages}"/> と
    // Run 3 つで組み立てていたが、Run 構成では Inlines 変更が親 TextBlock の Text プロパティ更新を伴わず、
    // TextBlockAutomationPeer の Name キャッシュが invalidate されないため、コードビハインドで
    // LiveRegionChanged を発火しても Narrator が新しいテキストを取得しなかった。
    // 派生プロパティ化し Text を単一バインドにすることで LiveRegion 通知時に新テキストが読み上げられる。
    public string PageNumberDisplay => $"{CurrentPage} / {TotalPages} ページ";

    public OperationLogSearchViewModel(
        IOperationLogRepository operationLogRepository,
        IDialogService dialogService,
        OperationLogExcelExportService excelExportService,
        ISafeFileLauncher safeFileLauncher,
        OperationLogger operationLogger)
    {
        _operationLogRepository = operationLogRepository;
        _dialogService = dialogService;
        _excelExportService = excelExportService;
        _safeFileLauncher = safeFileLauncher;
        _operationLogger = operationLogger;

        // デフォルトは今月
        var today = DateTime.Today;
        FromDate = new DateTime(today.Year, today.Month, 1);
        ToDate = today;

        // デフォルト選択
        SelectedAction = ActionTypes[0];
        SelectedTargetTable = TargetTables[0];
    }

    // keyset pagination カーソル（Issue #1479）。null は「カーソル未設定（空ページ or 初回前）」。
    private OperationLogCursor _firstCursor;
    private OperationLogCursor _lastCursor;

    /// <summary>
    /// 初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        await SearchAsync();
    }

    /// <summary>
    /// 検索を実行（Issue #787: 最終ページ＝最新データを表示。Issue #1479: keyset で直接最終ページを取得）
    /// </summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        using (BeginBusy("検索中..."))
        {
            var criteria = BuildSearchCriteria();
            var page = await _operationLogRepository.SearchLastPageAsync(criteria, PageSize);
            ApplyPage(page);
            CurrentPage = Math.Max(1, TotalPages);
        }
    }

    /// <summary>
    /// keyset pagination で取得したページを ViewModel 状態に反映（Issue #1479）。
    /// </summary>
    private void ApplyPage(OperationLogKeysetPage page)
    {
        Logs.Clear();
        foreach (var log in page.Items)
        {
            Logs.Add(ToDisplayItem(log));
        }

        TotalCount = page.TotalCount;
        TotalPages = PageSize > 0 && TotalCount > 0
            ? (int)Math.Ceiling((double)TotalCount / PageSize)
            : 0;
        HasPreviousPage = page.HasPrevious;
        HasNextPage = page.HasNext;
        _firstCursor = page.FirstCursor;
        _lastCursor = page.LastCursor;

        // Issue #1548/#1507: PageInfo の通知は TotalCount setter の [NotifyPropertyChangedFor] で
        // 自動発火するため、ここでの手動 OnPropertyChanged(nameof(PageInfo)) は不要（二重通知防止）。

        SetStatus(TotalCount > 0
            ? $"{TotalCount}件の操作ログが見つかりました"
            : "条件に一致する操作ログはありません", false);
    }

    /// <summary>
    /// 前のページへ（keyset, Issue #1479）
    /// </summary>
    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (!HasPreviousPage || _firstCursor == null) return;

        using (BeginBusy("検索中..."))
        {
            var criteria = BuildSearchCriteria();
            var page = await _operationLogRepository.SearchPreviousPageAsync(criteria, _firstCursor, PageSize);
            ApplyPage(page);
            CurrentPage = Math.Max(1, CurrentPage - 1);
            AnnouncePageNavigation();
        }
    }

    /// <summary>
    /// 次のページへ（keyset, Issue #1479）
    /// </summary>
    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (!HasNextPage || _lastCursor == null) return;

        using (BeginBusy("検索中..."))
        {
            var criteria = BuildSearchCriteria();
            var page = await _operationLogRepository.SearchNextPageAsync(criteria, _lastCursor, PageSize);
            ApplyPage(page);
            CurrentPage++;
            AnnouncePageNavigation();
        }
    }

    /// <summary>
    /// 最初のページへ（keyset, Issue #1479）
    /// </summary>
    [RelayCommand]
    public async Task FirstPageAsync()
    {
        if (CurrentPage == 1 && _firstCursor != null) return;

        using (BeginBusy("検索中..."))
        {
            var criteria = BuildSearchCriteria();
            var page = await _operationLogRepository.SearchFirstPageAsync(criteria, PageSize);
            ApplyPage(page);
            CurrentPage = 1;
            AnnouncePageNavigation();
        }
    }

    /// <summary>
    /// 最後のページへ（keyset, Issue #1479）
    /// </summary>
    [RelayCommand]
    public async Task LastPageAsync()
    {
        using (BeginBusy("検索中..."))
        {
            var criteria = BuildSearchCriteria();
            var page = await _operationLogRepository.SearchLastPageAsync(criteria, PageSize);
            ApplyPage(page);
            CurrentPage = Math.Max(1, TotalPages);
            AnnouncePageNavigation();
        }
    }

    /// <summary>
    /// Issue #1507: ページ送り完了時にスクリーンリーダー向けのアナウンスを <see cref="StatusMessage"/> にセットする。
    /// 検索時の StatusMessage（"N 件の操作ログが見つかりました"）と異なる文字列にすることで、
    /// PropertyChanged 通知が確実に発火し、 Polite Live Region として読み上げられる
    /// （CurrentPageNumberText 単体の Live Region 通知は Narrator が連続発火の中で取りこぼすため、
    /// 確実に読み上げ実績がある <see cref="StatusMessage"/> ルートで補強する）。
    /// </summary>
    private void AnnouncePageNavigation()
    {
        if (TotalPages > 0)
        {
            SetStatus(FormatPageNavigationStatus(CurrentPage, TotalPages, TotalCount), false);
        }
    }

    /// <summary>
    /// Issue #1507: ページ送り完了時の <see cref="StatusMessage"/> 文字列フォーマット（純粋関数）。
    /// 単体テスト容易化のため <c>internal static</c> に分離。フォーマットリグレッションを単体テストで固定する。
    /// </summary>
    internal static string FormatPageNavigationStatus(int currentPage, int totalPages, int totalCount)
    {
        return $"ページ {currentPage} / {totalPages} に移動しました（合計 {totalCount} 件）";
    }

    /// <summary>
    /// ページサイズ変更時（Issue #787: 最終ページに移動）
    /// </summary>
    partial void OnPageSizeChanged(int value)
    {
        _ = SearchForPageSizeChangeAsync();
    }

    /// <summary>
    /// ページサイズ変更に伴う再検索の本体。戻り値を捨てる呼び出し元に代わって、
    /// 本体自身が最後の受け皿になる（`.claude/rules/viewmodel-conventions.md` #1816）。
    /// 検索ボタン経由（RelayCommand）と違い、ここで受けないと例外は
    /// TaskScheduler.UnobservedTaskException まで誰にも観測されず、画面は旧ページのまま止まる。
    /// </summary>
    private async Task SearchForPageSizeChangeAsync()
    {
        try
        {
            await SearchAsync();
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.LogException(ex, "操作ログの検索（ページサイズ変更）");
            SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "操作ログの検索"), isError: true);
        }
    }

    /// <summary>
    /// 検索条件をクリア
    /// </summary>
    [RelayCommand]
    public void ClearFilters()
    {
        var today = DateTime.Today;
        FromDate = new DateTime(today.Year, today.Month, 1);
        ToDate = today;
        SelectedAction = ActionTypes[0];
        SelectedTargetTable = TargetTables[0];
        TargetIdFilter = string.Empty;
        OperatorNameFilter = string.Empty;
    }

    /// <summary>
    /// Excelエクスポート（Issue #786）
    /// </summary>
    [RelayCommand]
    public async Task ExportToExcelAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel ファイル (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            FileName = $"操作ログ_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExportToExcelFileAsync(dialog.FileName);
    }

    /// <summary>
    /// 指定ファイルへのExcelエクスポート本体。<see cref="ExportToExcelAsync"/> からファイル選択後に呼ばれるほか、
    /// ユニットテストから <see cref="SaveFileDialog"/> を介さずに実行する経路としても使用する。
    /// </summary>
    internal async Task ExportToExcelFileAsync(string filePath)
    {
        int? exportedCount = null;
        string errorMessage = null;
        using (BeginBusy("エクスポート中..."))
        {
            try
            {
                var criteria = BuildSearchCriteria();
                var logs = await _operationLogRepository.SearchAllAsync(criteria);

                await _excelExportService.ExportAsync(logs, filePath);

                LastExportedFile = filePath;
                exportedCount = logs.Count();
                SetStatus($"エクスポート完了: {exportedCount}件を出力しました", false);

                // Issue #1787: 操作ログ自身の書き出しも EXPORT として記録する。
                // 出力内容は職員氏名・IDm を含む個人情報であり、絞り込みコンボに「エクスポート」を
                // 用意した以上、この経路が記録されないと「この期間に持ち出しは無かった」という
                // 誤った結論を与える（記録経路は DataExportImportViewModel の1つだけだった）。
                await TryLogExportAsync(filePath, exportedCount.Value);
            }
            catch (Exception ex)
            {
                // Issue #1614: 生の ex.Message を UI に出さず、3要素準拠の文言を表示。技術詳細はログへ逃がす。
                ErrorDialogHelper.LogException(ex, "操作ログのエクスポート");
                errorMessage = ExceptionMessageFormatter.ToUserMessage(ex, "操作ログのエクスポート");
                SetStatus(errorMessage, true);
            }
        }

        // Issue #1383: BeginBusyスコープを抜けてIsBusy=falseが確定した後にダイアログを表示する。
        // スコープ内で表示するとMessageBoxがモーダルで待機する間プログレスバーが残り続ける。
        if (errorMessage != null)
        {
            _dialogService.ShowError(errorMessage, "エクスポートエラー");
        }
        else if (exportedCount.HasValue)
        {
            _dialogService.ShowInformation(
                $"Excelファイルを保存しました。\n\n出力先: {filePath}\n出力件数: {exportedCount}件",
                "エクスポート完了");
        }
    }

    /// <summary>
    /// エクスポートの監査ログを記録する。記録に失敗しても例外は伝播させない（Issue #1787）
    /// </summary>
    /// <remarks>
    /// ファイルは既に書き出し済みであり、ここでの失敗を <see cref="ExportToExcelFileAsync"/> の
    /// catch へ流すと「エクスポートに失敗しました」と通知されて職員が再実行する。
    /// CLAUDE.md（Issue #1727）の「コミット確定後の後処理を、成否の判定に巻き込まない」に従う。
    /// 記録の成否をユーザーへ通知しないのは、インポート（Issue #1741）と異なり
    /// 再実行による二重登録の危険が無く、案内すべき復旧行動が存在しないため。
    /// </remarks>
    private async Task TryLogExportAsync(string filePath, int recordCount)
    {
        try
        {
            await _operationLogger.LogExportAsync(
                OperationLogger.Tables.OperationLog, filePath, recordCount);
        }
        catch (Exception ex)
        {
            // 無言で握りつぶさない（本番のログファイルに残す必要があるため LogDebug は使わない）
            ErrorDialogHelper.LogException(ex, "操作ログエクスポートの操作ログ記録");
        }
    }

    /// <summary>
    /// エクスポートされたファイルを開く
    /// </summary>
    [RelayCommand]
    public void OpenExportedFile()
    {
        // Issue #1465: 拡張子ホワイトリスト経由で安全に起動
        var result = _safeFileLauncher.LaunchFile(LastExportedFile);
        if (!result.Success)
        {
            SetStatus(result.ErrorMessage, isError: true);
        }
    }

    /// <summary>
    /// 今日を期間に設定
    /// </summary>
    [RelayCommand]
    public void SetToday()
    {
        var today = DateTime.Today;
        FromDate = today;
        ToDate = today;
    }

    /// <summary>
    /// 今月を期間に設定
    /// </summary>
    [RelayCommand]
    public void SetThisMonth()
    {
        var today = DateTime.Today;
        FromDate = new DateTime(today.Year, today.Month, 1);
        ToDate = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
    }

    /// <summary>
    /// 先月を期間に設定
    /// </summary>
    [RelayCommand]
    public void SetLastMonth()
    {
        var lastMonth = DateTime.Today.AddMonths(-1);
        FromDate = new DateTime(lastMonth.Year, lastMonth.Month, 1);
        ToDate = new DateTime(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month));
    }

    /// <summary>
    /// 検索条件を構築
    /// </summary>
    private OperationLogSearchCriteria BuildSearchCriteria()
    {
        return new OperationLogSearchCriteria
        {
            FromDate = FromDate,
            ToDate = ToDate,
            Action = string.IsNullOrEmpty(SelectedAction?.Value) ? null : SelectedAction.Value,
            TargetTable = string.IsNullOrEmpty(SelectedTargetTable?.Value) ? null : SelectedTargetTable.Value,
            TargetId = string.IsNullOrWhiteSpace(TargetIdFilter) ? null : TargetIdFilter.Trim(),
            OperatorName = string.IsNullOrWhiteSpace(OperatorNameFilter) ? null : OperatorNameFilter.Trim()
        };
    }

    /// <summary>
    /// OperationLogを表示用DTOに変換
    /// </summary>
    private static OperationLogDisplayItem ToDisplayItem(OperationLog log)
    {
        // 詳細サマリーを生成
        var detailSummary = GenerateDetailSummary(log);
        // 対象の詳細表示名を生成
        var targetDisplayName = GenerateTargetDisplayName(log);

        return new OperationLogDisplayItem
        {
            Id = log.Id,
            Timestamp = log.Timestamp,
            Action = log.Action ?? "",
            TargetTable = log.TargetTable ?? "",
            TargetId = log.TargetId ?? "",
            TargetDisplayName = targetDisplayName,
            OperatorName = log.OperatorName,
            BeforeData = log.BeforeData,
            AfterData = log.AfterData,
            DetailSummary = detailSummary
        };
    }

    /// <summary>
    /// 対象の詳細表示名を生成（例: 「田中太郎（001）」「はやかけん 001」「R7.2.6 鉄道（博多～天神）」）
    /// </summary>
    private static string GenerateTargetDisplayName(OperationLog log)
    {
        // BeforeDataまたはAfterDataからJSONを取得（UPDATE/DELETEはBefore、INSERTはAfter）
        var jsonData = !string.IsNullOrEmpty(log.AfterData) ? log.AfterData : log.BeforeData;
        if (string.IsNullOrEmpty(jsonData))
        {
            return log.TargetId ?? "";
        }

        try
        {
            var doc = JsonDocument.Parse(jsonData);

            // Issue #1741: 一括操作（IMPORT / EXPORT / BACKUP / RESTORE、Issue #1302）の payload は
            // ファイル情報だけを持ちエンティティ項目を持たないため、テーブル別の生成へ回すと必ず空になる。
            // Action で振り分けないのは RESTORE がレコード単位復元（LogStaffRestoreAsync）と共用のため。
            // payload に FileName があるかで判定すれば、両者を取り違えない。
            var bulkOperationFileName = GetJsonPropertyValue(doc, "FileName") ?? "";
            if (bulkOperationFileName.Length > 0)
            {
                return bulkOperationFileName;
            }

            return log.TargetTable switch
            {
                "staff" => GenerateStaffDisplayName(doc),
                "ic_card" => GenerateCardDisplayName(doc),
                "ledger" => GenerateLedgerDisplayName(doc),
                _ => log.TargetId ?? ""
            };
        }
        catch
        {
            // JSON解析エラーの場合は従来のTargetIdを返す
            return log.TargetId ?? "";
        }
    }

    /// <summary>
    /// 職員の表示名を生成（例: 「田中太郎（001）」）
    /// </summary>
    private static string GenerateStaffDisplayName(JsonDocument doc)
    {
        var name = GetJsonPropertyValue(doc, "Name");
        var number = GetJsonPropertyValue(doc, "Number");

        if (string.IsNullOrEmpty(name))
        {
            return GetJsonPropertyValue(doc, "StaffIdm") ?? "";
        }

        if (!string.IsNullOrEmpty(number))
        {
            return $"{name}（{number}）";
        }

        return name;
    }

    /// <summary>
    /// カードの表示名を生成（例: 「はやかけん 001」）
    /// </summary>
    private static string GenerateCardDisplayName(JsonDocument doc)
    {
        var cardType = GetJsonPropertyValue(doc, "CardType");
        var cardNumber = GetJsonPropertyValue(doc, "CardNumber");

        if (string.IsNullOrEmpty(cardType) && string.IsNullOrEmpty(cardNumber))
        {
            return GetJsonPropertyValue(doc, "CardIdm") ?? "";
        }

        return $"{cardType ?? ""} {cardNumber ?? ""}".Trim();
    }

    /// <summary>
    /// 利用履歴の表示名を生成（例: 「R7.2.6 鉄道（博多～天神）」）
    /// </summary>
    private static string GenerateLedgerDisplayName(JsonDocument doc)
    {
        var dateStr = GetJsonPropertyValue(doc, "Date");
        var summary = GetJsonPropertyValue(doc, "Summary");

        var parts = new List<string>();

        // 日付を和暦に変換
        if (!string.IsNullOrEmpty(dateStr) && SqliteDateTimeFormat.TryParse(dateStr, out var date))
        {
            parts.Add(WarekiConverter.ToWareki(date));
        }

        // 摘要（長すぎる場合は省略）
        if (!string.IsNullOrEmpty(summary))
        {
            var displaySummary = summary.Length > 25 ? summary.Substring(0, 25) + "..." : summary;
            parts.Add(displaySummary);
        }

        return parts.Count > 0 ? string.Join(" ", parts) : GetJsonPropertyValue(doc, "Id")?.ToString() ?? "";
    }

    /// <summary>
    /// 詳細サマリーを生成
    /// </summary>
    private static string GenerateDetailSummary(OperationLog log)
    {
        var action = OperationLogDisplayNames.GetActionDisplayName(log.Action);
        var target = OperationLogDisplayNames.GetTableDisplayName(log.TargetTable);

        // UPDATE操作の場合は変更内容の詳細を表示（Issue #537）
        if (log.Action == "UPDATE" && !string.IsNullOrEmpty(log.BeforeData) && !string.IsNullOrEmpty(log.AfterData))
        {
            var changes = GetChangedFieldsDescription(log.TargetTable, log.BeforeData, log.AfterData);
            // Issue #1979: 利用明細の変化（バス停名の書き戻し等）も詳細列へ載せる。
            // 生成手段は OperationLogDetailFormatter ただ 1 つに寄せる（Excel と共通）。
            var detailChanges = OperationLogDetailFormatter.SummarizeDetailChangesForScreen(
                log.BeforeData, log.AfterData);
            var combined = string.Join("、", new[] { changes, detailChanges }
                .Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(combined))
            {
                return $"{target}を{action}: {combined}";
            }
        }

        // Issue #1979: 統合・分割は明細の振り分けが操作の本体なので件数の推移を示す。
        // 明細の内容そのものは列幅が足りないため Excel エクスポートが担う（04_機能設計書 §10.4a）。
        if (log.Action == "MERGE" || log.Action == "SPLIT")
        {
            var counts = OperationLogDetailFormatter.SummarizeDetailCountTransition(
                log.BeforeData, log.AfterData);
            if (!string.IsNullOrEmpty(counts))
            {
                return string.IsNullOrEmpty(log.TargetId)
                    ? $"{target}を{action}: {counts}"
                    : $"{target}（{log.TargetId}）を{action}: {counts}";
            }
        }

        if (string.IsNullOrEmpty(log.TargetId))
        {
            return $"{target}を{action}";
        }

        return $"{target}（{log.TargetId}）を{action}";
    }

    /// <summary>
    /// 変更されたフィールドの説明を生成（Issue #537）
    /// </summary>
    private static string GetChangedFieldsDescription(string? targetTable, string beforeJson, string afterJson)
    {
        try
        {
            var before = JsonDocument.Parse(beforeJson);
            var after = JsonDocument.Parse(afterJson);

            var changes = new List<string>();

            // テーブルごとに監視するフィールドを定義
            var fieldsToWatch = targetTable switch
            {
                "ledger" => new Dictionary<string, string>
                {
                    { "StaffName", "利用者" },
                    { "Summary", "摘要" },
                    { "Note", "備考" },
                    { "LenderIdm", "貸出者IDm" }
                },
                "staff" => new Dictionary<string, string>
                {
                    { "Name", "氏名" },
                    { "Number", "職員番号" },
                    { "Note", "備考" }
                },
                "ic_card" => new Dictionary<string, string>
                {
                    { "CardType", "カード種別" },
                    { "CardNumber", "カード番号" },
                    { "Note", "備考" }
                },
                _ => new Dictionary<string, string>()
            };

            foreach (var field in fieldsToWatch)
            {
                var beforeValue = GetJsonPropertyValue(before, field.Key);
                var afterValue = GetJsonPropertyValue(after, field.Key);

                // LenderIdmの変更は、StaffNameの変更として表示済みなのでスキップ
                if (field.Key == "LenderIdm")
                {
                    continue;
                }

                if (beforeValue != afterValue)
                {
                    var beforeDisplay = string.IsNullOrEmpty(beforeValue) ? "（なし）" : beforeValue;
                    var afterDisplay = string.IsNullOrEmpty(afterValue) ? "（なし）" : afterValue;

                    // 長すぎる値は省略
                    if (beforeDisplay.Length > 30) beforeDisplay = beforeDisplay.Substring(0, 30) + "...";
                    if (afterDisplay.Length > 30) afterDisplay = afterDisplay.Substring(0, 30) + "...";

                    changes.Add($"{field.Value}: {beforeDisplay}→{afterDisplay}");
                }
            }

            return string.Join("、", changes);
        }
        catch
        {
            // JSON解析エラーの場合は空文字列を返す
            return string.Empty;
        }
    }

    /// <summary>
    /// JSONドキュメントからプロパティ値を取得
    /// </summary>
    private static string? GetJsonPropertyValue(JsonDocument doc, string propertyName)
    {
        if (doc.RootElement.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            return prop.ToString();
        }
        return null;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }

}
