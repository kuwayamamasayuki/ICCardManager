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
    /// Issue #548: 分割線クリック方式UIに変更
    /// </summary>
    public partial class LedgerDetailViewModel : ObservableObject
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly SummaryGenerator _summaryGenerator;
        private readonly OperationLogger _operationLogger;
        private readonly ILogger<LedgerDetailViewModel> _logger;

        private Ledger _ledger = null!;

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
        /// 保存完了時のコールバック
        /// </summary>
        public Action? OnSaveCompleted { get; set; }

        /// <summary>
        /// 複数グループがあるかどうか（Issue #634: ボタン切り替え用）
        /// </summary>
        [ObservableProperty]
        private bool _hasMultipleGroups;

        /// <summary>
        /// 操作者IDm（ログ記録用）
        /// </summary>
        private string? _operatorIdm;

        private readonly LedgerSplitService _ledgerSplitService;

        public LedgerDetailViewModel(
            ILedgerRepository ledgerRepository,
            SummaryGenerator summaryGenerator,
            OperationLogger operationLogger,
            LedgerSplitService ledgerSplitService,
            ILogger<LedgerDetailViewModel> logger)
        {
            _ledgerRepository = ledgerRepository;
            _summaryGenerator = summaryGenerator;
            _operationLogger = operationLogger;
            _ledgerSplitService = ledgerSplitService;
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

            // 既存のGroupIdから分割線位置を設定
            InitializeDividersFromGroupIds();

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = false;
        }

        /// <summary>
        /// 既存のGroupIdから分割線位置を初期化
        /// </summary>
        private void InitializeDividersFromGroupIds()
        {
            for (int i = 0; i < Items.Count - 1; i++)
            {
                var current = Items[i];
                var next = Items[i + 1];

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
                    // 両方グループなしの場合は分割線なし
                    current.ShowDividerBelow = false;
                }
            }

            // 最後のアイテムには分割線なし
            if (Items.Count > 0)
            {
                Items[Items.Count - 1].ShowDividerBelow = false;
            }
        }

        /// <summary>
        /// 指定位置の分割線をトグル（挿入/削除）
        /// Issue #548: 分割線クリック方式UI
        /// </summary>
        /// <param name="index">分割線をトグルするアイテムのインデックス（この行の下の分割線）</param>
        public void ToggleDividerAt(int index)
        {
            if (index < 0 || index >= Items.Count - 1)
            {
                return; // 最後のアイテムの下には分割線を置けない
            }

            var item = Items[index];
            item.ShowDividerBelow = !item.ShowDividerBelow;

            // 分割線の状態からGroupIdを再計算
            RecalculateGroupsFromDividers();

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;

            if (item.ShowDividerBelow)
            {
                StatusMessage = "分割線を挿入しました（グループを分割）";
                _logger.LogDebug("Inserted divider after index {Index}", index);
            }
            else
            {
                StatusMessage = "分割線を削除しました（グループを統合）";
                _logger.LogDebug("Removed divider after index {Index}", index);
            }
        }

        /// <summary>
        /// 分割線の状態からGroupIdを再計算
        /// 連続する分割線なしのアイテムは同じグループになる
        /// </summary>
        /// <remarks>
        /// Issue #633: 分割線が1つでも存在する場合、単独アイテムにもGroupIdを付与する。
        /// これにより、SummaryGeneratorがGroupIdベースの摘要生成パスを使用し、
        /// ユーザーの明示的な分割操作が摘要に正しく反映される。
        /// 分割線がない場合はすべてのGroupIdをnullにして自動検出モードに戻す。
        /// </remarks>
        private void RecalculateGroupsFromDividers()
        {
            if (Items.Count == 0) return;

            // 分割線が存在するかチェック
            bool hasDividers = Items.Any(item => item.ShowDividerBelow);

            int currentGroupId = 1;
            int groupStartIndex = 0;

            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];

                if (item.ShowDividerBelow || i == Items.Count - 1)
                {
                    if (hasDividers)
                    {
                        // 分割線が存在する場合: 全アイテムにGroupIdを付与
                        // （単独アイテムも含む。これにより摘要生成でGroupIdパスが使われる）
                        for (int j = groupStartIndex; j <= i; j++)
                        {
                            Items[j].GroupId = currentGroupId;
                        }
                        currentGroupId++;
                    }
                    else
                    {
                        // 分割線なし: GroupIdをクリアして自動検出モードに戻す
                        for (int j = groupStartIndex; j <= i; j++)
                        {
                            Items[j].GroupId = null;
                        }
                    }

                    // 次のグループの開始位置
                    groupStartIndex = i + 1;
                }
            }
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

            HasMultipleGroups = groupCount >= 2;

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
        /// 自動検出に戻す（すべての分割線を削除）
        /// </summary>
        [RelayCommand]
        private void ResetToAutoDetect()
        {
            foreach (var item in Items)
            {
                item.GroupId = null;
                item.ShowDividerBelow = false;
            }

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;
            StatusMessage = "自動検出モードに戻しました（すべての分割線を削除）";

            _logger.LogDebug("Reset all groups to auto-detect");
        }

        /// <summary>
        /// すべて統合（すべての分割線を削除してグループ化）
        /// </summary>
        [RelayCommand]
        private void MergeAll()
        {
            if (Items.Count < 2)
            {
                StatusMessage = "統合する項目がありません";
                return;
            }

            // すべての分割線を削除
            foreach (var item in Items)
            {
                item.ShowDividerBelow = false;
            }

            // グループを再計算（すべてが1つのグループになる）
            RecalculateGroupsFromDividers();
            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;
            StatusMessage = "すべてを1つのグループに統合しました";

            _logger.LogDebug("Merged all items into one group");
        }

        /// <summary>
        /// すべて分割（すべての行の間に分割線を挿入）
        /// </summary>
        [RelayCommand]
        private void SplitAll()
        {
            if (Items.Count < 2)
            {
                StatusMessage = "分割する項目がありません";
                return;
            }

            // 最後以外のすべての行の下に分割線を挿入
            for (int i = 0; i < Items.Count - 1; i++)
            {
                Items[i].ShowDividerBelow = true;
            }

            // グループを再計算（すべてが個別になる）
            RecalculateGroupsFromDividers();
            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;
            StatusMessage = "すべてを個別に分割しました";

            _logger.LogDebug("Split all items into separate entries");
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
        /// 台帳分割による保存（Issue #634）
        /// </summary>
        [RelayCommand]
        private async Task SaveWithFullSplitAsync()
        {
            if (!HasChanges) return;

            IsBusy = true;
            StatusMessage = "分割中...";

            try
            {
                var updatedDetails = Items.Select(item =>
                {
                    var detail = item.Detail;
                    detail.GroupId = item.GroupId;
                    return detail;
                }).ToList();

                var result = await _ledgerSplitService.SplitAsync(
                    _ledger.Id, updatedDetails, _operatorIdm);

                if (!result.Success)
                {
                    StatusMessage = $"分割に失敗しました: {result.ErrorMessage}";
                    return;
                }

                HasChanges = false;
                StatusMessage = $"{result.CreatedLedgerIds.Count + 1}件の履歴に分割しました";
                _logger.LogInformation(
                    "Split ledger {LedgerId} into separate ledgers",
                    _ledger.Id);

                OnSaveCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to split ledger {LedgerId}", _ledger.Id);
                StatusMessage = $"エラー: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

    }
}
