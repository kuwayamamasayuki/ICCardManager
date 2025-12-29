using System;
using System.Collections.Generic;
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
        private readonly OperationLogger _operationLogger;

        private int _ledgerId;
        private string _cardIdm = string.Empty;

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
        /// 利用者名
        /// </summary>
        [ObservableProperty]
        private string _staffName = string.Empty;

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

        public LedgerEditViewModel(
            ILedgerRepository ledgerRepository,
            OperationLogger operationLogger)
        {
            _ledgerRepository = ledgerRepository;
            _operationLogger = operationLogger;
        }

        /// <summary>
        /// 履歴データで初期化
        /// </summary>
        public Task InitializeAsync(LedgerDto ledger)
        {
            if (ledger == null) return Task.CompletedTask;

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

                // 元の値を保存
                _originalSummary = ledger.Summary;
                _originalNote = ledger.Note ?? string.Empty;
            }
            finally
            {
                IsBusy = false;
            }

            return Task.CompletedTask;
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
                // 変更があるかチェック
                if (Summary == _originalSummary && Note == _originalNote)
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

                var result = await _ledgerRepository.UpdateAsync(ledger);

                if (result)
                {
                    // 操作ログを記録（GUI操作のためoperatorIdmはnull）
                    await _operationLogger.LogLedgerUpdateAsync(null, beforeLedger, ledger);

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
        /// キャンセルコマンド
        /// </summary>
        [RelayCommand]
        private void Cancel()
        {
            // 何もせずに閉じる（IsSavedはfalseのまま）
        }
    }
}
