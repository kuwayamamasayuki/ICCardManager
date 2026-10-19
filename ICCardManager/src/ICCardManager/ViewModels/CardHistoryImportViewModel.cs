#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ICCardManager.Common;
using ICCardManager.Common.Messages;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// カードの履歴からの取り込みダイアログの段階（Issue #2293）
    /// </summary>
    public enum CardHistoryImportPhase
    {
        /// <summary>交通系ICカードのタッチを待っている</summary>
        WaitingForTouch,

        /// <summary>カードを読み、取り込む利用を決めている</summary>
        Reading,

        /// <summary>取り込む利用（または取り込めない理由）を表示している</summary>
        Ready,

        /// <summary>取り込みが確定した</summary>
        Completed,
    }

    /// <summary>
    /// カードの履歴からの取り込みダイアログを閉じたときの結果（Issue #2293。呼び出し元の後処理が使う）
    /// </summary>
    public sealed class CardHistoryImportOutcome
    {
        public CardHistoryImportOutcome(
            bool hasImported, IReadOnlyList<Ledger> recordedLedgers, int? actualBalance, bool completed = false,
            bool cardNoLongerEligible = false)
        {
            HasImported = hasImported;
            Completed = completed || hasImported;
            CardNoLongerEligible = cardNoLongerEligible;
            RecordedLedgers = recordedLedgers ?? throw new ArgumentNullException(nameof(recordedLedgers));
            ActualBalance = actualBalance;
        }

        /// <summary>台帳に行を記録したか</summary>
        public bool HasImported { get; }

        /// <summary>
        /// 取り込みを実行し終えたか（記録した行が無かった場合も含む。一覧を見ている間に他の操作で記録されていたとき）
        /// </summary>
        public bool Completed { get; }

        /// <summary>
        /// カードが取り込みの対象でなくなっていたか（貸出中・削除・払い戻し）。呼び出し元は取り込みの案内を取り下げる
        /// （貸出中なら返却が利用履歴を記録する。押しても同じ理由で断られる）
        /// </summary>
        public bool CardNoLongerEligible { get; }

        /// <summary>記録した台帳行（新しく作った行と、同じ日の既存の行へ明細を足した行）</summary>
        public IReadOnlyList<Ledger> RecordedLedgers { get; }

        /// <summary>タッチで読み取ったカードの実残額（読み取りまで進まなかったら null）</summary>
        public int? ActualBalance { get; }
    }

    /// <summary>
    /// カードの履歴からの取り込みダイアログの ViewModel（Issue #2293）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 残額の食い違い警告（貸出中でないカード）から開いた履歴の「カードの履歴から取り込む...」で開く。
    /// 交通系ICカードのタッチを待ち、カードに残る利用履歴のうち台帳に未記録の利用を一覧で見せ、
    /// 職員証の認証のうえで記録する。取り込む範囲と台帳行の作り方は返却と同じ規則
    /// （<see cref="LendingService.PlanUsageImportAsync"/> / <see cref="LendingService.ImportUnrecordedUsageAsync"/>）。
    /// </para>
    /// <para>
    /// メイン画面のカード読み取りは、ダイアログの表示範囲（<see cref="Start"/> から <see cref="Cleanup"/> まで）で抑制する
    /// （<see cref="CardReadingSource.CardHistoryImport"/>）。タッチを待つ段階以外のタッチ（認証ダイアログでの職員証など）は無視する。
    /// </para>
    /// </remarks>
    public partial class CardHistoryImportViewModel : ViewModelBase
    {
        /// <summary>タッチを待つときの案内</summary>
        internal const string TouchPromptMessage =
            "取り込む交通系ICカードをカードリーダーにタッチし、読み取りが終わるまで載せたままにしてください。";

        /// <summary>認証が完了しなかったときの案内</summary>
        internal const string AuthenticationCancelledMessage =
            "職員証の認証が完了しなかったため、取り込んでいません。取り込むには、もう一度「取り込む」を押して職員証をタッチしてください。";

        private readonly ICardReader? _cardReader;
        private readonly LendingService _lendingService;
        private readonly IStaffAuthService _staffAuthService;
        private readonly IDispatcherService _dispatcherService;
        private readonly IMessenger _messenger;
        private readonly ILogger<CardHistoryImportViewModel> _logger;

        /// <summary>取り込む対象のカードの IDm</summary>
        private string _targetCardIdm = string.Empty;

        /// <summary>タッチで読んだカードの利用履歴（取り込みの確定で、この履歴から計画を立て直す）</summary>
        private IReadOnlyList<LedgerDetail>? _readHistory;

        /// <summary>読み取りの処理中（2 件目のタッチで再入しない）</summary>
        private bool _isHandlingCardRead;

        private bool _isStarted;
        private bool _isCleanedUp;

        public CardHistoryImportViewModel(
            ICardReader? cardReader,
            LendingService lendingService,
            IStaffAuthService staffAuthService,
            IDispatcherService dispatcherService,
            IMessenger messenger,
            ILogger<CardHistoryImportViewModel> logger)
        {
            _cardReader = cardReader;
            _lendingService = lendingService ?? throw new ArgumentNullException(nameof(lendingService));
            _staffAuthService = staffAuthService ?? throw new ArgumentNullException(nameof(staffAuthService));
            _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
            _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>対象カードの表示名（例: "はやかけん H-001"）</summary>
        [ObservableProperty]
        private string _cardDisplayName = string.Empty;

        /// <summary>現在の段階</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWaitingForTouch))]
        [NotifyPropertyChangedFor(nameof(IsCompleted))]
        [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
        private CardHistoryImportPhase _phase = CardHistoryImportPhase.WaitingForTouch;

        /// <summary>案内・結果・誤りの表示</summary>
        [ObservableProperty]
        private string _statusMessage = string.Empty;

        /// <summary><see cref="StatusMessage"/> が誤りの案内か</summary>
        [ObservableProperty]
        private bool _isStatusError;

        /// <summary>カードから読み取った残額の表示（読む前は「-」）</summary>
        [ObservableProperty]
        private string _cardBalanceText = "-";

        /// <summary>取り込むと台帳に記録される利用の件数の表示</summary>
        [ObservableProperty]
        private string _rowCountText = string.Empty;

        /// <summary>取り込む利用（カードから読んだ順を古い順に並べ直したもの）</summary>
        public ObservableCollection<LedgerDetailDto> Rows { get; } = new();

        /// <summary>タッチを待っているか（案内とデバッグ用の仮想タッチの表示条件）</summary>
        public bool IsWaitingForTouch => Phase == CardHistoryImportPhase.WaitingForTouch;

        /// <summary>取り込みが確定したか（ボタンの表示を「閉じる」へ切り替える）</summary>
        public bool IsCompleted => Phase == CardHistoryImportPhase.Completed;

        /// <summary>取り込む対象のカードの IDm（デバッグ用の仮想タッチが使う）</summary>
        public string TargetCardIdm => _targetCardIdm;

        /// <summary>タッチで読み取ったカードの実残額（読む前・読めなかったときは null）</summary>
        public int? ActualBalance { get; private set; }

        /// <summary>確定した取り込みの結果（確定する前は null）</summary>
        public UsageImportResult? ImportResult { get; private set; }

        /// <summary>台帳に行を記録したか（呼び出し元が履歴の再読み込みとバス停名入力へ進む条件）</summary>
        public bool HasImported => ImportResult?.Success == true && ImportResult.CreatedLedgers.Count > 0;

        /// <summary>閉じたときの結果（呼び出し元の後処理へ渡す）</summary>
        public CardHistoryImportOutcome Outcome => new(
            HasImported,
            (IReadOnlyList<Ledger>?)ImportResult?.CreatedLedgers ?? Array.Empty<Ledger>(),
            ActualBalance,
            completed: ImportResult?.Success == true,
            cardNoLongerEligible: _lastBlockReason is UsageImportBlockReason.CardIsLent or UsageImportBlockReason.CardNotInOperation);

        /// <summary>計画・確定で最後に断られた理由（断られていなければ None）</summary>
        private UsageImportBlockReason _lastBlockReason = UsageImportBlockReason.None;

        /// <summary>
        /// 取り込む対象のカードを受け取る（タッチの待ち受けは <see cref="Start"/> で始める）
        /// </summary>
        public void SetTarget(string cardIdm, string cardDisplayName)
        {
            if (string.IsNullOrEmpty(cardIdm))
            {
                throw new ArgumentException("取り込む交通系ICカードの IDm が空です。", nameof(cardIdm));
            }

            _targetCardIdm = cardIdm;
            CardDisplayName = cardDisplayName ?? string.Empty;
        }

        /// <summary>
        /// タッチの待ち受けを始め、メイン画面のカード読み取りを抑制する（ダイアログの表示時に呼ぶ）
        /// </summary>
        public void Start()
        {
            if (_isStarted || _isCleanedUp)
            {
                return;
            }

            _isStarted = true;
            _messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.CardHistoryImport));
            if (_cardReader != null)
            {
                _cardReader.CardRead += OnCardRead;
            }

            SetInfo(TouchPromptMessage);
        }

        /// <summary>
        /// 待ち受けを終え、抑制を解く（ダイアログを閉じたときに呼ぶ）
        /// </summary>
        public void Cleanup()
        {
            if (_isCleanedUp)
            {
                return;
            }

            _isCleanedUp = true;
            if (!_isStarted)
            {
                return;
            }

            if (_cardReader != null)
            {
                _cardReader.CardRead -= OnCardRead;
            }

            _messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.CardHistoryImport));
        }

        private void OnCardRead(object? sender, CardReadEventArgs e)
        {
            if (!IsWaitingForTouch)
            {
                return;
            }

            _dispatcherService.InvokeAsync(() => HandleCardReadAsync(e.Idm));
        }

        /// <summary>
        /// タッチされたカードを照合し、利用履歴と残額を読んで取り込む利用を決める
        /// </summary>
        /// <remarks>
        /// 本体全体を try/catch で包む（Issue #1816。ディスパッチ先で抜けた例外は観測されにくい）。
        /// 失敗したらタッチ待ちへ戻し、もう一度タッチすればやり直せるようにする。
        /// </remarks>
        internal async Task HandleCardReadAsync(string idm)
        {
            if (_cardReader == null || _isCleanedUp || _isHandlingCardRead || !IsWaitingForTouch)
            {
                return;
            }

            _isHandlingCardRead = true;
            try
            {
                if (!string.Equals(idm, _targetCardIdm, StringComparison.OrdinalIgnoreCase))
                {
                    SetError(BuildWrongCardMessage(CardDisplayName));
                    return;
                }

                Phase = CardHistoryImportPhase.Reading;

                int? balance;
                CardReadResult<IReadOnlyList<LedgerDetail>> history;
                UsageImportPlan plan;
                using (BeginBusy("交通系ICカードを読み取っています..."))
                {
                    balance = await ReadBalanceOrNullAsync(_cardReader, idm);
                    history = await _cardReader.TryReadHistoryAsync(idm);

                    if (!history.Success || balance == null)
                    {
                        Phase = CardHistoryImportPhase.WaitingForTouch;
                        SetError(BuildReadFailedMessage());
                        return;
                    }

                    plan = await _lendingService.PlanUsageImportAsync(_targetCardIdm, history.Value);
                }

                if (_isCleanedUp)
                {
                    return;
                }

                ActualBalance = balance;
                CardBalanceText = DisplayFormatters.FormatBalanceWithUnit(balance.Value);
                _readHistory = history.Value;
                ShowPlan(plan);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "カードの履歴からの取り込み: 交通系ICカードの読み取りに失敗しました");
                if (!_isCleanedUp)
                {
                    Phase = CardHistoryImportPhase.WaitingForTouch;
                    SetError(ExceptionMessageFormatter.ToUserMessage(ex, "交通系ICカードの読み取り") +
                             "復旧したら、もう一度カードをタッチしてください。");
                }
            }
            finally
            {
                _isHandlingCardRead = false;
            }
        }

        private async Task<int?> ReadBalanceOrNullAsync(ICardReader cardReader, string idm)
        {
            try
            {
                return await cardReader.ReadBalanceAsync(idm);
            }
            catch (Exception ex)
            {
                // カードを早く離したなどの読み取りの失敗は、もう一度タッチすれば直る（案内は呼び出し元が出す）
                _logger.LogWarning(ex, "カードの履歴からの取り込み: 残額を読み取れませんでした");
                return null;
            }
        }

        private void ShowPlan(UsageImportPlan plan)
        {
            Rows.Clear();
            Phase = CardHistoryImportPhase.Ready;

            if (plan.BlockReason != UsageImportBlockReason.None)
            {
                _lastBlockReason = plan.BlockReason;
                RowCountText = string.Empty;
                SetError(BuildBlockedMessage(plan.BlockReason, CardDisplayName));
                return;
            }

            // カードからは新しい順に読まれる。一覧は台帳と同じく古い順に並べる
            foreach (var detail in plan.DetailsToRecord.Reverse())
            {
                Rows.Add(detail.ToDto());
            }

            RowCountText = $"{Rows.Count}件";
            ImportCommand.NotifyCanExecuteChanged();

            if (Rows.Count == 0)
            {
                SetInfo(BuildNothingToImportMessage(plan.LowerBound));
                return;
            }

            SetInfo($"台帳に記録されていない利用が {Rows.Count} 件あります。内容を確かめてから「取り込む」を押してください。" +
                    "職員証の認証が必要です。");
        }

        private bool CanImport() =>
            Phase == CardHistoryImportPhase.Ready && Rows.Count > 0 && _readHistory != null;

        /// <summary>
        /// 職員証の認証を経て、一覧の利用を台帳へ記録する
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanImport))]
        public async Task ImportAsync()
        {
            // 最初の await より前に確定させる（認証待ちの間に何が変わっても、表示した履歴で記録する。#1761）
            var history = _readHistory;
            if (history == null)
            {
                return;
            }

            // 操作名はリテラルで書く（認証を要求する操作の一覧は画面遷移図と突き合わせて静的に検査している）
            using var auth = (await _staffAuthService.RequestAuthenticationAsync("カード履歴の取り込み"))?.Enter();
            if (auth == null)
            {
                SetInfo(AuthenticationCancelledMessage);
                return;
            }

            UsageImportResult result;
            try
            {
                using (BeginBusy("取り込み中..."))
                {
                    result = await _lendingService.ImportUnrecordedUsageAsync(_targetCardIdm, history);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "カードの履歴からの取り込みに失敗しました");
                SetError(ExceptionMessageFormatter.ToUserMessage(ex, "カードの履歴からの取り込み"));
                return;
            }

            if (!result.Success)
            {
                if (result.BlockReason != UsageImportBlockReason.None)
                {
                    // 一覧を出した後に貸出・削除された等。押し直しても同じ理由で断られるので、取り込めない状態にする
                    // （書き込みの失敗は一過性のことがあるので、押し直せるまま残す）
                    _readHistory = null;
                    _lastBlockReason = result.BlockReason;
                    ImportCommand.NotifyCanExecuteChanged();
                    SetError(BuildBlockedMessage(result.BlockReason, CardDisplayName));
                }
                else
                {
                    SetError(BuildImportFailedMessage(result.ErrorMessage));
                }

                return;
            }

            ImportResult = result;
            Phase = CardHistoryImportPhase.Completed;
            OnPropertyChanged(nameof(HasImported));
            SetInfo(BuildCompletionMessage(
                result.CreatedLedgers.Count, result.RecordedBalanceAfter, ActualBalance,
                recordedElsewhereCount: Math.Max(0, Rows.Count - result.RecordedUsageCount)));
        }

        /// <summary>タッチされたカードが対象と違うときの案内</summary>
        internal static string BuildWrongCardMessage(string cardName) =>
            $"タッチされたカードは{cardName}ではありません。履歴を取り込むのは{cardName}です。{cardName}をタッチしてください。";

        /// <summary>カードの残額・履歴を読めなかったときの案内</summary>
        internal static string BuildReadFailedMessage() =>
            "交通系ICカードの残額または利用履歴を読み取れませんでした。読み取りの途中でカードが離れた可能性があります。" +
            "カードをカードリーダーに載せ直し、読み取りが終わるまでそのままにしてください。";

        /// <summary>台帳に未記録の利用が無かったときの案内</summary>
        /// <param name="lowerBound">記録の下限（この日付以降を記録する。<see cref="UsageImportPlan.LowerBound"/>）</param>
        /// <remarks>
        /// 下限は導入行の日付とは限らない（導入行が無ければ台帳の最新行の日付、明細を持たない行があればその翌日）。
        /// 言い切らずに、実際の下限の日付を示す（それより前の未記録の利用は取り込まないため）。
        /// </remarks>
        internal static string BuildNothingToImportMessage(DateTime? lowerBound) =>
            (lowerBound.HasValue
                ? $"カードに残る利用履歴のうち、{DisplayFormatters.FormatDate(lowerBound.Value)} 以降の利用は台帳に記録済みです。"
                : "カードに残る利用履歴に、台帳へ記録していない利用は見つかりませんでした。") +
            "残額の食い違いが残る場合は、それより前の利用やカードに残っていない古い利用が記録されていない可能性があります。" +
            "履歴を確認し、CSVインポートまたは履歴の追加で補ってください。";

        /// <summary>取り込めない理由の案内</summary>
        internal static string BuildBlockedMessage(UsageImportBlockReason reason, string cardName) => reason switch
        {
            UsageImportBlockReason.CardIsLent =>
                $"{cardName}は貸出中のため、取り込めません。返却処理がカードの利用履歴を記録します。" +
                "この画面を閉じ、職員証と交通系ICカードをタッチして返却してください。",
            UsageImportBlockReason.NoBaseline =>
                $"{cardName}には台帳の記録が 1 行も無いため、どこまでが記録済みかを決められず取り込めません。" +
                "この画面を閉じ、履歴の「＋ 行を追加」でカードを使い始めたときの残額の行（「新規購入」など）を入力してから、もう一度取り込んでください。",
            _ =>
                $"{cardName}が見つからないか、削除・払い戻し済みのため取り込めません。" +
                "この画面を閉じ、交通系ICカード管理画面でカードの状態を確認してください。",
        };

        /// <summary>書き込みに失敗したときの案内（<paramref name="reason"/> はサービスが返す「なぜ」）</summary>
        internal static string BuildImportFailedMessage(string? reason) =>
            "カードの履歴を取り込めませんでした。" + (reason ?? string.Empty) +
            "台帳には何も記録していません。しばらく待ってから、もう一度「取り込む」を押してください。";

        /// <summary>
        /// 取り込みの後の案内を組み立てる（残額が一致したか・残る差額）
        /// </summary>
        /// <param name="importedCount">記録した台帳行の数</param>
        /// <param name="recordedBalanceAfter">取り込みの後の台帳の最新残額（読めなかったら null）</param>
        /// <param name="actualBalance">カードの実残額（読めなかったら null）</param>
        /// <param name="recordedElsewhereCount">一覧に出した利用のうち、この画面を開いている間に他の操作で記録されていた数</param>
        internal static string BuildCompletionMessage(int importedCount, int? recordedBalanceAfter, int? actualBalance, int recordedElsewhereCount = 0)
        {
            // チャージ・ポイント還元の行は利用者が空欄のままでよいので、利用の行に限って入力を促す
            var head = importedCount > 0
                ? $"{importedCount}件の行を台帳に記録しました。" +
                  (recordedElsewhereCount > 0
                      ? $"一覧のうち {recordedElsewhereCount} 件の利用は、この画面を開いている間に他の操作で記録されていました。"
                      : string.Empty) +
                  "利用の行の利用者は空欄です。分かる場合は履歴の各行の「変更」で入力してください。"
                : "一覧の利用は、この画面を開いている間に他の操作（返却や他のパソコンの取り込み）で記録されていたため、新たに記録した行はありません。";

            if (recordedBalanceAfter == null || actualBalance == null)
            {
                return head + "記録の残額を確認できませんでした。履歴で残額を確認してください。";
            }

            if (!WarningService.IsCardBalanceMismatched(actualBalance.Value, recordedBalanceAfter.Value))
            {
                return head +
                       $"記録の残額 {DisplayFormatters.FormatBalanceWithUnit(recordedBalanceAfter.Value)} は、カードの残額と一致しました。";
            }

            var difference = Math.Abs(actualBalance.Value - recordedBalanceAfter.Value);
            return head +
                   $"記録の残額 {DisplayFormatters.FormatBalanceWithUnit(recordedBalanceAfter.Value)} と" +
                   $"カードの残額 {DisplayFormatters.FormatBalanceWithUnit(actualBalance.Value)} には、" +
                   $"まだ {DisplayFormatters.FormatBalanceWithUnit(difference)} の差があります。" +
                   "カードに残っていない古い利用（直近 20 件より前）などが記録されていない可能性があります。" +
                   "履歴を確認し、CSVインポートまたは履歴の追加で補ってください。";
        }

        private void SetInfo(string message)
        {
            StatusMessage = message;
            IsStatusError = false;
        }

        private void SetError(string message)
        {
            StatusMessage = message;
            IsStatusError = true;
        }
    }
}
