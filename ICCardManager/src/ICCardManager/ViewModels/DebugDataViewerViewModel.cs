using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;

namespace ICCardManager.ViewModels
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
    public partial class DebugDataViewerViewModel : ViewModelBase
    {
        private readonly ICardReader _cardReader;
        private readonly DbContext _dbContext;

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

        public DebugDataViewerViewModel(ICardReader cardReader, DbContext dbContext)
        {
            _cardReader = cardReader;
            _dbContext = dbContext;

            // カード読み取りイベントを登録
            _cardReader.CardRead += OnCardRead;
        }

        /// <summary>
        /// 初期化
        /// </summary>
        public async Task InitializeAsync()
        {
            // 初期状態でstaffテーブルを読み込む
            await LoadTableDataAsync();
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
        private async void OnCardRead(object sender, CardReadEventArgs e)
        {
            if (!IsWaitingForCard) return;

            IsWaitingForCard = false;
            OnPropertyChanged(nameof(IsNotWaitingForCard));

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    CardStatusMessage = "読み取り中...";

                    // 基本情報
                    CardIdm = e.Idm;
                    CardSystemCode = e.SystemCode ?? "(不明)";

                    // 残高を読み取り
                    var balance = await _cardReader.ReadBalanceAsync(e.Idm);
                    CardBalance = balance.HasValue ? $"¥{balance.Value:N0}" : "(読み取り失敗)";

                    // 履歴を読み取り
                    var history = await _cardReader.ReadHistoryAsync(e.Idm);
                    var historyList = history.ToList();

                    // 生データ表示（履歴はすでにパース済みなので、概要を表示）
                    var rawDataBuilder = new StringBuilder();
                    rawDataBuilder.AppendLine($"IDm: {e.Idm}");
                    rawDataBuilder.AppendLine($"SystemCode: {e.SystemCode}");
                    rawDataBuilder.AppendLine($"履歴件数: {historyList.Count}件");
                    rawDataBuilder.AppendLine();
                    rawDataBuilder.AppendLine("※生のバイトデータは実機でのみ取得可能です。");
                    rawDataBuilder.AppendLine("※ここではパース後のデータを表示しています。");
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

                    CardStatusMessage = $"読み取り完了（履歴: {historyList.Count}件）";
                }
                catch (Exception ex)
                {
                    CardStatusMessage = $"エラー: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine($"[DebugDataViewer] カード読み取りエラー: {ex}");
                }
            });
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
                using (BeginBusy($"{SelectedTableName}を読み込み中..."))
                {
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
            }
            catch (Exception ex)
            {
                DbStatusMessage = $"エラー: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[DebugDataViewer] DB読み込みエラー: {ex}");
            }
        }

        /// <summary>
        /// クリーンアップ
        /// </summary>
        public void Cleanup()
        {
            _cardReader.CardRead -= OnCardRead;
        }

        partial void OnIsWaitingForCardChanged(bool value)
        {
            OnPropertyChanged(nameof(IsNotWaitingForCard));
        }
    }
}
