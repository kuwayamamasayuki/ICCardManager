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
        /// 事前に読み取った残高（Issue #381対応）
        /// </summary>
        /// <remarks>
        /// 未登録カード検出時にMainViewModelで残高を読み取り、この値に設定する。
        /// CreateNewPurchaseLedgerAsyncでこの値を使用することで、カードがリーダーから
        /// 離れた後でも正しい残高で「新規購入」レコードを作成できる。
        /// </remarks>
        private int? _preReadBalance;

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
            IDialogService dialogService)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
            _cardReader = cardReader;
            _cardTypeDetector = cardTypeDetector;
            _validationService = validationService;
            _operationLogger = operationLogger;
            _dialogService = dialogService;

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

                                    StatusMessage = $"{existing.CardNumber} を復元しました";
                                    IsStatusError = false;
                                    await LoadCardsAsync();
                                    CancelEdit();
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

                    var card = new IcCard
                    {
                        CardIdm = EditCardIdm,
                        CardType = EditCardType,
                        CardNumber = sanitizedCardNumber,
                        Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote
                    };

                    var success = await _cardRepository.InsertAsync(card);
                    if (success)
                    {
                        // 操作ログを記録
                        await _operationLogger.LogCardInsertAsync(null, card);

                        // カード残額を読み取り、「新規購入」レコードを作成
                        await CreateNewPurchaseLedgerAsync(EditCardIdm);

                        StatusMessage = "登録しました";
                        IsStatusError = false;
                        await LoadCardsAsync();
                        CancelEdit();
                    }
                    else
                    {
                        StatusMessage = "登録に失敗しました";
                        IsStatusError = true;
                    }
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

                        StatusMessage = "更新しました";
                        IsStatusError = false;
                        await LoadCardsAsync();
                        CancelEdit();
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
        private bool CanDelete() => SelectedCard != null && !SelectedCard.IsLent;

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
                    // 操作ログを記録
                    if (card != null)
                    {
                        await _operationLogger.LogCardDeleteAsync(null, card);
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
        private bool CanRefund() => SelectedCard != null && !SelectedCard.IsLent;

        /// <summary>
        /// 払い戻し処理
        /// </summary>
        /// <remarks>
        /// Issue #379対応: 交通系ICカードの払い戻しに対応。
        /// 払い戻し時は残高を払出金額として計上し、残高を0にしてからカードを論理削除する。
        /// これにより、物品出納簿に払い戻しの記録が残る。
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

            // 払い戻し確認ダイアログを表示
            var message = currentBalance > 0
                ? $"カード「{SelectedCard.CardType} {SelectedCard.CardNumber}」を払い戻しますか？\n\n現在の残高: ¥{currentBalance:N0}\n\n※払い戻し後、このカードは削除されます。"
                : $"カード「{SelectedCard.CardType} {SelectedCard.CardNumber}」を払い戻しますか？\n\n現在の残高: ¥0（残高なし）\n\n※払い戻し後、このカードは削除されます。";

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
                    // 払い戻し後のデータを取得（操作ログ用）
                    var card = await _cardRepository.GetByIdmAsync(SelectedCard.CardIdm);

                    // カードを論理削除
                    var deleteSuccess = await _cardRepository.DeleteAsync(SelectedCard.CardIdm);

                    if (deleteSuccess)
                    {
                        // 操作ログを記録（払い戻しは削除操作として記録）
                        if (card != null)
                        {
                            await _operationLogger.LogCardDeleteAsync(null, card);
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
                        StatusMessage = "カードの削除に失敗しました";
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

                                StatusMessage = $"{existing.CardNumber} を復元しました";
                                IsStatusError = false;
                                await LoadCardsAsync();
                                CancelEdit();
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
        /// 新規購入レコードを作成
        /// </summary>
        /// <param name="cardIdm">カードのIDm</param>
        private async Task CreateNewPurchaseLedgerAsync(string cardIdm)
        {
            try
            {
                // Issue #381対応: 事前に読み取った残高を優先的に使用
                // カードがリーダーから離れた後でも正しい残高で登録できる
                int? balance = _preReadBalance;

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
                    var ledger = new Ledger
                    {
                        CardIdm = cardIdm,
                        LenderIdm = null,  // 新規購入時は貸出者なし
                        Date = now,
                        Summary = "新規購入",
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
                // 残額が取得できなかった場合は、新規購入レコードは作成しない
                // （カードがタッチされていない、または読み取りエラー）
            }
            catch (Exception)
            {
                // 残額読み取りエラーの場合は、カード登録自体は成功させる
                // 新規購入レコードは後から手動で追加可能
            }
            finally
            {
                // 使用後は事前読み取り残高をクリア
                _preReadBalance = null;
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
