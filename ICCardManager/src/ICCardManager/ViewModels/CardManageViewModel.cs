using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
/// <summary>
    /// カード管理画面のViewModel
    /// </summary>
    public partial class CardManageViewModel : ViewModelBase
    {
        private readonly ICardRepository _cardRepository;
        private readonly ILedgerRepository _ledgerRepository;
        private readonly ICardReader _cardReader;
        private readonly CardTypeDetector _cardTypeDetector;
        private readonly IValidationService _validationService;
        private readonly OperationLogger _operationLogger;
        private readonly IDialogService _dialogService;
        private readonly IStaffAuthService _staffAuthService;
        private readonly LendingService _lendingService;

        [ObservableProperty]
        private ObservableCollection<CardDto> _cards = new();

        [ObservableProperty]
        private CardDto? _selectedCard;

        [ObservableProperty]
        private bool _isEditing;

        [ObservableProperty]
        private bool _isNewCard;

        [ObservableProperty]
        private string _editCardIdm = string.Empty;

        [ObservableProperty]
        private string _editCardType = string.Empty;

        [ObservableProperty]
        private string _editCardNumber = string.Empty;

        [ObservableProperty]
        private string _editNote = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isStatusError;

        [ObservableProperty]
        private bool _isWaitingForCard;

        /// <summary>
        /// 新規登録・更新・復元後にハイライト表示するカードのIDm
        /// </summary>
        [ObservableProperty]
        private string? _newlyRegisteredIdm;

        /// <summary>
        /// 事前に読み取った残高（Issue #381対応）
        /// </summary>
        /// <remarks>
        /// 未登録カード検出時にMainViewModelで残高を読み取り、この値に設定する。
        /// CreateNewPurchaseLedgerAsyncでこの値を使用することで、カードがリーダーから
        /// 離れた後でも正しい残高で「新規購入」レコードを作成できる。
        /// </remarks>
        private int? _preReadBalance;

        /// <summary>
        /// 事前に読み取った履歴（Issue #596対応）
        /// </summary>
        /// <remarks>
        /// 未登録カード検出時にMainViewModelで履歴を読み取り、この値に設定する。
        /// カード登録後にImportHistoryForRegistrationAsyncで当月分の履歴をインポートする。
        /// </remarks>
        private List<LedgerDetail> _preReadHistory;

        /// <summary>
        /// カード登録モードの選択結果（Issue #510対応）
        /// </summary>
        /// <remarks>
        /// null: 未選択（新規購入として扱う）
        /// IsNewPurchase=true: 新規購入
        /// IsNewPurchase=false: 紙の出納簿からの繰越
        /// </remarks>
        private Views.Dialogs.CardRegistrationModeResult? _registrationModeResult;

        /// <summary>
        /// カード種別の選択肢
        /// </summary>
        public ObservableCollection<string> CardTypes { get; } = new()
        {
            "はやかけん",
            "nimoca",
            "SUGOCA",
            "Suica",
            "PASMO",
            "ICOCA",
            "PiTaPa",
            "Kitaca",
            "TOICA",
            "manaca",
            "その他"
        };

        public CardManageViewModel(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository,
            ICardReader cardReader,
            CardTypeDetector cardTypeDetector,
            IValidationService validationService,
            OperationLogger operationLogger,
            IDialogService dialogService,
            IStaffAuthService staffAuthService,
            LendingService lendingService)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
            _cardReader = cardReader;
            _cardTypeDetector = cardTypeDetector;
            _validationService = validationService;
            _operationLogger = operationLogger;
            _dialogService = dialogService;
            _staffAuthService = staffAuthService;
            _lendingService = lendingService;

            // カード読み取りイベント
            _cardReader.CardRead += OnCardRead;
        }

        /// <summary>
        /// 初期化
        /// </summary>
        public async Task InitializeAsync()
        {
            await LoadCardsAsync();
        }

        /// <summary>
        /// カード一覧を読み込み
        /// </summary>
        [RelayCommand]
        public async Task LoadCardsAsync()
        {
            using (BeginBusy("読み込み中..."))
            {
                var cards = await _cardRepository.GetAllAsync();
                Cards.Clear();
                foreach (var card in cards.OrderBy(c => c.CardType).ThenBy(c => c.CardNumber))
                {
                    Cards.Add(card.ToDto());
                }
            }
        }

        /// <summary>
        /// 新規登録モードを開始
        /// </summary>
        [RelayCommand]
        public void StartNewCard()
        {
            SelectedCard = null;
            IsEditing = true;
            IsNewCard = true;
            EditCardIdm = string.Empty;
            EditCardType = "はやかけん";
            EditCardNumber = string.Empty;
            EditNote = string.Empty;
            StatusMessage = "カードをタッチするとIDmを読み取ります";
            IsStatusError = false;
            IsWaitingForCard = true;

            // MainViewModelでの未登録カード処理を抑制
            App.IsCardRegistrationActive = true;
        }

        /// <summary>
        /// IDmを指定して新規登録モードを開始（未登録カード検出時用）
        /// </summary>
        /// <param name="idm">カードのIDm</param>
        /// <returns>処理が完了したかどうか（削除済みカードの復元で完了した場合はtrue）</returns>
        public async Task<bool> StartNewCardWithIdmAsync(string idm)
        {
            // Issue #284対応: タッチ時点で削除済みカードチェックを行う
            var existing = await _cardRepository.GetByIdmAsync(idm, includeDeleted: true);
            if (existing != null)
            {
                if (existing.IsDeleted)
                {
                    // 削除済みカードの場合は復元を提案
                    var confirmed = _dialogService.ShowConfirmation(
                        $"このカードは以前 {existing.CardNumber} として登録されていましたが、削除されています。\n\n復元しますか？",
                        "削除済みカード");

                    if (confirmed)
                    {
                        var restored = await _cardRepository.RestoreAsync(idm);
                        if (restored)
                        {
                            // 操作ログを記録（復元後のデータを取得）
                            var restoredCard = await _cardRepository.GetByIdmAsync(idm);
                            if (restoredCard != null)
                            {
                                await _operationLogger.LogCardRestoreAsync(null, restoredCard);
                            }

                            _dialogService.ShowInformation(
                                $"{existing.CardNumber} を復元しました",
                                "復元完了");
                            return true; // ダイアログを閉じる
                        }
                        else
                        {
                            _dialogService.ShowError(
                                "復元に失敗しました",
                                "エラー");
                            return true; // ダイアログを閉じる
                        }
                    }
                    else
                    {
                        // Issue #314: 復元しない場合は案内メッセージを表示
                        _dialogService.ShowInformation(
                            $"このカードは以前 {existing.CardNumber} として登録されていたため、新規登録はできません。\n\n" +
                            "異なるカード番号等で登録したい場合は、先に復元を行い、その後に編集してください。",
                            "ご案内");
                        return true; // ダイアログを閉じる
                    }
                }
                else
                {
                    // 既に登録済みの場合はメッセージを表示して終了
                    _dialogService.ShowInformation(
                        $"このカードは {existing.CardNumber} として既に登録されています",
                        "登録済みカード");
                    return true; // ダイアログを閉じる
                }
            }

            // 未登録カードの場合は通常処理
            SelectedCard = null;
            IsEditing = true;
            IsNewCard = true;
            EditCardIdm = idm;

            // カード種別はユーザーに手動選択させる（IDmからの自動判定は技術的に不可能なため）
            // デフォルトはnimoca（利用頻度が最も高いため）
            EditCardType = "nimoca";

            EditCardNumber = string.Empty;
            EditNote = string.Empty;
            StatusMessage = "カードを読み取りました。カード種別を確認してください。";
            IsStatusError = false;
            IsWaitingForCard = false; // すでにIDmがあるので待機しない

            return false; // ダイアログは開いたまま
        }

        /// <summary>
        /// 事前に読み取った残高を設定（Issue #381対応）
        /// </summary>
        /// <remarks>
        /// MainViewModelで未登録カード検出時に残高を読み取り、この値を設定する。
        /// カードがリーダーから離れる前に残高を保持しておくことで、
        /// 後からCreateNewPurchaseLedgerAsyncで使用できる。
        /// </remarks>
        /// <param name="balance">カード残高（読み取り失敗時はnull）</param>
        public void SetPreReadBalance(int? balance)
        {
            _preReadBalance = balance;
        }

        /// <summary>
        /// 事前に読み取った履歴を設定（Issue #596対応）
        /// </summary>
        /// <remarks>
        /// MainViewModelで未登録カード検出時に履歴を読み取り、この値を設定する。
        /// カード登録後に当月分の履歴をインポートする際に使用する。
        /// </remarks>
        /// <param name="history">カード利用履歴</param>
        public void SetPreReadHistory(List<LedgerDetail> history)
        {
            _preReadHistory = history;
        }

        /// <summary>
        /// 編集コマンドが実行可能かどうか
        /// </summary>
        private bool CanEdit() => SelectedCard != null;

        /// <summary>
        /// 編集モードを開始
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEdit))]
        public void StartEdit()
        {
            if (SelectedCard == null) return;

            IsEditing = true;
            IsNewCard = false;
            EditCardIdm = SelectedCard.CardIdm;
            EditCardType = SelectedCard.CardType;
            EditCardNumber = SelectedCard.CardNumber;
            EditNote = SelectedCard.Note ?? string.Empty;
            StatusMessage = string.Empty;
            IsStatusError = false;
            IsWaitingForCard = false;
        }

        /// <summary>
        /// 保存
        /// </summary>
        [RelayCommand]
        public async Task SaveAsync()
        {
            // 入力値をサニタイズ
            var sanitizedCardNumber = InputSanitizer.SanitizeCardNumber(EditCardNumber);
            var sanitizedNote = InputSanitizer.SanitizeNote(EditNote);

            // バリデーション
            var idmResult = _validationService.ValidateCardIdm(EditCardIdm);
            if (!idmResult)
            {
                StatusMessage = idmResult.ErrorMessage!;
                IsStatusError = true;
                return;
            }

            var typeResult = _validationService.ValidateCardType(EditCardType);
            if (!typeResult)
            {
                StatusMessage = typeResult.ErrorMessage!;
                IsStatusError = true;
                return;
            }

            var numberResult = _validationService.ValidateCardNumber(sanitizedCardNumber);
            if (!numberResult)
            {
                StatusMessage = numberResult.ErrorMessage!;
                IsStatusError = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(sanitizedCardNumber))
            {
                // 自動採番
                sanitizedCardNumber = await _cardRepository.GetNextCardNumberAsync(EditCardType);
            }

            using (BeginBusy("保存中..."))
            {
                if (IsNewCard)
                {
                    // 重複チェック
                    var existing = await _cardRepository.GetByIdmAsync(EditCardIdm, includeDeleted: true);
                    if (existing != null)
                    {
                        if (existing.IsDeleted)
                        {
                            // 削除済みカードの場合は復元を提案
                            var confirmed = _dialogService.ShowConfirmation(
                                $"このカードは以前 {existing.CardNumber} として登録されていましたが、削除されています。\n\n復元しますか？",
                                "削除済みカード");

                            if (confirmed)
                            {
                                var restored = await _cardRepository.RestoreAsync(EditCardIdm);
                                if (restored)
                                {
                                    // 操作ログを記録（復元後のデータを取得）
                                    var restoredCard = await _cardRepository.GetByIdmAsync(EditCardIdm);
                                    if (restoredCard != null)
                                    {
                                        await _operationLogger.LogCardRestoreAsync(null, restoredCard);
                                    }

                                    var restoredIdm = EditCardIdm;
                                    StatusMessage = $"{existing.CardNumber} を復元しました";
                                    IsStatusError = false;
                                    await LoadCardsAsync();
                                    CancelEdit();
                                    SelectAndHighlight(restoredIdm);
                                }
                                else
                                {
                                    StatusMessage = "復元に失敗しました";
                                    IsStatusError = true;
                                }
                            }
                            else
                            {
                                // Issue #314: 復元しない場合は案内メッセージを表示
                                _dialogService.ShowInformation(
                                    $"このカードは以前 {existing.CardNumber} として登録されていたため、新規登録はできません。\n\n" +
                                    "異なるカード番号等で登録したい場合は、先に復元を行い、その後に編集してください。",
                                    "ご案内");
                                CancelEdit();
                            }
                            return;
                        }
                        else
                        {
                            StatusMessage = $"このカードは {existing.CardNumber} として既に登録されています";
                            IsStatusError = true;
                            return;
                        }
                    }

                    // Issue #510: 登録モード選択ダイアログを表示
                    var modeResult = ShowRegistrationModeDialog();
                    if (modeResult == null)
                    {
                        // キャンセルされた場合
                        StatusMessage = "登録がキャンセルされました";
                        IsStatusError = false;
                        return;
                    }
                    _registrationModeResult = modeResult;

                    var card = new IcCard
                    {
                        CardIdm = EditCardIdm,
                        CardType = EditCardType,
                        CardNumber = sanitizedCardNumber,
                        Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote,
                        StartingPageNumber = modeResult.StartingPageNumber
                    };

                    var success = await _cardRepository.InsertAsync(card);
                    if (success)
                    {
                        // 操作ログを記録
                        await _operationLogger.LogCardInsertAsync(null, card);

                        // Issue #596: 履歴のインポート対象を決定
                        var history = _preReadHistory;
                        if (history == null || history.Count == 0)
                        {
                            // フォールバック: カードから直接読み取り
                            try { history = (await _cardReader.ReadHistoryAsync(EditCardIdm))?.ToList(); }
                            catch { history = null; }
                        }

                        var importFromDate = GetImportFromDate(modeResult);
                        var filteredHistory = history?
                            .Where(d => d.UseDate.HasValue && d.UseDate.Value.Date >= importFromDate)
                            .OrderBy(d => d.UseDate)
                            .ThenByDescending(d => d.Balance)
                            .ToList();

                        if (filteredHistory != null && filteredHistory.Count > 0)
                        {
                            // 履歴がある場合: 初期残高を逆算してから初期レコード作成
                            var preHistoryBalance = CalculatePreHistoryBalance(filteredHistory);
                            await CreateInitialLedgerAsync(EditCardIdm, modeResult,
                                overrideDate: importFromDate, overrideBalance: preHistoryBalance);

                            // 履歴をインポート
                            var importResult = await _lendingService.ImportHistoryForRegistrationAsync(
                                EditCardIdm, filteredHistory, importFromDate);

                            if (importResult.MayHaveIncompleteHistory)
                            {
                                // Issue #664: カード内の履歴の実際の最古月を表示
                                var monthText = importResult.EarliestHistoryDate.HasValue
                                    ? $"{importResult.EarliestHistoryDate.Value.Month}月以降分"
                                    : "今月分";
                                _dialogService.ShowInformation(
                                    $"交通系ICカード内の履歴が{monthText}のため、それより前の履歴が不足している可能性があります。\n" +
                                    "不足分はCSVインポートで補完してください。",
                                    "履歴インポートの注意");
                            }
                        }
                        else
                        {
                            // 履歴がない場合: 従来どおり
                            await CreateInitialLedgerAsync(EditCardIdm, modeResult);
                        }

                        var savedIdm = EditCardIdm;
                        StatusMessage = "登録しました";
                        IsStatusError = false;
                        await LoadCardsAsync();
                        CancelEdit();
                        SelectAndHighlight(savedIdm);
                    }
                    else
                    {
                        StatusMessage = "登録に失敗しました";
                        IsStatusError = true;
                    }
                    _registrationModeResult = null;
                }
                else
                {
                    // 更新前のデータを取得（操作ログ用）
                    var beforeCard = await _cardRepository.GetByIdmAsync(EditCardIdm);

                    // 更新
                    var card = new IcCard
                    {
                        CardIdm = EditCardIdm,
                        CardType = EditCardType,
                        CardNumber = sanitizedCardNumber,
                        Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote,
                        IsLent = SelectedCard!.IsLent,
                        LastLentAt = SelectedCard.LentAt,
                        LastLentStaff = SelectedCard.LastLentStaff
                    };

                    var success = await _cardRepository.UpdateAsync(card);
                    if (success)
                    {
                        // 操作ログを記録
                        if (beforeCard != null)
                        {
                            await _operationLogger.LogCardUpdateAsync(null, beforeCard, card);
                        }

                        var updatedIdm = EditCardIdm;
                        StatusMessage = "更新しました";
                        IsStatusError = false;
                        await LoadCardsAsync();
                        CancelEdit();
                        SelectAndHighlight(updatedIdm);
                    }
                    else
                    {
                        StatusMessage = "更新に失敗しました";
                        IsStatusError = true;
                    }
                }
            }
        }

        /// <summary>
        /// 削除コマンドが実行可能かどうか
        /// </summary>
        /// <remarks>
        /// Issue #530: 払戻済カードは既に運用から除外されているため削除不可
        /// </remarks>
        private bool CanDelete() => SelectedCard != null && !SelectedCard.IsLent && !SelectedCard.IsRefunded;

        /// <summary>
        /// 削除
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDelete))]
        public async Task DeleteAsync()
        {
            if (SelectedCard == null) return;

            if (SelectedCard.IsLent)
            {
                StatusMessage = "貸出中のカードは削除できません";
                IsStatusError = true;
                return;
            }

            // Issue #429: ICカードの削除は認証が必要
            var authResult = await _staffAuthService.RequestAuthenticationAsync("交通系ICカードの削除");
            if (authResult == null)
            {
                // 認証キャンセルまたはタイムアウト
                return;
            }

            // 削除確認ダイアログを表示
            var confirmed = _dialogService.ShowWarningConfirmation(
                $"カード「{SelectedCard.CardType} {SelectedCard.CardNumber}」を削除しますか？\n\n※削除後も履歴データは保持されます。",
                "削除確認");

            if (!confirmed)
            {
                return;
            }

            using (BeginBusy("削除中..."))
            {
                // 削除前のデータを取得（操作ログ用）
                var card = await _cardRepository.GetByIdmAsync(SelectedCard.CardIdm);

                var success = await _cardRepository.DeleteAsync(SelectedCard.CardIdm);
                if (success)
                {
                    // 操作ログを記録（Issue #429: 認証済み職員のIDmを使用）
                    if (card != null)
                    {
                        await _operationLogger.LogCardDeleteAsync(authResult.Idm, card);
                    }

                    StatusMessage = "削除しました";
                    IsStatusError = false;
                    await LoadCardsAsync();
                    CancelEdit();
                }
                else
                {
                    StatusMessage = "削除に失敗しました";
                    IsStatusError = true;
                }
            }
        }

        /// <summary>
        /// 払い戻しが可能か判定
        /// </summary>
        /// <remarks>
        /// 払い戻しの条件:
        /// - カードが選択されている
        /// - 貸出中でない（手元にないカードは払い戻し操作自体が意味をなさない）
        /// </remarks>
        /// <remarks>
        /// Issue #530: 既に払戻済のカードは再度払い戻しできない
        /// </remarks>
        private bool CanRefund() => SelectedCard != null && !SelectedCard.IsLent && !SelectedCard.IsRefunded;

        /// <summary>
        /// 払い戻し処理
        /// </summary>
        /// <remarks>
        /// Issue #379対応: 交通系ICカードの払い戻しに対応。
        /// 払い戻し時は残高を払出金額として計上し、残高を0にする。
        /// Issue #530対応: 払戻済カードは削除せず「払戻済」状態として保持。
        /// 払戻済カードは帳票作成時に引き続き選択可能だが、貸出対象からは除外される。
        /// </remarks>
        [RelayCommand(CanExecute = nameof(CanRefund))]
        public async Task RefundAsync()
        {
            if (SelectedCard == null) return;

            if (SelectedCard.IsLent)
            {
                StatusMessage = "貸出中のカードは払い戻しできません";
                IsStatusError = true;
                return;
            }

            // 最新の残高を取得
            var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(SelectedCard.CardIdm);
            var currentBalance = latestLedger?.Balance ?? 0;

            // 払い戻し確認ダイアログを表示（Issue #530: 削除ではなく払戻済状態になることを明記）
            var message = currentBalance > 0
                ? $"カード「{SelectedCard.CardType} {SelectedCard.CardNumber}」を払い戻しますか？\n\n現在の残高: ¥{currentBalance:N0}\n\n※払い戻し後、このカードは「払戻済」となり、貸出対象外になります。\n　帳票の作成には引き続き使用できます。"
                : $"カード「{SelectedCard.CardType} {SelectedCard.CardNumber}」を払い戻しますか？\n\n現在の残高: ¥0（残高なし）\n\n※払い戻し後、このカードは「払戻済」となり、貸出対象外になります。\n　帳票の作成には引き続き使用できます。";

            var confirmed = _dialogService.ShowWarningConfirmation(message, "払い戻し確認");

            if (!confirmed)
            {
                return;
            }

            using (BeginBusy("払い戻し処理中..."))
            {
                // 払い戻しのLedgerを作成
                var now = DateTime.Now;
                var refundLedger = new Ledger
                {
                    CardIdm = SelectedCard.CardIdm,
                    LenderIdm = null,
                    Date = now,
                    Summary = SummaryGenerator.GetRefundSummary(),
                    Income = 0,
                    Expense = currentBalance,  // 残高を払出金額として計上
                    Balance = 0,                // 払い戻し後の残高は0
                    StaffName = null,
                    Note = null,
                    ReturnerIdm = null,
                    LentAt = null,
                    ReturnedAt = null,
                    IsLentRecord = false
                };

                var ledgerId = await _ledgerRepository.InsertAsync(refundLedger);

                if (ledgerId > 0)
                {
                    // 払い戻し前のデータを取得（操作ログ用）
                    var beforeCard = await _cardRepository.GetByIdmAsync(SelectedCard.CardIdm);

                    // Issue #530: カードを「払戻済」状態に設定（論理削除ではない）
                    var refundSuccess = await _cardRepository.SetRefundedAsync(SelectedCard.CardIdm);

                    if (refundSuccess)
                    {
                        // 払い戻し後のデータを取得（操作ログ用）
                        var afterCard = await _cardRepository.GetByIdmAsync(SelectedCard.CardIdm);

                        // 操作ログを記録（払い戻しはカード更新として記録）
                        if (beforeCard != null && afterCard != null)
                        {
                            await _operationLogger.LogCardUpdateAsync(null, beforeCard, afterCard);
                        }

                        StatusMessage = currentBalance > 0
                            ? $"払い戻しが完了しました（払戻額: ¥{currentBalance:N0}）"
                            : "払い戻しが完了しました";
                        IsStatusError = false;
                        await LoadCardsAsync();
                        CancelEdit();
                    }
                    else
                    {
                        StatusMessage = "払戻済状態への変更に失敗しました";
                        IsStatusError = true;
                    }
                }
                else
                {
                    StatusMessage = "払い戻し記録の作成に失敗しました";
                    IsStatusError = true;
                }
            }
        }

        /// <summary>
        /// 編集をキャンセル
        /// </summary>
        [RelayCommand]
        public void CancelEdit()
        {
            IsEditing = false;
            IsNewCard = false;
            IsWaitingForCard = false;
            EditCardIdm = string.Empty;
            EditCardType = string.Empty;
            EditCardNumber = string.Empty;
            EditNote = string.Empty;
            StatusMessage = string.Empty;
            IsStatusError = false;

            // ICカード登録モードを解除
            App.IsCardRegistrationActive = false;
        }

        /// <summary>
        /// カード読み取りイベント
        /// </summary>
        private void OnCardRead(object sender, CardReadEventArgs e)
        {
            if (!IsWaitingForCard) return;

            // UIスレッドで非同期実行（登録済みチェックを即座に行うため）
            System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                EditCardIdm = e.Idm;
                IsWaitingForCard = false;

                // 即座に登録済みチェックを実行（Issue #284）
                var existing = await _cardRepository.GetByIdmAsync(e.Idm, includeDeleted: true);
                if (existing != null)
                {
                    if (existing.IsDeleted)
                    {
                        // 削除済みカードの場合は復元を提案
                        var confirmed = _dialogService.ShowConfirmation(
                            $"このカードは以前 {existing.CardNumber} として登録されていましたが、削除されています。\n\n復元しますか？",
                            "削除済みカード");

                        if (confirmed)
                        {
                            var restored = await _cardRepository.RestoreAsync(e.Idm);
                            if (restored)
                            {
                                // 操作ログを記録（復元後のデータを取得）
                                var restoredCard = await _cardRepository.GetByIdmAsync(e.Idm);
                                if (restoredCard != null)
                                {
                                    await _operationLogger.LogCardRestoreAsync(null, restoredCard);
                                }

                                var restoredIdm = e.Idm;
                                StatusMessage = $"{existing.CardNumber} を復元しました";
                                IsStatusError = false;
                                await LoadCardsAsync();
                                CancelEdit();
                                SelectAndHighlight(restoredIdm);
                            }
                            else
                            {
                                StatusMessage = "復元に失敗しました";
                                IsStatusError = true;
                            }
                        }
                        else
                        {
                            // Issue #314: 復元しない場合は案内メッセージを表示
                            _dialogService.ShowInformation(
                                $"このカードは以前 {existing.CardNumber} として登録されていたため、新規登録はできません。\n\n" +
                                "異なるカード番号等で登録したい場合は、先に復元を行い、その後に編集してください。",
                                "ご案内");
                            CancelEdit();
                        }
                    }
                    else
                    {
                        // 既に登録済みの場合はメッセージを表示（赤色で目立たせる: Issue #286）
                        StatusMessage = $"このカードは {existing.CardNumber} として既に登録されています";
                        IsStatusError = true;
                        // フォームはそのままにして、ユーザーが確認できるようにする
                    }
                    return;
                }

                // 未登録カードの場合は通常処理
                // カード種別はユーザーに手動選択させる（IDmからの自動判定は技術的に不可能なため）
                // デフォルトはnimoca（利用頻度が最も高いため）
                EditCardType = "nimoca";
                StatusMessage = "カードを読み取りました。カード種別を確認してください。";
                IsStatusError = false;

                // Issue #443対応: カード読み取り時点で残高を事前取得
                // カードがリーダーにある間に残高を読み取り、保存時に使用する
                // これにより、ユーザーがフォーム入力中にカードを離しても正しい残高で登録できる
                try
                {
                    _preReadBalance = await _cardReader.ReadBalanceAsync(e.Idm);
                }
                catch
                {
                    // 残高読み取り失敗時はnullのまま（CreateNewPurchaseLedgerAsyncで再試行される）
                    _preReadBalance = null;
                }

                // Issue #665: カード読み取り時点で履歴も事前取得
                // カードがリーダーにある間に履歴を読み取り、保存時に使用する
                // これにより、ユーザーがカードを離しても正しく履歴をインポートできる
                try
                {
                    _preReadHistory = (await _cardReader.ReadHistoryAsync(e.Idm))?.ToList();
                }
                catch
                {
                    _preReadHistory = null;
                }

                // 注意: App.IsCardRegistrationActive はここで解除しない
                // ダイアログが開いている間は常にフラグを維持し、
                // CancelEdit() または Cleanup() でのみ解除する
            });
        }

        /// <summary>
        /// 選択カード変更時の処理
        /// </summary>
        partial void OnSelectedCardChanged(CardDto? value)
        {
            // コマンドの実行可否を再評価
            StartEditCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            RefundCommand.NotifyCanExecuteChanged();  // Issue #446対応: 払い戻しボタンの状態も更新

            // 新規登録モード中は選択変更を無視
            if (IsNewCard) return;

            // カードが選択された場合、編集中なら選択したカードの情報で更新
            if (value != null && IsEditing)
            {
                EditCardIdm = value.CardIdm;
                EditCardType = value.CardType;
                EditCardNumber = value.CardNumber;
                EditNote = value.Note ?? string.Empty;
                StatusMessage = string.Empty;
                IsStatusError = false;
            }
        }

#if DEBUG
        /// <summary>
        /// デバッグ用: カード読み取りをシミュレート
        /// </summary>
        [RelayCommand]
        public void SimulateCardRead()
        {
            if (!IsWaitingForCard) return;

            if (_cardReader is MockCardReader mockReader)
            {
                // 未使用のIDmを生成
                var newIdm = $"07FE{Guid.NewGuid().ToString("N").Substring(0, 12).ToUpper()}";
                mockReader.SimulateCardRead(newIdm);
            }
        }
#endif

        /// <summary>
        /// カード登録モード選択ダイアログを表示（Issue #510）
        /// </summary>
        /// <returns>選択結果。キャンセル時はnull</returns>
        private Views.Dialogs.CardRegistrationModeResult? ShowRegistrationModeDialog()
        {
            return _dialogService.ShowCardRegistrationModeDialog();
        }

        /// <summary>
        /// 初期レコード（新規購入または繰越）を作成（Issue #510）
        /// </summary>
        /// <param name="cardIdm">カードのIDm</param>
        /// <param name="modeResult">登録モードの選択結果</param>
        /// <param name="overrideDate">日付の上書き（Issue #596: 履歴がある場合、インポート開始日を使用）</param>
        /// <param name="overrideBalance">残高の上書き（Issue #596: 履歴がある場合、逆算した初期残高を使用）</param>
        private async Task CreateInitialLedgerAsync(
            string cardIdm,
            Views.Dialogs.CardRegistrationModeResult modeResult,
            DateTime? overrideDate = null,
            int? overrideBalance = null)
        {
            try
            {
                // Issue #596: overrideBalanceが指定された場合はそれを使用
                // Issue #381対応: 事前に読み取った残高を優先的に使用
                int? balance = overrideBalance ?? _preReadBalance;

                // 事前読み取り残高がない場合のみ、カードから読み取りを試みる
                // （手動で新規登録モードを開始した場合のフォールバック）
                if (!balance.HasValue)
                {
                    balance = await _cardReader.ReadBalanceAsync(cardIdm);
                }

                // 残額が取得できた場合のみレコードを作成
                if (balance.HasValue)
                {
                    var now = DateTime.Now;

                    // Issue #510: 登録モードに応じて摘要を決定
                    string summary;
                    if (modeResult.IsNewPurchase)
                    {
                        summary = "新規購入";
                    }
                    else
                    {
                        // 繰越モード: 「○月から繰越」
                        summary = SummaryGenerator.GetMidYearCarryoverSummary(modeResult.CarryoverMonth!.Value);
                    }

                    // Issue #596: overrideDateが指定された場合はそれを使用
                    DateTime recordDate;
                    if (overrideDate.HasValue)
                    {
                        recordDate = overrideDate.Value;
                    }
                    else if (modeResult.IsNewPurchase)
                    {
                        // Issue #658: 購入日が指定されている場合はその日付を使用
                        recordDate = modeResult.PurchaseDate?.Date ?? now;
                    }
                    else
                    {
                        // Issue #599: 繰越モードの場合は繰越月の翌月1日をレコード日付とする
                        recordDate = SummaryGenerator.GetMidYearCarryoverDate(modeResult.CarryoverMonth!.Value, now);
                    }

                    var ledger = new Ledger
                    {
                        CardIdm = cardIdm,
                        LenderIdm = null,  // 新規購入/繰越時は貸出者なし
                        Date = recordDate,
                        Summary = summary,
                        Income = balance.Value,  // 受入金額 = カード残額
                        Expense = 0,
                        Balance = balance.Value,
                        StaffName = null,  // 利用者なし
                        Note = null,
                        ReturnerIdm = null,
                        LentAt = null,
                        ReturnedAt = null,
                        IsLentRecord = false
                    };

                    await _ledgerRepository.InsertAsync(ledger);
                }
                // 残額が取得できなかった場合は、初期レコードは作成しない
                // （カードがタッチされていない、または読み取りエラー）
            }
            catch (Exception)
            {
                // 残額読み取りエラーの場合は、カード登録自体は成功させる
                // 初期レコードは後から手動で追加可能
            }
            finally
            {
                // 使用後は事前読み取り残高をクリア
                _preReadBalance = null;
            }
        }

        /// <summary>
        /// 履歴インポート前の初期残高を逆算（Issue #596）
        /// </summary>
        /// <remarks>
        /// 最も古い履歴エントリの残高と金額から、その取引前の残高を計算する。
        /// チャージの場合: 残高 - 金額 = チャージ前の残高
        /// 利用の場合: 残高 + 金額 = 利用前の残高
        /// </remarks>
        /// <param name="sortedHistory">日付順にソート済みの履歴リスト</param>
        /// <returns>最初の取引前の残高</returns>
        internal static int CalculatePreHistoryBalance(List<LedgerDetail> sortedHistory)
        {
            var oldest = sortedHistory
                .Where(d => d.UseDate.HasValue && d.Balance.HasValue)
                .OrderBy(d => d.UseDate)
                .ThenByDescending(d => d.Balance)
                .FirstOrDefault();

            if (oldest == null) return 0;

            if (oldest.IsCharge || oldest.IsPointRedemption)
                return (oldest.Balance ?? 0) - (oldest.Amount ?? 0);
            else
                return (oldest.Balance ?? 0) + (oldest.Amount ?? 0);
        }

        /// <summary>
        /// 履歴インポートの開始日を取得（Issue #596）
        /// </summary>
        /// <remarks>
        /// 新規購入: 当日（Issue #657: 月初めではなく購入日を使用）
        /// 繰越: 繰越月の翌月1日（SummaryGenerator.GetMidYearCarryoverDateを使用）
        /// </remarks>
        internal static DateTime GetImportFromDate(Views.Dialogs.CardRegistrationModeResult modeResult)
        {
            if (modeResult.IsNewPurchase)
                return modeResult.PurchaseDate?.Date ?? DateTime.Today;
            else
                return SummaryGenerator.GetMidYearCarryoverDate(
                    modeResult.CarryoverMonth!.Value, DateTime.Now);
        }

        /// <summary>
        /// 保存・更新・復元後に該当行を選択しハイライト対象に設定する
        /// </summary>
        /// <param name="idm">ハイライト対象のカードIDm</param>
        private void SelectAndHighlight(string idm)
        {
            var item = Cards.FirstOrDefault(c => c.CardIdm == idm);
            if (item != null)
            {
                SelectedCard = item;
                NewlyRegisteredIdm = idm;
            }
        }

        /// <summary>
        /// クリーンアップ
        /// </summary>
        public void Cleanup()
        {
            _cardReader.CardRead -= OnCardRead;

            // ダイアログ終了時にフラグを解除
            App.IsCardRegistrationActive = false;
        }
    }
}
