using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data;
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
    /// 残高の自動計算が使えない理由（Issue #1740）
    /// </summary>
    public enum AutoBalanceUnavailableReason
    {
        /// <summary>自動計算が使える</summary>
        None,

        /// <summary>
        /// 自動計算の起点となる直前行を特定できない。
        /// 履歴一覧のページ先頭行・表示期間の先頭行（繰越行も無い場合）が該当する。
        /// </summary>
        PreviousRowNotIdentified,

        /// <summary>
        /// Editモードで利用日を変更したため、この行が入る位置と直前行が変わり起点を確定できない。
        /// </summary>
        EditDateChanged
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
        private readonly DbContext _dbContext;

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
        /// 直前行の残高（自動計算の起点）
        /// </summary>
        /// <remarks>
        /// Addモードは挿入位置に追随して <see cref="RecalculateBalance"/> が毎回設定する。
        /// Editモードは編集対象行が固定のため <see cref="InitializeForEditAsync"/> で一度だけ設定する（Issue #1740）。
        /// </remarks>
        [ObservableProperty]
        private int _previousBalance;

        /// <summary>
        /// 残高の自動計算が使えない理由（Issue #1740）
        /// </summary>
        [ObservableProperty]
        private AutoBalanceUnavailableReason _autoBalanceUnavailableReason;

        /// <summary>
        /// 残高の自動計算が使えるか（Issue #1740）
        /// </summary>
        /// <remarks>
        /// 直前行の残高（自動計算の起点）を確定できるときだけ true。
        /// false のときは <see cref="RecalculateBalance"/> が残高に触れず、手入力のみとなる。
        /// 可否は挿入位置・利用日の変更で変わるため <see cref="UpdateAutoBalanceAvailability"/> が都度更新する。
        /// </remarks>
        public bool CanAutoBalance =>
            AutoBalanceUnavailableReason == AutoBalanceUnavailableReason.None;

        /// <summary>
        /// 「自動計算」チェックボックスのToolTip（Issue #1740）
        /// </summary>
        /// <remarks>
        /// 自動計算が使えない場合は、無効化されている理由と復旧手段を示す
        /// （<c>.claude/rules/error-messages.md</c> の「何が／なぜ／どうすれば」）。
        /// 「どうすれば」は画面上に実在する操作でなければならないため、履歴一覧に無い
        /// 「表示期間を広げる」等は案内しない（期間は暦月固定で広げる操作が存在しない）。
        /// </remarks>
        public string AutoBalanceToolTip
        {
            get
            {
                switch (AutoBalanceUnavailableReason)
                {
                    case AutoBalanceUnavailableReason.PreviousRowNotIdentified:
                        return "残高の自動計算は前の行の残高を起点にしますが、その前の行が履歴一覧に表示されていません。" +
                               "1つ上の行の残高を確認し、残高欄に金額を直接入力してください。";
                    case AutoBalanceUnavailableReason.EditDateChanged:
                        return "利用日を変更したため、この行が入る位置と直前の行が変わります。" +
                               "自動計算の起点を確定できないため、残高欄に金額を直接入力してください。";
                    default:
                        return "ONにすると、前の行の残高 + 受入 - 払出 で自動計算されます（アクセスキー: Alt+A）";
                }
            }
        }

        partial void OnAutoBalanceUnavailableReasonChanged(AutoBalanceUnavailableReason value)
        {
            OnPropertyChanged(nameof(CanAutoBalance));
            OnPropertyChanged(nameof(AutoBalanceToolTip));
        }

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
        /// 削除要求フラグ（ダイアログを閉じて呼び出し元で削除を実行する通知用）Issue #750
        /// </summary>
        [ObservableProperty]
        private bool _isDeleteRequested;

        /// <summary>
        /// 保存が可能か
        /// </summary>
        [ObservableProperty]
        private bool _canSave;

        /// <summary>
        /// Issue #1279: 最初のエラーが発生しているフィールドのプロパティ名。
        /// Dialog のコードビハインドがこの値を監視し、該当 TextBox にフォーカスを移動する。
        /// エラーなしの場合は null。
        /// </summary>
        [ObservableProperty]
        private string? _firstErrorField;

        /// <summary>
        /// 削除が可能か（Edit モードでは常に true）Issue #750 / Issue #1574。
        /// </summary>
        /// <remarks>
        /// 当初は貸出中レコードを保護するため <c>!IsLentRecord</c> を条件としていたが、
        /// 異常状態で残った「（貸出中）」行の復旧手段がなくなる問題（Issue #1574）を受け、
        /// Edit モードでは常に削除可能とした。誤操作防止は <see cref="RequestDelete"/> の
        /// 確認メッセージで担保する（貸出中レコードの場合は専用の警告を表示）。
        /// </remarks>
        [ObservableProperty]
        private bool _canDelete;

        /// <summary>
        /// 編集対象が「貸出中」状態の履歴行か（Issue #1574）。
        /// 削除確認メッセージの分岐に使用する。
        /// </summary>
        [ObservableProperty]
        private bool _isLentRecord;

        /// <summary>
        /// パンくずテキスト（Issue #1134）
        /// </summary>
        [ObservableProperty]
        private string _breadcrumbText = string.Empty;

        /// <summary>
        /// 「保存して次へ」ボタンを表示するか（Issue #1134）
        /// </summary>
        [ObservableProperty]
        private bool _showSaveAndNextButton;

        /// <summary>
        /// 「保存して次へ」が要求されたか（Issue #1134）
        /// </summary>
        [ObservableProperty]
        private bool _isSaveAndEditNextRequested;

        /// <summary>
        /// 「次へ（保存しない）」が要求されたか（Issue #1134）
        /// </summary>
        [ObservableProperty]
        private bool _isSkipToNextRequested;

        /// <summary>
        /// 「戻る」が要求されたか（Issue #1134）
        /// </summary>
        [ObservableProperty]
        private bool _isBackRequested;

        /// <summary>
        /// 全行リスト（挿入位置計算用）
        /// </summary>
        private List<LedgerDto> _allLedgers = new();

        /// <summary>
        /// <see cref="_allLedgers"/> の先頭がカードの履歴の先頭でもあるか（Addモード用、Issue #1740）
        /// </summary>
        private bool _historyStartsAtCardBeginning;

        /// <summary>
        /// Editモードで呼び出し元から渡された直前行の残高（Issue #1740）。
        /// null は「直前行を特定できない」を表す。
        /// </summary>
        private int? _editPreviousBalance;

        /// <summary>
        /// Editモードの初期利用日（Issue #1740）。ここから変わったら自動計算の起点が確定できなくなる。
        /// </summary>
        private DateTime _initialEditDate;

        /// <summary>
        /// 自動計算を ON にする直前の残高（Issue #1740）。OFF に戻したときの復元用。
        /// </summary>
        private int? _balanceBeforeAutoBalance;

        public LedgerRowEditViewModel(
            ILedgerRepository ledgerRepository,
            IStaffRepository staffRepository,
            OperationLogger operationLogger,
            DbContext dbContext)
        {
            _ledgerRepository = ledgerRepository;
            _staffRepository = staffRepository;
            _operationLogger = operationLogger;
            _dbContext = dbContext;
        }

        /// <summary>
        /// Addモードで初期化
        /// </summary>
        /// <param name="cardIdm">対象カードIDm</param>
        /// <param name="allLedgers">表示中の全履歴（挿入位置プレビュー用）</param>
        /// <param name="operatorIdm">認証済み職員IDm</param>
        /// <param name="historyStartsAtCardBeginning">
        /// <paramref name="allLedgers"/> の先頭が、そのカードの履歴の先頭でもあるか（Issue #1740）。
        /// true のときに限り、先頭への挿入（<c>InsertIndex == 0</c>）で直前残高 0 を起点にしてよい。
        /// false（ページ2以降・表示期間より前に履歴がある場合）では起点を特定できないため自動計算を無効化する。
        /// 既定値が false なのは、呼び出し元が渡し忘れても「0 起点で残高を破壊する」より
        /// 「自動計算が使えない」に倒すため。
        /// </param>
        public async Task InitializeForAddAsync(
            string cardIdm,
            List<LedgerDto> allLedgers,
            string operatorIdm,
            bool historyStartsAtCardBeginning = false)
        {
            _cardIdm = cardIdm;
            _operatorIdm = operatorIdm;
            _allLedgers = allLedgers;
            _historyStartsAtCardBeginning = historyStartsAtCardBeginning;

            Mode = LedgerRowEditMode.Add;
            DialogTitle = "履歴行の追加";
            EditDate = DateTime.Today;

            await LoadStaffListAsync();

            // 挿入位置を末尾に設定
            InsertIndex = _allLedgers.Count;
            UpdateContextRows();
            UpdateAutoBalanceAvailability();
            RecalculateBalance();
            Validate();

            OnPropertyChanged(nameof(IsAddMode));
        }

        /// <summary>
        /// Editモードで初期化
        /// </summary>
        /// <param name="ledgerDto">編集対象</param>
        /// <param name="operatorIdm">認証済み職員IDm</param>
        /// <param name="previousBalance">
        /// 履歴一覧の表示順で編集対象の直前にある行の残高（Issue #1740）。
        /// 自動計算の起点として使う。直前行が表示範囲に無い場合（ページ先頭行など）は null を渡すこと。
        /// null のときは自動計算を使用不可（<see cref="CanAutoBalance"/> = false）にして手入力のみとする。
        /// 既定値が null なのは、呼び出し元が渡し忘れても残高を破壊せず「便利機能が使えない」だけで済ませるため。
        /// </param>
        public async Task InitializeForEditAsync(LedgerDto ledgerDto, string operatorIdm, int? previousBalance = null)
        {
            _operatorIdm = operatorIdm;
            _cardIdm = ledgerDto.CardIdm;
            _editLedgerId = ledgerDto.Id;

            Mode = LedgerRowEditMode.Edit;
            DialogTitle = "履歴行の修正";

            // 完全なLedgerオブジェクトを取得
            // Issue #1740: 状態を書き換えるのはこの null チェックを通ってから。
            // 手前で IsAutoBalance を代入すると Validate() が走り、行が既に削除されている
            // （共有モードで他PCが削除した）ケースで空ダイアログに「摘要が空です」という
            // 実際の原因と無関係なエラーが出る。
            var ledger = await _ledgerRepository.GetByIdAsync(ledgerDto.Id);
            if (ledger == null) return;

            // Issue #1740: 金額の復元で誤った自動計算が走らないよう、値の代入より先に自動計算を止める。
            // （IsAutoBalance の既定値は true のため、Income/Expense の代入で RecalculateBalance が発火する）
            IsAutoBalance = false; // 編集モードでは手動入力開始
            _editPreviousBalance = previousBalance;
            _initialEditDate = ledger.Date;
            PreviousBalance = previousBalance ?? 0;
            UpdateAutoBalanceAvailability();

            EditDate = ledger.Date;
            Summary = ledger.Summary;
            Income = ledger.Income;
            Expense = ledger.Expense;
            Balance = ledger.Balance;
            Note = ledger.Note ?? string.Empty;

            await LoadStaffListAsync();

            // 現在の利用者を選択（Issue #1303）
            // 1) まず LenderIdm 完全一致を試行
            // 2) 一致が無い場合は StaffName でフォールバック
            //    - 旧バグ（返却時の利用 Ledger で LenderIdm 未設定）由来の行を救済
            //    - 論理削除等で LenderIdm が一致しない場合も同名アクティブ職員に紐づける
            //    - 同名別職員を選んでしまう可能性はあるが、物品出納簿は氏名表示のみで区別不可のため許容
            SelectedStaff = StaffList.FirstOrDefault(s => s.StaffIdm == ledger.LenderIdm);
            if (SelectedStaff == null && !string.IsNullOrEmpty(ledger.StaffName))
            {
                SelectedStaff = StaffList.FirstOrDefault(s => s.Name == ledger.StaffName);
            }

            // Issue #1574: 「（貸出中）」状態の履歴行が異常状態で残った場合の復旧手段として、
            // 貸出中レコードも削除可能とする。誤操作防止は RequestDelete() の確認メッセージで担保。
            CanDelete = true;
            IsLentRecord = ledgerDto.IsLentRecord;

            Validate();
            OnPropertyChanged(nameof(IsAddMode));

            // Issue #1134: 初期化完了後から変更追跡を開始
            _hasFieldChanges = false;
            _trackChanges = true;
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
            TrackFieldChange();
            RecalculateBalance();
            Validate();
        }

        /// <summary>
        /// Expense変更時のコールバック
        /// </summary>
        partial void OnExpenseChanged(int value)
        {
            TrackFieldChange();
            RecalculateBalance();
            Validate();
        }

        /// <summary>
        /// IsAutoBalance変更時のコールバック
        /// </summary>
        /// <remarks>
        /// Issue #1740: ON にすると自動計算値で残高を上書きするため、上書き前の値を退避し
        /// OFF に戻したときに復元する。復元手段が無いと、試しにチェックを入れて外しただけで
        /// 元の残高（DB 値）が失われ、そのまま保存されて不整合が下の行へ伝播する。
        /// </remarks>
        partial void OnIsAutoBalanceChanged(bool value)
        {
            if (value)
            {
                _balanceBeforeAutoBalance = Balance;
                RecalculateBalance();
            }
            else if (_balanceBeforeAutoBalance.HasValue)
            {
                Balance = _balanceBeforeAutoBalance.Value;
                _balanceBeforeAutoBalance = null;
            }
            Validate();
        }

        /// <summary>
        /// Balance変更時のコールバック
        /// </summary>
        partial void OnBalanceChanged(int value)
        {
            TrackFieldChange();
            Validate();
        }

        /// <summary>
        /// Summary変更時のコールバック
        /// </summary>
        partial void OnSummaryChanged(string value)
        {
            TrackFieldChange();
            Validate();
        }

        /// <summary>
        /// EditDate変更時のコールバック
        /// </summary>
        partial void OnNoteChanged(string value)
        {
            TrackFieldChange();
        }

        partial void OnEditDateChanged(DateTime value)
        {
            TrackFieldChange();
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
                UpdateAutoBalanceAvailability();
                RecalculateBalance();
            }
            else if (Mode == LedgerRowEditMode.Edit)
            {
                // Issue #1740: Editモードでも利用日は変更できる。日付を変えると行の入る位置が
                // 変わり、初期化時に確定した直前行はもう直前ではなくなるため、自動計算を無効化する。
                // （Addモードのように挿入位置プレビューを持たないため、直前行を引き直す手段が無い）
                UpdateAutoBalanceAvailability();
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
                UpdateAutoBalanceAvailability();
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
                UpdateAutoBalanceAvailability();
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
        /// 残高の自動計算が使えるかを現在の状態から判定して更新する（Issue #1740）。
        /// </summary>
        /// <remarks>
        /// 可否は固定ではなく、挿入位置の移動（Add）・利用日の変更（Add/Edit）で変わる。
        /// 使えなくなったときは <see cref="IsAutoBalance"/> も解除し、残高欄が編集可能になることで
        /// 利用者に状態の変化が見えるようにする（チェックが入ったまま計算されない状態を作らない）。
        /// </remarks>
        private void UpdateAutoBalanceAvailability()
        {
            AutoBalanceUnavailableReason reason;

            if (Mode == LedgerRowEditMode.Add)
            {
                // 先頭への挿入で起点 0 が正しいのは、一覧の先頭がカードの履歴の先頭でもあるときだけ。
                // ページ2以降や表示期間より前に履歴がある場合、0 を起点にすると残高を破壊する。
                reason = (InsertIndex > 0 || _historyStartsAtCardBeginning)
                    ? AutoBalanceUnavailableReason.None
                    : AutoBalanceUnavailableReason.PreviousRowNotIdentified;
            }
            else if (!_editPreviousBalance.HasValue)
            {
                reason = AutoBalanceUnavailableReason.PreviousRowNotIdentified;
            }
            else
            {
                // 利用日を変えると行の入る位置が変わり、初期化時の直前行はもう直前ではない
                reason = EditDate.Date == _initialEditDate.Date
                    ? AutoBalanceUnavailableReason.None
                    : AutoBalanceUnavailableReason.EditDateChanged;
            }

            if (reason == AutoBalanceUnavailableReason) return;

            AutoBalanceUnavailableReason = reason;

            if (reason != AutoBalanceUnavailableReason.None && IsAutoBalance)
            {
                // OnIsAutoBalanceChanged が自動計算前の残高を復元する
                IsAutoBalance = false;
            }
        }

        /// <summary>
        /// 残高を再計算
        /// </summary>
        private void RecalculateBalance()
        {
            if (!IsAutoBalance) return;

            // Issue #1740: 直前行の残高が特定できていないときは残高に触れない。
            // Editモードで PreviousBalance が既定値 0 のまま計算され、DBの正しい残高を
            // 「0 + 受入 - 払出」で上書きしていた不具合への fail-safe。
            if (!CanAutoBalance) return;

            if (Mode == LedgerRowEditMode.Add)
            {
                // 挿入位置の直前行の残高を取得。
                // InsertIndex == 0 で 0 を起点にできるのは一覧の先頭がカードの履歴の先頭のときだけで、
                // それ以外は UpdateAutoBalanceAvailability が自動計算を無効化しているためここへ来ない。
                PreviousBalance = (InsertIndex > 0 && InsertIndex <= _allLedgers.Count)
                    ? _allLedgers[InsertIndex - 1].Balance
                    : 0;
            }
            // Editモードは編集対象行が固定のため、InitializeForEditAsync が設定した
            // PreviousBalance をそのまま使う（Addモードのように毎回引き直す必要がない）。

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
            // Issue #1279: 最初のエラーフィールドをリセット
            FirstErrorField = null;

            // Issue #1275: エラーメッセージに「何が/なぜ/どうすれば」の3要素を含める

            // 摘要が空かチェック
            if (string.IsNullOrWhiteSpace(Summary))
            {
                ValidationMessage = "摘要が空です。鉄道、バス、チャージ等の取引内容を入力してください。";
                CanSave = false;
                FirstErrorField = nameof(Summary);
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
                ValidationMessage =
                    $"計算後の残高が {Balance:N0}円（マイナス）になります。" +
                    "受入金額を増やすか、払出金額を減らしてください。";
                CanSave = false;
                FirstErrorField = nameof(Balance);
                return;
            }

            // Income/Expenseが負かチェック
            if (Income < 0)
            {
                ValidationMessage =
                    $"受入金額に負の値（{Income:N0}円）が設定されています。" +
                    "0以上の金額を入力してください。";
                CanSave = false;
                FirstErrorField = nameof(Income);
                return;
            }
            if (Expense < 0)
            {
                ValidationMessage =
                    $"払出金額に負の値（{Expense:N0}円）が設定されています。" +
                    "0以上の金額を入力してください。";
                CanSave = false;
                FirstErrorField = nameof(Expense);
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
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "台帳行の保存");
                StatusMessage = ExceptionMessageFormatter.ToUserMessage(ex, "保存");
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

            // Issue #1458: Ledger INSERT と監査ログ INSERT を同一トランザクションで実行
            using var scope = await _dbContext.BeginTransactionAsync();
            var newId = await _ledgerRepository.InsertAsync(newLedger, scope.Transaction);
            if (newId > 0)
            {
                newLedger.Id = newId;
                await _operationLogger.LogLedgerInsertAsync(newLedger, scope.Transaction);
                scope.Commit();
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

            // Issue #1458: Ledger UPDATE と監査ログ INSERT を同一トランザクションで実行
            bool result;
            using (var scope = await _dbContext.BeginTransactionAsync())
            {
                result = await _ledgerRepository.UpdateAsync(ledger, scope.Transaction);
                if (result)
                {
                    await _operationLogger.LogLedgerUpdateAsync(beforeLedger, ledger, scope.Transaction);
                    scope.Commit();
                }
            }

            if (result)
            {
                // Issue #983: 摘要編集時にバス停名をDetailに同期
                // 摘要を直接編集するとLedger.Summaryは更新されるがDetail.BusStopsは更新されない。
                // この不整合を放置すると、統合時にSummaryGenerator.Generate()が
                // Detail.BusStopsから摘要を再生成し、修正前のバス停名に戻ってしまう。
                // SMB 共有モードのロック競合を避けるため tx の外で実行する。
                if (beforeLedger.Summary != ledger.Summary)
                {
                    await SyncBusStopsFromSummaryAsync(ledger);
                }

                IsSaved = true;
            }
            else
            {
                StatusMessage = "保存に失敗しました";
            }
        }

        /// <summary>
        /// 摘要からバス停名を抽出してDetail.BusStopsに同期する（Issue #983）
        /// </summary>
        /// <remarks>
        /// 摘要の直接編集ではLedger.Summaryのみ更新されDetail.BusStopsは未更新のため、
        /// 統合時の摘要再生成で修正前のバス停名に戻る問題を防止する。
        /// </remarks>
        private async Task SyncBusStopsFromSummaryAsync(Ledger ledger)
        {
            var busDetails = ledger.Details.Where(d => d.IsBus).ToList();
            if (busDetails.Count == 0) return;

            // Issue #1818: 抽出パターンは組織設定 BusLabel から導出する
            if (!SummaryGenerator.TryExtractBusStops(ledger.Summary, out var busStopText)) return;

            var busStopUpdates = new List<(int SequenceNumber, string BusStops)>();

            if (busDetails.Count == 1)
            {
                busStopUpdates.Add((busDetails[0].SequenceNumber, busStopText));
            }
            else
            {
                // 複数件のバス利用: 「、」で分割してDetailに対応付け
                var parts = busStopText.Split('、');
                if (parts.Length == busDetails.Count)
                {
                    for (int i = 0; i < parts.Length; i++)
                    {
                        busStopUpdates.Add((busDetails[i].SequenceNumber, parts[i]));
                    }
                }
            }

            if (busStopUpdates.Count > 0)
            {
                await _ledgerRepository.UpdateDetailBusStopsAsync(ledger.Id, busStopUpdates);
            }
        }

        /// <summary>
        /// 削除を要求（Issue #750 / Issue #1574）。
        /// </summary>
        /// <remarks>
        /// 実際の削除は呼び出し元（MainViewModel）で行う。ダイアログは確認後に閉じる。
        /// 貸出中レコード（Issue #1574）の場合は専用の警告メッセージを表示する。
        /// </remarks>
        [RelayCommand]
        private void RequestDelete()
        {
            if (!CanDelete) return;

            var message = IsLentRecord
                ? "この履歴は「貸出中」状態のレコードです。\n" +
                  "削除すると、このカードの貸出中状態も解消されます\n" +
                  "（他に貸出中レコードが残っている場合は維持されます）。\n\n" +
                  "通常は、メイン画面で交通系ICカードをタッチして返却操作を\n" +
                  "行うのが正しい復旧方法です。それでも削除しますか？\n\n" +
                  "削除した履歴は元に戻せません。"
                : "この履歴を削除してよろしいですか？\n\n削除した履歴は元に戻せません。";

            var result = System.Windows.MessageBox.Show(
                message,
                "履歴の削除", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;

            IsDeleteRequested = true;
        }

        /// <summary>
        /// キャンセル
        /// </summary>
        [RelayCommand]
        private void Cancel()
        {
            // 何もせずに閉じる（IsSavedはfalseのまま）
        }

        /// <summary>
        /// 保存して次の履歴を編集（Issue #1134）
        /// </summary>
        [RelayCommand]
        private async Task SaveAndEditNext()
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

                // IsSavedがtrueになった場合、代わりにIsSaveAndEditNextRequestedを設定
                if (IsSaved)
                {
                    IsSaved = false;
                    IsSaveAndEditNextRequested = true;
                }
            }
            catch (Exception ex)
            {
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "台帳行の保存");
                StatusMessage = ExceptionMessageFormatter.ToUserMessage(ex, "保存");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// パンくずテキストを設定（Issue #1134: 詳細画面から開かれた場合用）
        /// </summary>
        public void SetBreadcrumb(string text)
        {
            BreadcrumbText = text;
        }

        /// <summary>
        /// 保存せず次の履歴へ（Issue #1134）
        /// </summary>
        [RelayCommand]
        private void SkipToNext()
        {
            if (HasUnsavedChanges())
            {
                var result = System.Windows.MessageBox.Show(
                    "変更が保存されていません。破棄して次へ進みますか？",
                    "確認", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                if (result != System.Windows.MessageBoxResult.Yes) return;
            }
            IsSkipToNextRequested = true;
        }

        /// <summary>
        /// 前の履歴へ戻る（Issue #1134）
        /// </summary>
        [RelayCommand]
        private void Back()
        {
            if (HasUnsavedChanges())
            {
                var result = System.Windows.MessageBox.Show(
                    "変更が保存されていません。破棄して前へ戻りますか？",
                    "確認", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                if (result != System.Windows.MessageBoxResult.Yes) return;
            }
            IsBackRequested = true;
        }

        /// <summary>
        /// 未保存の変更があるか判定（Issue #1134）
        /// </summary>
        /// <remarks>
        /// Editモードで初期値と現在値を比較。Addモードではフィールドに入力があれば変更ありとする。
        /// </remarks>
        private bool HasUnsavedChanges()
        {
            if (Mode == LedgerRowEditMode.Add)
            {
                return !string.IsNullOrWhiteSpace(Summary) || Income != 0 || Expense != 0;
            }

            // Editモード: 初期値が設定されていれば比較
            return _hasFieldChanges;
        }

        /// <summary>
        /// フィールド変更フラグ（Editモード用）
        /// </summary>
        private bool _hasFieldChanges;

        /// <summary>
        /// 初期化完了後に変更追跡を開始するフラグ
        /// </summary>
        private bool _trackChanges;

        /// <summary>
        /// フィールド変更を追跡する
        /// </summary>
        private void TrackFieldChange()
        {
            if (_trackChanges) _hasFieldChanges = true;
        }
    }
}
