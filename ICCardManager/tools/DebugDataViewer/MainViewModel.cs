using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;

namespace DebugDataViewer
{
    /// <summary>
    /// 履歴アイテム（デバッグ表示用）
    /// </summary>
    public class CardHistoryItem
    {
        public int Index { get; set; }
        public DateTime? UseDate { get; set; }
        public string EntryStation { get; set; }
        public string ExitStation { get; set; }
        public int? Amount { get; set; }
        public int? Balance { get; set; }
        public string TransactionType { get; set; }
        public string RawData { get; set; }
    }

    /// <summary>
    /// デバッグ用データビューアのViewModel
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly ICardReader _cardReader;
        private readonly DbContext _dbContext;

        #region 共通プロパティ

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _busyMessage = string.Empty;

        [ObservableProperty]
        private string _databasePath = string.Empty;

        #endregion

        #region カードデータ関連プロパティ

        [ObservableProperty]
        private string _cardIdm = string.Empty;

        [ObservableProperty]
        private string _cardSystemCode = string.Empty;

        [ObservableProperty]
        private string _cardBalance = string.Empty;

        [ObservableProperty]
        private string _rawHistoryData = string.Empty;

        [ObservableProperty]
        private string _cardStatusMessage = "カードをタッチしてください";

        [ObservableProperty]
        private bool _isWaitingForCard;

        public bool IsNotWaitingForCard => !IsWaitingForCard;

        [ObservableProperty]
        private ObservableCollection<CardHistoryItem> _cardHistoryItems = new();

        #endregion

        #region DBデータ関連プロパティ

        [ObservableProperty]
        private ObservableCollection<string> _tableNames = new()
        {
            "staff",
            "ic_card",
            "ledger",
            "ledger_detail",
            "operation_log",
            "settings"
        };

        [ObservableProperty]
        private string _selectedTableName = "staff";

        [ObservableProperty]
        private DataView _tableData;

        [ObservableProperty]
        private string _dbStatusMessage = string.Empty;

        [ObservableProperty]
        private int _recordCount;

        #endregion

        public MainViewModel(ICardReader cardReader, DbContext dbContext)
        {
            _cardReader = cardReader;
            _dbContext = dbContext;

            // データベースパスを取得
            DatabasePath = _dbContext.DatabasePath;

            // カード読み取りイベントを登録
            _cardReader.CardRead += OnCardRead;
            _cardReader.ConnectionStateChanged += OnConnectionStateChanged;
            _cardReader.Error += OnCardReaderError;
        }

        /// <summary>
        /// 初期化
        /// </summary>
        public async Task InitializeAsync()
        {
            // 初期状態でstaffテーブルを読み込む
            await LoadTableDataAsync();

            // カードリーダーの監視を開始
            try
            {
                await _cardReader.StartReadingAsync();
                CardStatusMessage = _cardReader.ConnectionState == CardReaderConnectionState.Connected
                    ? "カードをタッチしてください"
                    : "カードリーダーに接続中...";
            }
            catch (Exception ex)
            {
                CardStatusMessage = $"カードリーダー初期化エラー: {ex.Message}";
            }
        }

        /// <summary>
        /// 接続状態変更イベントハンドラ
        /// </summary>
        private void OnConnectionStateChanged(object sender, ConnectionStateChangedEventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                switch (e.State)
                {
                    case CardReaderConnectionState.Connected:
                        if (!IsWaitingForCard)
                        {
                            CardStatusMessage = "カードをタッチしてください";
                        }
                        break;
                    case CardReaderConnectionState.Disconnected:
                        CardStatusMessage = $"カードリーダー未接続: {e.Message ?? "リーダーを確認してください"}";
                        break;
                    case CardReaderConnectionState.Reconnecting:
                        CardStatusMessage = $"再接続中... (試行 {e.RetryCount})";
                        break;
                }
            });
        }

        /// <summary>
        /// カードリーダーエラーイベントハンドラ
        /// </summary>
        private void OnCardReaderError(object sender, Exception e)
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CardStatusMessage = $"エラー: {e.Message}";
            });
        }

        /// <summary>
        /// カード読み取り開始
        /// </summary>
        [RelayCommand]
        private void ReadCard()
        {
            IsWaitingForCard = true;
            CardStatusMessage = "カードをタッチしてください...";
            OnPropertyChanged(nameof(IsNotWaitingForCard));

            // クリア
            CardIdm = string.Empty;
            CardSystemCode = string.Empty;
            CardBalance = string.Empty;
            RawHistoryData = string.Empty;
            CardHistoryItems.Clear();
        }

        /// <summary>
        /// カード読み取りイベントハンドラ
        /// </summary>
        private void OnCardRead(object sender, CardReadEventArgs e)
        {
            if (!IsWaitingForCard) return;

            IsWaitingForCard = false;
            OnPropertyChanged(nameof(IsNotWaitingForCard));

            // UIスレッドで非同期処理を実行
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ReadCardDataAsync(e));
        }

        /// <summary>
        /// カードデータを読み取る（UIスレッドで実行）
        /// </summary>
        private async Task ReadCardDataAsync(CardReadEventArgs e)
        {
            var rawDataBuilder = new StringBuilder();
            var errors = new List<string>();

            // 交通系ICカードのシステムコード
            const string CYBERNETICS_SYSTEM_CODE = "88B4";

            try
            {
                CardStatusMessage = "基本情報を読み取り中...";

                // 基本情報（CardReadイベントから取得済み）
                CardIdm = e.Idm;
                CardSystemCode = e.SystemCode ?? "(不明)";

                // カード種別を判定
                var isTransportCard = string.Equals(e.SystemCode, CYBERNETICS_SYSTEM_CODE, StringComparison.OrdinalIgnoreCase);
                var cardTypeName = GetCardTypeName(e.SystemCode);

                rawDataBuilder.AppendLine($"IDm: {e.Idm}");
                rawDataBuilder.AppendLine($"SystemCode: {e.SystemCode}");
                rawDataBuilder.AppendLine($"カード種別: {cardTypeName}");
                rawDataBuilder.AppendLine();

                if (!isTransportCard)
                {
                    rawDataBuilder.AppendLine("=== 注意 ===");
                    rawDataBuilder.AppendLine("このカードは交通系ICカードではありません。");
                    rawDataBuilder.AppendLine("残高・履歴データは交通系ICカード専用の機能です。");
                    rawDataBuilder.AppendLine();

                    CardBalance = "(対象外)";
                    RawHistoryData = rawDataBuilder.ToString();
                    CardStatusMessage = $"読み取り完了 - {cardTypeName}（残高・履歴なし）";
                    return;
                }

                // 残高を読み取り
                CardStatusMessage = "残高を読み取り中...（カードを離さないでください）";
                try
                {
                    var balance = await _cardReader.ReadBalanceAsync(e.Idm);
                    if (balance.HasValue)
                    {
                        CardBalance = $"¥{balance.Value:N0}";
                        rawDataBuilder.AppendLine($"残高: {balance.Value}円 (0x{balance.Value:X4})");
                    }
                    else
                    {
                        CardBalance = "(読み取り失敗 - null)";
                        errors.Add("残高: 読み取り結果がnull");
                        rawDataBuilder.AppendLine("残高: 読み取り失敗 (null)");
                    }
                }
                catch (Exception balanceEx)
                {
                    CardBalance = $"(エラー: {balanceEx.Message})";
                    errors.Add($"残高: {balanceEx.Message}");
                    rawDataBuilder.AppendLine($"残高: エラー - {balanceEx.Message}");
                    rawDataBuilder.AppendLine($"  詳細: {balanceEx.GetType().Name}");
                }

                // 履歴を読み取り
                CardStatusMessage = "履歴を読み取り中...（カードを離さないでください）";
                var historyList = new List<LedgerDetail>();
                try
                {
                    var history = await _cardReader.ReadHistoryAsync(e.Idm);
                    historyList = history?.ToList() ?? new List<LedgerDetail>();
                    rawDataBuilder.AppendLine($"履歴件数: {historyList.Count}件");
                }
                catch (Exception historyEx)
                {
                    errors.Add($"履歴: {historyEx.Message}");
                    rawDataBuilder.AppendLine($"履歴: エラー - {historyEx.Message}");
                    rawDataBuilder.AppendLine($"  詳細: {historyEx.GetType().Name}");
                }

                rawDataBuilder.AppendLine();
                if (errors.Count > 0)
                {
                    rawDataBuilder.AppendLine("=== エラー詳細 ===");
                    foreach (var error in errors)
                    {
                        rawDataBuilder.AppendLine($"・{error}");
                    }
                    rawDataBuilder.AppendLine();
                    rawDataBuilder.AppendLine("※カードを読み取り中は離さないでください");
                }

                RawHistoryData = rawDataBuilder.ToString();

                // 履歴データを成形して表示
                CardHistoryItems.Clear();
                var index = 1;
                foreach (var detail in historyList)
                {
                    CardHistoryItems.Add(new CardHistoryItem
                    {
                        Index = index++,
                        UseDate = detail.UseDate,
                        EntryStation = detail.EntryStation ?? "-",
                        ExitStation = detail.ExitStation ?? "-",
                        Amount = detail.Amount,
                        Balance = detail.Balance,
                        TransactionType = detail.IsCharge ? "チャージ" : (detail.IsBus ? "バス" : "鉄道"),
                        RawData = FormatDetailAsRaw(detail)
                    });
                }

                // 完了メッセージ
                if (errors.Count > 0)
                {
                    CardStatusMessage = $"読み取り完了（一部エラーあり: {errors.Count}件）- 詳細は生データ欄を確認";
                }
                else
                {
                    CardStatusMessage = $"読み取り完了（履歴: {historyList.Count}件）";
                }
            }
            catch (Exception ex)
            {
                CardStatusMessage = $"予期せぬエラー: {ex.Message}";
                rawDataBuilder.AppendLine();
                rawDataBuilder.AppendLine("=== 予期せぬエラー ===");
                rawDataBuilder.AppendLine(ex.ToString());
                RawHistoryData = rawDataBuilder.ToString();
            }
        }

        /// <summary>
        /// システムコードからカード種別名を取得
        /// </summary>
        private string GetCardTypeName(string systemCode)
        {
            if (string.IsNullOrEmpty(systemCode))
                return "不明";

            return systemCode.ToUpperInvariant() switch
            {
                "88B4" => "交通系ICカード（Cybernetics）",
                "0003" => "FeliCa Lite（職員証・会員証等）",
                "8008" => "WAON",
                "8004" => "Edy",
                "FE00" => "FeliCa Plug / NFC Forum Type 3 Tag",
                "12FC" => "FeliCa（汎用）",
                _ => $"不明 (0x{systemCode})"
            };
        }

        /// <summary>
        /// 履歴詳細を擬似的な生データ形式で表示
        /// </summary>
        private string FormatDetailAsRaw(LedgerDetail detail)
        {
            // 実際の生データは取得できないため、データの内容を16進数風に表示
            var parts = new List<string>();

            if (detail.UseDate.HasValue)
            {
                var dt = detail.UseDate.Value;
                parts.Add($"{dt:yyMMddHHmm}");
            }

            if (detail.Amount.HasValue)
            {
                parts.Add($"AMT:{detail.Amount.Value:X4}");
            }

            if (detail.Balance.HasValue)
            {
                parts.Add($"BAL:{detail.Balance.Value:X4}");
            }

            return string.Join(" ", parts);
        }

        /// <summary>
        /// テーブルデータを読み込み
        /// </summary>
        [RelayCommand]
        private async Task LoadTableDataAsync()
        {
            if (string.IsNullOrEmpty(SelectedTableName))
            {
                DbStatusMessage = "テーブルを選択してください";
                return;
            }

            try
            {
                IsBusy = true;
                BusyMessage = $"{SelectedTableName}を読み込み中...";

                await Task.Run(() =>
                {
                    var connection = _dbContext.GetConnection();

                    // テーブル名をサニタイズ（SQLインジェクション対策）
                    var validTables = new[] { "staff", "ic_card", "ledger", "ledger_detail", "operation_log", "settings" };
                    if (!validTables.Contains(SelectedTableName))
                    {
                        throw new ArgumentException($"無効なテーブル名: {SelectedTableName}");
                    }

                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT * FROM {SelectedTableName} ORDER BY 1";

                    using var adapter = new System.Data.SQLite.SQLiteDataAdapter(command);
                    var dataTable = new DataTable();
                    adapter.Fill(dataTable);

                    // UIスレッドで更新
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        TableData = dataTable.DefaultView;
                        RecordCount = dataTable.Rows.Count;
                        DbStatusMessage = $"{SelectedTableName}テーブルを読み込みました";
                    });
                });
            }
            catch (Exception ex)
            {
                DbStatusMessage = $"エラー: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[DebugDataViewer] DB読み込みエラー: {ex}");
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        /// <summary>
        /// クリーンアップ
        /// </summary>
        public async void Cleanup()
        {
            // カードリーダーの監視を停止
            try
            {
                await _cardReader.StopReadingAsync();
            }
            catch
            {
                // 停止時のエラーは無視
            }

            // イベント購読を解除
            _cardReader.CardRead -= OnCardRead;
            _cardReader.ConnectionStateChanged -= OnConnectionStateChanged;
            _cardReader.Error -= OnCardReaderError;
        }

        partial void OnIsWaitingForCardChanged(bool value)
        {
            OnPropertyChanged(nameof(IsNotWaitingForCard));
        }
    }
}
