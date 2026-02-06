using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 利用履歴詳細表示用のアイテム
    /// </summary>
    public partial class LedgerDetailItemViewModel : ObservableObject
    {
        /// <summary>
        /// 元のLedgerDetail
        /// </summary>
        public LedgerDetail Detail { get; }

        /// <summary>
        /// リスト内のインデックス（選択操作用）
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 選択状態
        /// </summary>
        [ObservableProperty]
        private bool _isSelected;

        /// <summary>
        /// グループID
        /// </summary>
        [ObservableProperty]
        private int? _groupId;

        /// <summary>
        /// 利用日時表示
        /// </summary>
        public string UseDateDisplay => Detail.UseDate?.ToString("yyyy/MM/dd HH:mm") ?? "-";

        /// <summary>
        /// 区間表示
        /// </summary>
        public string RouteDisplay
        {
            get
            {
                if (Detail.IsCharge)
                {
                    return "チャージ";
                }
                if (Detail.IsPointRedemption)
                {
                    return "ポイント還元";
                }
                if (Detail.IsBus)
                {
                    return $"バス（{Detail.BusStops ?? "★"}）";
                }
                if (!string.IsNullOrEmpty(Detail.EntryStation) && !string.IsNullOrEmpty(Detail.ExitStation))
                {
                    return $"{Detail.EntryStation} → {Detail.ExitStation}";
                }
                return "-";
            }
        }

        /// <summary>
        /// 金額表示
        /// </summary>
        public string AmountDisplay => Detail.Amount.HasValue ? $"{Detail.Amount:N0}円" : "-";

        /// <summary>
        /// 残高表示
        /// </summary>
        public string BalanceDisplay => Detail.Balance.HasValue ? $"{Detail.Balance:N0}円" : "-";

        /// <summary>
        /// チャージフラグ
        /// </summary>
        public bool IsCharge => Detail.IsCharge;

        /// <summary>
        /// バス利用フラグ
        /// </summary>
        public bool IsBus => Detail.IsBus;

        /// <summary>
        /// グループ表示色のインデックス（グループごとに異なる色を割り当てるため）
        /// </summary>
        [ObservableProperty]
        private int _groupColorIndex;

        /// <summary>
        /// グループラベル（A, B, C...）アクセシビリティ対応: Issue #548
        /// 色だけでなくアルファベットでもグループを識別可能に
        /// </summary>
        [ObservableProperty]
        private string _groupLabel = "-";

        /// <summary>
        /// このアイテムの下に分割線を表示するか（Issue #548: 分割線UI）
        /// </summary>
        [ObservableProperty]
        private bool _showDividerBelow;

        public LedgerDetailItemViewModel(LedgerDetail detail, int index)
        {
            Detail = detail;
            Index = index;
            _groupId = detail.GroupId;
        }
    }

    /// <summary>
    /// 利用履歴詳細ダイアログ用ViewModel（Issue #484: 統合・分割機能対応）
    /// </summary>
    public partial class LedgerDetailViewModel : ObservableObject
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly SummaryGenerator _summaryGenerator;
        private readonly OperationLogger _operationLogger;
        private readonly ILogger<LedgerDetailViewModel> _logger;

        private Ledger _ledger = null!;
        private int _nextGroupId = 1;

        /// <summary>
        /// 詳細アイテムリスト
        /// </summary>
        public ObservableCollection<LedgerDetailItemViewModel> Items { get; } = new();

        /// <summary>
        /// 日付表示
        /// </summary>
        [ObservableProperty]
        private string _dateDisplay = string.Empty;

        /// <summary>
        /// 摘要表示
        /// </summary>
        [ObservableProperty]
        private string _summaryDisplay = string.Empty;

        /// <summary>
        /// 受入金額表示
        /// </summary>
        [ObservableProperty]
        private string _incomeDisplay = string.Empty;

        /// <summary>
        /// 払出金額表示
        /// </summary>
        [ObservableProperty]
        private string _expenseDisplay = string.Empty;

        /// <summary>
        /// 残高表示
        /// </summary>
        [ObservableProperty]
        private string _balanceDisplay = string.Empty;

        /// <summary>
        /// 利用者名
        /// </summary>
        [ObservableProperty]
        private string _staffName = string.Empty;

        /// <summary>
        /// 備考
        /// </summary>
        [ObservableProperty]
        private string _note = string.Empty;

        /// <summary>
        /// 詳細件数表示
        /// </summary>
        [ObservableProperty]
        private string _detailCountDisplay = string.Empty;

        /// <summary>
        /// 変更があるかどうか
        /// </summary>
        [ObservableProperty]
        private bool _hasChanges;

        /// <summary>
        /// 処理中かどうか
        /// </summary>
        [ObservableProperty]
        private bool _isBusy;

        /// <summary>
        /// ステータスメッセージ
        /// </summary>
        [ObservableProperty]
        private string _statusMessage = string.Empty;

        /// <summary>
        /// 統合コマンドが実行可能か
        /// </summary>
        public bool CanMerge => Items.Count(i => i.IsSelected) >= 2;

        /// <summary>
        /// 分割コマンドが実行可能か
        /// </summary>
        public bool CanSplit => Items.Any(i => i.IsSelected && i.GroupId.HasValue);

        /// <summary>
        /// 保存完了時のコールバック
        /// </summary>
        public Action? OnSaveCompleted { get; set; }

        /// <summary>
        /// 操作者IDm（ログ記録用）
        /// </summary>
        private string? _operatorIdm;

        public LedgerDetailViewModel(
            ILedgerRepository ledgerRepository,
            SummaryGenerator summaryGenerator,
            OperationLogger operationLogger,
            ILogger<LedgerDetailViewModel> logger)
        {
            _ledgerRepository = ledgerRepository;
            _summaryGenerator = summaryGenerator;
            _operationLogger = operationLogger;
            _logger = logger;
        }

        /// <summary>
        /// 初期化
        /// </summary>
        public async Task InitializeAsync(int ledgerId, string? operatorIdm = null)
        {
            _operatorIdm = operatorIdm;
            _ledger = await _ledgerRepository.GetByIdAsync(ledgerId);

            if (_ledger == null)
            {
                throw new InvalidOperationException($"Ledger ID {ledgerId} が見つかりません");
            }

            // ヘッダー情報を設定
            DateDisplay = WarekiConverter.ToWareki(_ledger.Date);
            SummaryDisplay = _ledger.Summary;
            IncomeDisplay = _ledger.Income > 0 ? $"{_ledger.Income:N0}円" : string.Empty;
            ExpenseDisplay = _ledger.Expense > 0 ? $"{_ledger.Expense:N0}円" : string.Empty;
            BalanceDisplay = $"{_ledger.Balance:N0}円";
            StaffName = _ledger.StaffName ?? "-";
            Note = _ledger.Note ?? string.Empty;

            // 詳細アイテムを設定
            Items.Clear();
            var index = 0;
            foreach (var detail in _ledger.Details)
            {
                Items.Add(new LedgerDetailItemViewModel(detail, index++));
            }

            // 既存のGroupIdから次のGroupIdを決定
            _nextGroupId = Items.Any(i => i.GroupId.HasValue)
                ? Items.Where(i => i.GroupId.HasValue).Max(i => i.GroupId!.Value) + 1
                : 1;

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = false;
        }

        /// <summary>
        /// グループの色インデックスとラベルを更新（Issue #548: アクセシビリティ対応）
        /// </summary>
        private void UpdateGroupColors()
        {
            var groupIds = Items
                .Where(i => i.GroupId.HasValue)
                .Select(i => i.GroupId!.Value)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            foreach (var item in Items)
            {
                if (item.GroupId.HasValue)
                {
                    var groupIndex = groupIds.IndexOf(item.GroupId.Value);
                    item.GroupColorIndex = groupIndex % 5 + 1; // 1-5の色
                    // A, B, C... のラベルを設定（アクセシビリティ対応）
                    item.GroupLabel = ((char)('A' + groupIndex % 26)).ToString();
                }
                else
                {
                    item.GroupColorIndex = 0; // グループなし
                    item.GroupLabel = "-";
                }
            }

            // 分割線の表示を更新（Issue #548: 分割線UI）
            UpdateDividerLines();
        }

        /// <summary>
        /// 分割線の表示を更新（Issue #548）
        /// グループ境界に分割線を表示
        /// </summary>
        private void UpdateDividerLines()
        {
            for (int i = 0; i < Items.Count; i++)
            {
                var current = Items[i];
                // 最後のアイテムには分割線を表示しない
                if (i == Items.Count - 1)
                {
                    current.ShowDividerBelow = false;
                    continue;
                }

                var next = Items[i + 1];
                // 現在と次のアイテムのグループが異なる場合、または
                // どちらかがグループに属していない場合に分割線を表示
                bool currentHasGroup = current.GroupId.HasValue;
                bool nextHasGroup = next.GroupId.HasValue;

                if (currentHasGroup && nextHasGroup)
                {
                    // 両方グループに属している場合、グループIDが異なれば分割線
                    current.ShowDividerBelow = current.GroupId != next.GroupId;
                }
                else if (currentHasGroup || nextHasGroup)
                {
                    // 片方だけグループに属している場合は分割線
                    current.ShowDividerBelow = true;
                }
                else
                {
                    // 両方グループなしの場合は分割線なし（自動検出モード）
                    current.ShowDividerBelow = false;
                }
            }
        }

        /// <summary>
        /// 詳細件数表示を更新
        /// </summary>
        private void UpdateDetailCountDisplay()
        {
            var groupCount = Items
                .Where(i => i.GroupId.HasValue)
                .Select(i => i.GroupId!.Value)
                .Distinct()
                .Count();

            if (groupCount > 0)
            {
                DetailCountDisplay = $"{Items.Count}件の詳細（{groupCount}グループ）";
            }
            else
            {
                DetailCountDisplay = $"{Items.Count}件の詳細";
            }
        }

        /// <summary>
        /// 選択状態変更時の処理
        /// </summary>
        public void OnSelectionChanged()
        {
            OnPropertyChanged(nameof(CanMerge));
            OnPropertyChanged(nameof(CanSplit));
        }

        /// <summary>
        /// 選択した項目を統合（Ctrl+G）
        /// </summary>
        [RelayCommand]
        private void Merge()
        {
            var selectedItems = Items.Where(i => i.IsSelected).ToList();
            if (selectedItems.Count < 2)
            {
                StatusMessage = "2つ以上の項目を選択してください";
                return;
            }

            // 新しいグループIDを割り当て
            var newGroupId = _nextGroupId++;
            foreach (var item in selectedItems)
            {
                item.GroupId = newGroupId;
                item.IsSelected = false;
            }

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;
            StatusMessage = $"{selectedItems.Count}件を統合しました";

            _logger.LogDebug("Merged {Count} items into group {GroupId}", selectedItems.Count, newGroupId);
        }

        /// <summary>
        /// 選択した項目を分割（Ctrl+U）
        /// </summary>
        [RelayCommand]
        private void Split()
        {
            var selectedItems = Items.Where(i => i.IsSelected && i.GroupId.HasValue).ToList();
            if (selectedItems.Count == 0)
            {
                StatusMessage = "グループ化された項目を選択してください";
                return;
            }

            // グループIDを解除
            foreach (var item in selectedItems)
            {
                item.GroupId = null;
                item.IsSelected = false;
            }

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;
            StatusMessage = $"{selectedItems.Count}件の統合を解除しました";

            _logger.LogDebug("Split {Count} items from groups", selectedItems.Count);
        }

        /// <summary>
        /// 自動検出に戻す
        /// </summary>
        [RelayCommand]
        private void ResetToAutoDetect()
        {
            foreach (var item in Items)
            {
                item.GroupId = null;
                item.IsSelected = false;
            }

            _nextGroupId = 1;
            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;
            StatusMessage = "自動検出モードに戻しました";

            _logger.LogDebug("Reset all groups to auto-detect");
        }

        /// <summary>
        /// 変更を保存
        /// </summary>
        [RelayCommand]
        private async Task SaveAsync()
        {
            if (!HasChanges)
            {
                return;
            }

            IsBusy = true;
            StatusMessage = "保存中...";

            try
            {
                // 詳細のGroupIdを更新
                var updatedDetails = Items.Select(item =>
                {
                    var detail = item.Detail;
                    detail.GroupId = item.GroupId;
                    return detail;
                }).ToList();

                // 詳細を置き換え
                var success = await _ledgerRepository.ReplaceDetailsAsync(_ledger.Id, updatedDetails);
                if (!success)
                {
                    StatusMessage = "保存に失敗しました";
                    return;
                }

                // 摘要を再生成
                var newSummary = _summaryGenerator.Generate(updatedDetails);
                if (!string.IsNullOrEmpty(newSummary) && newSummary != _ledger.Summary)
                {
                    // 更新前の状態を保存
                    var beforeLedger = new Ledger
                    {
                        Id = _ledger.Id,
                        CardIdm = _ledger.CardIdm,
                        Summary = _ledger.Summary,
                        Date = _ledger.Date,
                        Balance = _ledger.Balance
                    };

                    _ledger.Summary = newSummary;
                    await _ledgerRepository.UpdateAsync(_ledger);
                    SummaryDisplay = newSummary;

                    // 操作ログを記録（operatorIdmを設定してGUI操作を区別）
                    if (!string.IsNullOrEmpty(_operatorIdm))
                    {
                        await _operationLogger.LogLedgerUpdateAsync(_operatorIdm, beforeLedger, _ledger);
                    }
                }

                HasChanges = false;
                StatusMessage = "保存しました";
                _logger.LogInformation("Saved ledger detail changes for ledger {LedgerId}", _ledger.Id);

                OnSaveCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save ledger detail changes");
                StatusMessage = $"エラー: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// すべて選択
        /// </summary>
        [RelayCommand]
        private void SelectAll()
        {
            foreach (var item in Items)
            {
                item.IsSelected = true;
            }
            OnSelectionChanged();
        }

        /// <summary>
        /// 選択解除
        /// </summary>
        [RelayCommand]
        private void DeselectAll()
        {
            foreach (var item in Items)
            {
                item.IsSelected = false;
            }
            OnSelectionChanged();
        }
    }
}
