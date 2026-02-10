using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 利用履歴編集ダイアログのViewModel
    /// </summary>
    public partial class LedgerEditViewModel : ViewModelBase
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly IStaffRepository _staffRepository;
        private readonly OperationLogger _operationLogger;

        private int _ledgerId;
        private string _cardIdm = string.Empty;

        /// <summary>
        /// 操作者の職員IDm（Issue #429: 認証済み職員のIDm）
        /// </summary>
        private string? _operatorIdm;

        /// <summary>
        /// ステータスメッセージ
        /// </summary>
        [ObservableProperty]
        private string _statusMessage = string.Empty;

        /// <summary>
        /// 日付表示
        /// </summary>
        [ObservableProperty]
        private string _dateDisplay = string.Empty;

        /// <summary>
        /// 摘要（編集可能）
        /// </summary>
        [ObservableProperty]
        private string _summary = string.Empty;

        /// <summary>
        /// 備考（編集可能）
        /// </summary>
        [ObservableProperty]
        private string _note = string.Empty;

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
        /// 利用者名（表示用）
        /// </summary>
        [ObservableProperty]
        private string _staffName = string.Empty;

        /// <summary>
        /// 職員リスト（選択肢）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<Staff> _staffList = new();

        /// <summary>
        /// 選択中の職員
        /// </summary>
        [ObservableProperty]
        private Staff? _selectedStaff;

        /// <summary>
        /// 受入金額があるか
        /// </summary>
        [ObservableProperty]
        private bool _hasIncome;

        /// <summary>
        /// 払出金額があるか
        /// </summary>
        [ObservableProperty]
        private bool _hasExpense;

        /// <summary>
        /// 保存完了フラグ（ダイアログを閉じる通知用）
        /// </summary>
        [ObservableProperty]
        private bool _isSaved;

        /// <summary>
        /// 元の摘要（変更検知用）
        /// </summary>
        private string _originalSummary = string.Empty;

        /// <summary>
        /// 元の備考（変更検知用）
        /// </summary>
        private string _originalNote = string.Empty;

        /// <summary>
        /// 元の利用者IDm（変更検知用）
        /// </summary>
        private string? _originalLenderIdm;

        public LedgerEditViewModel(
            ILedgerRepository ledgerRepository,
            IStaffRepository staffRepository,
            OperationLogger operationLogger)
        {
            _ledgerRepository = ledgerRepository;
            _staffRepository = staffRepository;
            _operationLogger = operationLogger;
        }

        /// <summary>
        /// 履歴データで初期化
        /// </summary>
        /// <param name="ledger">編集対象の履歴データ</param>
        /// <param name="operatorIdm">操作者の職員IDm（Issue #429: 認証済み職員のIDm）</param>
        public async Task InitializeAsync(LedgerDto ledger, string? operatorIdm = null)
        {
            if (ledger == null) return;

            _operatorIdm = operatorIdm;

            IsBusy = true;
            BusyMessage = "読み込み中...";

            try
            {
                _ledgerId = ledger.Id;
                _cardIdm = ledger.CardIdm;

                DateDisplay = ledger.DateDisplay;
                Summary = ledger.Summary;
                Note = ledger.Note ?? string.Empty;
                IncomeDisplay = ledger.IncomeDisplay;
                ExpenseDisplay = ledger.ExpenseDisplay;
                BalanceDisplay = ledger.BalanceDisplay + "円";
                StaffName = ledger.StaffName ?? "-";
                HasIncome = ledger.HasIncome;
                HasExpense = ledger.HasExpense;

                // 職員リストを読み込み
                var staffMembers = await _staffRepository.GetAllAsync();
                StaffList.Clear();
                foreach (var staff in staffMembers.OrderBy(s => s.Name))
                {
                    StaffList.Add(staff);
                }

                // 現在の利用者を選択状態にする
                // LenderIdmはLedgerDtoに含まれていないため、DBから取得
                var fullLedger = await _ledgerRepository.GetByIdAsync(_ledgerId);
                if (fullLedger != null)
                {
                    _originalLenderIdm = fullLedger.LenderIdm;
                    SelectedStaff = StaffList.FirstOrDefault(s => s.StaffIdm == fullLedger.LenderIdm);
                }

                // 元の値を保存
                _originalSummary = ledger.Summary;
                _originalNote = ledger.Note ?? string.Empty;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 保存コマンド
        /// </summary>
        [RelayCommand]
        private async Task Save()
        {
            IsBusy = true;
            BusyMessage = "保存中...";
            StatusMessage = string.Empty;

            try
            {
                // 利用者の変更があるかチェック
                var newLenderIdm = SelectedStaff?.StaffIdm;
                var staffChanged = newLenderIdm != _originalLenderIdm;

                // 変更があるかチェック
                if (Summary == _originalSummary && Note == _originalNote && !staffChanged)
                {
                    IsSaved = true;
                    return;
                }

                // 完全なLedgerオブジェクトを取得して更新
                var ledger = await _ledgerRepository.GetByIdAsync(_ledgerId);
                if (ledger == null)
                {
                    StatusMessage = "履歴データが見つかりません";
                    return;
                }

                // 変更前の状態を保存
                var beforeLedger = new Ledger
                {
                    Id = ledger.Id,
                    CardIdm = ledger.CardIdm,
                    LenderIdm = ledger.LenderIdm,
                    Date = ledger.Date,
                    Summary = ledger.Summary,
                    Income = ledger.Income,
                    Expense = ledger.Expense,
                    Balance = ledger.Balance,
                    StaffName = ledger.StaffName,
                    Note = ledger.Note,
                    ReturnerIdm = ledger.ReturnerIdm,
                    LentAt = ledger.LentAt,
                    ReturnedAt = ledger.ReturnedAt,
                    IsLentRecord = ledger.IsLentRecord
                };

                // 値を更新
                ledger.Summary = Summary;
                ledger.Note = Note;

                // 利用者の変更がある場合（Issue #529, Issue #636）
                if (staffChanged)
                {
                    if (SelectedStaff != null)
                    {
                        ledger.LenderIdm = SelectedStaff.StaffIdm;
                        ledger.StaffName = SelectedStaff.Name;
                    }
                    else
                    {
                        // 利用者を空欄にする（Issue #636）
                        ledger.LenderIdm = null;
                        ledger.StaffName = null;
                    }
                }

                var result = await _ledgerRepository.UpdateAsync(ledger);

                if (result)
                {
                    // 操作ログを記録（Issue #429: 認証済み職員のIDmを使用）
                    await _operationLogger.LogLedgerUpdateAsync(_operatorIdm, beforeLedger, ledger);

                    IsSaved = true;
                }
                else
                {
                    StatusMessage = "保存に失敗しました";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"エラー: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 利用者の選択をクリア（Issue #636）
        /// </summary>
        [RelayCommand]
        private void ClearStaff()
        {
            SelectedStaff = null;
        }

        /// <summary>
        /// キャンセルコマンド
        /// </summary>
        [RelayCommand]
        private void Cancel()
        {
            // 何もせずに閉じる（IsSavedはfalseのまま）
        }
    }
}
