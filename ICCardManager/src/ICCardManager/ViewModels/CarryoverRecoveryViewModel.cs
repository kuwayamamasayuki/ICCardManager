#nullable enable
using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 繰越情報の復旧ダイアログの ViewModel（Issue #2255）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 紙の出納簿から移行したカードの繰越情報（開始ページ番号・繰越累計・対象年度）が過去の版で失われた場合に、
    /// IT担当者へ DB の修正を依頼せず、その場で書き戻す。入力欄の初期値は操作ログに残っていた失われた値
    /// （失われていない項目は現在の値）で、職員は紙の出納簿と突き合わせて確かめてから保存する。
    /// </para>
    /// <para>
    /// 保存には職員証の認証を求める（カードの削除と同じ。Issue #429）。繰越情報は物品出納簿の頁番号と
    /// 年度累計に効く値で、監査ログ（6 年保存）に実際の操作者を残すため。
    /// </para>
    /// </remarks>
    public partial class CarryoverRecoveryViewModel : ViewModelBase
    {
        /// <summary>認証が完了しなかったときの案内</summary>
        internal const string AuthenticationCancelledMessage =
            "職員証の認証が完了しなかったため、保存していません。保存するには、もう一度「保存」を押して職員証をタッチしてください。";

        private readonly ICardRepository _cardRepository;
        private readonly CardManagementService _cardManagementService;
        private readonly IStaffAuthService _staffAuthService;
        private readonly ISystemClock _clock;
        private readonly ILogger<CarryoverRecoveryViewModel> _logger;

        /// <summary>復旧の対象（一覧で選んだ行の検出結果）</summary>
        private CarryoverDataLossItem? _target;

        /// <summary>
        /// 画面を開いたときに読んだ繰越情報
        /// </summary>
        /// <remarks>
        /// 職員はこの値を「現在の値」として見たうえで入力を決める。保存の直前に読み直した値がこれと
        /// 食い違うなら、開いている間に他のパソコンで復旧された等の競合であり、見ていない値を上書きしない。
        /// </remarks>
        private CarryoverInfo? _loadedCurrent;

        public CarryoverRecoveryViewModel(
            ICardRepository cardRepository,
            CardManagementService cardManagementService,
            IStaffAuthService staffAuthService,
            ISystemClock clock,
            ILogger<CarryoverRecoveryViewModel> logger)
        {
            _cardRepository = cardRepository ?? throw new ArgumentNullException(nameof(cardRepository));
            _cardManagementService = cardManagementService ?? throw new ArgumentNullException(nameof(cardManagementService));
            _staffAuthService = staffAuthService ?? throw new ArgumentNullException(nameof(staffAuthService));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 入力に誤りがあったとき、誤りのある入力欄へフォーカスを移すよう View へ求める
        /// </summary>
        public event EventHandler<CarryoverInputField>? FocusRequested;

        /// <summary>対象カードの表示名（例: "はやかけん 001"）</summary>
        [ObservableProperty]
        private string _cardDisplayName = string.Empty;

        /// <summary>開始ページ番号の入力</summary>
        [ObservableProperty]
        private string _startingPageNumberText = string.Empty;

        /// <summary>繰越累計受入の入力（円）</summary>
        [ObservableProperty]
        private string _carryoverIncomeTotalText = string.Empty;

        /// <summary>繰越累計払出の入力（円）</summary>
        [ObservableProperty]
        private string _carryoverExpenseTotalText = string.Empty;

        /// <summary>対象年度の入力（西暦。空欄は「年度なし」）</summary>
        [ObservableProperty]
        private string _carryoverFiscalYearText = string.Empty;

        /// <summary>失われた開始ページ番号（失われていなければ「（消失なし）」）</summary>
        [ObservableProperty]
        private string _lostStartingPageNumberText = string.Empty;

        /// <summary>失われた繰越累計受入</summary>
        [ObservableProperty]
        private string _lostCarryoverIncomeTotalText = string.Empty;

        /// <summary>失われた繰越累計払出</summary>
        [ObservableProperty]
        private string _lostCarryoverExpenseTotalText = string.Empty;

        /// <summary>失われた対象年度</summary>
        [ObservableProperty]
        private string _lostCarryoverFiscalYearText = string.Empty;

        /// <summary>現在の開始ページ番号</summary>
        [ObservableProperty]
        private string _currentStartingPageNumberText = string.Empty;

        /// <summary>現在の繰越累計受入</summary>
        [ObservableProperty]
        private string _currentCarryoverIncomeTotalText = string.Empty;

        /// <summary>現在の繰越累計払出</summary>
        [ObservableProperty]
        private string _currentCarryoverExpenseTotalText = string.Empty;

        /// <summary>現在の対象年度</summary>
        [ObservableProperty]
        private string _currentCarryoverFiscalYearText = string.Empty;

        /// <summary>案内・エラーの文言</summary>
        [ObservableProperty]
        private string _statusMessage = string.Empty;

        /// <summary><see cref="StatusMessage"/> がエラーか</summary>
        [ObservableProperty]
        private bool _isStatusError;

        /// <summary>
        /// 保存できる状態か（カードを読めた・競合を検出していない）
        /// </summary>
        /// <remarks>
        /// 競合を検出した後は保存させない。開いたときに読んだ値はもう古く、同じ画面からやり直しても
        /// 同じ競合になるか、見ていない値を上書きするかのどちらかになるため。
        /// </remarks>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
        private bool _canSave;

        /// <summary>保存が完了したか（View はこれを見てダイアログを閉じる）</summary>
        [ObservableProperty]
        private bool _isSaved;

        /// <summary>
        /// 復旧の対象を受け取り、カードの現在の繰越情報を読んで入力欄を用意する
        /// </summary>
        /// <param name="target">一覧で選んだ行の検出結果</param>
        /// <remarks>
        /// 読み取りの失敗は例外にせず画面に案内して保存できない状態にする。ダイアログを開く経路
        /// （<c>INavigationService.ShowDialogAsync</c> の初期化）で例外を投げると、画面を開けないまま
        /// 一覧へ戻り、職員には何が起きたか分からない。
        /// </remarks>
        public async Task InitializeAsync(CarryoverDataLossItem target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            CardDisplayName = target.CardDisplayName ?? string.Empty;

            LostStartingPageNumberText = target.LostStartingPageNumber.HasValue
                ? target.LostStartingPageNumber.Value.ToString(CultureInfo.CurrentCulture)
                : CarryoverDataLossViewModel.NotLostText;
            LostCarryoverIncomeTotalText = FormatLostAmount(target.LostCarryoverIncomeTotal);
            LostCarryoverExpenseTotalText = FormatLostAmount(target.LostCarryoverExpenseTotal);
            LostCarryoverFiscalYearText = target.LostCarryoverFiscalYear.HasValue
                ? FormatFiscalYear(target.LostCarryoverFiscalYear.Value)
                : CarryoverDataLossViewModel.NotLostText;

            IcCard? card;
            try
            {
                card = await _cardRepository.GetByIdmAsync(target.CardIdm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "繰越情報の復旧画面でカードの読み込みに失敗しました");
                SetError(ExceptionMessageFormatter.ToUserMessage(ex, "カード情報の読み込み"));
                CanSave = false;
                return;
            }

            if (card == null)
            {
                SetError(BuildCardNotFoundMessage(CardDisplayName));
                CanSave = false;
                return;
            }

            var current = CarryoverInfo.From(card);

            CurrentStartingPageNumberText = current.StartingPageNumber.ToString(CultureInfo.CurrentCulture);
            CurrentCarryoverIncomeTotalText = DisplayFormatters.FormatAmountWithUnit(current.CarryoverIncomeTotal);
            CurrentCarryoverExpenseTotalText = DisplayFormatters.FormatAmountWithUnit(current.CarryoverExpenseTotal);
            CurrentCarryoverFiscalYearText = current.CarryoverFiscalYear.HasValue
                ? FormatFiscalYear(current.CarryoverFiscalYear.Value)
                : "（なし）";

            // 初期値: 失われた項目は失われた値、失われていない項目は現在の値。
            // 金額は桁区切りなしで入れる（そのまま編集しやすく、カンマの付け外しで迷わせない）。
            StartingPageNumberText = ToInputText(target.LostStartingPageNumber ?? current.StartingPageNumber);
            CarryoverIncomeTotalText = ToInputText(target.LostCarryoverIncomeTotal ?? current.CarryoverIncomeTotal);
            CarryoverExpenseTotalText = ToInputText(target.LostCarryoverExpenseTotal ?? current.CarryoverExpenseTotal);
            var initialYear = target.LostCarryoverFiscalYear ?? current.CarryoverFiscalYear;
            CarryoverFiscalYearText = initialYear.HasValue ? ToInputText(initialYear.Value) : string.Empty;

            // 一覧を作った後（このダイアログを開く前）に、他のパソコンや別の操作で既に書き戻されていないか。
            // 検知は「現在も既定値のまま」の項目だけを失われた項目として返すので、失われた項目の現在値が
            // 既定値でなければ、一覧が古い。そのまま保存させると、先に書き戻された値（紙の出納簿と突き合わせて
            // 直したかもしれない値）を、操作ログに残っていた古い値で上書きする。
            if (IsAlreadyRecovered(target, current))
            {
                SetError(BuildAlreadyRecoveredMessage(CardDisplayName));
                CanSave = false;
                return;
            }

            _loadedCurrent = current;
            StatusMessage = string.Empty;
            IsStatusError = false;
            CanSave = true;
        }

        /// <summary>
        /// 失われた項目のいずれかが、もう既定値でないか（一覧を作った後に書き戻された）
        /// </summary>
        internal static bool IsAlreadyRecovered(CarryoverDataLossItem target, CarryoverInfo current) =>
            (target.LostStartingPageNumber.HasValue && current.StartingPageNumber != CarryoverInfo.DefaultStartingPageNumber)
            || (target.LostCarryoverIncomeTotal.HasValue && current.CarryoverIncomeTotal != CarryoverInfo.DefaultCarryoverTotal)
            || (target.LostCarryoverExpenseTotal.HasValue && current.CarryoverExpenseTotal != CarryoverInfo.DefaultCarryoverTotal)
            || (target.LostCarryoverFiscalYear.HasValue && current.CarryoverFiscalYear.HasValue);

        /// <summary>
        /// 入力を検証し、職員証の認証を経て繰越情報を書き戻す
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanSave))]
        public async Task SaveAsync()
        {
            // 最初の await より前に確定させる（認証待ちの間に何が変わっても、検証した値と対象で書く。#1761）
            var target = _target;
            var loadedCurrent = _loadedCurrent;
            if (target == null || loadedCurrent == null)
            {
                return;
            }

            var parsed = CarryoverInfoInput.Parse(
                StartingPageNumberText,
                CarryoverIncomeTotalText,
                CarryoverExpenseTotalText,
                CarryoverFiscalYearText,
                target,
                loadedCurrent,
                FiscalYearHelper.GetFiscalYear(_clock.Now));

            if (parsed.Value is null)
            {
                SetError(parsed.ErrorMessage ?? string.Empty);
                if (parsed.ErrorField.HasValue)
                {
                    FocusRequested?.Invoke(this, parsed.ErrorField.Value);
                }

                return;
            }

            var replacement = parsed.Value;
            var cardName = CardDisplayName;

            // 操作名はリテラルで書く。認証を要求する操作の一覧は画面遷移図と突き合わせて静的に検査しており
            // （ScreenTransitionDiagramConsistencyTests）、定数経由だとその検査から漏れる
            var auth = await _staffAuthService.RequestAuthenticationAsync("繰越情報の復旧");
            if (auth == null)
            {
                StatusMessage = AuthenticationCancelledMessage;
                IsStatusError = false;
                return;
            }

            try
            {
                using (BeginBusy("保存中..."))
                {
                    // 監査ログの「変更前」は書き込む直前の DB の値にする（開いたときの値は、貸出状態や備考が
                    // 変わっていれば古い。Issue #1760）。繰越情報が開いたときから変わっていたら競合として止める。
                    var before = await _cardRepository.GetByIdmAsync(target.CardIdm);
                    if (before == null || !CarryoverInfo.From(before).Equals(loadedCurrent))
                    {
                        SetConflict(cardName);
                        return;
                    }

                    var recovered = await _cardManagementService.RecoverCarryoverInfoAsync(before, replacement);
                    if (!recovered)
                    {
                        SetConflict(cardName);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "繰越情報の復旧に失敗しました");
                SetError(ExceptionMessageFormatter.ToUserMessage(ex, "繰越情報の復旧"));
                return;
            }

            IsSaved = true;
        }

        /// <summary>
        /// 保存の直前に、カードが削除されていた・繰越情報が変わっていたときの案内を組み立てる
        /// </summary>
        internal static string BuildConflictMessage(string cardName) =>
            $"{cardName}の繰越情報を復旧できませんでした。この画面を開いた後に、他のパソコンや別の操作でカードが削除されたか、" +
            "繰越情報が変更された可能性があります。この画面を閉じ、一覧で状態を確認してからやり直してください。";

        /// <summary>
        /// 一覧を作った後に、失われた項目が既に書き戻されていたときの案内を組み立てる
        /// </summary>
        internal static string BuildAlreadyRecoveredMessage(string cardName) =>
            $"{cardName}の繰越情報は、一覧を開いた後に他のパソコンや別の操作で既に書き戻された可能性があります。" +
            "この画面を閉じ、一覧で状態を確認してください。";

        /// <summary>
        /// 画面を開いたときにカードが見つからなかったときの案内を組み立てる
        /// </summary>
        internal static string BuildCardNotFoundMessage(string cardName) =>
            $"{cardName}が見つかりません。他のパソコンや別の操作で削除された可能性があります。" +
            "この画面を閉じ、一覧で状態を確認してください。";

        private void SetConflict(string cardName)
        {
            SetError(BuildConflictMessage(cardName));
            CanSave = false;
        }

        private void SetError(string message)
        {
            StatusMessage = message;
            IsStatusError = true;
        }

        private static string FormatLostAmount(int? value) =>
            value.HasValue
                ? DisplayFormatters.FormatAmountWithUnit(value.Value)
                : CarryoverDataLossViewModel.NotLostText;

        // 年度は西暦で示す（入力欄も西暦。一覧の表示と揃える）
        private static string FormatFiscalYear(int fiscalYear) =>
            fiscalYear.ToString(CultureInfo.InvariantCulture) + "年度";

        private static string ToInputText(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
