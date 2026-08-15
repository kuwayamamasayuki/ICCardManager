using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ICCardManager.Common;
using ICCardManager.Common.Messages;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;

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
        private readonly IValidationService _validationService;
        private readonly OperationLogger _operationLogger;
        private readonly IDialogService _dialogService;
        private readonly IStaffAuthService _staffAuthService;
        private readonly LendingService _lendingService;
        private readonly IMessenger _messenger;
        private readonly ILogger<CardManageViewModel>? _logger;

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
            IValidationService validationService,
            OperationLogger operationLogger,
            IDialogService dialogService,
            IStaffAuthService staffAuthService,
            LendingService lendingService,
            IMessenger messenger,
            ILogger<CardManageViewModel>? logger = null)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
            _cardReader = cardReader;
            _validationService = validationService;
            _operationLogger = operationLogger;
            _dialogService = dialogService;
            _staffAuthService = staffAuthService;
            _lendingService = lendingService;
            _messenger = messenger;
            _logger = logger;

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
                foreach (var card in cards.OrderByCardDefault(c => c.CardType, c => c.CardNumber))
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
            EditCardType = "nimoca";
            EditCardNumber = string.Empty;
            EditNote = string.Empty;
            StatusMessage = "カードをタッチするとIDmを読み取ります";
            IsStatusError = false;
            IsWaitingForCard = true;

            // MainViewModelでの未登録カード処理を抑制（Issue #852）
            _messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.CardRegistration));
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
                            // Issue #1760: 再読取が null になるのは復元の直後に他 PC が削除した場合だけ。
                            // 復元は確定済みなので記録を落とさず、復元前のデータで補う。
                            var restoredCard = await _cardRepository.GetByIdmAsync(idm)
                                ?? CreateRestoredSnapshot(existing);
                            await _operationLogger.LogCardRestoreAsync(restoredCard);

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
        /// ユーザー向けメッセージで交通系ICカードを特定するための表示名を組み立てる。
        /// </summary>
        /// <remarks>
        /// Issue #1759: 競合エラーの文言で同じ表記を使うため 1 か所に集約する
        /// （<c>StaffManageViewModel.FormatStaffLabel</c> と対になる）。
        /// </remarks>
        private static string FormatCardLabel(string cardType, string cardNumber)
            => $"交通系ICカード「{cardType} {cardNumber}」";

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

            var isAutoNumbered = string.IsNullOrWhiteSpace(sanitizedCardNumber);
            if (isAutoNumbered)
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
                                    // Issue #1760: 再読取が null になるのは復元の直後に他 PC が
                                    // 削除した場合だけ。復元は確定済みなので記録を落とさない。
                                    var restoredCard = await _cardRepository.GetByIdmAsync(EditCardIdm)
                                        ?? CreateRestoredSnapshot(existing);
                                    await _operationLogger.LogCardRestoreAsync(restoredCard);

                                    var restoredIdm = EditCardIdm;
                                    var restoredNumber = existing.CardNumber;
                                    await LoadCardsAsync();
                                    CancelEdit();
                                    SelectAndHighlight(restoredIdm);
                                    // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                                    // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                                    StatusMessage = $"{restoredNumber} を復元しました";
                                    IsStatusError = false;
                                }
                                else
                                {
                                    // Issue #1759: RestoreAsync が false を返すのは
                                    // UPDATE ... WHERE card_idm = @cardIdm AND is_deleted = 1 が
                                    // 0 行に一致した場合だけ。つまり他 PC が先に復元したことを意味する。
                                    await LoadCardsAsync();
                                    StatusMessage = ConcurrencyConflictMessage.ForRestore(
                                        FormatCardLabel(existing.CardType, existing.CardNumber), "カード一覧");
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

                    // Issue #1215: 紙の出納簿からの繰越時は累計受入・払出の初期値を保存
                    if (!modeResult.IsNewPurchase && modeResult.CarryoverMonth.HasValue)
                    {
                        var carryoverDate = SummaryGenerator.GetMidYearCarryoverDate(
                            modeResult.CarryoverMonth.Value, DateTime.Now);
                        card.CarryoverIncomeTotal = modeResult.CarryoverIncomeTotal;
                        card.CarryoverExpenseTotal = modeResult.CarryoverExpenseTotal;
                        card.CarryoverFiscalYear = FiscalYearHelper.GetFiscalYear(
                            carryoverDate.Year, carryoverDate.Month);
                    }

                    bool success;
                    try
                    {
                        success = await _cardRepository.InsertAsync(card);
                    }
                    catch (DuplicateCardNumberException duplicate)
                    {
                        if (isAutoNumbered)
                        {
                            // Issue #1106: 自動採番で番号が競合した場合、再採番してリトライ
                            sanitizedCardNumber = await _cardRepository.GetNextCardNumberAsync(EditCardType);
                            card.CardNumber = sanitizedCardNumber;
                            success = await _cardRepository.InsertAsync(card);
                        }
                        else
                        {
                            // 手動指定の番号が重複。文言は例外側（Issue #1757 で集約）を使い、
                            // 登録・編集・CSVインポートで同じ案内になるようにする
                            StatusMessage = duplicate.UserFriendlyMessage;
                            IsStatusError = true;
                            _registrationModeResult = null;
                            return;
                        }
                    }

                    if (success)
                    {
                        // 操作ログを記録
                        await _operationLogger.LogCardInsertAsync(card);

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

                        // Issue #1727: 履歴インポートが失敗した場合の理由（成功時は null）
                        string historyImportFailureReason = null;

                        if (filteredHistory != null && filteredHistory.Count > 0)
                        {
                            // 履歴がある場合: 初期残高を逆算してから初期レコードを組み立てる
                            var preHistoryBalance = CalculatePreHistoryBalance(filteredHistory);
                            // Issue #819: ユーザーが繰越額を明示的に入力した場合はそちらを優先
                            var initialBalance = modeResult.CarryoverBalance ?? preHistoryBalance;
                            var initialLedger = await BuildInitialLedgerAsync(EditCardIdm, modeResult,
                                overrideDate: importFromDate, overrideBalance: initialBalance);

                            // Issue #1727: 初期残高行は履歴最古エントリから逆算した値のため、
                            // 履歴行と同一トランザクションで確定させる（片方だけ残ると残高チェーンがずれる）。
                            var importResult = await _lendingService.ImportHistoryForRegistrationAsync(
                                EditCardIdm, filteredHistory, importFromDate, initialLedger);

                            if (!importResult.Success)
                            {
                                // Issue #1727: 以前はここで Success を見ておらず「登録しました」と表示していた。
                                // 台帳には 1 行も入っていないため、必ずユーザーへ通知する。
                                historyImportFailureReason = string.IsNullOrWhiteSpace(importResult.FailureReason)
                                    ? "データベースへの書き込み中に問題が発生しました。"
                                    : importResult.FailureReason;
                            }
                            else if (importResult.MayHaveIncompleteHistory)
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
                        try
                        {
                            await LoadCardsAsync();
                        }
                        catch (Exception ex) when (historyImportFailureReason != null)
                        {
                            // Issue #1727: 取込が失敗する原因（共有フォルダの切断・DB のロック）は、
                            // 直後の一覧再読込でも同じく例外になる。ここで例外を通すと
                            // **失敗の通知そのものが失われ**、無言失敗に逆戻りする
                            // （カード行と操作ログはコミット済みなので、職員は登録失敗と誤解して
                            // 再登録し「既に登録されています」に突き当たる）。
                            // 例外フィルタで失敗時のみ握るため、成功時の挙動は変えない。
                            _logger?.LogWarning(ex,
                                "履歴取込失敗の通知前に行うカード一覧の再読込に失敗しました。" +
                                "一覧は古い可能性がありますが、取込失敗の通知は続行します。");
                        }
                        CancelEdit();
                        SelectAndHighlight(savedIdm);

                        // Issue #1727: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                        // 結果の表示は必ず後処理のあとに行う（先に設定すると消えて何も表示されない）。
                        if (historyImportFailureReason != null)
                        {
                            _dialogService.ShowError(
                                $"交通系ICカード（管理番号 {sanitizedCardNumber}）の登録は完了しました。\n\n" +
                                $"ただし、カード内の利用履歴を台帳に取り込めませんでした。{historyImportFailureReason}\n\n" +
                                "取込は取り消されたため、この交通系ICカードの台帳には利用履歴の行も" +
                                "登録時の残高の行も記録されていません。このままでは月次帳票の残額が" +
                                "実際のカード残高と一致しません。\n\n" +
                                "履歴画面のCSVインポートで利用履歴を取り込むか、" +
                                "履歴画面から残高の行を手動で追加してください。",
                                "利用履歴の取込に失敗");
                            StatusMessage = "カードは登録しましたが利用履歴を取り込めませんでした。" +
                                "履歴画面のCSVインポートで補完してください。";
                            IsStatusError = true;
                        }
                        else
                        {
                            StatusMessage = "登録しました";
                            IsStatusError = false;
                        }
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

                    // Issue #1760: 読み取れなかった時点で「対象カードは現在 is_deleted = 0 として
                    // 存在しない」ことが確定しているため、UpdateAsync を呼ばずに競合として扱う。
                    // 通常は UpdateAsync の WHERE（同じ is_deleted = 0）も 0 行になるが、
                    // 読み取りと書き込みの間に他 PC がカードを復元すると 1 行に一致して成功し、
                    // 更新だけが通って operation_log には 1 行も残らない。operation_log は
                    // 6 年保存される唯一の監査記録であり、「誰がいつ何を変更したのか分からない変更」が
                    // 残ることは、誤った記録が残るのと同等以上に問題になる。
                    if (beforeCard == null)
                    {
                        await NotifyUpdateConflictAsync(EditCardType, sanitizedCardNumber);
                        return;
                    }

                    // Issue #1726: この画面で編集できるのはカード種別・管理番号・備考の3項目だけ。
                    // それ以外の列（繰越累計 #1215 / 開始ページ番号 #510 / 貸出状態 / 払戻状態）は
                    // 専用の経路でのみ変化するため、DB の最新値（beforeCard）をそのまま引き継ぐ。
                    // 引き継がないと OperationLogger が IcCard 全体を JSON 化して BeforeData /
                    // AfterData に記録するため、「開始ページ番号 7 → 1」「払戻済み: はい → いいえ」の
                    // ような実際には起きていない変更が監査ログに残る。
                    // 一覧（SelectedCard）ではなく beforeCard を使うのは、一覧が GetAllAsync の
                    // キャッシュ由来で自動更新されず、共有モードでは他 PC の貸出が反映されないため。
                    var card = new IcCard
                    {
                        CardIdm = EditCardIdm,
                        CardType = EditCardType,
                        CardNumber = sanitizedCardNumber,
                        Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote,
                        IsLent = beforeCard.IsLent,
                        LastLentAt = beforeCard.LastLentAt,
                        LastLentStaff = beforeCard.LastLentStaff,
                        IsRefunded = beforeCard.IsRefunded,
                        RefundedAt = beforeCard.RefundedAt,
                        StartingPageNumber = beforeCard.StartingPageNumber,
                        CarryoverIncomeTotal = beforeCard.CarryoverIncomeTotal,
                        CarryoverExpenseTotal = beforeCard.CarryoverExpenseTotal,
                        CarryoverFiscalYear = beforeCard.CarryoverFiscalYear
                    };

                    bool success;
                    try
                    {
                        success = await _cardRepository.UpdateAsync(card);
                    }
                    catch (DuplicateCardNumberException duplicate)
                    {
                        // Issue #1757: 登録経路と同じ案内を出す。捕捉しないと未処理例外ハンドラー
                        // 任せになり、「予期しないエラーが発生しました。／エラーコード: CARD001」の
                        // モーダルダイアログが出る（AppException 継承前は SYS999）。原因は分かっても
                        // 操作の文脈から切り離されるため、ステータス欄でその場に出す。
                        // CancelEdit() は呼ばない（番号だけ直して再保存できるようにする）。
                        StatusMessage = duplicate.UserFriendlyMessage;
                        IsStatusError = true;
                        return;
                    }

                    if (success)
                    {
                        // 操作ログを記録（beforeCard は上のガードで非 null が確定している。Issue #1760）
                        await _operationLogger.LogCardUpdateAsync(beforeCard, card);

                        var updatedIdm = EditCardIdm;
                        await LoadCardsAsync();
                        CancelEdit();
                        SelectAndHighlight(updatedIdm);
                        // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                        // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                        StatusMessage = "更新しました";
                        IsStatusError = false;
                    }
                    else
                    {
                        // Issue #1759: UpdateAsync が false を返すのは
                        // UPDATE ... WHERE card_idm = @cardIdm AND is_deleted = 0 が
                        // 0 行に一致した場合だけ（Issue #1753）。つまり編集中に対象カードが
                        // 論理削除されたことを意味する。
                        await NotifyUpdateConflictAsync(EditCardType, sanitizedCardNumber);
                    }
                }
            }
        }

        /// <summary>
        /// 更新対象のカードが見つからなかった（競合）ことを案内し、カード一覧を再読込する
        /// </summary>
        /// <param name="fallbackCardType">一覧に対象が残っていないときに名指しへ使うカード種別</param>
        /// <param name="fallbackCardNumber">同じく管理番号</param>
        /// <remarks>
        /// <para>
        /// Issue #1753: 一覧を再読込しないと削除済みのカードが選択されたまま残り、
        /// 何度保存しても同じメッセージが出続ける（「競合検出時は UI 側で一覧を再読込すること」）。
        /// 再読込を先に行うのは、文言が「再読み込みしました」と述べるため。
        /// <c>CancelEdit()</c> は呼ばない（入力内容を消さない。Issue #1757）。
        /// </para>
        /// <para>
        /// 「何が」は<b>編集後の入力値ではなく一覧に載っている値</b>で名指しする。
        /// 管理番号や種別を書き換えている途中なら、編集後の値は一覧のどこにも
        /// 存在せず「一覧で状態を確認して」という案内が実行できなくなる。
        /// 一覧の再読込で DataGrid の選択が解除され <see cref="SelectedCard"/> は null に
        /// なる（<c>SelectedItem</c> は TwoWay バインド）ため、再読込より前に確定させる。
        /// </para>
        /// <para>
        /// Issue #1760: この「ラベル確定 → 再読込 → 文言設定」の順序を守る箇所が
        /// 複数（更新前データの欠落・更新の影響行数 0・払戻前データの欠落）に増えたため、
        /// 呼び出し側へ書き写さずここへ集約する。
        /// </para>
        /// </remarks>
        private Task NotifyUpdateConflictAsync(string fallbackCardType, string fallbackCardNumber)
            => NotifyConflictAsync(ConcurrencyConflictMessage.ForUpdate, fallbackCardType, fallbackCardNumber);

        /// <summary>
        /// 払い戻し対象のカードが見つからなかった（競合）ことを案内し、カード一覧を再読込する
        /// </summary>
        /// <param name="fallbackCardType">一覧に対象が残っていないときに名指しへ使うカード種別</param>
        /// <param name="fallbackCardNumber">同じく管理番号</param>
        /// <remarks>
        /// Issue #1760: 「なぜ」は更新と同じ（対象行が削除された）だが、「何が」は利用者が
        /// 実際に行った操作で述べる。<see cref="NotifyUpdateConflictAsync"/> を流用すると
        /// 払い戻しを試みた職員に「更新できませんでした」と案内することになる。
        /// </remarks>
        private Task NotifyRefundConflictAsync(string fallbackCardType, string fallbackCardNumber)
            => NotifyConflictAsync(ConcurrencyConflictMessage.ForRefund, fallbackCardType, fallbackCardNumber);

        /// <summary>
        /// 競合の案内文言を組み立てて表示する（ラベル確定 → 一覧再読込 → 文言設定の順序を守る）
        /// </summary>
        /// <param name="messageFactory">操作に対応する <see cref="ConcurrencyConflictMessage"/> のファクトリ</param>
        /// <param name="fallbackCardType">一覧に対象が残っていないときに名指しへ使うカード種別</param>
        /// <param name="fallbackCardNumber">同じく管理番号</param>
        private async Task NotifyConflictAsync(
            Func<string, string, string> messageFactory,
            string fallbackCardType,
            string fallbackCardNumber)
        {
            var conflictLabel = SelectedCard != null
                ? FormatCardLabel(SelectedCard.CardType, SelectedCard.CardNumber)
                : FormatCardLabel(fallbackCardType, fallbackCardNumber);

            // Issue #1760: 更新前データを読めずに書き込みを中止した経路は、リポジトリの
            // 書き込みを 1 回も通らないため、影響行数 0 でのキャッシュ破棄（Issue #1759）が
            // 働かない。LoadCardsAsync() は GetAllAsync のキャッシュ（既定 TTL 60 秒／
            // 共有モード 15 秒）を読むため、ここで破棄しないと削除済みのカードを含む
            // 古い一覧が返り、「一覧を再読み込みしました」という案内が事実にならない。
            _cardRepository.InvalidateCache();
            await LoadCardsAsync();
            StatusMessage = messageFactory(conflictLabel, "カード一覧");
            IsStatusError = true;
        }

        /// <summary>
        /// 削除コマンドが実行可能かどうか
        /// </summary>
        /// <remarks>
        /// Issue #530: 払戻済カードは既に運用から除外されているため削除不可
        /// </remarks>
        /// <remarks>
        /// Issue #1109: IsLentチェックをここから除外。
        /// 共有モードで他PCがカードを貸出中にすると、ヘルスチェックでSelectedCard.IsLentが
        /// trueに更新されボタンがサイレントに無効化される。ユーザーにフィードバックがないため、
        /// ボタンは有効のまま、DeleteAsync内で即時エラーメッセージを表示する。
        /// </remarks>
        private bool CanDelete() => SelectedCard != null && !SelectedCard.IsRefunded;

        /// <summary>
        /// 削除
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDelete))]
        public async Task DeleteAsync()
        {
            if (SelectedCard == null) return;

            if (SelectedCard.IsLent)
            {
                // Issue #1109: 編集フォーム非表示時でもユーザーにフィードバックするためダイアログで通知
                _dialogService.ShowError("このカードは貸出中のため削除できません。", "削除できません");
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
                // Issue #1760: 識別情報は再読込より前に確定させる（再読込で選択が解除されるため）
                var targetIdm = SelectedCard.CardIdm;
                var targetLabel = FormatCardLabel(SelectedCard.CardType, SelectedCard.CardNumber);

                // 削除前のデータを取得（操作ログ用）
                //
                // Issue #1760: 読めなければ削除自体を行わない。読み取れない時点で対象カードは
                // 現在存在しないが、その直後に他 PC が復元すると DeleteAsync（WHERE is_deleted = 0）は
                // 1 行に一致して成功し得る。従来の `if (card != null)` ガードでは、論理削除だけが
                // 確定して operation_log には 1 行も残らなかった。削除は監査上もっとも重要な操作で、
                // 「誰がいつ削除したのか分からない」記録は後から復元できない。
                var card = await _cardRepository.GetByIdmAsync(targetIdm);
                if (card == null)
                {
                    await NotifyDeleteConflictAsync(targetLabel);
                    return;
                }

                var deleteResult = await _cardRepository.DeleteAsync(targetIdm);
                if (deleteResult == CardOperationResult.Success)
                {
                    // 操作ログを記録（Issue #429: 認証済み職員のIDmを使用）
                    await _operationLogger.LogCardDeleteAsync(card);

                    await LoadCardsAsync();
                    CancelEdit();
                    // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                    // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                    StatusMessage = "削除しました";
                    IsStatusError = false;
                }
                else if (deleteResult == CardOperationResult.NotFound)
                {
                    // Issue #1760: 事前読み取りで検出した場合と**同じ条件**（対象行が無い）なので
                    // 同じ文言で案内する。検出のタイミングによって案内が変わると、
                    // 同じ状況に別の説明が出ることになる（Issue #1757 と同じ判断）。
                    await NotifyDeleteConflictAsync(targetLabel);
                }
                else
                {
                    // Issue #1109: 失敗原因に応じた具体的なメッセージをダイアログで表示
                    // （編集フォーム非表示時はStatusMessageが見えないため）
                    var failureMessage = GetOperationFailureMessage(deleteResult, "削除");
                    _dialogService.ShowError(failureMessage, "削除できません");
                    await LoadCardsAsync();
                }
            }
        }

        /// <summary>
        /// 削除対象のカードが見つからなかった（競合）ことを案内し、カード一覧を再読込する
        /// </summary>
        /// <param name="targetLabel">対象カードの表示名（再読込より前に確定させたもの）</param>
        /// <remarks>
        /// Issue #1760: 削除は編集フォーム非表示時にも実行できるため、案内はステータス欄ではなく
        /// ダイアログで出す（Issue #1109 と同じ理由）。文言が「再読み込みしました」と述べるため
        /// 再読込を先に行い、書き込みを 1 回も通らない経路ではキャッシュも明示的に破棄する。
        /// </remarks>
        private async Task NotifyDeleteConflictAsync(string targetLabel)
        {
            _cardRepository.InvalidateCache();
            await LoadCardsAsync();
            _dialogService.ShowError(
                ConcurrencyConflictMessage.ForDelete(targetLabel, "カード一覧"),
                "削除できません");
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
        /// <remarks>
        /// Issue #1109: IsLentチェックをここから除外（CanDeleteと同じ理由）。
        /// </remarks>
        private bool CanRefund() => SelectedCard != null && !SelectedCard.IsRefunded;

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
                // Issue #1109: 編集フォーム非表示時でもユーザーにフィードバックするためダイアログで通知
                _dialogService.ShowError("このカードは貸出中のため払い戻しできません。", "払い戻しできません");
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
                var refundCardIdm = SelectedCard.CardIdm;

                // 払い戻し前のデータを取得（操作ログ用）
                //
                // Issue #1760: 書き込みより**前**に取得し、読めなければ払い戻し自体を行わない。
                // 読み取れない（is_deleted = 0 で引けない）時点で対象カードは現在存在しないが、
                // その直後に他 PC が復元すると SetRefundedAsync は 1 行に一致して成功し得る。
                // 従来の `if (beforeCard != null)` ガードでは、払戻済への変更だけが確定して
                // operation_log には 1 行も残らなかった。
                //
                // 副次的に「読み取り時点で既に対象カードが無い」場合の払戻台帳の作成も避けられるが、
                // **払戻台帳だけが残る状態を完全には防げない**。この読み取りと SetRefundedAsync の
                // 間に他 PC が削除・貸出した場合、台帳の INSERT は既にコミット済みで
                // SetRefundedAsync だけが失敗する（下の失敗分岐はダイアログを出すのみ）。
                // 恒久対処には台帳と払戻済更新を 1 トランザクションに束ねるか補償削除が要る（別 Issue）。
                var beforeCard = await _cardRepository.GetByIdmAsync(refundCardIdm);
                if (beforeCard == null)
                {
                    await NotifyRefundConflictAsync(SelectedCard.CardType, SelectedCard.CardNumber);
                    return;
                }

                // 払い戻しのLedgerを作成
                var now = DateTime.Now;
                var refundLedger = new Ledger
                {
                    CardIdm = refundCardIdm,
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
                    // Issue #530: カードを「払戻済」状態に設定（論理削除ではない）
                    var refundResult = await _cardRepository.SetRefundedAsync(refundCardIdm);

                    if (refundResult == CardOperationResult.Success)
                    {
                        // 払い戻し後のデータを取得（操作ログ用）
                        //
                        // Issue #1760: 再読取が null になるのは、払い戻しが確定した直後に
                        // 他 PC がこのカードを論理削除した場合だけ。払い戻しは既に確定しているため、
                        // 再読取の失敗を理由に監査記録を落としてはならない。この操作が変えた列
                        // （払戻状態）だけを払い戻し前のデータへ適用したスナップショットで記録する。
                        var afterCard = await _cardRepository.GetByIdmAsync(refundCardIdm)
                            ?? CreateRefundedSnapshot(beforeCard, now);

                        // 操作ログを記録（払い戻しはカード更新として記録）
                        await _operationLogger.LogCardUpdateAsync(beforeCard, afterCard);

                        await LoadCardsAsync();
                        CancelEdit();
                        // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                        // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                        StatusMessage = currentBalance > 0
                            ? $"払い戻しが完了しました（払戻額: ¥{currentBalance:N0}）"
                            : "払い戻しが完了しました";
                        IsStatusError = false;
                    }
                    else
                    {
                        // Issue #1109: 失敗原因に応じた具体的なメッセージをダイアログで表示
                        var failureMessage = GetOperationFailureMessage(refundResult, "払い戻し");
                        _dialogService.ShowError(failureMessage, "払い戻しできません");
                        await LoadCardsAsync();
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
        /// 払い戻し後のカードの状態を、払い戻し前のデータから組み立てる
        /// </summary>
        /// <param name="beforeCard">払い戻し前に読み取ったカード</param>
        /// <param name="refundedAt">払い戻しを実施した日時</param>
        /// <remarks>
        /// Issue #1760: 払い戻し直後の再読取が失敗したときに、操作ログの <c>AfterData</c> として使う。
        /// <c>SetRefundedAsync</c> が変えるのは <c>is_refunded</c> / <c>refunded_at</c> の 2 列だけなので、
        /// それ以外は払い戻し前の値をそのまま引き継ぐ（引き継がないと「開始ページ番号 7 → 1」のような
        /// 実際には起きていない変更が監査ログに残る。Issue #1726 と同じ理由）。
        /// <para>
        /// <paramref name="refundedAt"/> は呼び出し側が採った時刻であり、
        /// <c>SetRefundedAsync</c> が書く <c>datetime('now','localtime')</c> とは厳密には一致しない。
        /// 再読取が失敗した以上 DB の値は取得できず、記録を落とすより近似値で残す方が監査に資する。
        /// </para>
        /// </remarks>
        private static IcCard CreateRefundedSnapshot(IcCard beforeCard, DateTime refundedAt)
        {
            return new IcCard
            {
                CardIdm = beforeCard.CardIdm,
                CardType = beforeCard.CardType,
                CardNumber = beforeCard.CardNumber,
                Note = beforeCard.Note,
                IsDeleted = beforeCard.IsDeleted,
                DeletedAt = beforeCard.DeletedAt,
                IsLent = beforeCard.IsLent,
                LastLentAt = beforeCard.LastLentAt,
                LastLentStaff = beforeCard.LastLentStaff,
                StartingPageNumber = beforeCard.StartingPageNumber,
                CarryoverIncomeTotal = beforeCard.CarryoverIncomeTotal,
                CarryoverExpenseTotal = beforeCard.CarryoverExpenseTotal,
                CarryoverFiscalYear = beforeCard.CarryoverFiscalYear,
                IsRefunded = true,
                RefundedAt = refundedAt
            };
        }

        /// <summary>
        /// 復元後のカードの状態を、復元前に読み取ったデータから組み立てる
        /// </summary>
        /// <param name="deletedCard">復元前に読み取ったカード（<c>includeDeleted: true</c> で取得したもの）</param>
        /// <remarks>
        /// Issue #1760: 復元直後の再読取が失敗したときに、操作ログの <c>AfterData</c> として使う。
        /// <c>RestoreAsync</c> が変えるのは <c>is_deleted</c> / <c>deleted_at</c> の 2 列だけなので、
        /// それ以外は復元前の値をそのまま引き継ぐ（<see cref="CreateRefundedSnapshot"/> と同じ理由）。
        /// </remarks>
        private static IcCard CreateRestoredSnapshot(IcCard deletedCard)
        {
            return new IcCard
            {
                CardIdm = deletedCard.CardIdm,
                CardType = deletedCard.CardType,
                CardNumber = deletedCard.CardNumber,
                Note = deletedCard.Note,
                IsLent = deletedCard.IsLent,
                LastLentAt = deletedCard.LastLentAt,
                LastLentStaff = deletedCard.LastLentStaff,
                IsRefunded = deletedCard.IsRefunded,
                RefundedAt = deletedCard.RefundedAt,
                StartingPageNumber = deletedCard.StartingPageNumber,
                CarryoverIncomeTotal = deletedCard.CarryoverIncomeTotal,
                CarryoverExpenseTotal = deletedCard.CarryoverExpenseTotal,
                CarryoverFiscalYear = deletedCard.CarryoverFiscalYear,
                IsDeleted = false,
                DeletedAt = null
            };
        }

        /// <summary>
        /// カード操作の失敗原因に応じたエラーメッセージを返す
        /// </summary>
        /// <param name="result">操作結果</param>
        /// <param name="operationName">操作名（「削除」「払い戻し」等）</param>
        private static string GetOperationFailureMessage(CardOperationResult result, string operationName)
        {
            return result switch
            {
                CardOperationResult.NotFound => $"{operationName}対象のカードが見つかりませんでした。画面を更新してください。",
                CardOperationResult.CardIsLent => $"このカードは貸出中のため{operationName}できません。",
                CardOperationResult.Conflict => $"他のPCでカードの状態が変更されたため{operationName}できませんでした。画面を更新してから再度お試しください。",
                _ => $"{operationName}に失敗しました。",
            };
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

            // ICカード登録モードを解除（Issue #852）
            _messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.CardRegistration));
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
                                // Issue #1760: 再読取が null になるのは復元の直後に他 PC が
                                // 削除した場合だけ。復元は確定済みなので記録を落とさない。
                                var restoredCard = await _cardRepository.GetByIdmAsync(e.Idm)
                                    ?? CreateRestoredSnapshot(existing);
                                await _operationLogger.LogCardRestoreAsync(restoredCard);

                                var restoredIdm = e.Idm;
                                var restoredNumber = existing.CardNumber;
                                await LoadCardsAsync();
                                CancelEdit();
                                SelectAndHighlight(restoredIdm);
                                // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
                                // 完了メッセージは必ず後処理のあとに設定する（先に設定すると一度も表示されない）。
                                StatusMessage = $"{restoredNumber} を復元しました";
                                IsStatusError = false;
                            }
                            else
                            {
                                // Issue #1759: 保存経路（SaveAsync）の復元分岐と同じ扱い。
                                // false は「他 PC が先に復元した」ことを意味する。
                                await LoadCardsAsync();
                                StatusMessage = ConcurrencyConflictMessage.ForRestore(
                                    FormatCardLabel(existing.CardType, existing.CardNumber), "カード一覧");
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

                // 注意: CardRegistration抑制はここで解除しない
                // ダイアログが開いている間は常に抑制を維持し、
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
            return _dialogService.ShowCardRegistrationModeDialog(_preReadBalance);
        }

        /// <summary>
        /// 初期レコード（新規購入または繰越）を作成して登録（Issue #510）
        /// </summary>
        /// <remarks>
        /// 履歴インポートを伴わない経路（カード内に対象履歴が無い場合）で使用する。
        /// 履歴がある場合は <see cref="BuildInitialLedgerAsync"/> で組み立てたものを
        /// <c>LendingService.ImportHistoryForRegistrationAsync</c> へ渡し、
        /// 履歴行と同一トランザクションで登録すること（Issue #1727）。
        /// </remarks>
        /// <param name="cardIdm">カードのIDm</param>
        /// <param name="modeResult">登録モードの選択結果</param>
        private async Task CreateInitialLedgerAsync(
            string cardIdm,
            Views.Dialogs.CardRegistrationModeResult modeResult)
        {
            var ledger = await BuildInitialLedgerAsync(cardIdm, modeResult);
            if (ledger == null)
            {
                // 残額が取得できなかった場合は、初期レコードは作成しない
                // （カードがタッチされていない、または読み取りエラー）
                return;
            }

            try
            {
                await _ledgerRepository.InsertAsync(ledger);
            }
            catch (Exception ex)
            {
                // Issue #1282: 初期レコードの登録に失敗しても、カード登録自体は成功させる。
                //
                // **注意（Issue #1727 のレビュー指摘）**: この握りつぶしは「軽微だから」ではない。
                // ここで失われる行は「新規購入 / ○月から繰越」＝**そのカード唯一の受入行**であり、
                // 欠落すると月次帳票で「受入 − 払出 = 残額」が年度を通して成立しなくなる。
                // 履歴インポート経路（#1727 で修正済み）と同じ無言のデータ欠損が、
                // この分岐にはまだ残っている。加えてこの経路は ExecuteWithRetryAsync で
                // 包まれていないため、共有モードの一過性 SQLITE_BUSY で一発失敗し得る。
                // 是正には失敗をユーザーへ通知する経路が必要なため、Issue #1763 で扱う。
                _logger?.LogWarning(ex,
                    "カード登録後の初期残額レコードの登録に失敗しました。" +
                    "カード登録自体は成功しており、初期レコードは後から手動で追加できます。");
            }
        }

        /// <summary>
        /// 初期レコード（新規購入または繰越）を組み立てる（Issue #1727 で <see cref="CreateInitialLedgerAsync"/> から分離）
        /// </summary>
        /// <remarks>
        /// DB へは書き込まない。呼び出し元が登録タイミング（単独 / 履歴インポートと同一トランザクション）を決める。
        /// </remarks>
        /// <param name="cardIdm">カードのIDm</param>
        /// <param name="modeResult">登録モードの選択結果</param>
        /// <param name="overrideDate">日付の上書き（Issue #596: 履歴がある場合、インポート開始日を使用）</param>
        /// <param name="overrideBalance">残高の上書き（Issue #596: 履歴がある場合、逆算した初期残高を使用）</param>
        /// <returns>組み立てた初期レコード。残額が取得できない場合や組み立てに失敗した場合は null</returns>
        private async Task<Ledger> BuildInitialLedgerAsync(
            string cardIdm,
            Views.Dialogs.CardRegistrationModeResult modeResult,
            DateTime? overrideDate = null,
            int? overrideBalance = null)
        {
            try
            {
                // Issue #596: overrideBalanceが指定された場合はそれを使用
                // Issue #756: ユーザー指定の繰越額を次に優先
                // Issue #381対応: 事前に読み取った残高を最後に使用
                int? balance = overrideBalance ?? modeResult.CarryoverBalance ?? _preReadBalance;

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
                    // 繰越月が3月の場合は年度末＝前年度繰越と同義なので、
                    // 「前年度より繰越」として扱い受入金額にも残高を記録する。
                    var isFiscalYearCarryover = !modeResult.IsNewPurchase
                        && modeResult.CarryoverMonth!.Value == 3;
                    string summary;
                    if (modeResult.IsNewPurchase)
                    {
                        summary = "新規購入";
                    }
                    else if (isFiscalYearCarryover)
                    {
                        // 3月から繰越 = 前年度より繰越
                        summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary();
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
                        // 3月の場合は4月1日＝新年度初日になる
                        recordDate = SummaryGenerator.GetMidYearCarryoverDate(modeResult.CarryoverMonth!.Value, now);
                    }

                    // 年度途中導入の繰越（「○月から繰越」）の場合、受入金額は空欄にする。
                    // ただし3月（前年度繰越）と新規購入は受入金額に残高を記録する。
                    // 月次帳票のルール: 受入欄に金額が入るのは4月の前年度繰越と新規購入のみ。
                    var hasIncome = modeResult.IsNewPurchase || isFiscalYearCarryover;
                    var ledger = new Ledger
                    {
                        CardIdm = cardIdm,
                        LenderIdm = null,  // 新規購入/繰越時は貸出者なし
                        Date = recordDate,
                        Summary = summary,
                        Income = hasIncome ? balance.Value : 0,
                        Expense = 0,
                        Balance = balance.Value,
                        StaffName = null,  // 利用者なし
                        Note = null,
                        ReturnerIdm = null,
                        LentAt = null,
                        ReturnedAt = null,
                        IsLentRecord = false
                    };

                    return ledger;
                }

                // 残額が取得できなかった場合は、初期レコードは作成しない
                // （カードがタッチされていない、または読み取りエラー）
                return null;
            }
            catch (Exception ex)
            {
                // Issue #1282: 残額読み取りエラーの場合は、カード登録自体は成功させる
                // 初期レコードは後から手動で追加可能。ただし原因追跡のため
                // 警告レベルで記録する（カード登録フローの一部なので失敗は稀な想定）。
                _logger?.LogWarning(ex,
                    "カード登録後の初期残額レコード作成に失敗しました。" +
                    "カード登録自体は成功しており、初期レコードは後から手動で追加できます。");
                return null;
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
        /// 保存・更新・復元後にハイライト対象のIDmを設定する
        /// </summary>
        /// <remarks>
        /// View層がNewlyRegisteredIdmの変更を監視し、該当行のスクロール＋ハイライトを行う。
        /// 選択行の背景色と競合しないよう、SelectedCardは設定しない（View層で選択解除する）。
        /// </remarks>
        /// <param name="idm">ハイライト対象のカードIDm</param>
        private void SelectAndHighlight(string idm)
        {
            // 同じIDmの連続操作でもPropertyChangedが発火するようリセット
            NewlyRegisteredIdm = null;
            NewlyRegisteredIdm = idm;
        }

        /// <summary>
        /// クリーンアップ
        /// </summary>
        public void Cleanup()
        {
            _cardReader.CardRead -= OnCardRead;

            // ダイアログ終了時に抑制を解除（Issue #852）
            _messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.CardRegistration));
        }
    }
}
