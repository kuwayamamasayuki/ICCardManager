#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Security;
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

        /// <summary>
        /// 確認ダイアログの表示（Issue #1837 で <c>MessageBox.Show</c> 直呼びから移行）
        /// </summary>
        /// <remarks>
        /// オーナーを渡さない <c>MessageBox</c> は WPF が <c>GetActiveWindow()</c> で解決するため、
        /// アプリが非フォアグラウンドのときは ownerless になり背後のウィンドウが無効化されない
        /// （Issue #1794）。<c>IDialogService</c> は <c>DialogOwnerResolver</c> でオーナーを解決する。
        /// </remarks>
        private readonly IDialogService _dialogService;

        private string _cardIdm = string.Empty;
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
        /// 同行者数（本人を含まない。Issue #1906）
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayStaffNamePreview))]
        private int _companionCount;

        /// <summary>
        /// 氏名欄の表示プレビュー（「博多 花子 外1名」、Issue #1906）
        /// </summary>
        public string DisplayStaffNamePreview =>
            CompanionCount < 0 ? string.Empty : StaffNameFormatter.Format(ResolveSelectedStaffName(), CompanionCount);

        /// <summary>
        /// Issue #2300: 編集対象行の利用者が職員一覧（在籍職員）に無いとき、その利用者をこの行の
        /// 選択肢として一覧の先頭へ加えたもの。無ければ null。
        /// </summary>
        /// <remarks>
        /// 一覧に無い利用者（論理削除した職員・CSV で取り込んだ職員マスタに無い氏名）の行で
        /// <see cref="SelectedStaff"/> を null のままにすると、保存が「利用者をクリアした」と区別できず、
        /// 別の項目だけを直した保存で <c>staff_name</c> / <c>lender_idm</c> が NULL で上書きされていた
        /// （staff を論理削除にしている理由＝履歴参照時に氏名を表示する、に反する）。
        /// 選択肢として持たせれば「そのまま」「クリア（×）」「別の職員へ付け替え」の 3 つが
        /// 画面上の状態としてそのまま区別でき、空欄に見えるのに氏名が残る、という見えない状態も作らない。
        /// </remarks>
        private Staff? _unlistedStaffEntry;

        /// <summary>
        /// Issue #2300: <see cref="_unlistedStaffEntry"/> を作ったときの行の氏名（氏名欄の表示プレビュー用）
        /// </summary>
        private string? _unlistedStaffOriginalName;

        /// <summary>
        /// Issue #2300: 一覧に無い利用者（<see cref="_unlistedStaffEntry"/>）を選択中か（注記の表示条件）
        /// </summary>
        public bool IsUnlistedStaffSelected =>
            _unlistedStaffEntry != null && ReferenceEquals(SelectedStaff, _unlistedStaffEntry);

        /// <summary>
        /// Issue #2300: 一覧に無い利用者の選択肢の表示名。ドロップダウンで在籍職員と見分けられるよう目印を付ける。
        /// </summary>
        /// <remarks>
        /// 氏名が無く貸出者 IDm だけが残る行は、誰の記録かを見分けられるよう職員証の IDm を添える。
        /// IDm は職員証の唯一の認証要素なので、生の値ではなく <see cref="IdmMasker.Mask"/> を通した値にする
        /// （末尾 4 文字で個体を識別できる。#1940）。表示名は保存に使わない（この選択肢のまま保存するときは利用者の列に触れず、保存直前に読み直した行の値を残す）。
        /// </remarks>
        internal static string FormatUnlistedStaffLabel(string? staffName, string? lenderIdm)
        {
            // 空白だけの氏名は氏名なしとして扱う（StaffNameFormatter.Format と同じ判定）
            if (staffName is not null && !string.IsNullOrWhiteSpace(staffName))
            {
                return $"{staffName}（職員一覧にいません）";
            }

            return $"氏名なし（職員証 {IdmMasker.Mask(lenderIdm ?? string.Empty)}、職員一覧にいません）";
        }

        /// <summary>
        /// Issue #2300: 一覧に無い利用者を選択中に表示する注記
        /// </summary>
        public string UnlistedStaffNotice =>
            "この利用者は職員一覧にいません（削除した職員や、CSVで取り込んだ氏名など）。" +
            "ほかの項目だけを直して保存しても、利用者はそのまま残ります。" +
            "利用者を変える場合は一覧から選び直し、空欄にする場合は「×」を押してください。";

        /// <summary>
        /// 職員リスト
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<Staff> _staffList = new();

        /// <summary>
        /// 選択中の職員
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayStaffNamePreview))]
        private Staff? _selectedStaff;

        /// <summary>
        /// 挿入位置の直前の行（最大 2 行、Addモード用。挿入位置プレビューでマーカーの上に描く）
        /// </summary>
        /// <remarks>
        /// Issue #2239: 前後の行を 1 つの一覧にまとめてマーカーをその下に描くと、マーカーが実際の
        /// 挿入位置より最大 2 行下に見える。前の行と後の行を分けて持ち、マーカーを両者の間に描く。
        /// </remarks>
        public ObservableCollection<LedgerDto> RowsBeforeInsert { get; } = new();

        /// <summary>
        /// 挿入位置の直後の行（最大 2 行、Addモード用。挿入位置プレビューでマーカーの下に描く）
        /// </summary>
        public ObservableCollection<LedgerDto> RowsAfterInsert { get; } = new();

        /// <summary>
        /// 挿入位置（<see cref="_allLedgers"/> 内のインデックス。この位置の行の直前に挿入する）
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
        /// Issue #2007: 呼び出し元（MainViewModel）が全期間の残高整合性チェックで検知した
        /// 導入時残高の訂正案。無ければ null。
        /// </summary>
        private InitialBalanceCorrection? _initialBalanceCorrection;

        /// <summary>
        /// Issue #2007: 導入時残高の訂正案があるか（提案エリアの表示条件・適用コマンドの実行可否）
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyInitialBalanceSuggestionCommand))]
        private bool _hasInitialBalanceSuggestion;

        /// <summary>
        /// Issue #2007: 提案エリアの文言（何が／なぜ／どうすれば）
        /// </summary>
        [ObservableProperty]
        private string _initialBalanceSuggestionText = string.Empty;

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

        /// <summary>
        /// Issue #2325: Addモードで、自動計算が使えなくなったため残高が利用者の入力を待っている（未入力）か。
        /// </summary>
        /// <remarks>
        /// Addモードは自動計算 ON で始まり、ON にする直前の残高（<see cref="_balanceBeforeAutoBalance"/>）を持たない。
        /// 挿入位置を先頭へ動かして自動計算が使えなくなると、前の位置で計算した残高が手入力の値のように残り、
        /// そのまま保存すると根拠の無い残高が台帳へ入っていた。使えなくなった時点で残高を 0 に戻してこのフラグを立て、
        /// 利用者が残高を入力する（または自動計算を ON に戻す）まで警告し、保存の前に確認する。
        /// </remarks>
        private bool _isAddBalanceNotEntered;

        /// <summary>
        /// Issue #2325: Editモードで、競合の案内に名指しする対象（ダイアログを開いた時点の日付・摘要）。
        /// 未保存の入力値では名指ししない（#1759）。
        /// </summary>
        private string _editTargetLabel = string.Empty;

        /// <summary>
        /// Issue #2325: 保存で、編集対象の行がもう無いこと（他の PC・返却による削除）を検出したか。
        /// </summary>
        private bool _conflictDetected;

        /// <summary>
        /// ダイアログを閉じたあと、呼び出し元が履歴一覧を読み込み直す必要があるか（Issue #2325）
        /// </summary>
        /// <remarks>
        /// 保存で競合（編集対象の行が他 PC や返却で削除された）を検出したとき true。何も保存していないが、
        /// 一覧に残っている行はもう DB と一致しないため、閉じたら読み込み直す
        /// （#1759「一覧を確認するよう案内するなら、案内する側が先に再読込する」。<c>LedgerDetailViewModel</c> と同じ形）。
        /// 保存できたときは <see cref="IsSaved"/>（呼び出し元はダイアログの結果 true で読み直す）が担う。
        /// </remarks>
        public bool NeedsHistoryReload => _conflictDetected || _busStopConflictDetected;

        /// <summary>
        /// Issue #2325: バス停名の同期が競合した（明細が他 PC や別の編集で変わった）か。行は残っているので保存は塞がないが、
        /// 案内どおり閉じたら一覧を読み直させる（コードレビューで検出）。
        /// </summary>
        private bool _busStopConflictDetected;

        /// <summary>
        /// Issue #2325: 「次へ」「戻る」を実行できるか。編集対象の行がもう無いと分かった後は、閉じると一覧を読み直して
        /// 隣の行へは進まない（呼び出し元）ので、押せても閉じるだけになる。押せないようにして行動と結果を一致させる。
        /// </summary>
        public bool CanNavigateToAdjacent => !_conflictDetected && !_busStopConflictDetected;

        /// <summary>
        /// Issue #2325: バス停名の同期の競合の印を設定し、「次へ」「戻る」の可否を更新する。
        /// 競合の後は閉じると一覧を読み直して隣の行へは進まないので、押せても閉じるだけになる（コードレビュー 2 回目）。
        /// </summary>
        private void SetBusStopConflictDetected(bool value)
        {
            if (_busStopConflictDetected == value)
            {
                return;
            }

            _busStopConflictDetected = value;
            SkipToNextCommand.NotifyCanExecuteChanged();
            BackCommand.NotifyCanExecuteChanged();
        }

        public LedgerRowEditViewModel(
            ILedgerRepository ledgerRepository,
            IStaffRepository staffRepository,
            OperationLogger operationLogger,
            DbContext dbContext,
            IDialogService dialogService)
        {
            _ledgerRepository = ledgerRepository;
            _staffRepository = staffRepository;
            _operationLogger = operationLogger;
            _dbContext = dbContext;
            _dialogService = dialogService;
        }

        /// <summary>
        /// Addモードで初期化
        /// </summary>
        /// <param name="cardIdm">対象カードIDm</param>
        /// <param name="allLedgers">表示中の全履歴（挿入位置プレビュー用）</param>
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
            bool historyStartsAtCardBeginning = false)
        {
            _cardIdm = cardIdm;
            _allLedgers = allLedgers;
            _historyStartsAtCardBeginning = historyStartsAtCardBeginning;

            Mode = LedgerRowEditMode.Add;
            DialogTitle = "履歴行の追加";
            EditDate = DateTime.Today;

            await LoadStaffListAsync();

            // 挿入位置を末尾に設定
            InsertIndex = _allLedgers.Count;
            UpdateInsertPreviewRows();
            UpdateAutoBalanceAvailability();
            RecalculateBalance();
            Validate();

            OnPropertyChanged(nameof(IsAddMode));
        }

        /// <summary>
        /// Editモードで初期化
        /// </summary>
        /// <param name="ledgerDto">編集対象</param>
        /// <param name="previousBalance">
        /// 履歴一覧の表示順で編集対象の直前にある行の残高（Issue #1740）。
        /// 自動計算の起点として使う。直前行が表示範囲に無い場合（ページ先頭行など）は null を渡すこと。
        /// null のときは自動計算を使用不可（<see cref="CanAutoBalance"/> = false）にして手入力のみとする。
        /// 既定値が null なのは、呼び出し元が渡し忘れても残高を破壊せず「便利機能が使えない」だけで済ませるため。
        /// </param>
        /// <param name="initialBalanceCorrection">
        /// Issue #2007: 導入時残高の訂正案。呼び出し元が全期間の残高整合性チェックで、この行が
        /// 「導入時残高の誤り」の形状の導入行だと検知したときだけ渡す。無ければ null。
        /// </param>
        public async Task InitializeForEditAsync(
            LedgerDto ledgerDto, int? previousBalance = null,
            InitialBalanceCorrection? initialBalanceCorrection = null)
        {
            _cardIdm = ledgerDto.CardIdm;
            _editLedgerId = ledgerDto.Id;
            // Issue #2325: 競合の案内で名指しする対象は、開いた時点の値で確定させる（削除の案内と同じ表記）
            _editTargetLabel = $"履歴「{ledgerDto.DateDisplay} {ledgerDto.Summary}」";

            Mode = LedgerRowEditMode.Edit;
            DialogTitle = "履歴行の修正";

            // 完全なLedgerオブジェクトを取得
            // Issue #1740: 状態を書き換えるのはこの null チェックを通ってから。
            // 手前で IsAutoBalance を代入すると Validate() が走り、行が既に削除されている
            // （共有モードで他PCが削除した）ケースで空ダイアログに「摘要が空です」という
            // 実際の原因と無関係なエラーが出る。
            var ledger = await _ledgerRepository.GetByIdAsync(ledgerDto.Id);
            if (ledger == null)
            {
                return;
            }

            // Issue #1740: 金額の復元で誤った自動計算が走らないよう、値の代入より先に自動計算を止める。
            // （IsAutoBalance の既定値は true のため、Income/Expense の代入で RecalculateBalance が発火する）
            IsAutoBalance = false; // 編集モードでは手動入力開始
            _editPreviousBalance = previousBalance;
            _initialEditDate = ledger.Date;
            // Issue #2325: 摘要の代入（Validate が走る）より前に置く
            _isPointRedemptionRow = ledger.Details.Count > 0 && ledger.Details.All(d => d.IsPointRedemption);
            PreviousBalance = previousBalance ?? 0;
            UpdateAutoBalanceAvailability();

            EditDate = ledger.Date;
            Summary = ledger.Summary;
            Income = ledger.Income;
            Expense = ledger.Expense;
            Balance = ledger.Balance;
            Note = ledger.Note ?? string.Empty;
            CompanionCount = ledger.CompanionCount;

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

            // Issue #2300: IDm・氏名のどちらでも在籍職員に紐づかないが利用者の記録がある行は、
            // その利用者をこの行だけの選択肢として加えて選択する（null のままにすると保存で消える）
            if (SelectedStaff == null
                && (!string.IsNullOrWhiteSpace(ledger.StaffName) || !string.IsNullOrWhiteSpace(ledger.LenderIdm)))
            {
                _unlistedStaffOriginalName = ledger.StaffName;
                _unlistedStaffEntry = new Staff
                {
                    StaffIdm = ledger.LenderIdm ?? string.Empty,
                    Name = FormatUnlistedStaffLabel(ledger.StaffName, ledger.LenderIdm),
                };
                StaffList.Insert(0, _unlistedStaffEntry);
                SelectedStaff = _unlistedStaffEntry;
            }

            // Issue #1574: 「（貸出中）」状態の履歴行が異常状態で残った場合の復旧手段として、
            // 貸出中レコードも削除可能とする。誤操作防止は RequestDelete() の確認メッセージで担保。
            CanDelete = true;
            IsLentRecord = ledgerDto.IsLentRecord;

            // Issue #2007: 導入時残高の訂正案は、この行に対するものだけを受け付ける
            // （呼び出し元が別の行の提案を渡しても、無関係な行の値を書き換えさせない）。
            _initialBalanceCorrection =
                initialBalanceCorrection != null && initialBalanceCorrection.LedgerId == ledgerDto.Id
                    ? initialBalanceCorrection
                    : null;
            HasInitialBalanceSuggestion = _initialBalanceCorrection != null;
            InitialBalanceSuggestionText = _initialBalanceCorrection != null
                ? InitialBalanceCorrectionMessage.ForEditDialog(_initialBalanceCorrection)
                : string.Empty;

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
            // Issue #2300: 一覧を作り直すときは、前の行のために加えた「一覧に無い利用者」も捨てる
            _unlistedStaffEntry = null;
            _unlistedStaffOriginalName = null;
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
                _isAddBalanceNotEntered = false;
                // Issue #2325（コードレビュー 3 回目）: Addモードで退避するのは、利用者が OFF の間に入力した残高だけ。
                // OFF にしただけ（入力なし）の残高は前の挿入位置で計算した値であり、退避すると先頭へ動かしたときに
                // 「復元」されて、手入力の値として保存され得る（この Issue の欠陥そのもの）
                _balanceBeforeAutoBalance = Mode == LedgerRowEditMode.Add && !_isAddBalanceEnteredWhileManual
                    ? null
                    : Balance;
                RecalculateBalance();
            }
            else if (_balanceBeforeAutoBalance.HasValue)
            {
                SetBalanceProgrammatically(_balanceBeforeAutoBalance.Value);
                _balanceBeforeAutoBalance = null;
                // 退避していたのは利用者が入力した値で、_isAddBalanceEnteredWhileManual は立ったまま（下ろす操作は無い）なので、
                // 復元した残高も利用者の値として扱われる。一度立つと下りないため、復元する値はその後の挿入位置の移動や
                // 金額の変更から見て古くなり得る（「OFF に戻したときは ON にする直前の残高を復元する」§3.12 の範囲内。
                // 07 UT-029a8 No.41c で固定）
            }
            Validate();
        }

        /// <summary>
        /// Balance変更時のコールバック
        /// </summary>
        partial void OnBalanceChanged(int value)
        {
            // Issue #2325: 残高が変わった（利用者が入力した）ら、未入力の状態は終わる。
            // 自動計算が使えなくなったときに 0 へ戻す代入もここを通るが、フラグはその後で立てる
            _isAddBalanceNotEntered = false;
            if (!IsAutoBalance && !_isSettingBalanceProgrammatically)
            {
                _isAddBalanceEnteredWhileManual = true;
            }
            TrackFieldChange();
            Validate();
        }

        /// <summary>
        /// Issue #2325: 自動計算 OFF の間に利用者が残高を入力したか（Addモードで、ON にするときに退避するかの判断に使う）
        /// </summary>
        private bool _isAddBalanceEnteredWhileManual;

        /// <summary>
        /// Issue #2325: プログラムが残高を代入している最中か（利用者の入力と区別するため）
        /// </summary>
        private bool _isSettingBalanceProgrammatically;

        /// <summary>
        /// Issue #2325: 利用者の入力として数えずに残高を代入する（復元・未入力への 0）
        /// </summary>
        private void SetBalanceProgrammatically(int value)
        {
            _isSettingBalanceProgrammatically = true;
            try
            {
                Balance = value;
            }
            finally
            {
                _isSettingBalanceProgrammatically = false;
            }
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

        partial void OnCompanionCountChanged(int value)
        {
            TrackFieldChange();
            Validate();
        }

        // Issue #2141: 利用者の付け替えも未保存の変更として追跡する（追跡していなかったため、
        // 利用者だけを変えて Esc・✕ で閉じると確認なしで変更が消えた）。初期化中は _trackChanges が false
        partial void OnSelectedStaffChanged(Staff? value)
        {
            TrackFieldChange();
            OnPropertyChanged(nameof(IsUnlistedStaffSelected));
        }

        /// <summary>
        /// Issue #2300: 選択中の利用者の氏名（一覧に無い利用者なら、行に記録されていた氏名そのもの）
        /// </summary>
        private string? ResolveSelectedStaffName() =>
            IsUnlistedStaffSelected ? _unlistedStaffOriginalName : SelectedStaff?.Name;

        partial void OnEditDateChanged(DateTime value)
        {
            TrackFieldChange();
            if (Mode == LedgerRowEditMode.Add && _allLedgers.Count > 0)
            {
                // 日付に基づいて挿入位置を自動調整。
                // Issue #2239: ここは時刻込みで比べ、Validate の日付の警告は日単位で比べる。利用日の入力は常に 0 時なので、
                // 同じ日の行のうち時刻を持つもの（貸出中レコード）の手前へ入り、日単位の警告とは食い違わない
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
                UpdateInsertPreviewRows();
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
        /// Issue #2007: 導入時残高の訂正案を受入・残額へ適用する。
        /// </summary>
        /// <remarks>
        /// 新規購入・前年度より繰越は受入欄にも残高を書く行なので受入と残額の両方を、
        /// ○月から繰越は残額だけを逆算値にする（<see cref="InitialBalanceCorrection.AppliesToIncome"/>）。
        /// 片方だけ直すと月次帳票の「受入 − 払出 = 残額」が崩れたまま残るため、1 操作で揃える。
        /// 保存は通常の行編集と同じ経路（監査ログも同じ）。
        /// </remarks>
        [RelayCommand(CanExecute = nameof(HasInitialBalanceSuggestion))]
        private void ApplyInitialBalanceSuggestion()
        {
            if (_initialBalanceCorrection == null)
            {
                return;
            }

            // 自動計算が ON のままだと、Income の代入で Balance が「前行 + 受入」へ再計算され、
            // さらに OFF へ戻したときに適用前の残高（_balanceBeforeAutoBalance）が復元されて適用が消える。
            // 適用は手入力値の確定なので、先に OFF にしてから Income → Balance の順に代入する。
            IsAutoBalance = false;
            if (_initialBalanceCorrection.AppliesToIncome)
            {
                Income = _initialBalanceCorrection.SuggestedBalance;
            }
            Balance = _initialBalanceCorrection.SuggestedBalance;
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
                UpdateInsertPreviewRows();
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
                UpdateInsertPreviewRows();
                UpdateAutoBalanceAvailability();
                RecalculateBalance();
                Validate();
            }
        }

        /// <summary>
        /// 挿入位置プレビューの直前の行・直後の行を更新
        /// </summary>
        private void UpdateInsertPreviewRows()
        {
            RowsBeforeInsert.Clear();
            RowsAfterInsert.Clear();

            // 挿入位置の前後2行ずつを、マーカーの上（前の行）と下（後の行）に分けて表示する（Issue #2239）
            var startIdx = Math.Max(0, InsertIndex - 2);
            var endIdx = Math.Min(_allLedgers.Count, InsertIndex + 2);

            for (int i = startIdx; i < endIdx; i++)
            {
                (i < InsertIndex ? RowsBeforeInsert : RowsAfterInsert).Add(_allLedgers[i]);
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

            if (reason == AutoBalanceUnavailableReason)
            {
                return;
            }

            AutoBalanceUnavailableReason = reason;

            if (reason != AutoBalanceUnavailableReason.None && IsAutoBalance)
            {
                // 復元する値（自動計算を ON にする直前の残高）があるかは、OFF にする前に確かめる
                // （OnIsAutoBalanceChanged が復元と同時に退避値を捨てる）
                var hasValueToRestore = _balanceBeforeAutoBalance.HasValue;

                // OnIsAutoBalanceChanged が自動計算前の残高を復元する
                IsAutoBalance = false;

                // Issue #2325: Addモードは自動計算 ON で始まるため、利用者が一度も OFF にしていなければ復元する値を持たず、
                // 残高には前の挿入位置で計算した値が残る。それは今の位置の残高ではない（起点が分からない）ので、
                // 確定値に見せない。0 に戻して未入力として扱い、入力を促す（Validate の警告・保存前の確認）。
                // 復元する値があったとき（OFF にして入力した残高から ON にした）は、その値を残す（§3.12「OFF に戻したときに復元する」）
                if (Mode == LedgerRowEditMode.Add && !hasValueToRestore)
                {
                    SetBalanceProgrammatically(0);
                    _isAddBalanceNotEntered = true;
                }
            }
            else if (reason != AutoBalanceUnavailableReason.None && Mode == LedgerRowEditMode.Add
                     && !_isAddBalanceEnteredWhileManual && !_isAddBalanceNotEntered)
            {
                // Issue #2325（コードレビュー 4 回目）: 自動計算を自分で OFF にした（入力なし）まま使えない位置へ動いた。
                // 残高は前の位置で計算した値のままで、今の位置の残高ではないので、上と同じく未入力として扱う。
                // OFF の間に入力した残高があれば（_isAddBalanceEnteredWhileManual）、それは利用者の値なので残す。
                // 使える位置で OFF にしたときに計算値を残す挙動（手で微調整するための OFF）はここに来ないので変わらない
                SetBalanceProgrammatically(0);
                _isAddBalanceNotEntered = true;
            }
            else if (reason == AutoBalanceUnavailableReason.None && Mode == LedgerRowEditMode.Add && _isAddBalanceNotEntered)
            {
                // Issue #2325（コードレビューで検出）: 未入力のまま自動計算が使える位置へ戻ったら、自動計算を ON に戻す。
                // 戻さないと「直前の行が表示されていないため自動計算できません」という警告と保存前の確認が、
                // 起点が分かる今の位置について事実と食い違う。ON にすると未入力の扱いも解ける（OnIsAutoBalanceChanged）。
                // 利用者が残高を入力していれば（フラグは解けている）その値を残す
                IsAutoBalance = true;
                // ON にした時点の 0 は利用者の値ではないので退避しない（退避すると、もう一度先頭へ動かしたときに
                // 「復元する値がある」扱いになり、未入力の扱いが立たない）
                _balanceBeforeAutoBalance = null;
            }
        }

        /// <summary>
        /// 残高を再計算
        /// </summary>
        private void RecalculateBalance()
        {
            if (!IsAutoBalance)
            {
                return;
            }

            // Issue #1740: 直前行の残高が特定できていないときは残高に触れない。
            // Editモードで PreviousBalance が既定値 0 のまま計算され、DBの正しい残高を
            // 「0 + 受入 - 払出」で上書きしていた不具合への fail-safe。
            if (!CanAutoBalance)
            {
                return;
            }

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
        /// 警告メッセージを追加する（Issue #1914）
        /// </summary>
        /// <remarks>
        /// <see cref="WarningMessage"/> へ直接代入すると、後から立った警告が先の警告を
        /// 消して「最後の 1 つ」しか残らない（Issue #1811 と同型）。複数該当し得る以上、
        /// 追加は必ず本メソッドを通して改行で連結する。
        /// </remarks>
        private void AddWarning(string message)
        {
            WarningMessage = string.IsNullOrEmpty(WarningMessage)
                ? message
                : WarningMessage + Environment.NewLine + message;
        }

        /// <summary>
        /// 受入・払出が両方 0 円でも正当な行か（「両方0円」警告の除外。Issue #2325）
        /// </summary>
        /// <remarks>
        /// 導入行（新規購入・○月から繰越・前年度より繰越）・ポイント還元・貸出中レコード。
        /// 摘要の文言は組織設定で変えられるため、リテラルの直書きではなく設定から導出した判定を使う
        /// （service-conventions.md「設定値で生成したものは、設定値で判定する」#1818）。旧実装は
        /// 「繰越」「新規購入」「ポイント還元」を直書きしており、ポイント還元の文言を変えた組織では
        /// ポイント還元の行を開くたびに警告が出ていた。貸出中レコードは摘要ではなく行自身のフラグで判定する（#2044）。
        /// <para>
        /// ポイント還元は<b>行自身の情報（明細がすべてポイント還元）を優先</b>し、明細の無い行（手入力の追加等）だけ摘要で見る
        /// （コードレビューで検出）。摘要を現在の設定値とだけ比べると、文言を変える前に保存された行（旧文言）を開くたびに
        /// 警告が出る — #2044「保存済みの行を現在の設定値で判定しない」と同じ形。明細の無い旧文言の行のため、
        /// 設定の既定値の文言（<see cref="SummaryTextOptions.PointRedemption"/> の初期値）も除外に含める。
        /// </para>
        /// </remarks>
        private bool IsZeroAmountExpectedSummary() =>
            IsLentRecord
            || _isPointRedemptionRow
            || Ledger.IsInitialRecordSummary(Summary)
            || Summary == SummaryGenerator.GetPointRedemptionSummary()
            || Summary == DefaultPointRedemptionSummary;

        /// <summary>ポイント還元の摘要の既定値（組織設定で変える前に保存された行の文言）</summary>
        private static readonly string DefaultPointRedemptionSummary = new SummaryTextOptions().PointRedemption;

        /// <summary>
        /// Issue #2325: Editモードで開いた行の明細がすべてポイント還元か（摘要の文言に依らない行自身の情報）
        /// </summary>
        private bool _isPointRedemptionRow;

        /// <summary>
        /// Issue #2325: Addモードで自動計算が使えなくなり、残高が未入力のときの警告（何が／なぜ／どうすれば）
        /// </summary>
        internal const string AddBalanceNotEnteredWarning =
            "残高が未入力です（0円のまま）。挿入位置の直前の行が履歴一覧に表示されていないため、" +
            "残高を自動計算できません。1つ上の行の残高 + 受入 - 払出 を確認し、残高欄に金額を入力してください。";

        /// <summary>
        /// Issue #2325: 残高が未入力のまま保存しようとしたときに保存してよいか確認する。
        /// </summary>
        /// <returns>保存を続けてよい場合 true</returns>
        /// <remarks>
        /// 保存は塞がない（入力したい残高が 0 円のこともある。0 を入力し直しても値が変わらず未入力の状態が解けない）。
        /// 書き込みの前に確認する（ui-conventions.md #1811）。処理中表示より前に呼ぶ（#1793）。
        /// </remarks>
        private bool ConfirmAddBalanceNotEntered()
        {
            if (Mode != LedgerRowEditMode.Add || !_isAddBalanceNotEntered)
            {
                return true;
            }

            return _dialogService.ShowWarningConfirmation(
                $"残高が {Balance:N0}円 のまま入力されていません。挿入位置の直前の行が履歴一覧に表示されていないため、" +
                "残高を自動計算していません。\n\nこのまま保存すると、この金額が残高として記録されます。保存しますか？\n" +
                "（「いいえ」を選び、1つ上の行の残高 + 受入 - 払出 を残高欄に入力してから保存してください）",
                "残高の確認");
        }

        /// <summary>
        /// バリデーション
        /// </summary>
        private void Validate()
        {
            ValidationMessage = string.Empty;
            WarningMessage = string.Empty;
            // Issue #2325: 編集対象の行がもう無いと分かったら、何を直しても保存は成功しないので保存させない
            CanSave = !_conflictDetected;
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

            // 受入・払出が両方0かチェック（0 円が正当な行は除外）
            if (Income == 0 && Expense == 0 && !IsZeroAmountExpectedSummary())
            {
                AddWarning("受入と払出が両方0円です。繰越等でなければ金額を入力してください。");
            }

            // Issue #2007: 受入欄に残高を書く導入行（新規購入・前年度より繰越）で受入と残額が食い違うと、
            // 月次帳票の「受入 − 払出 = 残額」が崩れる。導入時残高を片方だけ直す操作ミスの予防。
            // 保存は塞がない（#1812「曖昧な入力は塞ぐより解決結果を見せる」）。
            if (Ledger.InitialRecordCarriesIncome(Summary) && Income != Balance)
            {
                AddWarning(InitialBalanceCorrectionMessage.ForIncomeBalanceMismatch(Income, Balance));
            }

            // Issue #2325: Addモードで自動計算が使えなくなり、残高がまだ入力されていない
            if (Mode == LedgerRowEditMode.Add && _isAddBalanceNotEntered)
            {
                AddWarning(AddBalanceNotEnteredWarning);
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

            // Issue #1906: 同行者数の範囲チェック
            if (CompanionCount < 0 || CompanionCount > StaffNameFormatter.MaxCompanionCount)
            {
                ValidationMessage =
                    $"同行者数が {CompanionCount} で範囲外です。" +
                    $"本人を除く人数を0～{StaffNameFormatter.MaxCompanionCount}の整数で入力してください。";
                CanSave = false;
                FirstErrorField = nameof(CompanionCount);
                return;
            }

            // Issue #1914: 摘要の全角括弧の対応が取れていないと、バス停名の抽出
            //（SummaryGenerator.ExtractBusStopBlocks）が終端を決められず、保存後の
            // 明細への同期（Issue #983）が働かない。無言で同期を諦めると、履歴統合時に
            // 古いバス停名から摘要が再生成され、ここでの編集が取り消されたように見える。
            if (SummaryGenerator.ContainsBusLabel(Summary) &&
                !SummaryGenerator.HasBalancedFullWidthParentheses(Summary))
            {
                AddWarning(
                    "摘要の全角括弧「（」「）」の対応が取れていません。" +
                    "このままではバス停名を明細へ反映できず、履歴を統合したときに" +
                    "編集前のバス停名へ戻ります。括弧を対にするか、" +
                    "バス停名の括弧を半角「(」「)」に置き換えてください。");
            }

            // Addモードの場合の日付チェック
            if (Mode == LedgerRowEditMode.Add && _allLedgers.Count > 0)
            {
                // 挿入位置の前後と日付の整合性をチェック。
                // Issue #2239: 日単位で比べる。貸出中レコードは日付に貸出時刻を持ち（利用日の入力は 0 時）、
                // 時刻まで比べると同じ日の行の下へ入れただけで「前の行より古い」と警告していた
                if (InsertIndex > 0 && _allLedgers[InsertIndex - 1].Date.Date > EditDate.Date)
                {
                    AddWarning("日付が前の行より古くなっています。挿入位置を確認してください。");
                }
                if (InsertIndex < _allLedgers.Count && _allLedgers[InsertIndex].Date.Date < EditDate.Date)
                {
                    AddWarning("日付が次の行より新しくなっています。挿入位置を確認してください。");
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
            if (!CanSave || !ConfirmAddBalanceNotEntered())
            {
                return;
            }

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
                CompanionCount = CompanionCount,
                IsLentRecord = false
            };

            // Issue #1458: Ledger INSERT と監査ログ INSERT を同一トランザクションで実行
            // Issue #2202: トランザクションの本体ごと UI スレッドの外へ移す（DbContext.RunOffUiThreadAsync の remarks）。
            // 本体の中では画面に結び付いたプロパティを変えない（IsSaved は戻ってから立てる）。
            var inserted = await _dbContext.RunOffUiThreadAsync(async () =>
            {
                using var scope = await _dbContext.BeginTransactionAsync();
                var newId = await _ledgerRepository.InsertAsync(newLedger, scope.Transaction);
                if (newId <= 0)
                {
                    return false;
                }

                newLedger.Id = newId;
                await _operationLogger.LogLedgerInsertAsync(newLedger, scope.Transaction);
                scope.Commit();
                return true;
            });
            if (inserted)
            {
                IsSaved = true;
            }
            else
            {
                // Issue #2325: InsertAsync は last_insert_rowid() を返すので通常ここへは来ない（失敗は例外になる）。
                // 来た場合も何も確定していない（commit 前に抜けている）ことと、取れる行動を伝える
                StatusMessage = "履歴の行の追加に失敗しました（データベースに行が書き込まれず、何も保存していません）。" +
                                "しばらく待ってから、もう一度保存してください。";
            }
        }

        /// <summary>
        /// 編集モードの保存処理
        /// </summary>
        private async Task SaveEditAsync()
        {
            // Issue #1949: 操作者は最初の await より前に確定させる。
            // モーダルダイアログの表示中もメッセージポンプは回るため（#1807）、
            // DB 読み取りの待機中にコンボボックスの選択がキーボード操作で変わり得る。
            var selectedStaff = SelectedStaff;

            var ledger = await _ledgerRepository.GetByIdAsync(_editLedgerId);
            if (ledger == null)
            {
                // Issue #2325: 読み直しで行が無い＝UPDATE が 0 行になるのと同じ競合（他 PC の削除・返却で貸出中レコードが消えた）
                NotifyRowGoneConflict();
                return;
            }

            // 変更前のスナップショット。
            // Issue #1979: 手組みのコピーは明細（Details）を引き継がないため、監査ログの
            // BeforeData が常に「明細ゼロ」になり、明細を表示できるようにした途端
            // 全明細が「（なし）→ …」という起きていない変更として並ぶ。複製の手段は
            // LedgerCloner ただ 1 つに寄せる（列を足したとき片方だけ更新される形を残さない。#1763 / #1959）。
            var beforeLedger = LedgerCloner.Clone(ledger);

            // 値を更新
            ledger.Date = EditDate;
            ledger.Summary = Summary;
            ledger.Income = Income;
            ledger.Expense = Expense;
            ledger.Balance = Balance;
            ledger.Note = Note;
            ledger.CompanionCount = CompanionCount;

            // Issue #2300: 一覧に無い利用者（_unlistedStaffEntry）のまま保存する場合は、利用者の列に触れない。
            // 上で読み直した行の LenderIdm / StaffName がそのまま UPDATE に載る（表示用の目印付きの名前や、
            // IDm の無い行の空文字を書き込まない）。開いたときの値を書き戻すと、共有フォルダモードで
            // 開いている間に別の PC が利用者を直した場合に、利用者に触れていない保存が古い値で上書きしてしまう
            if (selectedStaff == null)
            {
                ledger.LenderIdm = null;
                ledger.StaffName = null;
            }
            else if (!ReferenceEquals(selectedStaff, _unlistedStaffEntry))
            {
                ledger.LenderIdm = selectedStaff.StaffIdm;
                ledger.StaffName = selectedStaff.Name;
            }

            // Issue #1458: Ledger UPDATE と監査ログ INSERT を同一トランザクションで実行
            // Issue #2202: トランザクションの本体ごと UI スレッドの外へ移す（DbContext.RunOffUiThreadAsync の remarks）。
            // 本体の中では画面に結び付いたプロパティを変えない（結果を返し、IsSaved・StatusMessage は戻ってから設定する）。
            var (result, busStopConflict) = await _dbContext.RunOffUiThreadAsync(async () =>
            {
                bool updated;
                var conflict = false;
                using (var scope = await _dbContext.BeginTransactionAsync())
                {
                    updated = await _ledgerRepository.UpdateAsync(ledger, scope.Transaction);
                    if (updated)
                    {
                        // Issue #983: 摘要編集時にバス停名をDetailに同期
                        // 摘要を直接編集するとLedger.Summaryは更新されるがDetail.BusStopsは更新されない。
                        // この不整合を放置すると、統合時にSummaryGenerator.Generate()が
                        // Detail.BusStopsから摘要を再生成し、修正前のバス停名に戻ってしまう。
                        //
                        // Issue #1945: 同期は摘要の UPDATE と「同じ論理操作」なので同一 tx に束ねる（#1806）。
                        // 旧実装は commit のあと tx の外で実行し、しかも戻り値を見ていなかったため、
                        // 履歴詳細の全置換（ReplaceDetailsAsync の DELETE + INSERT）で ledger_detail.id が振り直されていると
                        // 同期が 0 行で素通りし、摘要だけが新しいバス停名で確定して
                        // 6 年保存の台帳が「摘要はバス停名入り・明細は★のまま」と自己矛盾した。
                        if (beforeLedger.Summary != ledger.Summary)
                        {
                            var syncOk = await SyncBusStopsFromSummaryAsync(ledger, scope.Transaction);
                            if (!syncOk)
                            {
                                // commit せずに抜ける（scope の Dispose で巻き戻る）
                                updated = false;
                                conflict = true;
                            }
                        }

                        // Issue #1979: 監査ログは同期のあとに記録する。OperationLogger は
                        // SerializeToJson が呼ばれた時点の状態を写すため（#1959）、先に記録すると
                        // AfterData のバス停名が同期前のまま残り、この Issue が可視化しようとした
                        // 「バス停名の書き戻し」がまさに監査から抜け落ちる。
                        if (updated)
                        {
                            await _operationLogger.LogLedgerUpdateAsync(beforeLedger, ledger, scope.Transaction);
                            scope.Commit();
                        }
                    }
                }

                return (updated, conflict);
            });

            if (result)
            {
                // Issue #2325（コードレビュー 2 回目）: バス停名の競合の後に保存し直して成功したら、競合の印を下ろす。
                // 残すと「保存できた」のに呼び出し元が競合の再読込として扱い、「保存して次へ」が次の行を開かない
                SetBusStopConflictDetected(false);
                IsSaved = true;
            }
            else if (busStopConflict)
            {
                SetBusStopConflictDetected(true);
                StatusMessage = BusStopConflictMessage;
            }
            else
            {
                // Issue #2325: UpdateAsync の WHERE は id だけなので、false は「行が無くなった」の 1 つに定まる
                NotifyRowGoneConflict();
            }
        }

        /// <summary>
        /// Issue #2325: 編集対象の行がもう無い（他 PC の削除・返却による貸出中レコードの削除）ことを案内し、
        /// 閉じたら呼び出し元に履歴一覧を読み直させる。
        /// </summary>
        /// <remarks>
        /// 何度保存しても同じ失敗になるので、保存を無効にし、入力を「未保存の変更」として残さない
        /// （残すと閉じるときに破棄の確認が出る。<c>LedgerDetailViewModel</c> #2177 と同じ判断）。
        /// 入力欄は消さない（#1757「エラー表示で入力内容を消さない」。控えを取ってから閉じられる）。
        /// </remarks>
        private void NotifyRowGoneConflict()
        {
            _conflictDetected = true;
            _hasFieldChanges = false;
            CanSave = false;
            // 行はもう無いので削除もできない（押すと確認の後に削除の競合として 2 度目の案内になる。コードレビュー 3 回目）
            CanDelete = false;
            SkipToNextCommand.NotifyCanExecuteChanged();
            BackCommand.NotifyCanExecuteChanged();
            StatusMessage = ConcurrencyConflictMessage.ForUpdateReloadOnClose(_editTargetLabel, "履歴一覧");
        }

        /// <summary>
        /// Issue #1945: バス停名の同期が競合（影響行数 0）したときの案内文言。
        /// 「何が」「なぜ」「どうすれば」の 3 要素で組み立てる。
        /// </summary>
        internal const string BusStopConflictMessage =
            "摘要とバス停名を保存できませんでした。この履歴の明細が、他のパソコンや別の編集操作で" +
            "変更された可能性があります。変更は取り消していますので、この画面を閉じると履歴一覧を" +
            "再読み込みしますので、最新の内容を確認してからやり直してください。";

        /// <summary>
        /// 摘要からバス停名を抽出してDetail.BusStopsに同期する（Issue #983）
        /// </summary>
        /// <remarks>
        /// 摘要の直接編集ではLedger.Summaryのみ更新されDetail.BusStopsは未更新のため、
        /// 統合時の摘要再生成で修正前のバス停名に戻る問題を防止する。
        /// </remarks>
        /// <returns>
        /// 書き戻す対象が無い場合も含め、明細を更新できた場合 true。
        /// 影響行数 0（<c>ledger_detail.id</c> の振り直し等の競合、Issue #1945）の場合 false。
        /// </returns>
        private async Task<bool> SyncBusStopsFromSummaryAsync(Ledger ledger, SQLiteTransaction transaction)
        {
            // Issue #1904: バス停名（摘要中の出現順）との位置対応付けは、バス明細を
            // 生成側の出力順（GetBusStopEmissionOrder）で並べて初めて成立する
            //（並び順の定義を消費側に書き写さない。LedgerMergeService.SyncBusStopsFromSummary と同じ）
            var busDetails = SummaryGenerator.GetBusStopEmissionOrder(ledger.Details);
            if (busDetails.Count == 0)
            {
                return true;
            }

            // Issue #1818: 抽出パターンは組織設定 BusLabel から導出する
            var blocks = SummaryGenerator.ExtractBusStopBlocks(ledger.Summary);
            if (blocks.Count == 0)
            {
                return true;
            }

            var busStopUpdates = new List<(int SequenceNumber, string BusStops)>();

            if (busDetails.Count == 1)
            {
                // 先頭ブロックのみ書き戻す（従来挙動）。複数ブロックの結合テキストを
                // 1 明細へ書き込むと「A～B、C～D」となり ParseBusRoute で解析できない
                busStopUpdates.Add((busDetails[0].SequenceNumber, blocks[0]));
            }
            else
            {
                // 複数件のバス利用: 「、」で分割してDetailに対応付け
                var parts = string.Join("、", blocks).Split('、');
                if (parts.Length == busDetails.Count)
                {
                    for (int i = 0; i < parts.Length; i++)
                    {
                        busStopUpdates.Add((busDetails[i].SequenceNumber, parts[i]));
                    }
                }
            }

            if (busStopUpdates.Count == 0)
            {
                return true;
            }

            var updated = await _ledgerRepository.UpdateDetailBusStopsAsync(ledger.Id, busStopUpdates, transaction);
            if (updated)
            {
                // Issue #1979: DB だけを書き換えると、直後に JSON 化される監査ログの AfterData が
                // 同期前のバス停名（★）を「変更後」として記録する。in-memory も揃える。
                foreach (var (sequenceNumber, busStops) in busStopUpdates)
                {
                    var detail = ledger.Details.FirstOrDefault(d => d.SequenceNumber == sequenceNumber);
                    if (detail != null)
                    {
                        detail.BusStops = busStops;
                    }
                }
            }

            return updated;
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
            if (!CanDelete)
            {
                return;
            }

            var message = IsLentRecord
                ? "この履歴は「貸出中」状態のレコードです。\n" +
                  "削除すると、このカードの貸出中状態も解消されます\n" +
                  "（他に貸出中レコードが残っている場合は維持されます）。\n\n" +
                  "通常は、メイン画面で交通系ICカードをタッチして返却操作を\n" +
                  "行うのが正しい復旧方法です。それでも削除しますか？\n\n" +
                  "削除した履歴は元に戻せません。"
                : "この履歴を削除してよろしいですか？\n\n削除した履歴は元に戻せません。";

            if (!_dialogService.ShowWarningConfirmation(message, "履歴の削除"))
            {
                return;
            }

            IsDeleteRequested = true;
        }

        /// <summary>
        /// ダイアログのクローズ要求時に View が呼ぶ処理（Issue #2141）
        /// </summary>
        /// <remarks>
        /// 「キャンセル」ボタンと Escape キー（<see cref="RequestCloseCommand"/>）から呼ばれる。
        /// View は <c>Window.Close()</c> を設定し、閉じる経路をすべて <c>OnClosing</c> の
        /// 破棄確認（<see cref="CanClose"/>）へ通す（<c>LedgerDetailViewModel</c> #1743 と同じ形）。
        /// </remarks>
        public Action? OnCloseRequested { get; set; }

        /// <summary>
        /// ダイアログのクローズを要求する（Issue #2141）
        /// </summary>
        /// <remarks>
        /// <c>Button.IsCancel</c> を使わない。<c>IsCancel</c> は Click の後に無条件で
        /// <c>DialogResult=false</c> を設定し、破棄確認で「いいえ」を選んでも閉じる動作が残る（#1743）。
        /// </remarks>
        [RelayCommand]
        private void RequestClose()
        {
            OnCloseRequested?.Invoke();
        }

        /// <summary>
        /// ダイアログを閉じてよいか判定する（Issue #2141）
        /// </summary>
        /// <param name="confirmDiscard">未保存の変更を破棄してよいか利用者に確認する処理</param>
        /// <returns>閉じてよい場合 true</returns>
        /// <remarks>
        /// <para>
        /// 摘要・金額・備考の修正途中に Esc・「キャンセル」・✕ を押すと、確認なしで入力が失われていた。
        /// 「次へ」「戻る」は <see cref="HasUnsavedChanges"/> で確認していたが、閉じる経路だけが通っていなかった。
        /// </para>
        /// <para>
        /// <b>行き先が決まった閉じる（保存・削除要求・次へ・戻る）は確認しない。</b>これらは
        /// <c>PropertyChanged</c> を受けた View が <c>Close()</c> するので、同じ <c>OnClosing</c> を通る。
        /// 保存の後も入力値は初期値と異なる（＝未保存の変更ありと判定される）ため、先に除外しないと
        /// 保存するたびに「破棄しますか」と尋ねることになる。「次へ」「戻る」は各コマンドの中で確認済み。
        /// </para>
        /// <para>
        /// 処理中（保存中）は閉じさせない。保存の結果（<see cref="IsSaved"/>）は処理中に立つので、
        /// 行き先の判定を先に置く。
        /// </para>
        /// </remarks>
        public bool CanClose(Func<bool> confirmDiscard)
        {
            if (IsSaved || IsDeleteRequested || IsSaveAndEditNextRequested || IsSkipToNextRequested || IsBackRequested)
            {
                return true;
            }

            if (IsBusy)
            {
                return false;
            }

            if (!HasUnsavedChanges())
            {
                return true;
            }

            return confirmDiscard();
        }

        /// <summary>
        /// 保存して次の履歴を編集（Issue #1134）
        /// </summary>
        [RelayCommand]
        private async Task SaveAndEditNext()
        {
            if (!CanSave || !ConfirmAddBalanceNotEntered())
            {
                return;
            }

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
        [RelayCommand(CanExecute = nameof(CanNavigateToAdjacent))]
        private void SkipToNext()
        {
            if (HasUnsavedChanges())
            {
                if (!_dialogService.ShowWarningConfirmation(
                    "変更が保存されていません。破棄して次へ進みますか？", "確認"))
                {
                    return;
                }
            }
            IsSkipToNextRequested = true;
        }

        /// <summary>
        /// 前の履歴へ戻る（Issue #1134）
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanNavigateToAdjacent))]
        private void Back()
        {
            if (HasUnsavedChanges())
            {
                if (!_dialogService.ShowWarningConfirmation(
                    "変更が保存されていません。破棄して前へ戻りますか？", "確認"))
                {
                    return;
                }
            }
            IsBackRequested = true;
        }

        /// <summary>
        /// 未保存の変更があるか判定（Issue #1134）
        /// </summary>
        /// <remarks>
        /// Editモードで初期値と現在値を比較。Addモードではフィールドに入力があれば変更ありとする。
        /// Issue #2141: 閉じる経路（Esc・キャンセル・✕）の確認もこの判定に拠るため、追加モードで
        /// 備考・同行者数・利用者だけを入力した場合も変更ありとする（摘要・金額しか見ていなかった頃は、
        /// これらだけを入れて閉じると確認なしで入力が消えた）。日付は含めない — 既定値（今日）から
        /// 挿入位置の操作でも変わるため、入力の有無の代理にならない。
        /// </remarks>
        private bool HasUnsavedChanges()
        {
            if (Mode == LedgerRowEditMode.Add)
            {
                return !string.IsNullOrWhiteSpace(Summary) || Income != 0 || Expense != 0
                    || !string.IsNullOrWhiteSpace(Note) || CompanionCount != 0 || SelectedStaff != null;
            }

            // Editモード: 初期値が設定されていれば比較。
            // Issue #2325: 編集対象の行がもう無いと分かった後の入力は保存できないので、破棄の確認の対象にしない
            return _hasFieldChanges && !_conflictDetected;
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
            if (_trackChanges)
            {
                _hasFieldChanges = true;
            }
        }
    }
}
