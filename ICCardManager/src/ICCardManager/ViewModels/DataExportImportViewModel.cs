#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ICCardManager.Common;
using ICCardManager.Common.Messages;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Win32;


namespace ICCardManager.ViewModels;

/// <summary>
/// データタイプの選択肢
/// </summary>
public enum DataType
{
    Cards,
    Staff,
    Ledgers,
    LedgerDetails
}

/// <summary>
/// DataType enumの表示名変換コンバーター
/// </summary>
public class DataTypeToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DataType dataType)
        {
            return dataType switch
            {
                DataType.Cards => "カード一覧",
                DataType.Staff => "職員一覧",
                DataType.Ledgers => "利用履歴",
                DataType.LedgerDetails => "利用履歴詳細",
                _ => dataType.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// ImportAction enumの表示名変換コンバーター
/// </summary>
public class ImportActionToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Services.ImportAction action)
        {
            return action switch
            {
                Services.ImportAction.Insert => "追加",
                Services.ImportAction.Update => "修正",
                Services.ImportAction.Skip => "スキップ",
                Services.ImportAction.Restore => "復元",
                _ => action.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// データエクスポート/インポートViewModel
/// </summary>
public partial class DataExportImportViewModel : ViewModelBase
{
    private readonly CsvExportService _exportService;
    private readonly CsvImportService _importService;
    private readonly IDialogService _dialogService;
    private readonly ICardRepository _cardRepository;
    private readonly ICardReader? _cardReader;
    private readonly OperationLogger _operationLogger;
    private readonly IMessenger _messenger;
    /// <summary>
    /// UI スレッドへのディスパッチ。Issue #1843: 生の <c>Dispatcher.InvokeAsync</c> は
    /// <c>DispatcherOperation&lt;Task&gt;</c> を返すため内側の <c>Task</c> の例外を観測できない。
    /// この実装（<c>WpfDispatcherService</c>）は <c>Unwrap()</c> して観測しログへ残す（Issue #1725）。
    /// </summary>
    private readonly IDispatcherService _dispatcherService;
    private readonly ISafeFileLauncher _safeFileLauncher;
    private readonly IStaffAuthService _staffAuthService;
    private readonly StaffManagementService _staffManagementService;

    [ObservableProperty]
    private DataType _selectedExportType = DataType.Cards;

    /// <summary>
    /// インポートするデータ種別
    /// </summary>
    /// <remarks>
    /// Issue #2262: 「直接インポート」を使えるかはこの種別で決まる（<see cref="CanImportDirectly"/>）ため、
    /// 変わったらコマンドの実行可否を通知する。
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImportDirectly))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private DataType _selectedImportType = DataType.Cards;

    [ObservableProperty]
    private DateTime _exportStartDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);

    [ObservableProperty]
    private DateTime _exportEndDate = DateTime.Now;

    [ObservableProperty]
    private bool _includeDeletedInExport;

    [ObservableProperty]
    private bool _skipExistingOnImport = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private ObservableCollection<string> _importErrors = new();

    [ObservableProperty]
    private string _lastExportedFile = string.Empty;

    [ObservableProperty]
    private string _lastImportedFile = string.Empty;

    [ObservableProperty]
    private string _importPreviewFile = string.Empty;

    [ObservableProperty]
    private CsvImportPreviewResult? _importPreview;

    [ObservableProperty]
    private ObservableCollection<CsvImportPreviewItem> _previewItems = new();

    [ObservableProperty]
    private bool _hasPreview;

    [ObservableProperty]
    private string _previewSummary = string.Empty;

    // Issue #511: カード選択機能
    /// <summary>
    /// 利用履歴インポート用: 登録済みカード一覧（ドロップダウン用）
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<CardDto> _availableCards = new();

    /// <summary>
    /// 利用履歴インポート用: 選択されたインポート先カード
    /// </summary>
    [ObservableProperty]
    private CardDto? _selectedTargetCard;

    /// <summary>
    /// 利用履歴インポート用: カード指定方法（true=一覧から選択、false=カードタッチ）
    /// </summary>
    [ObservableProperty]
    private bool _useCardListSelection = true;

    /// <summary>
    /// 利用履歴インポート用: カードリーダーが利用可能か
    /// </summary>
    [ObservableProperty]
    private bool _isCardReaderAvailable;

    /// <summary>
    /// 利用履歴インポート用: タッチで読み取ったカードのIDm
    /// </summary>
    [ObservableProperty]
    private string _touchedCardIdm = string.Empty;

    /// <summary>
    /// 利用履歴インポート用: タッチで読み取ったカードの情報表示
    /// </summary>
    [ObservableProperty]
    private string _touchedCardInfo = string.Empty;

    /// <summary>
    /// 利用履歴インポート用: カードタッチ待機中かどうか
    /// </summary>
    [ObservableProperty]
    private bool _isWaitingForCardTouch;

    /// <summary>
    /// インポートが実行されたか（Issue #744: ダイアログ閉じた後のリフレッシュ判定用）
    /// </summary>
    public bool HasImported { get; private set; }

    /// <summary>
    /// エクスポート用データタイプの選択肢
    /// </summary>
    public DataType[] ExportDataTypes { get; } = { DataType.Cards, DataType.Staff, DataType.Ledgers, DataType.LedgerDetails };

    /// <summary>
    /// インポート用データタイプの選択肢
    /// </summary>
    public DataType[] ImportDataTypes { get; } = { DataType.Cards, DataType.Staff, DataType.Ledgers, DataType.LedgerDetails };

    /// <summary>
    /// データタイプの表示名を取得
    /// </summary>
    public static string GetDataTypeDisplayName(DataType dataType)
    {
        return dataType switch
        {
            DataType.Cards => "カード一覧",
            DataType.Staff => "職員一覧",
            DataType.Ledgers => "利用履歴",
            DataType.LedgerDetails => "利用履歴詳細",
            _ => dataType.ToString()
        };
    }

    /// <summary>
    /// 履歴エクスポートが選択されているか（日付範囲UIの表示制御用）
    /// </summary>
    public bool IsLedgerExportSelected => SelectedExportType == DataType.Ledgers || SelectedExportType == DataType.LedgerDetails;

    /// <summary>
    /// 利用履歴インポートが選択されているか（カード指定UIの表示制御用）
    /// </summary>
    public bool IsLedgerImportSelected => SelectedImportType == DataType.Ledgers;

    /// <summary>
    /// 「直接インポート」（プレビューを経ない取り込み）を使えるか（Issue #2262）
    /// </summary>
    /// <remarks>
    /// <see cref="ImportCommand"/> の実行可否と、ボタンの有効・無効を兼ねる。判定は <see cref="IsDirectImportAllowed"/>。
    /// </remarks>
    public bool CanImportDirectly => IsDirectImportAllowed(SelectedImportType);

    /// <summary>
    /// そのデータ種別を「直接インポート」で取り込めるか（Issue #2262）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 直接インポートできるのは交通系ICカード（カード一覧）だけで、しかも新しいカードの登録に限る
    /// （<see cref="CsvImportService.ImportNewCardsAsync"/>。登録済みのカードを変更・復元する行があれば取り込まない）。
    /// </para>
    /// <para>
    /// 職員・利用履歴・利用履歴詳細の取り込みは、職員の登録・変更・復元や、6 年保存の台帳の追加・変更・明細の置き換えを
    /// 行い得る。どれも職員管理画面・履歴画面では職員証の認証を求める操作で、取り込みも認証を経て行う。認証する前に
    /// 「何が変わるか」をプレビューで確かめさせるため、直接インポートを使えなくする（以前はプレビューを経ずに書き換えられた）。
    /// </para>
    /// </remarks>
    internal static bool IsDirectImportAllowed(DataType dataType) => dataType == DataType.Cards;

    /// <summary>
    /// 直接インポートを使えないデータ種別で、直接インポートが要求されたときの案内（Issue #2262）
    /// </summary>
    /// <remarks>
    /// ボタンはコマンドの実行可否で無効になるため、通常は表示されない（アクセスキー等でコマンドが実行された場合の受け皿）。
    /// </remarks>
    internal const string DirectImportRequiresPreviewMessage =
        "職員一覧・利用履歴・利用履歴詳細は「直接インポート」では取り込めません。取り込みには職員証の認証が必要なため、" +
        "「プレビュー」で内容を確認してから「インポート実行」を押してください。";

    /// <summary>
    /// プレビューDataGridの列1ヘッダー（データ種別に応じて変化）
    /// </summary>
    public string PreviewColumn1Header => SelectedImportType switch
    {
        DataType.LedgerDetails => "利用履歴ID",
        DataType.Ledgers => "カード",
        _ => "IDm"
    };

    /// <summary>
    /// プレビューDataGridの列2ヘッダー（データ種別に応じて変化）
    /// </summary>
    public string PreviewColumn2Header => SelectedImportType switch
    {
        DataType.LedgerDetails => "カード",
        DataType.Ledgers => "摘要",
        DataType.Cards => "カード種別",
        DataType.Staff => "氏名",
        _ => "名前"
    };

    /// <summary>
    /// プレビューDataGridの列3ヘッダー（データ種別に応じて変化）
    /// </summary>
    public string PreviewColumn3Header => SelectedImportType switch
    {
        DataType.LedgerDetails => "詳細件数",
        DataType.Ledgers => "日付",
        DataType.Cards => "管理番号",
        DataType.Staff => "職員番号",
        _ => "追加情報"
    };

    /// <summary>
    /// Issue #1302: 監査ログ用に DataType を operation_log.target_table の値へ変換
    /// </summary>
    private static string MapDataTypeToTableName(DataType dataType) => dataType switch
    {
        DataType.Cards => OperationLogger.Tables.IcCard,
        DataType.Staff => OperationLogger.Tables.Staff,
        DataType.Ledgers => OperationLogger.Tables.Ledger,
        DataType.LedgerDetails => OperationLogger.Tables.LedgerDetail,
        _ => "unknown"
    };

    /// <summary>
    /// インポート時に使用するターゲットカードIDmを取得
    /// </summary>
    /// <returns>選択またはタッチされたカードのIDm。未選択の場合はnull</returns>
    private string? GetTargetCardIdm()
    {
        if (SelectedImportType != DataType.Ledgers)
        {
            return null;
        }

        if (UseCardListSelection)
        {
            return SelectedTargetCard?.CardIdm;
        }
        else
        {
            return string.IsNullOrWhiteSpace(TouchedCardIdm) ? null : TouchedCardIdm;
        }
    }

    public DataExportImportViewModel(
        CsvExportService exportService,
        CsvImportService importService,
        IDialogService dialogService,
        ICardRepository cardRepository,
        OperationLogger operationLogger,
        IMessenger messenger,
        ISafeFileLauncher safeFileLauncher,
        IDispatcherService dispatcherService,
        IStaffAuthService staffAuthService,
        StaffManagementService staffManagementService,
        ICardReader? cardReader = null)
    {
        _exportService = exportService;
        _importService = importService;
        _dialogService = dialogService;
        _cardRepository = cardRepository;
        _operationLogger = operationLogger;
        _messenger = messenger;
        _safeFileLauncher = safeFileLauncher;
        _dispatcherService = dispatcherService;
        // Issue #2262: 職員・利用履歴・利用履歴詳細の取り込みは職員証の認証を経て行う。既定値を持たない必須引数にする
        _staffAuthService = staffAuthService ?? throw new ArgumentNullException(nameof(staffAuthService));
        _staffManagementService = staffManagementService ?? throw new ArgumentNullException(nameof(staffManagementService));
        _cardReader = cardReader;

        // カードリーダーイベント購読
        if (_cardReader != null)
        {
            _cardReader.CardRead += OnCardRead;
        }
    }

    /// <summary>
    /// Issue #1514: カードタッチ待機状態が変化した際に、
    /// MainViewModel 側の OnCardRead が二重起動するのを防ぐため
    /// CardReadingSuppressedMessage を発信する。
    /// </summary>
    /// <remarks>
    /// 待機開始 (true) で抑制 ON、待機終了 (false) で抑制 OFF を送信する。
    /// CancelCardTouch・OnCardRead・ClearTargetCard・Cleanup・例外経路など
    /// IsWaitingForCardTouch が false に戻るすべての経路で
    /// 一律に抑制解除が走るようにここに集約している。
    /// </remarks>
    partial void OnIsWaitingForCardTouchChanged(bool value)
    {
        _messenger.Send(new CardReadingSuppressedMessage(value, CardReadingSource.DataImport));
    }

    /// <summary>
    /// 初期化（登録済みカード一覧の読み込み）
    /// </summary>
    public async Task InitializeAsync()
    {
        // 登録済みカード一覧を取得
        var cards = await _cardRepository.GetAllAsync();
        AvailableCards.Clear();
        foreach (var card in cards.OrderByCardDefault(c => c.CardType, c => c.CardNumber))
        {
            AvailableCards.Add(card.ToDto());
        }

        // カードリーダーが利用可能か確認
        IsCardReaderAvailable = _cardReader != null &&
                                _cardReader.ConnectionState == CardReaderConnectionState.Connected;
    }

    partial void OnSelectedExportTypeChanged(DataType value)
    {
        OnPropertyChanged(nameof(IsLedgerExportSelected));
    }

    partial void OnSelectedImportTypeChanged(DataType value)
    {
        OnPropertyChanged(nameof(IsLedgerImportSelected));
        OnPropertyChanged(nameof(PreviewColumn1Header));
        OnPropertyChanged(nameof(PreviewColumn2Header));
        OnPropertyChanged(nameof(PreviewColumn3Header));

        // 利用履歴以外が選択された場合、カード指定をクリア
        if (value != DataType.Ledgers)
        {
            SelectedTargetCard = null;
            TouchedCardIdm = string.Empty;
            TouchedCardInfo = string.Empty;
            IsWaitingForCardTouch = false;
        }
    }

    /// <summary>
    /// エクスポートを実行
    /// </summary>
    [RelayCommand]
    public async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName = GetDefaultExportFileName()
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExportToFileAsync(dialog.FileName);
    }

    /// <summary>
    /// 指定ファイルへのエクスポート本体。<see cref="ExportAsync"/> からファイル選択後に呼ばれるほか、
    /// ユニットテストから <see cref="SaveFileDialog"/> を介さずに実行する経路としても使用する。
    /// </summary>
    internal async Task ExportToFileAsync(string filePath)
    {
        // Issue #2111: 対象データ種別を最初の await より前にローカル変数へ確定させる。
        // データ種別のコンボボックスは処理中オーバーレイがあってもキーボードで操作できるため、
        // 出力後にプロパティを読み直すと、監査ログへ実際とは異なる対象テーブルが記録される（インポート側の Issue #1741 と同じ罠）。
        var exportType = SelectedExportType;
        CsvExportResult? result = null;
        using (BeginBusy("エクスポート中..."))
        {
            // Issue #1062: UIスレッドにプログレスバー描画の機会を与える
            await Task.Yield();
            try
            {
                switch (exportType)
                {
                    case DataType.Cards:
                        result = await _exportService.ExportCardsAsync(filePath, IncludeDeletedInExport);
                        break;

                    case DataType.Staff:
                        result = await _exportService.ExportStaffAsync(filePath, IncludeDeletedInExport);
                        break;

                    case DataType.Ledgers:
                        result = await _exportService.ExportLedgersAsync(filePath, ExportStartDate, ExportEndDate);
                        break;

                    case DataType.LedgerDetails:
                        result = await _exportService.ExportLedgerDetailsAsync(filePath, ExportStartDate, ExportEndDate);
                        break;

                    default:
                        SetStatus("不正なデータタイプです", true);
                        return;
                }

                if (result.Success)
                {
                    LastExportedFile = result.FilePath;
                    SetStatus($"エクスポート完了: {result.ExportedCount}件を出力しました", false);

                    // Issue #1302: 監査ログ記録
                    // Issue #2111: 記録の失敗を出力の失敗として通知しない（TryLogExportAsync の remarks 参照）
                    await TryLogExportAsync(MapDataTypeToTableName(exportType), result);
                }
                else
                {
                    SetStatus($"エクスポートエラー: {result.ErrorMessage}", true);
                }
            }
            catch (Exception ex)
            {
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "エクスポート");
                SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "エクスポート"), true);
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Export Error] {ex.GetType().Name}: {ex.Message}");
#endif
                return;
            }
        }

        // Issue #1383: BeginBusyスコープを抜けてIsBusy=falseが確定した後にダイアログを表示する。
        // スコープ内で表示するとMessageBoxがモーダルで待機する間プログレスバーが残り続ける。
        if (result == null)
        {
            return;
        }
        if (result.Success)
        {
            // Issue #512: 保存完了メッセージを表示
            _dialogService.ShowInformation(
                $"CSVファイルを保存しました。\n\n出力先: {result.FilePath}\n出力件数: {result.ExportedCount}件",
                "エクスポート完了");
        }
        else
        {
            // Issue #1991: CsvExportService の失敗結果は ToFailureResult に集約され、
            // ErrorMessage が必ず「〇〇のエクスポートに失敗しました。…」と「何が」を述べる。
            // ここで「エクスポートに失敗しました。」を前置すると同じことを 2 度述べることになる
            // （「何が」の二重化。#1991 が Detail.cs で ToReason へ寄せたのと同じ理由）。
            // 「何が」の総称はダイアログのタイトルが担う。
            _dialogService.ShowError(result.ErrorMessage, "エクスポートエラー");
        }
    }

    /// <summary>
    /// インポートプレビューを実行
    /// </summary>
    [RelayCommand]
    public async Task PreviewImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ClearPreview();
        ImportErrors.Clear();

        using (BeginBusy("プレビュー読み込み中..."))
        {
            // Issue #1062: UIスレッドにプログレスバー描画の機会を与える
            await Task.Yield();
            try
            {
                CsvImportPreviewResult preview;

                switch (SelectedImportType)
                {
                    case DataType.Cards:
                        preview = await _importService.PreviewCardsAsync(dialog.FileName, SkipExistingOnImport);
                        break;

                    case DataType.Staff:
                        preview = await _importService.PreviewStaffAsync(dialog.FileName, SkipExistingOnImport);
                        break;

                    case DataType.Ledgers:
                        // Issue #511: ターゲットカードIDmを渡す
                        preview = await _importService.PreviewLedgersAsync(dialog.FileName, SkipExistingOnImport, GetTargetCardIdm());
                        break;

                    case DataType.LedgerDetails:
                        preview = await _importService.PreviewLedgerDetailsAsync(dialog.FileName);
                        break;

                    default:
                        SetStatus("不正なデータタイプです", true);
                        return;
                }

                ImportPreviewFile = dialog.FileName;
                ImportPreview = preview;

                if (!string.IsNullOrEmpty(preview.ErrorMessage))
                {
                    SetStatus($"プレビューエラー: {preview.ErrorMessage}", true);
                    return;
                }

                // プレビューアイテムを設定
                PreviewItems.Clear();
                foreach (var item in preview.Items)
                {
                    PreviewItems.Add(item);
                }

                // エラー詳細を追加
                foreach (var error in preview.Errors.Take(10))
                {
                    ImportErrors.Add($"行{error.LineNumber}: {error.Message}");
                }

                if (preview.Errors.Count > 10)
                {
                    ImportErrors.Add($"... 他 {preview.Errors.Count - 10}件のエラー");
                }

                // プレビューサマリを設定
                var summaryParts = new List<string>();
                if (preview.NewCount > 0)
                {
                    summaryParts.Add($"新規 {preview.NewCount}件");
                }

                if (preview.UpdateCount > 0)
                {
                    summaryParts.Add($"更新 {preview.UpdateCount}件");
                }

                if (preview.SkipCount > 0)
                {
                    summaryParts.Add($"スキップ {preview.SkipCount}件");
                }

                if (preview.ErrorCount > 0)
                {
                    summaryParts.Add($"エラー {preview.ErrorCount}件");
                }

                PreviewSummary = string.Join("、", summaryParts);
                HasPreview = true;

                if (preview.IsValid)
                {
                    SetStatus("プレビューを確認して「インポート実行」ボタンを押してください", false);
                }
                else
                {
                    SetStatus($"バリデーションエラーがあります。{preview.ErrorCount}件のエラーを修正してください", true);
                }
            }
            catch (Exception ex)
            {
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "インポートプレビュー");
                SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "インポートのプレビュー"), true);
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Preview Error] {ex.GetType().Name}: {ex.Message}");
#endif
            }
        }
    }

    /// <summary>
    /// インポートを実行（プレビュー確認後）
    /// </summary>
    [RelayCommand]
    public async Task ExecuteImportAsync()
    {
        if (ImportPreview == null || string.IsNullOrEmpty(ImportPreviewFile))
        {
            SetStatus("先にプレビューを実行してください", true);
            return;
        }

        if (!ImportPreview.IsValid)
        {
            SetStatus("バリデーションエラーがあります。修正後に再度プレビューしてください", true);
            return;
        }

        // Issue #1741: インポート元パスをここで確定させて引数として渡す。
        // ImportPreviewFile は画面の一時状態で、取り込み後の ClearPreview() が空にするため、
        // 取り込み後に読み直すと監査ログへ空パスが記録される。
        await RunImportAsync(ImportPreviewFile);
    }

    /// <summary>
    /// インポート本体。プレビュー経由（<see cref="ExecuteImportAsync"/>）と
    /// 直接インポート（<see cref="ImportAsync"/>）の共通処理（Issue #1785）
    /// </summary>
    /// <param name="sourceFilePath">
    /// 取り込む CSV のパス。呼び出し元で確定済みの値を渡すこと（Issue #1741）。
    /// 画面状態のプロパティを本メソッド内で読み直してはならない。
    /// </param>
    /// <remarks>
    /// 2つのコマンドは差分8行の全文複製（約115行）で、結果処理・監査記録に関わる修正を
    /// 毎回2箇所へ適用する必要があった。Issue #1741 では実際に一方の経路だけが壊れており、
    /// 複製側が無傷であることを別途確認する作業が発生した。
    /// インポートの分岐を増やすときは必ずここへ書き、コマンド側へ複製しないこと。
    /// テストから直接呼べるよう internal にしている（<see cref="ImportAsync"/> は
    /// <see cref="OpenFileDialog"/> をコマンド内で生成するため単体テストから起動できない）。
    /// <para>
    /// Issue #2262: 職員・利用履歴・利用履歴詳細の取り込みは、職員証の認証を経て行う（職員一覧は登録済みの職員が
    /// 0 人なら免除）。認証が完了しなければ何も書き込まず、プレビューを残したまま案内する。
    /// 処理中の表示・サービスの呼び分け・結果の通知は <see cref="ImportAndReportAsync"/> が持つ。
    /// </para>
    /// </remarks>
    /// <param name="directImport">
    /// 直接インポート（<see cref="ImportAsync"/>。プレビューを経ない）なら <c>true</c>。交通系ICカードの
    /// 新しいカードの登録だけに限る（<see cref="IsDirectImportAllowed"/>。Issue #2262）
    /// </param>
    internal async Task RunImportAsync(string sourceFilePath, bool directImport = false)
    {
        // Issue #2262: 取り込みの条件は認証より前に確定させる。認証ダイアログ（職員証タッチ待ち）の間も
        // UI スレッドは入力を処理するため、認証の後で画面のプロパティを読み直さない（ui-conventions「選択状態のスナップショット」）。
        // Issue #1741: 対象データ種別も同じ理由でここで確定させる（監査ログの対象テーブルが実際と食い違わないように）。
        var request = new ImportRequest(
            sourceFilePath,
            SelectedImportType,
            SkipExistingOnImport,
            GetTargetCardIdm(),
            directImport);

        ImportErrors.Clear();

        // Issue #1783: 「インポート完了: <ファイル名>」（DataExportImportDialog.xaml の LastImportedFile 表示）は
        // 直前の操作の結果を表す欄であり、次の取り込みを始めた時点で前回の結果は無効になる。
        // 開始時にクリアしないと、前回成功したファイル名が失敗通知の隣に残り、
        // 同じファイルを直して取り込み直して失敗した場合には同一ファイル名で
        // 「インポートに失敗しました」と「インポート完了: 同じファイル」が同居する。
        // 例外で中断する経路（共有フォルダーの切断等）も同じ経路でまとめて塞げる。
        LastImportedFile = string.Empty;

        if (directImport && !IsDirectImportAllowed(request.DataType))
        {
            SetStatus(DirectImportRequiresPreviewMessage, true);
            return;
        }

        // Issue #2262: 職員・利用履歴・利用履歴詳細の取り込みは、職員証の認証を経て、認証した職員の操作として行う。
        // 認証ダイアログは処理中（BeginBusy）の外で出す（背後に処理中の表示を出さない）。取り込み全体
        // （1 行ごとの操作ログ・取り込み全体の操作ログを含む）が認証した職員のスコープの内側で行われる。
        bool authorized;
        switch (request.DataType)
        {
            case DataType.Staff:
                var requiresAuthentication = await TryDetermineStaffImportRequiresAuthenticationAsync();
                if (requiresAuthentication == null)
                {
                    // 判定に失敗した（案内は表示済み）。何も書き込まない
                    return;
                }

                authorized = await RunStaffImportWithAuthenticationAsync(
                    requiresAuthentication.Value, () => ImportAndReportAsync(request));
                break;

            case DataType.Ledgers:
                authorized = await RunLedgerImportWithAuthenticationAsync(() => ImportAndReportAsync(request));
                break;

            case DataType.LedgerDetails:
                authorized = await RunLedgerDetailImportWithAuthenticationAsync(() => ImportAndReportAsync(request));
                break;

            default:
                // 交通系ICカードは認証を求めない（交通系ICカード管理画面の登録・編集・復元と同じ。04 §9）
                await ImportAndReportAsync(request);
                authorized = true;
                break;
        }

        if (!authorized)
        {
            // 何も書き込んでいない。プレビューは残すので、もう一度「インポート実行」を押せば取り込める
            SetStatus(BuildImportAuthenticationCancelledMessage(request.DataType), true);
        }
    }

    /// <summary>
    /// 取り込みの条件（認証の前に確定させた値。Issue #2262）
    /// </summary>
    private sealed class ImportRequest
    {
        public ImportRequest(string sourceFilePath, DataType dataType, bool skipExisting, string? targetCardIdm, bool directImport)
        {
            SourceFilePath = sourceFilePath;
            DataType = dataType;
            SkipExisting = skipExisting;
            TargetCardIdm = targetCardIdm;
            DirectImport = directImport;
        }

        /// <summary>取り込む CSV のパス（呼び出し元で確定済み。Issue #1741）</summary>
        public string SourceFilePath { get; }

        /// <summary>取り込むデータ種別</summary>
        public DataType DataType { get; }

        /// <summary>既存データをスキップするか</summary>
        public bool SkipExisting { get; }

        /// <summary>利用履歴の取り込み先カード（Issue #511。利用履歴以外は null）</summary>
        public string? TargetCardIdm { get; }

        /// <summary>直接インポート（プレビューを経ない）か</summary>
        public bool DirectImport { get; }
    }

    /// <summary>
    /// 職員の取り込みに職員証の認証を求めるかを判定する（Issue #2262）
    /// </summary>
    /// <returns>求めるなら <c>true</c>、登録済みの職員が 0 人なら <c>false</c>、判定に失敗したら <c>null</c>（案内は表示済み）</returns>
    /// <remarks>
    /// 免除の判定は職員管理画面と同じ <see cref="StaffManagementService.RequiresOperatorAuthenticationAsync"/> に寄せる
    /// （Issue #2263。初回セットアップで職員一覧の CSV を取り込むとき、認証できる職員証がまだ無い）。
    /// DB を待つ間は処理中にする（処理中の外で DB を待つと、その間に画面を操作できてしまう）。
    /// </remarks>
    private async Task<bool?> TryDetermineStaffImportRequiresAuthenticationAsync()
    {
        using (BeginBusy("確認中..."))
        {
            try
            {
                return await _staffManagementService.RequiresOperatorAuthenticationAsync();
            }
            catch (Exception ex)
            {
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "インポート");
                SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "インポート"), true);
                return null;
            }
        }
    }

    /// <summary>
    /// 職員の取り込みを、操作者の職員証認証を経て行う（Issue #2262）
    /// </summary>
    /// <param name="requiresAuthentication">認証を求めるか（登録済みの職員が 0 人なら <c>false</c>）</param>
    /// <param name="import">認証した操作の中で行う取り込みと結果の表示</param>
    /// <returns><paramref name="import"/> を実行したら <c>true</c>。認証が完了しなかったら <c>false</c></returns>
    /// <remarks>
    /// <para>
    /// 認証結果の <c>Enter()</c> はこのメソッドの流れで操作者セッションを開始し、<paramref name="import"/> は
    /// その流れの内側で実行されるため、取り込みの操作ログには認証した職員が記録される（Issue #2260）。
    /// </para>
    /// <para>
    /// データ種別ごとに 3 つに分けているのは、操作名を <c>RequestAuthenticationAsync</c> へ文字列リテラルで渡すため
    /// （静的検査がリテラルを数える。Issue #2255）。
    /// </para>
    /// </remarks>
    private async Task<bool> RunStaffImportWithAuthenticationAsync(bool requiresAuthentication, Func<Task> import)
    {
        if (!requiresAuthentication)
        {
            // 初回セットアップ（登録済みの職員が 0 人）。認証できる職員証がまだ無いため免除し、
            // 操作ログは「GUI操作」として記録される（職員管理画面の登録と同じ。Issue #2263）
            await import();
            return true;
        }

        using var authResult = (await _staffAuthService.RequestAuthenticationAsync("職員のインポート"))?.Enter();
        if (authResult == null)
        {
            return false;
        }

        await import();
        return true;
    }

    /// <summary>
    /// 利用履歴の取り込みを、操作者の職員証認証を経て行う（Issue #2262。
    /// 詳細は <see cref="RunStaffImportWithAuthenticationAsync"/>）
    /// </summary>
    /// <remarks>
    /// 新しい行を追加するだけの取り込みでも認証を求める。履歴画面の「追加」が認証を求める操作だから（SEQ-AUTH-01）。
    /// </remarks>
    private async Task<bool> RunLedgerImportWithAuthenticationAsync(Func<Task> import)
    {
        using var authResult = (await _staffAuthService.RequestAuthenticationAsync("利用履歴のインポート"))?.Enter();
        if (authResult == null)
        {
            return false;
        }

        await import();
        return true;
    }

    /// <summary>
    /// 利用履歴詳細の取り込みを、操作者の職員証認証を経て行う（Issue #2262。
    /// 詳細は <see cref="RunStaffImportWithAuthenticationAsync"/>）
    /// </summary>
    /// <remarks>
    /// 利用履歴詳細の取り込みは、既存の履歴の明細を置き換えて摘要・金額を作り直すか、新しい履歴を作る。
    /// どちらも履歴画面では認証を求める操作（明細の保存・履歴の追加）に当たる。
    /// </remarks>
    private async Task<bool> RunLedgerDetailImportWithAuthenticationAsync(Func<Task> import)
    {
        using var authResult = (await _staffAuthService.RequestAuthenticationAsync("利用履歴詳細のインポート"))?.Enter();
        if (authResult == null)
        {
            return false;
        }

        await import();
        return true;
    }

    /// <summary>
    /// 取り込みで職員証の認証が完了しなかったときの案内を組み立てる（Issue #2262）
    /// </summary>
    internal static string BuildImportAuthenticationCancelledMessage(DataType dataType)
        => $"職員証の認証が完了しなかったため、{GetDataTypeDisplayName(dataType)}を取り込んでいません。プレビューはそのままです。" +
           "取り込むには、もう一度「インポート実行」を押し、登録済みの職員の職員証で認証してください。";

    /// <summary>
    /// 取り込みの本体（処理中の表示・サービスの呼び分け・結果の反映と通知）
    /// </summary>
    /// <param name="request">認証の前に確定させた取り込みの条件</param>
    private async Task ImportAndReportAsync(ImportRequest request)
    {
        // Issue #1784: 結果ダイアログは BeginBusy スコープを抜けて IsBusy=false が確定した後に表示する。
        // スコープ内で表示すると MessageBox がモーダルで待機する間、
        // DataExportImportDialog.xaml の全面オーバーレイ（Grid.RowSpan=6・不透明）と不確定 ProgressBar が
        // 「インポートが完了しました」の背後で回り続け、処理が終わったのか職員が判断できない。
        // Issue #1383 がエクスポート経路（ExportToFileAsync）で解消した不具合のインポート版。
        //
        // 表示処理そのものを遅延させる（結果オブジェクトを持ち回すのではなく Action として持つ）ことで、
        // 分岐ごとに組み立てた文言をその場に残したまま、ダイアログを出す地点を1か所へ集約できる。
        // 分岐が増えても「文言の組み立て」と「表示のタイミング」が離れないため、
        // 新しい分岐だけスコープ内で表示する形の再発を構造的に防ぐ。
        Action? pendingResultDialog = null;

        using (BeginBusy("インポート中..."))
        {
            // Issue #1062: UIスレッドにプログレスバー描画の機会を与える
            await Task.Yield();
            try
            {
                CsvImportResult result;
                var sourceFilePath = request.SourceFilePath;

                // Issue #1741: 対象データ種別は呼び出し元（RunImportAsync）が最初の await より前に確定させている。
                // SelectedImportType は await 中のキー操作（データ種別コンボのアクセスキー）で変わり得るため、
                // 監査ログ記録時にプロパティを読み直すと実際とは異なる対象テーブルが記録される。
                var importTargetTable = MapDataTypeToTableName(request.DataType);

                switch (request.DataType)
                {
                    case DataType.Cards:
                        // Issue #2262: 直接インポートは新しいカードの登録だけ（登録済みのカードの変更・復元はプレビュー経由に限る）
                        result = request.DirectImport
                            ? await _importService.ImportNewCardsAsync(sourceFilePath)
                            : await _importService.ImportCardsAsync(sourceFilePath, request.SkipExisting);
                        break;

                    case DataType.Staff:
                        result = await _importService.ImportStaffAsync(sourceFilePath, request.SkipExisting);
                        break;

                    case DataType.Ledgers:
                        // Issue #511: ターゲットカードIDmを渡す
                        result = await _importService.ImportLedgersAsync(sourceFilePath, request.SkipExisting, request.TargetCardIdm);
                        break;

                    case DataType.LedgerDetails:
                        result = await _importService.ImportLedgerDetailsAsync(sourceFilePath);
                        break;

                    default:
                        SetStatus("不正なデータタイプです", true);
                        return;
                }

                // Issue #744: インポート実行済みフラグを設定（ダイアログ終了後のリフレッシュ用）
                if (result.ImportedCount > 0)
                {
                    HasImported = true;
                }

                pendingResultDialog = await HandleImportResultAsync(result, sourceFilePath, importTargetTable);
            }
            catch (Exception ex)
            {
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "インポート");
                var importErrorMessage = ExceptionMessageFormatter.ToUserMessage(ex, "インポート");
                SetStatus(importErrorMessage, true);
                pendingResultDialog = () => _dialogService.ShowError(importErrorMessage, "インポートエラー");
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Import Error] {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[Import Error] StackTrace: {ex.StackTrace}");
#endif
            }
        }

        // Issue #1784: ここが結果ダイアログを表示する唯一の地点。IsBusy=false が確定済み。
        // データ種別が不正な分岐は using の内側で return するため、ここには到達せず
        // ダイアログも表示しない（ExportToFileAsync の default 分岐と同じ扱い）。
        //
        // 表示処理は try の外側にあるため、ここを素通しにすると通知の失敗が
        // AsyncRelayCommand 経由の未捕捉例外になる。この時点で取り込みは既にコミットされ
        // 監査ログも記録済みのため、職員は成功直後にクラッシュを見て「データが入ったのか」を
        // 判断できない（Issue #1783 が消したはずの曖昧さの再発）。
        // 「コミット確定後の後処理を、成否の判定に巻き込まない」（Issue #1727）に従い、
        // 表示の失敗はログへ逃がす。結果は SetStatus 済みでステータス欄に残るため情報は失われない。
        try
        {
            pendingResultDialog?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.LogException(ex, "インポート結果の通知");
        }
    }

    /// <summary>
    /// インポート結果（成功／失敗／部分成功）の画面反映・監査ログ記録を行い、
    /// 結果ダイアログの表示処理を呼び出し元へ返す（Issue #1785）
    /// </summary>
    /// <remarks>
    /// Issue #1784: 本メソッドはダイアログを<b>表示しない</b>。呼び出し元
    /// （<see cref="RunImportAsync"/>）が <c>BeginBusy</c> スコープを抜けてから実行する。
    /// ステータス文言・エラー一覧・プレビュー破棄といった画面状態の更新はダイアログより先に
    /// 反映させたいため、ここに残してある。分岐を追加するときは
    /// <c>_dialogService.ShowXxx</c> を直接呼ばず、戻り値の <see cref="Action"/> に載せること。
    /// </remarks>
    /// <param name="result">インポートサービスの実行結果</param>
    /// <param name="sourceFilePath">監査ログへ記録するインポート元パス（確定済みの値）</param>
    /// <param name="targetTable">監査ログへ記録する対象テーブル（確定済みの値）</param>
    /// <returns>スコープを抜けた後に実行する結果ダイアログの表示処理</returns>
    private async Task<Action> HandleImportResultAsync(
        CsvImportResult result,
        string sourceFilePath,
        string targetTable)
    {
        // Issue #1781: プレビューの破棄は「成否」ではなく「書き込みが確定したか」で判断する。
        // 部分成功（Success=false / ErrorMessage なし）でも登録済みの行は確定しているため、
        // 陳腐化したプレビューを残すと「インポート実行」ボタン
        // （DataExportImportDialog.xaml の IsEnabled="{Binding HasPreview}"）が
        // 同じファイルで有効なまま残り、押すと丸ごと取り込み直せてしまう。
        // ic_card / staff / ledger は skip-existing で救われるが ledger_detail は救われず、
        // 明細がそのまま重複してカードの残高チェーンが壊れる。
        //
        // 判定を成功／部分成功の各分岐へ分けず1か所に置く（同じ判断を2か所に分けない、Issue #1728）。
        // 「Success」を条件に含めるのは、全件スキップ（ImportedCount=0 の成功）でも
        // 従来どおりプレビューを畳むため。
        //
        // Issue #1782: 取り込み経路（プレビュー経由／直接インポート）では分岐しない。直接インポートは
        // 「表示中のプレビューとは無関係だから残す」としていたが、無関係なのは入力元だけである。
        // プレビューの登録・スキップ件数は取り込み前の DB を基準に算出した値で、取り込みが確定した
        // 時点で古くなる。残すと別ファイルのプレビューに対して「インポート実行」が有効なまま残り、
        // 押せば修正前のファイルを修正済みデータの上へ丸ごと取り込み直せてしまう。
        // 「消すかどうか」を呼び出し元の引数で選ばせない — 選べる限り、あとから増えた経路が
        // 再び誤った値を渡す（CLAUDE.md #1726「呼び出し元に引き継がせるのではなく、選択肢を無くす」）。
        var importCommitted = result.Success || result.ImportedCount > 0;

        // ClearPreview() が ImportPreviewFile を空にするため、案内文の材料は破棄する前に確定させる
        // （取り込み後に画面状態のプロパティを読み直さない、Issue #1741 と同じ罠）。
        var discardedPreviewNotice = importCommitted
            ? BuildDiscardedPreviewNotice(HasPreview, ImportPreviewFile, sourceFilePath)
            : string.Empty;

        if (importCommitted)
        {
            // Issue #1783: 「インポート完了: <ファイル名>」の表示も、プレビュー破棄と同じ
            // 「書き込みが確定したか」で決める。以前は結果を検査する前に無条件で代入していたため、
            // 対象カードを解決できない CSV のように完全に失敗した取り込みでも
            // 赤い「インポートに失敗しました」ダイアログの隣に緑の完了表示が並び、
            // データが入ったのかどうか職員が判断できなかった。
            // 新しい条件式を立てず importCommitted に相乗りさせる（同じ判断を2か所に分けない、Issue #1728）。
            // すぐ隣の ExportAsync は if (result.Success) の内側で LastExportedFile を代入しており、
            // インポート側だけがこの形から外れていた。
            LastImportedFile = sourceFilePath;
            ClearPreview();
        }

        if (result.Success)
        {
            var message = $"インポート完了: {result.ImportedCount}件を登録しました" + FormatUpdatedCountNote(result);
            if (result.SkippedCount > 0)
            {
                message += $"（{result.SkippedCount}件はスキップ）";
            }
            SetStatus(message, false);

            // Issue #1302: 監査ログ記録
            // Issue #1741: 本メソッド冒頭の ClearPreview() で空になる ImportPreviewFile ではなく
            // 呼び出し元が確定させた引数を使う（空パスだと後からファイルを追跡できない）。
            // 記録の失敗を取り込みの失敗として通知しない（TryLogImportAsync の remarks 参照）
            var auditLogged = await TryLogImportAsync(targetTable, sourceFilePath, result);

            var completionMessage =
                $"インポートが完了しました。\n\n登録件数: {result.ImportedCount}件" + FormatUpdatedCountNote(result)
                + (result.SkippedCount > 0 ? $"\nスキップ: {result.SkippedCount}件" : "");

            if (auditLogged)
            {
                var informationMessage = completionMessage + discardedPreviewNotice;
                return () => _dialogService.ShowInformation(informationMessage, "インポート完了");
            }

            var warningMessage = completionMessage + AuditLogFailureNotice + discardedPreviewNotice;
            return () => _dialogService.ShowWarning(
                warningMessage,
                "インポート完了（操作ログ記録の失敗あり）");
        }

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            SetStatus($"インポートエラー: {result.ErrorMessage}", true);
            var errorMessage = $"インポートに失敗しました。\n\n{result.ErrorMessage}";
            return () => _dialogService.ShowError(errorMessage, "インポートエラー");
        }

        // ここから先はエラー付きの終了。「登録が確定した行があるか」で文言を分ける（Issue #1745）。
        //
        // 従来はこの分岐を無条件に「部分成功」とみなし「インポート完了（一部エラー）」と表示していたが、
        // ledger のインポートがトランザクション化され（#1745）、カード／職員と同様に
        // 1件でも失敗すれば全件ロールバックするようになったため、ImportedCount=0 が通常の失敗形になった。
        // 「完了しました」の見出しの隣で本文が「登録が確定した行はありません」と述べる自己矛盾になり、
        // 職員はデータが入ったのか判断できない（見出しと実態を一致させる、Issue #1783 と同じ判断）。
        //
        // 新しい条件式を立てず、既に「書き込みが確定したか」を表している importCommitted に相乗りさせる
        // （同じ判断を2か所に分けない、Issue #1728）。この分岐では Success=false のため
        // importCommitted は ImportedCount > 0 と等価になる。
        var partialStatusMessage = importCommitted
            ? $"インポート完了（一部エラー）: {result.ImportedCount}件を登録、{result.ErrorCount}件がエラー"
            : $"インポートを中断しました: {result.ErrorCount}件がエラー（登録0件）";
        if (result.SkippedCount > 0)
        {
            partialStatusMessage += $"、{result.SkippedCount}件はスキップ";
        }
        SetStatus(partialStatusMessage, !importCommitted);

        // エラー詳細を追加
        foreach (var error in result.Errors.Take(10))
        {
            ImportErrors.Add($"行{error.LineNumber}: {error.Message}");
        }

        if (result.Errors.Count > 10)
        {
            ImportErrors.Add($"... 他 {result.Errors.Count - 10}件のエラー");
        }

        // Issue #1302: 部分成功でもデータは書き込まれたため監査ログ記録
        var partialAuditLogged = true;
        if (result.ImportedCount > 0)
        {
            partialAuditLogged = await TryLogImportAsync(targetTable, sourceFilePath, result);
        }

        var partialWarningMessage =
            (importCommitted
                ? $"インポートが完了しましたが、一部エラーがあります。\n\n登録件数: {result.ImportedCount}件\nエラー: {result.ErrorCount}件"
                : $"エラーがあったため、インポートを中断しました。\n\nエラー: {result.ErrorCount}件")
            + (result.SkippedCount > 0 ? $"\nスキップ: {result.SkippedCount}件" : "")
            + BuildPartialImportGuidance(result.ImportedCount)
            + (partialAuditLogged ? "" : AuditLogFailureNotice)
            + discardedPreviewNotice;
        return () => _dialogService.ShowWarning(
            partialWarningMessage,
            importCommitted ? "インポート完了（一部エラー）" : "インポートを中断しました");
    }

    /// <summary>
    /// 登録件数のうち既存データを書き換えた件数の注記（Issue #2262。書き換えが無ければ空文字）
    /// </summary>
    internal static string FormatUpdatedCountNote(CsvImportResult result)
        => result.UpdatedCount > 0 ? $"（うち既存データの更新 {result.UpdatedCount}件）" : string.Empty;

    /// <summary>
    /// 部分成功（一部エラー）時の復旧手順を組み立てる（Issue #1781）
    /// </summary>
    /// <remarks>
    /// 従来の「詳細はエラー一覧を確認してください。」だけでは、職員がエラー行を直した CSV を
    /// 丸ごと取り込み直す運用を招く。<c>ledger_detail</c> のインポートは skip-existing を
    /// 持たないため、それは確実に二重登録になりカードの残高チェーンが壊れる。
    /// 「何が」＝一部の行がエラー、「なぜ」＝登録済みの行は確定済みで再取り込みは重複、
    /// 「どうすれば」＝エラー行だけの CSV を作って再プレビュー、の3要素で構成する。
    /// <para>
    /// <paramref name="importedCount"/> が 0 のときに二重登録へ言及しないのは、
    /// 起きていない事象を警告すると職員が存在しない重複を探して原因究明が止まるため
    /// （error-messages.md「原因を断定する前に、その原因が成立する構成かを確認する」）。
    /// </para>
    /// </remarks>
    /// <param name="importedCount">実際に登録が確定した件数</param>
    internal static string BuildPartialImportGuidance(int importedCount)
    {
        if (importedCount > 0)
        {
            return $"\n\n登録された{importedCount}件は取り込みが確定済みのため、"
                + "同じファイルをそのまま再度インポートすると二重登録になります。"
                // Issue #2176: 利用履歴詳細の取り込みは、利用履歴 ID ごと・カードと日付ごとに巻き戻すため、
                // エラー一覧に出る行（先頭の 1 行）より巻き戻った範囲が広いことがある。範囲はエラーの文言が名指しする
                + "画面下部のエラー一覧に表示された行（エラーの文言が取り込み直す範囲を示している場合は、その範囲の行）だけを残したCSVファイルを作成し、"
                + "あらためてプレビューしてからインポートしてください。";
        }

        return "\n\n登録が確定した行はありません。"
            + "画面下部のエラー一覧を確認してCSVファイルを修正し、"
            + "あらためてプレビューしてからインポートしてください。";
    }

    /// <summary>
    /// 取り込み確定に伴って破棄したプレビューについて、完了ダイアログへ添える案内を組み立てる（Issue #1782）
    /// </summary>
    /// <remarks>
    /// プレビュー経由の取り込み（<see cref="ExecuteImportAsync"/>）で消えるのは、いま取り込んだ
    /// ファイル自身のプレビューであり、消えて当然のため案内しない。案内するのは直接インポートで
    /// 「別のファイルのプレビューが表示されたまま」だった場合に限る。起きていない事象を案内すると
    /// 職員は存在しない作業の消失を探して混乱する
    /// （error-messages.md「原因を断定する前に、その原因が成立する構成かを確認する」）。
    /// <para>
    /// 文言は「何が」＝表示中だった別ファイルのプレビューを消したこと、「なぜ」＝取り込みで
    /// データが変わり件数が最新でなくなること、「どうすれば」＝あらためてプレビューしてから
    /// インポートすること、の3要素で構成する。
    /// </para>
    /// </remarks>
    /// <param name="hadPreview">取り込み確定の直前にプレビューが表示されていたか</param>
    /// <param name="previewFilePath">そのプレビューのファイルパス（<see cref="ClearPreview"/> が空にする前の値）</param>
    /// <param name="importedFilePath">実際に取り込んだファイルのパス</param>
    internal static string BuildDiscardedPreviewNotice(
        bool hadPreview,
        string previewFilePath,
        string importedFilePath)
    {
        if (!hadPreview || string.IsNullOrEmpty(previewFilePath))
        {
            return string.Empty;
        }

        // 同じファイルなら「別のファイルのプレビューが消えた」ではないため案内しない。
        // Windows のパスは大文字小文字を区別しないため OrdinalIgnoreCase で比較する。
        if (string.Equals(previewFilePath, importedFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $"\n\n※表示していた別ファイル「{Path.GetFileName(previewFilePath)}」のプレビューはクリアしました。"
            + "今回の取り込みでデータが変わり、そのプレビューの登録・スキップ件数は最新ではなくなったためです。"
            + "そのファイルも取り込む場合は、あらためてプレビューしてからインポートしてください。";
    }

    /// <summary>
    /// 監査ログの記録に失敗したときに完了ダイアログへ添える案内（Issue #1741）
    /// </summary>
    /// <remarks>
    /// データの取り込みは確定済みのため、再実行を促す文言にしてはならない（二重登録になる）。
    /// 「何が」＝操作ログへの記録に失敗、「なぜ」＝取り込み自体は完了済み、
    /// 「どうすれば」＝再実行しない／管理者へ連絡、の3要素で構成する。
    /// </remarks>
    internal const string AuditLogFailureNotice =
        "\n\n※データの取り込みは完了していますが、操作ログへの記録に失敗しました。"
        + "取り込んだ内容は反映済みのため、同じファイルを再度インポートしないでください。"
        + "監査記録が必要な場合はシステム管理者へ連絡してください。";

    /// <summary>
    /// インポートの監査ログを記録する。記録に失敗しても例外は伝播させず false を返す（Issue #1741）
    /// </summary>
    /// <remarks>
    /// 監査ログ記録は取り込みがコミットされた後の後処理であり、ここでの失敗を
    /// <see cref="RunImportAsync"/> の catch へ流すと、確定済みの取り込みが
    /// 「インポートに失敗しました」と通知され、職員が再実行して二重登録を招く。
    /// CLAUDE.md（Issue #1727）の「コミット確定後の後処理を、成否の判定に巻き込まない」に従う。
    /// <see cref="OperationLogger.LogImportAsync"/> は内部で例外を握りつぶさないため、
    /// 共有モードで他 PC が DB をロックしていると SQLITE_BUSY が実際に送出される。
    /// </remarks>
    /// <returns>記録できたら true、失敗したら false</returns>
    private async Task<bool> TryLogImportAsync(string targetTable, string sourceFilePath, CsvImportResult result)
    {
        try
        {
            // Issue #2262: 追加と更新を分けて記録する（以前は利用履歴の取り込みが合計を「登録件数」として記録していた）
            await _operationLogger.LogImportAsync(
                targetTable,
                sourceFilePath,
                result.ImportedCount - result.UpdatedCount,
                result.UpdatedCount,
                result.SkippedCount,
                result.ErrorCount);
            return true;
        }
        catch (Exception ex)
        {
            // 無言で握りつぶさない。技術的詳細はログへ、ユーザーへは呼び出し元が案内する。
            ErrorDialogHelper.LogException(ex, "インポートの操作ログ記録");
            return false;
        }
    }

    /// <summary>
    /// エクスポートの監査ログを記録する。記録に失敗しても例外は伝播させない（Issue #2111）
    /// </summary>
    /// <remarks>
    /// 監査ログ記録はファイルの出力が確定した後の後処理であり、ここでの失敗を
    /// <see cref="ExportToFileAsync"/> の catch へ流すと、保存済みのエクスポートが
    /// 「エクスポートに失敗しました」と通知され、職員が再実行する。
    /// 「コミット確定後の後処理を、成否の判定に巻き込まない」（Issue #1805）に従う。
    /// <see cref="OperationLogger.LogExportAsync"/> は内部で例外を握りつぶさないため、
    /// 共有モードで他 PC が DB をロックしていると SQLITE_BUSY が実際に送出される。
    /// <para>
    /// 記録の成否を職員へ通知しないのは、操作ログ画面の Excel エクスポート
    /// （<c>OperationLogSearchViewModel.TryLogExportAsync</c>、Issue #1787）と同じ判断に揃えるため。
    /// インポート（<see cref="TryLogImportAsync"/>、Issue #1741）は再実行が二重登録を招くので
    /// 「再実行しない」と案内する必要があるが、エクスポートの再実行は保存ダイアログが上書きを確認するだけで
    /// データを壊さず、案内すべき復旧行動が無い。同じ失敗を画面ごとに別の扱いにしない。
    /// </para>
    /// </remarks>
    /// <param name="targetTable">監査ログへ記録する対象テーブル（出力開始時点で確定済みの値）</param>
    /// <param name="result">成功したエクスポートの結果</param>
    private async Task TryLogExportAsync(string targetTable, CsvExportResult result)
    {
        try
        {
            await _operationLogger.LogExportAsync(
                targetTable,
                result.FilePath,
                result.ExportedCount);
        }
        catch (Exception ex)
        {
            // 無言で握りつぶさない。技術的詳細は本番のログファイルへ残す。
            ErrorDialogHelper.LogException(ex, "エクスポートの操作ログ記録");
        }
    }

    /// <summary>
    /// プレビューをクリア
    /// </summary>
    [RelayCommand]
    public void ClearPreview()
    {
        ImportPreviewFile = string.Empty;
        ImportPreview = null;
        PreviewItems.Clear();
        HasPreview = false;
        PreviewSummary = string.Empty;
    }

    /// <summary>
    /// インポートを実行（旧API互換 - 直接インポート）
    /// </summary>
    /// <remarks>
    /// Issue #2262: 交通系ICカードの新しいカードの登録だけに使える（<see cref="IsDirectImportAllowed"/>）。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanImportDirectly))]
    public async Task ImportAsync()
    {
        if (!CanImportDirectly)
        {
            SetStatus(DirectImportRequiresPreviewMessage, true);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // Issue #1782: この経路のインポート元はプレビューではなくファイルダイアログの選択結果だが、
        // 取り込みが確定したらプレビュー表示も畳む。残すと別ファイルのプレビューに対して
        // 「インポート実行」ボタンが有効なまま残り、古いファイルを取り込み直せてしまう。
        await RunImportAsync(dialog.FileName, directImport: true);
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
    /// エクスポート先フォルダを開く
    /// </summary>
    [RelayCommand]
    public void OpenExportFolder()
    {
        // Issue #1465: ISafeFileLauncher 経由で explorer.exe を直接起動
        var folder = string.IsNullOrEmpty(LastExportedFile) ? null : Path.GetDirectoryName(LastExportedFile);
        var result = _safeFileLauncher.LaunchFolder(folder ?? string.Empty);
        if (!result.Success)
        {
            SetStatus(result.ErrorMessage, isError: true);
        }
    }

    /// <summary>
    /// デフォルトのエクスポートファイル名を取得
    /// </summary>
    private string GetDefaultExportFileName()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        return SelectedExportType switch
        {
            DataType.Cards => $"cards_{timestamp}.csv",
            DataType.Staff => $"staff_{timestamp}.csv",
            DataType.Ledgers => $"ledgers_{ExportStartDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_{ExportEndDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv",
            DataType.LedgerDetails => $"ledger_details_{ExportStartDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_{ExportEndDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv",
            _ => $"export_{timestamp}.csv"
        };
    }

    #region Issue #511: カードタッチによるIDm取得

    /// <summary>
    /// カードタッチ待機を開始
    /// </summary>
    [RelayCommand]
    public async Task StartCardTouchAsync()
    {
        if (_cardReader == null)
        {
            SetStatus("カードリーダーが利用できません", true);
            return;
        }

        // 接続状態を再確認
        IsCardReaderAvailable = _cardReader.ConnectionState == CardReaderConnectionState.Connected;
        if (!IsCardReaderAvailable)
        {
            SetStatus("カードリーダーが接続されていません", true);
            return;
        }

        // タッチ待機を開始
        IsWaitingForCardTouch = true;
        TouchedCardIdm = string.Empty;
        TouchedCardInfo = "カードをタッチしてください...";
        SetStatus("カードをタッチしてください", false);

        // 読み取りを開始（既に開始されている場合は何もしない）
        try
        {
            if (!_cardReader.IsReading)
            {
                await _cardReader.StartReadingAsync();
            }
        }
        catch (Exception ex)
        {
            IsWaitingForCardTouch = false;
            TouchedCardInfo = string.Empty;
            // Issue #1817: カードリーダーの失敗はネイティブ由来（DllNotFound / SEH / Win32）で
            // ex.Message が英語になる。生のまま出さず（Issue #1614）、技術的詳細は
            // ErrorDialogHelper.LogException でファイルログへ逃がす（当 ViewModel は ILogger 非注入）。
            ErrorDialogHelper.LogException(ex, "カードリーダーの開始");
            // ExceptionMessageFormatter.ToUserMessage へ委譲しないのは、この失敗が
            // ネイティブ由来（DllNotFound / SEH / Win32）で必ず default 分岐
            //（「しばらく待ってから再度実行してください」）へ落ちるため（#1817 のコードレビュー指摘）。
            // PaSoRi の未接続や felicalib.dll の欠落は待っても解消しない＝実行できない行動指示になる。
            // 取れる行動が違う経路には専用の文言を置く（#1757）。語彙は同メソッド冒頭の
            // 「カードリーダーが接続されていません」と揃える。
            SetStatus(
                "カードリーダーの開始に失敗しました。" +
                "カードリーダーが正しく接続されているか確認してから、もう一度実行してください。",
                true);
        }
    }

    /// <summary>
    /// カードタッチ待機をキャンセル
    /// </summary>
    [RelayCommand]
    public void CancelCardTouch()
    {
        IsWaitingForCardTouch = false;
        TouchedCardInfo = string.IsNullOrWhiteSpace(TouchedCardIdm)
            ? string.Empty
            : TouchedCardInfo; // 既にカードが読み取られていれば情報を維持
        SetStatus(string.Empty, false);
    }

    /// <summary>
    /// カード読み取りイベントハンドラ
    /// </summary>
    private void OnCardRead(object? sender, CardReadEventArgs e)
    {
        if (!IsWaitingForCardTouch)
        {
            return;
        }

        // UIスレッドで実行
        // Issue #1843: 生の Dispatcher.InvokeAsync は DispatcherOperation<Task> を返すため、
        // await しても内側の Task の例外は観測できない（Unwrap() が要る。Issue #1725）。
        // IDispatcherService 経由なら Unwrap 済みの Task が観測され、失敗はログへ残る。
        // 本体（HandleCardReadAsync）の try/catch は受け皿として残すが、catch ブロック自身が
        // 失敗し得る（#1745）ため、ディスパッチ側の観測と二重に守る。
        // あわせて async void を解消している（例外の逃げ道をもう 1 つ塞ぐ）。
        _dispatcherService.InvokeAsync(() => HandleCardReadAsync(e.Idm));
    }

    /// <summary>
    /// カードタッチ待ち中に読み取った IDm を照合してカード指定へ反映する
    /// </summary>
    /// <remarks>
    /// 本体全体を try/catch で包む（Issue #1816）。例外が抜けると
    /// <c>IsWaitingForCardTouch = false</c> だけが確定した「読み取れたように見える」状態で止まる。
    /// 失敗時はタッチ待ちへ戻し、もう一度タッチすれば再試行できるようにする。
    /// なお Issue #1843 以降、ディスパッチは <c>IDispatcherService</c> 経由となり、
    /// ここを抜けた例外も呼び出し側（<c>WpfDispatcherService</c>）が観測してログへ残す
    /// （生の <c>Dispatcher.InvokeAsync</c> の頃は GC 契機の
    /// <c>TaskScheduler.UnobservedTaskException</c> まで遅れていた。Issue #1725 / #1742）。
    /// </remarks>
    internal async Task HandleCardReadAsync(string idm)
    {
        try
        {
            await HandleCardReadCoreAsync(idm);
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.LogException(ex, "交通系ICカードの読み取り");

            // Issue #1614: 生の ex.Message は出さず、3要素の文言へ変換する
            TouchedCardIdm = string.Empty;
            TouchedCardInfo = string.Empty;

            // Issue #1816: タッチ待ちへ戻すのは、ダイアログがまだ開いているときだけ。
            // IsWaitingForCardTouch = true は OnIsWaitingForCardTouchChanged 経由で
            // CardReadingSuppressedMessage(true, DataImport) を送るため、Cleanup（ダイアログの Closing）が
            // 既に走ったあとで立てると、抑制を解除する経路がどこにも残らない。
            // その結果 MainViewModel の _suppressionSources に DataImport が残り続け、
            // メイン画面のカードタッチがアプリ再起動まで一切無視される（#1807 と同じ固着）。
            // 読み取りの失敗は共有モードの DB ロック／UNC 断で起き、ダイアログを閉じる操作と同時に起こり得る。
            if (!_isCleanedUp)
            {
                IsWaitingForCardTouch = true;
            }

            SetStatus(
                ExceptionMessageFormatter.ToUserMessage(ex, "交通系ICカードの読み取り") +
                " 復旧したら、もう一度カードをタッチしてください。",
                true);
        }
    }

    /// <summary>
    /// カード読み取り処理の本体（例外は呼び出し元 <see cref="HandleCardReadAsync"/> が受け止める）
    /// </summary>
    private async Task HandleCardReadCoreAsync(string idm)
    {
        // Issue #1816: 入口ゲート（OnCardRead）はカードリーダースレッドで判定され、
        // 解除は UI スレッドのここで初めて行われる。連続タッチでは 2 件目もゲートを
        // 通過済みで queue されているため、取得地点で再判定する（#1807 と同じ形）
        if (!IsWaitingForCardTouch)
        {
            return;
        }

        // Issue #1952: 再入は専用フラグで塞ぐ。抑制の解放（IsWaitingForCardTouch = false）を
        // モーダルの表示範囲の外へ移した結果、入口ゲートは処理中も true のままになるため、
        // ゲートだけでは待機中・モーダル表示中の 2 件目を止められない。
        if (_isHandlingCardRead)
        {
            return;
        }

        _isHandlingCardRead = true;
        try
        {
            // 読み取ったIDmで登録済みカードを検索
            var card = await _cardRepository.GetByIdmAsync(idm);

            // Issue #1952: 解放を await より後ろへ移した結果、この待機中もタッチ待ちは true のままで、
            // 「キャンセル」ボタン（Visibility は IsWaitingForCardTouch に束縛）が押せる状態で残る。
            // 共有モードの照合は秒単位かかり得るため、この窓でキャンセル／指定のクリア／
            // データ種別の切替／ダイアログ終了（Cleanup）が起き、抑制が解放され得る。
            // そのまま続けると未登録カード警告モーダルを**抑制 OFF のまま**開くことになり、
            // 本 Issue が塞いだ欠陥（背後で貸出・返却が進む）がこの経路から再現する。
            // 副作用（TouchedCardIdm / TouchedCardInfo / モーダル）より前に再判定し、
            // 中止したタッチは通知せず捨てる（#1842「副作用を起こす前に判定を終える」）。
            if (!IsWaitingForCardTouch)
            {
                return;
            }

            if (card != null)
            {
                // 登録済みカードが見つかった
                TouchedCardIdm = card.CardIdm;
                var shortIdm = card.CardIdm.Length > 8
                    ? card.CardIdm.Substring(0, 8) + "..."
                    : card.CardIdm;
                TouchedCardInfo = $"{card.CardType} {card.CardNumber} ({shortIdm})";
                SetStatus($"カードを読み取りました: {card.CardType} {card.CardNumber}", false);
            }
            else
            {
                // 未登録カード
                TouchedCardIdm = string.Empty;
                TouchedCardInfo = "未登録のカードです";
                SetStatus("このカードはシステムに登録されていません。先に交通系ICカード管理画面で登録してください。", true);
                _dialogService.ShowWarning(
                    "タッチされたカードはシステムに登録されていません。\n\n利用履歴をインポートするには、先に交通系ICカード管理画面で対象の交通系ICカードを登録してください。",
                    "未登録カード");
            }

            // Issue #1952: 抑制の解放は「登録モードの終わり」＝モーダルを閉じたあとに行う（#1807）。
            // OnIsWaitingForCardTouchChanged が CardReadingSuppressedMessage(false, DataImport) を
            // 送るため、ここより前に置くと GetByIdmAsync の待機中と未登録カード警告モーダルの
            // 表示中は抑制が外れる。モーダルは入れ子のメッセージポンプで「回り続ける」ので、
            // その間のタッチは MainViewModel へ届き、ダイアログの背後で貸出・返却が進む。
            // 例外時はここへ到達しない＝タッチ待ち（＝抑制 ON）のまま
            // HandleCardReadAsync の catch が再試行を案内する（#1816）。
            IsWaitingForCardTouch = false;
        }
        finally
        {
            _isHandlingCardRead = false;
        }
    }

    /// <summary>
    /// カード指定をクリア
    /// </summary>
    [RelayCommand]
    public void ClearTargetCard()
    {
        SelectedTargetCard = null;
        TouchedCardIdm = string.Empty;
        TouchedCardInfo = string.Empty;
        IsWaitingForCardTouch = false;
    }

    /// <summary>
    /// クリーンアップ（ダイアログ終了時に呼び出し）
    /// </summary>
    public void Cleanup()
    {
        // Issue #1816: 以降は「抑制を解除する経路が無い」状態。
        // 読み取りの後始末（例外経路）がタッチ待ちを立て直さないようにする。
        _isCleanedUp = true;

        if (_cardReader != null)
        {
            _cardReader.CardRead -= OnCardRead;
        }
        IsWaitingForCardTouch = false;
    }

    /// <summary>
    /// カード読み取りの処理中か（Issue #1952）。
    /// 抑制の解放をモーダルの表示範囲の外へ移したため、入口ゲート
    /// （<see cref="IsWaitingForCardTouch"/>）は処理中も true のままになる。
    /// 再入（待機中・モーダル表示中の 2 件目のタッチ）はこのフラグで塞ぎ、
    /// <c>finally</c> で必ず解除する（解除を忘れると 1 度の失敗で
    /// カード指定がアプリ再起動まで不能になる。#1725）
    /// </summary>
    private bool _isHandlingCardRead;

    /// <summary>
    /// ダイアログが閉じられ <see cref="Cleanup"/> が済んだか（Issue #1816）。
    /// 済んだあとにタッチ待ちを立て直すと、カード読み取り抑制が解除されないまま残る
    /// </summary>
    private bool _isCleanedUp;

    #endregion

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }
}
