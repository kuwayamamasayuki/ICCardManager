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
    /// 行の追加/全項目編集モード
    /// </summary>
    public enum LedgerRowEditMode
    {
        /// <summary>行の追加</summary>
        Add,
        /// <summary>全項目修正</summary>
        Edit
    }

    /// <summary>
    /// 履歴行の追加/全項目編集ダイアログのViewModel（Issue #635）
    /// </summary>
    /// <remarks>
    /// <para>Addモード: 日付・摘要・金額等を入力し、挿入位置プレビューで位置を確認して挿入</para>
    /// <para>Editモード: 既存行の全項目（日付・金額含む）を変更可能</para>
    /// </remarks>
    public partial class LedgerRowEditViewModel : ViewModelBase
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly IStaffRepository _staffRepository;
        private readonly OperationLogger _operationLogger;

        private string _cardIdm = string.Empty;
        private string? _operatorIdm;
        private int _editLedgerId;

        /// <summary>
        /// 編集モード（Add/Edit）
        /// </summary>
        [ObservableProperty]
        private LedgerRowEditMode _mode;

        /// <summary>
        /// ダイアログタイトル
        /// </summary>
        [ObservableProperty]
        private string _dialogTitle = "履歴行の追加";

        /// <summary>
        /// 日付
        /// </summary>
        [ObservableProperty]
        private DateTime _editDate = DateTime.Today;

        /// <summary>
        /// 摘要
        /// </summary>
        [ObservableProperty]
        private string _summary = string.Empty;

        /// <summary>
        /// 受入金額
        /// </summary>
        [ObservableProperty]
        private int _income;

        /// <summary>
        /// 払出金額
        /// </summary>
        [ObservableProperty]
        private int _expense;

        /// <summary>
        /// 残高
        /// </summary>
        [ObservableProperty]
        private int _balance;

        /// <summary>
        /// 残高を自動計算するか
        /// </summary>
        [ObservableProperty]
        private bool _isAutoBalance = true;

        /// <summary>
        /// 備考
        /// </summary>
        [ObservableProperty]
        private string _note = string.Empty;

        /// <summary>
        /// 職員リスト
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<Staff> _staffList = new();

        /// <summary>
        /// 選択中の職員
        /// </summary>
        [ObservableProperty]
        private Staff? _selectedStaff;

        /// <summary>
        /// 挿入位置前後の行（Addモード用）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<LedgerDto> _contextRows = new();

        /// <summary>
        /// 挿入位置（ContextRows内のインデックス）
        /// </summary>
        [ObservableProperty]
        private int _insertIndex;

        /// <summary>
        /// 挿入位置の直前行の残高
        /// </summary>
        [ObservableProperty]
        private int _previousBalance;

        /// <summary>
        /// 挿入位置プレビューを表示するか（Addモードのみ）
        /// </summary>
        public bool IsAddMode => Mode == LedgerRowEditMode.Add;

        /// <summary>
        /// バリデーションエラーメッセージ
        /// </summary>
        [ObservableProperty]
        private string _validationMessage = string.Empty;

        /// <summary>
        /// 警告メッセージ
        /// </summary>
        [ObservableProperty]
        private string _warningMessage = string.Empty;

        /// <summary>
        /// ステータスメッセージ
        /// </summary>
        [ObservableProperty]
        private string _statusMessage = string.Empty;

        /// <summary>
        /// 保存完了フラグ（ダイアログを閉じる通知用）
        /// </summary>
        [ObservableProperty]
        private bool _isSaved;

        /// <summary>
        /// 保存が可能か
        /// </summary>
        [ObservableProperty]
        private bool _canSave;

        /// <summary>
        /// 全行リスト（挿入位置計算用）
        /// </summary>
        private List<LedgerDto> _allLedgers = new();

        public LedgerRowEditViewModel(
            ILedgerRepository ledgerRepository,
            IStaffRepository staffRepository,
            OperationLogger operationLogger)
        {
            _ledgerRepository = ledgerRepository;
            _staffRepository = staffRepository;
            _operationLogger = operationLogger;
        }

        /// <summary>
        /// Addモードで初期化
        /// </summary>
        /// <param name="cardIdm">対象カードIDm</param>
        /// <param name="allLedgers">表示中の全履歴（挿入位置プレビュー用）</param>
        /// <param name="operatorIdm">認証済み職員IDm</param>
        public async Task InitializeForAddAsync(string cardIdm, List<LedgerDto> allLedgers, string operatorIdm)
        {
            _cardIdm = cardIdm;
            _operatorIdm = operatorIdm;
            _allLedgers = allLedgers;

            Mode = LedgerRowEditMode.Add;
            DialogTitle = "履歴行の追加";
            EditDate = DateTime.Today;

            await LoadStaffListAsync();

            // 挿入位置を末尾に設定
            InsertIndex = _allLedgers.Count;
            UpdateContextRows();
            RecalculateBalance();
            Validate();

            OnPropertyChanged(nameof(IsAddMode));
        }

        /// <summary>
        /// Editモードで初期化
        /// </summary>
        /// <param name="ledgerDto">編集対象</param>
        /// <param name="operatorIdm">認証済み職員IDm</param>
        public async Task InitializeForEditAsync(LedgerDto ledgerDto, string operatorIdm)
        {
            _operatorIdm = operatorIdm;
            _cardIdm = ledgerDto.CardIdm;
            _editLedgerId = ledgerDto.Id;

            Mode = LedgerRowEditMode.Edit;
            DialogTitle = "履歴行の修正";

            // 完全なLedgerオブジェクトを取得
            var ledger = await _ledgerRepository.GetByIdAsync(ledgerDto.Id);
            if (ledger == null) return;

            EditDate = ledger.Date;
            Summary = ledger.Summary;
            Income = ledger.Income;
            Expense = ledger.Expense;
            Balance = ledger.Balance;
            IsAutoBalance = false; // 編集モードでは手動入力開始
            Note = ledger.Note ?? string.Empty;

            await LoadStaffListAsync();

            // 現在の利用者を選択
            SelectedStaff = StaffList.FirstOrDefault(s => s.StaffIdm == ledger.LenderIdm);

            Validate();
            OnPropertyChanged(nameof(IsAddMode));
        }

        /// <summary>
        /// 職員リストを読み込み
        /// </summary>
        private async Task LoadStaffListAsync()
        {
            var staffMembers = await _staffRepository.GetAllAsync();
            StaffList.Clear();
            foreach (var staff in staffMembers.OrderBy(s => s.Name))
            {
                StaffList.Add(staff);
            }
        }

        /// <summary>
        /// Income変更時のコールバック
        /// </summary>
        partial void OnIncomeChanged(int value)
        {
            RecalculateBalance();
            Validate();
        }

        /// <summary>
        /// Expense変更時のコールバック
        /// </summary>
        partial void OnExpenseChanged(int value)
        {
            RecalculateBalance();
            Validate();
        }

        /// <summary>
        /// IsAutoBalance変更時のコールバック
        /// </summary>
        partial void OnIsAutoBalanceChanged(bool value)
        {
            if (value)
            {
                RecalculateBalance();
            }
            Validate();
        }

        /// <summary>
        /// Balance変更時のコールバック
        /// </summary>
        partial void OnBalanceChanged(int value)
        {
            Validate();
        }

        /// <summary>
        /// Summary変更時のコールバック
        /// </summary>
        partial void OnSummaryChanged(string value)
        {
            Validate();
        }

        /// <summary>
        /// EditDate変更時のコールバック
        /// </summary>
        partial void OnEditDateChanged(DateTime value)
        {
            if (Mode == LedgerRowEditMode.Add && _allLedgers.Count > 0)
            {
                // 日付に基づいて挿入位置を自動調整
                var newIndex = _allLedgers.Count;
                for (int i = 0; i < _allLedgers.Count; i++)
                {
                    if (_allLedgers[i].Date > value)
                    {
                        newIndex = i;
                        break;
                    }
                }
                InsertIndex = newIndex;
                UpdateContextRows();
                RecalculateBalance();
            }
            Validate();
        }

        /// <summary>
        /// 挿入位置を1つ上に移動
        /// </summary>
        [RelayCommand]
        private void MoveInsertPositionUp()
        {
            if (InsertIndex > 0)
            {
                InsertIndex--;
                UpdateContextRows();
                RecalculateBalance();
                Validate();
            }
        }

        /// <summary>
        /// 挿入位置を1つ下に移動
        /// </summary>
        [RelayCommand]
        private void MoveInsertPositionDown()
        {
            if (InsertIndex < _allLedgers.Count)
            {
                InsertIndex++;
                UpdateContextRows();
                RecalculateBalance();
                Validate();
            }
        }

        /// <summary>
        /// 挿入位置前後のコンテキスト行を更新
        /// </summary>
        private void UpdateContextRows()
        {
            ContextRows.Clear();

            // 挿入位置の前後2行ずつを表示
            var startIdx = Math.Max(0, InsertIndex - 2);
            var endIdx = Math.Min(_allLedgers.Count, InsertIndex + 2);

            for (int i = startIdx; i < endIdx; i++)
            {
                ContextRows.Add(_allLedgers[i]);
            }
        }

        /// <summary>
        /// 残高を再計算
        /// </summary>
        private void RecalculateBalance()
        {
            if (!IsAutoBalance) return;

            if (Mode == LedgerRowEditMode.Add)
            {
                // 挿入位置の直前行の残高を取得
                if (InsertIndex > 0 && InsertIndex <= _allLedgers.Count)
                {
                    PreviousBalance = _allLedgers[InsertIndex - 1].Balance;
                }
                else
                {
                    PreviousBalance = 0;
                }
            }

            Balance = PreviousBalance + Income - Expense;
        }

        /// <summary>
        /// バリデーション
        /// </summary>
        private void Validate()
        {
            ValidationMessage = string.Empty;
            WarningMessage = string.Empty;
            CanSave = true;

            // 摘要が空かチェック
            if (string.IsNullOrWhiteSpace(Summary))
            {
                ValidationMessage = "摘要を入力してください";
                CanSave = false;
                return;
            }

            // 受入・払出が両方0かチェック（繰越等の特殊パターンは除外）
            var isSpecialSummary = Summary.Contains("繰越") ||
                                   Summary.Contains("新規購入") ||
                                   Summary == "ポイント還元";
            if (Income == 0 && Expense == 0 && !isSpecialSummary)
            {
                WarningMessage = "受入と払出が両方0円です。繰越等でなければ金額を入力してください。";
            }

            // 残高が負になるチェック
            if (Balance < 0)
            {
                ValidationMessage = "残高がマイナスになります";
                CanSave = false;
                return;
            }

            // Income/Expenseが負かチェック
            if (Income < 0)
            {
                ValidationMessage = "受入金額は0以上にしてください";
                CanSave = false;
                return;
            }
            if (Expense < 0)
            {
                ValidationMessage = "払出金額は0以上にしてください";
                CanSave = false;
                return;
            }

            // Addモードの場合の日付チェック
            if (Mode == LedgerRowEditMode.Add && _allLedgers.Count > 0)
            {
                // 挿入位置の前後と日付の整合性をチェック
                if (InsertIndex > 0 && _allLedgers[InsertIndex - 1].Date > EditDate)
                {
                    WarningMessage = "日付が前の行より古くなっています。挿入位置を確認してください。";
                }
                if (InsertIndex < _allLedgers.Count && _allLedgers[InsertIndex].Date < EditDate)
                {
                    WarningMessage = "日付が次の行より新しくなっています。挿入位置を確認してください。";
                }
            }
        }

        /// <summary>
        /// 利用者の選択をクリア
        /// </summary>
        [RelayCommand]
        private void ClearStaff()
        {
            SelectedStaff = null;
        }

        /// <summary>
        /// 保存
        /// </summary>
        [RelayCommand]
        private async Task Save()
        {
            if (!CanSave) return;

            IsBusy = true;
            BusyMessage = "保存中...";
            StatusMessage = string.Empty;

            try
            {
                if (Mode == LedgerRowEditMode.Add)
                {
                    await SaveAddAsync();
                }
                else
                {
                    await SaveEditAsync();
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
        /// 追加モードの保存処理
        /// </summary>
        private async Task SaveAddAsync()
        {
            var newLedger = new Ledger
            {
                CardIdm = _cardIdm,
                Date = EditDate,
                Summary = Summary,
                Income = Income,
                Expense = Expense,
                Balance = Balance,
                StaffName = SelectedStaff?.Name,
                LenderIdm = SelectedStaff?.StaffIdm,
                Note = Note,
                IsLentRecord = false
            };

            var newId = await _ledgerRepository.InsertAsync(newLedger);
            if (newId > 0)
            {
                newLedger.Id = newId;
                await _operationLogger.LogLedgerInsertAsync(_operatorIdm, newLedger);
                IsSaved = true;
            }
            else
            {
                StatusMessage = "保存に失敗しました";
            }
        }

        /// <summary>
        /// 編集モードの保存処理
        /// </summary>
        private async Task SaveEditAsync()
        {
            var ledger = await _ledgerRepository.GetByIdAsync(_editLedgerId);
            if (ledger == null)
            {
                StatusMessage = "履歴データが見つかりません";
                return;
            }

            // 変更前のスナップショット
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
            ledger.Date = EditDate;
            ledger.Summary = Summary;
            ledger.Income = Income;
            ledger.Expense = Expense;
            ledger.Balance = Balance;
            ledger.Note = Note;

            if (SelectedStaff != null)
            {
                ledger.LenderIdm = SelectedStaff.StaffIdm;
                ledger.StaffName = SelectedStaff.Name;
            }
            else
            {
                ledger.LenderIdm = null;
                ledger.StaffName = null;
            }

            var result = await _ledgerRepository.UpdateAsync(ledger);
            if (result)
            {
                await _operationLogger.LogLedgerUpdateAsync(_operatorIdm, beforeLedger, ledger);
                IsSaved = true;
            }
            else
            {
                StatusMessage = "保存に失敗しました";
            }
        }

        /// <summary>
        /// キャンセル
        /// </summary>
        [RelayCommand]
        private void Cancel()
        {
            // 何もせずに閉じる（IsSavedはfalseのまま）
        }
    }
}
