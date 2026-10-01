using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
using Microsoft.Extensions.Logging;

namespace ICCardManager.ViewModels;

/// <summary>
/// メイン画面の履歴パネルの ViewModel（Issue #2159 で <see cref="MainViewModel"/> から抽出）。
/// </summary>
/// <remarks>
/// <para>
/// 履歴の表示（期間・ページ送り・繰越行・残高不整合のハイライト）、行の追加・変更・削除、統合と取り消し、
/// 返却確認（#1907）を受け持つ。メイン画面が持つもの（警告エリア・残高ダッシュボード・貸出中一覧・
/// 処理中オーバーレイ）を変えるときは <see cref="IHistoryPanelHost"/> を通して頼む。
/// 境界と連携の設計は <c>docs/superpowers/specs/2026-10-01-issue-2159-history-panel-viewmodel-design.md</c>。
/// </para>
/// <para>
/// <see cref="ViewModelBase"/> ではなく <see cref="ObservableObject"/> を継承する。<see cref="ViewModelBase"/> を
/// 継承すると誰も束縛していない 2 つ目の処理中状態（<c>IsBusy</c>）ができ、そちらの <c>BeginBusy</c> を呼んでも
/// メイン画面のオーバーレイは出ない。処理中は必ず <see cref="IHistoryPanelHost.BeginBusy"/> へ寄せる。
/// </para>
/// </remarks>
public partial class HistoryPanelViewModel : ObservableObject
{
    private readonly ILedgerRepository _ledgerRepository;
    private readonly ICardRepository _cardRepository;
    private readonly DbContext _dbContext;
    private readonly IStaffAuthService _staffAuthService;
    private readonly LedgerMergeService _ledgerMergeService;
    private readonly INavigationService _navigationService;
    private readonly OperationLogger _operationLogger;
    private readonly LedgerConsistencyChecker _ledgerConsistencyChecker;
    private readonly IToastNotificationService _toastNotificationService;
    private readonly ILogger<HistoryPanelViewModel>? _logger;

    private IHistoryPanelHost? _host;

    /// <summary>
    /// Issue #1814: 履歴ページ番号を 1 回の読み込みでクランプできる上限回数。
    /// 数えるのは「クランプした回数」であって再取得回数ではない。
    /// クランプは通常 1 回で収束する（総件数から求めた有効ページで取り直すため）。
    /// 共有モードで他 PC の削除が連続した場合に無限ループさせないための上限であり、
    /// 到達すると 1 ページ目へ戻して取得を確定する（<see cref="LoadHistoryLedgersAsync"/> 参照）。
    /// したがって 1 回の読み込みが発行する <c>GetPagedAsync</c> は最大
    /// <c>MaxHistoryPageClampAttempts + 1</c>（=4）回。
    /// </summary>
    private const int MaxHistoryPageClampAttempts = 3;

    public HistoryPanelViewModel(
        ILedgerRepository ledgerRepository,
        ICardRepository cardRepository,
        DbContext dbContext,
        IStaffAuthService staffAuthService,
        LedgerMergeService ledgerMergeService,
        INavigationService navigationService,
        OperationLogger operationLogger,
        LedgerConsistencyChecker ledgerConsistencyChecker,
        IToastNotificationService toastNotificationService,
        ILogger<HistoryPanelViewModel>? logger = null)
    {
        _ledgerRepository = ledgerRepository;
        _cardRepository = cardRepository;
        _dbContext = dbContext;
        _staffAuthService = staffAuthService;
        _ledgerMergeService = ledgerMergeService;
        _navigationService = navigationService;
        _operationLogger = operationLogger;
        _ledgerConsistencyChecker = ledgerConsistencyChecker;
        _toastNotificationService = toastNotificationService;
        _logger = logger;

        // 履歴表示用の年リストを初期化（今年度から過去6年分）
        var currentYear = DateTime.Today.Year;
        for (int year = currentYear; year >= currentYear - 6; year--)
        {
            HistoryAvailableYears.Add(year);
        }

        // 履歴期間のデフォルト設定（今月）
        var today = DateTime.Today;
        HistoryFromDate = new DateTime(today.Year, today.Month, 1);
        HistoryToDate = today;
        HistorySelectedYear = today.Year;
        HistorySelectedMonth = today.Month;
        UpdateHistoryPeriodDisplay();
    }

    /// <summary>
    /// 履歴パネルを載せる画面を接続する（メイン画面のコンストラクタから 1 度だけ呼ぶ）。
    /// </summary>
    /// <remarks>
    /// DI はメイン画面より先に履歴パネルを生成するため、コンストラクタでは受け取れない。
    /// </remarks>
    internal void AttachHost(IHistoryPanelHost host)
    {
        if (_host != null && !ReferenceEquals(_host, host))
        {
            throw new InvalidOperationException("履歴パネルは既に別の画面へ接続されています。");
        }

        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// 接続された画面。未接続のまま要求したら例外にする（黙って何もしないと、配線漏れが
    /// 「ダッシュボードが古いまま」の形で潜在化する。#1820）。
    /// </summary>
    private IHistoryPanelHost Host => _host
        ?? throw new InvalidOperationException(
            "履歴パネルが画面へ接続されていません（AttachHost を呼んでください）。");

    #region 履歴表示関連プロパティ

    /// <summary>
    /// 履歴表示中のカード
    /// </summary>
    [ObservableProperty]
    private CardDto? _historyCard;

    /// <summary>
    /// 履歴一覧
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<LedgerDto> _historyLedgers = new();

    /// <summary>
    /// 履歴表示中かどうか
    /// </summary>
    [ObservableProperty]
    private bool _isHistoryVisible;

    /// <summary>
    /// Issue #1907: 表示中の履歴が「返却直後に自動表示した返却確認」かどうか（案内バナーの表示条件）
    /// </summary>
    /// <remarks>
    /// 返却確認の履歴は職員の操作で開いたものではないため、次の職員証タッチで自動的に閉じる
    /// （<see cref="CloseReturnHistoryReviewIfUntouched"/>）。ただし職員が履歴パネルを操作した
    /// （<see cref="MarkReturnHistoryReviewTouched"/>）あとは手動で開いたのと同じ扱いにし、閉じない。
    /// 待機中のカードタッチ・警告クリックで開いた履歴（<see cref="ShowHistoryAsync"/>）では false に戻る。
    /// </remarks>
    [ObservableProperty]
    private bool _isReturnHistoryReview;

    /// <summary>
    /// Issue #1907: 返却確認の履歴パネルを職員が操作したか（キー・クリック・ホイール）。
    /// true なら次の職員証タッチでも閉じず、別カードの返却確認にも置き換えない。
    /// </summary>
    private bool _returnHistoryReviewTouched;

    /// <summary>
    /// Issue #1907: 直前の返却で台帳に記録された行の ID。一覧を作り直すたびに
    /// <see cref="LedgerDto.IsRecentlyRecorded"/> を付け直すため、履歴を閉じるまで保持する。
    /// </summary>
    private readonly HashSet<int> _recentlyRecordedLedgerIds = new();

    /// <summary>
    /// Issue #1907: 返却確認バナーの見出し
    /// </summary>
    public string ReturnHistoryReviewMessage => "返却した利用履歴を確認してください";

    /// <summary>
    /// Issue #1907: 返却確認バナーの補足（今回の行の見分け方・直し方・閉じる契機）
    /// </summary>
    public string ReturnHistoryReviewNote =>
        "「今回」列に ✔ の付いた行が今回の返却で記録された利用です。" +
        "バス停名や駅名の入力漏れ・誤りがあれば、行の「変更」から修正できます。" +
        "この表示は次の職員証タッチで自動的に閉じます（履歴を操作した場合は閉じません）。";

    /// <summary>
    /// 残高不整合のあるLedgerIdとその期待残高・実際残高のマップ（Issue #1052）
    /// </summary>
    /// <remarks>
    /// Issue #2007: <c>IsInitialBalanceCorrection</c> は「導入時残高の訂正案として付け替えたマーカー」
    /// であることを表す。このとき期待残高＝直後の記録から逆算した残高、実際残高＝導入行の記録。
    /// 通常の不整合（前行から前方計算した期待値）とは意味が違うため、表示文言はこのフラグで分岐する。
    /// 摘要文字列（「新規購入」等）で分岐すると、導入行の摘要を持つ行に通常経路でマーカーが付いたとき
    /// （CSV 取込や編集で導入行が先頭でなくなった場合等）に、前方計算の値を「逆算した残高」と偽って
    /// 案内してしまう（#1763「同じ判断を配らない」／#1883「食い違った状態を表現できなくする」）。
    /// </remarks>
    private Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)> _balanceInconsistencies = new();

    /// <summary>
    /// 履歴表示中のカードの現在残高
    /// </summary>
    [ObservableProperty]
    private int _historyCurrentBalance;

    /// <summary>
    /// 履歴の表示期間開始日
    /// </summary>
    /// <remarks>
    /// Issue #2030: 表示期間の左右の矢印（前の月／次の月）は開始月を基準に移動先を決めるため、
    /// 開始日が変わるたびに実行可否を再評価する。
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToPreviousMonthCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToNextMonthCommand))]
    private DateTime _historyFromDate;

    /// <summary>
    /// 履歴の表示期間終了日
    /// </summary>
    [ObservableProperty]
    private DateTime _historyToDate;

    /// <summary>
    /// 履歴の選択中期間表示
    /// </summary>
    [ObservableProperty]
    private string _historyPeriodDisplay = string.Empty;

    /// <summary>
    /// 月選択ポップアップを表示中か
    /// </summary>
    [ObservableProperty]
    private bool _isHistoryMonthSelectorOpen;

    /// <summary>
    /// 履歴の選択中の年
    /// </summary>
    [ObservableProperty]
    private int _historySelectedYear;

    /// <summary>
    /// 履歴の選択中の月
    /// </summary>
    [ObservableProperty]
    private int _historySelectedMonth;

    /// <summary>
    /// 履歴の現在ページ
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToFirstPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToPrevPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToNextPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToLastPage))]
    [NotifyPropertyChangedFor(nameof(HistoryPageDisplay))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToFirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToNextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToLastPageCommand))]
    private int _historyCurrentPage = 1;

    /// <summary>
    /// 履歴の総ページ数
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToFirstPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToPrevPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToNextPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToLastPage))]
    [NotifyPropertyChangedFor(nameof(HistoryPageDisplay))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToFirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToNextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToLastPageCommand))]
    private int _historyTotalPages = 1;

    /// <summary>
    /// 履歴の総件数
    /// </summary>
    [ObservableProperty]
    private int _historyTotalCount;

    /// <summary>
    /// 履歴の1ページあたり表示件数
    /// </summary>
    [ObservableProperty]
    private int _historyPageSize = 50;

    /// <summary>
    /// 履歴のステータスメッセージ
    /// </summary>
    [ObservableProperty]
    private string _historyStatusMessage = string.Empty;

    /// <summary>
    /// 履歴ページ表示
    /// </summary>
    public string HistoryPageDisplay => $"{HistoryCurrentPage} / {HistoryTotalPages}";

    /// <summary>
    /// 履歴: 最初のページに移動可能か
    /// </summary>
    public bool HistoryCanGoToFirstPage => HistoryCurrentPage > 1;

    /// <summary>
    /// 履歴: 前のページに移動可能か
    /// </summary>
    public bool HistoryCanGoToPrevPage => HistoryCurrentPage > 1;

    /// <summary>
    /// 履歴: 次のページに移動可能か
    /// </summary>
    public bool HistoryCanGoToNextPage => HistoryCurrentPage < HistoryTotalPages;

    /// <summary>
    /// 履歴: 最後のページに移動可能か
    /// </summary>
    public bool HistoryCanGoToLastPage => HistoryCurrentPage < HistoryTotalPages;

    /// <summary>
    /// 選択可能な年のリスト（過去6年分）
    /// </summary>
    public ObservableCollection<int> HistoryAvailableYears { get; } = new();

    /// <summary>
    /// 月のリスト（1～12）
    /// </summary>
    public ObservableCollection<int> HistoryAvailableMonths { get; } = new()
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12
    };

    #endregion

    /// <summary>
    /// 残高不整合のハイライトを消してから、カードの当月の履歴を開く（Issue #2159）。
    /// </summary>
    /// <remarks>
    /// 待機中のカードタッチ・残高ダッシュボード・残額不足／残額の食い違い警告のクリックから呼ぶ。
    /// 抽出前は 4 か所の呼び出し元がそれぞれ「ハイライトを消す → 開く」を書いていた。
    /// <paramref name="card"/> が null（他 PC で削除された等）ならハイライトを消すだけで開かない（抽出前と同じ）。
    /// </remarks>
    public async Task ShowCardHistoryAsync(IcCard? card)
    {
        _balanceInconsistencies.Clear();
        if (card == null) return;

        await ShowHistoryAsync(card);
    }

    /// <summary>
    /// 履歴表示（メイン画面に表示）
    /// </summary>
    /// <param name="card">表示するカード</param>
    /// <param name="fromDate">
    /// Issue #2007: 表示期間の開始日。省略時は当月 1 日。導入時残高の誤りを案内するときは
    /// 導入行（何年も前になり得る）を画面に出すため、その日付から表示する。
    /// </param>
    /// <param name="recentlyRecordedLedgerIds">
    /// Issue #1907: 直前の返却で記録された行の ID（返却確認の強調対象）。返却確認以外の経路では省略する。
    /// 省略した経路で開いた履歴は返却確認ではない（<see cref="IsReturnHistoryReview"/> は false に戻る）。
    /// </param>
    private async Task ShowHistoryAsync(IcCard card, DateTime? fromDate = null, IEnumerable<int>? recentlyRecordedLedgerIds = null)
    {
        // Issue #1907: どの経路で開いても、前の返却確認の状態（バナー・強調・操作済みの印）は引き継がない
        EndReturnHistoryReview();
        if (recentlyRecordedLedgerIds != null)
        {
            _recentlyRecordedLedgerIds.UnionWith(recentlyRecordedLedgerIds);
        }

        HistoryCard = card.ToDto();
        HistoryCurrentPage = 1;

        // 期間を今月にリセット（fromDate 指定時はその日から今日まで）
        var today = DateTime.Today;
        var defaultFrom = new DateTime(today.Year, today.Month, 1);
        HistoryFromDate = fromDate.HasValue && fromDate.Value.Date <= today
            ? fromDate.Value.Date
            : defaultFrom;
        HistoryToDate = today;
        EnsureHistoryYearAvailable(today.Year);
        HistorySelectedYear = today.Year;
        HistorySelectedMonth = today.Month;
        UpdateHistoryPeriodDisplay();

        // Issue #2030: 矢印の実行可否は「今日」にも依存するが、MVVM Toolkit の RelayCommand は
        // CommandManager.RequerySuggested を購読しないため、開始日が前回と同じ値だと再評価されない
        // （8/31 に開いた履歴を 9/1 に開き直しても ▶ が無効のまま残る）。開くたびに明示的に通知する
        HistoryGoToPreviousMonthCommand.NotifyCanExecuteChanged();
        HistoryGoToNextMonthCommand.NotifyCanExecuteChanged();

        await LoadHistoryLedgersAsync();
        IsHistoryVisible = true;
    }

    /// <summary>
    /// 履歴を閉じる
    /// </summary>
    [RelayCommand]
    public void CloseHistory()
    {
        IsHistoryVisible = false;
        HistoryCard = null;
        HistoryLedgers.Clear();
        _balanceInconsistencies.Clear();
        EndReturnHistoryReview();
    }

    /// <summary>
    /// Issue #1814: 総件数から履歴の総ページ数を求める。
    /// 0 件でも 1 ページ（＝空の 1 ページ目）として扱う。
    /// ループ内の 2 箇所で同じ式を使うため、式の重複を避けて切り出している。
    /// </summary>
    private int CalculateHistoryTotalPages(int totalCount) =>
        Math.Max(1, (int)Math.Ceiling((double)totalCount / HistoryPageSize));

    /// <summary>
    /// 履歴データを読み込み
    /// </summary>
    /// <param name="preserveCheckedRows">
    /// true のとき、再読込の前後で同じ台帳 ID の行のチェック（統合対象の選択）を引き継ぐ。
    /// 利用者の操作を契機としない再読込（共有モードの定期リフレッシュ・手動更新・再接続）でのみ true にする。
    /// </param>
    /// <remarks>
    /// Issue #1814: ページ番号のクランプと再取得を検証するため internal で公開している。
    ///
    /// Issue #1923: 共有モードの定期リフレッシュ（ヘルスチェックと同じ 15 秒周期）が
    /// 履歴一覧を作り直すため、統合対象として入れたチェックが利用者の操作と無関係に消えていた。
    /// チェックは「隣接する 2 行以上」を選ぶ操作で、選び終える前に消えると統合が実行できない。
    /// 利用者が起こした再読込（ページ送り・期間変更・統合や削除の直後）はチェックが無効に
    /// なるのが正しいため、引き継ぎは呼び出し元が明示した経路に限る。
    /// </remarks>
    internal async Task LoadHistoryLedgersAsync(bool preserveCheckedRows = false)
    {
        if (HistoryCard == null) return;

        // Issue #2159: オーバーレイはメイン画面の IsBusy に束縛されているため、ホストのスコープを開く
        using (Host.BeginBusy("読み込み中..."))
        {
            // Issue #1923: 引き継ぐチェックを Clear の前に退避する。
            // 繰越行（Issue #1155）はチェックボックス自体を表示しないため対象外。
            var checkedLedgerIds = preserveCheckedRows
                ? new HashSet<int>(HistoryLedgers
                    .Where(d => d.IsChecked && !d.IsCarryoverRow)
                    .Select(d => d.Id))
                : new HashSet<int>();

            HistoryLedgers.Clear();

            // ページングされた履歴を取得
            //
            // Issue #1814: 総ページ数は取得結果（totalCount）からしか分からないため、
            // ページ番号のクランプは取得の「後」にしかできない。クランプしただけで取り直さないと、
            // 履歴の個別削除（Issue #635）や統合（Issue #1458）で総件数が減った直後に
            // 「一覧は空（削除前のページ番号で問い合わせたため）なのに、件数表示とページ番号は
            // クランプ後の有効値」という食い違いが残る。ページ送りボタンも CanExecute=false で
            // 無効になるため、期間変更か履歴の開き直し以外に復旧手段が無い。
            // → クランプが起きたら取り直す。通常は 1 回で収束する（有効なページ番号で問い合わせ直すため）が、
            //    共有モードでは取り直しの最中にも他 PC の削除で総件数がさらに減り得るため上限を設ける。
            //
            // ループを抜けるときの不変条件:
            //   「一覧（rawLedgers）・件数表示（totalCount）・ページ番号（HistoryCurrentPage）が
            //     すべて同じ 1 回の取得に由来する」
            // これは #1814 の欠陥そのものの否定であり、打ち切り経路でも必ず成立させる。
            // **クランプしてから取り直さずに抜けると、この不変条件が破れる** — 一覧はクランプ前の
            // 無効なページの結果（＝空）で、ページ番号だけがクランプ後になるため、クランプ先が
            // 1 ページ目だとページ送りが全て CanExecute=false になり #1814 の状態に着地する。
            // したがって打ち切り時は 1 ページ目へ落として最後に 1 回だけ取り直す。
            // **1 ページ目は totalCount > 0 なら必ず行を返す（OFFSET 0）**ため、この 1 回で
            // 決定的に整合した状態へ着地でき、以降の再取得は要らない。
            IEnumerable<Ledger> rawLedgers;
            int totalCount;
            var clampCount = 0;
            while (true)
            {
                // 注: 日付はyyyy-MM-dd形式で保存されているため、AddDays(1)は不要
                (rawLedgers, totalCount) = await _ledgerRepository.GetPagedAsync(
                    HistoryCard.CardIdm, HistoryFromDate, HistoryToDate, HistoryCurrentPage, HistoryPageSize);

                // ページ情報を更新
                HistoryTotalCount = totalCount;
                HistoryTotalPages = CalculateHistoryTotalPages(totalCount);

                // 現在のページが総ページ数以内なら、上記の不変条件が成立している
                if (HistoryCurrentPage <= HistoryTotalPages) break;

                if (++clampCount >= MaxHistoryPageClampAttempts)
                {
                    // IDm はログへ生で出さない（IdmMasker を通す）。
                    // 障害調査で必要なのはカードの特定であり、管理番号があれば足りる。
                    _logger?.LogWarning(
                        "履歴ページのクランプが {ClampCount} 回連続で発生したため、1 ページ目へ戻して取得を確定します。" +
                        // 日付は ILogger の書式指定子（CurrentCulture で整形される）ではなく
                        // 整形済みの文字列を渡す。和暦カレンダーが既定の環境で年が和暦になり、
                        // DB に入っている値と突き合わせられなくなるため（Issue #1985）。
                        "カード={CardIdm}（管理番号={CardNumber}） 期間={From}～{To} 総件数={TotalCount}",
                        clampCount, IdmMasker.Mask(HistoryCard.CardIdm), HistoryCard.CardNumber,
                        SqliteDateTimeFormat.ToDateText(HistoryFromDate),
                        SqliteDateTimeFormat.ToDateText(HistoryToDate), totalCount);

                    HistoryCurrentPage = 1;
                    (rawLedgers, totalCount) = await _ledgerRepository.GetPagedAsync(
                        HistoryCard.CardIdm, HistoryFromDate, HistoryToDate, 1, HistoryPageSize);
                    HistoryTotalCount = totalCount;
                    HistoryTotalPages = CalculateHistoryTotalPages(totalCount);
                    break;
                }

                HistoryCurrentPage = HistoryTotalPages;
            }

            // Issue #1740: 表示期間の直前残高をチェーン開始点のシードとして渡す。
            // シードが無いと、同額のポイント還元と利用が同日にある形状（Issue #1004）で
            // 残高チェーンが循環して開始点を特定できず id 順フォールバックへ落ちる。
            // この並びは #1740 以降「自動計算の起点＝DB へ書き戻す残高」の根拠になったため、
            // 表示上の見間違いでは済まなくなった。
            // 2ページ目以降はページ先頭行の直前残高を特定できないため渡さない
            // （誤ったシードは、シード無しより悪い並びを生む）。
            int? precedingBalance = HistoryCurrentPage == 1
                ? await GetPrecedingBalanceAsync(
                    HistoryCard.CardIdm, HistoryFromDate.Year, HistoryFromDate.Month)
                : null;

            // Issue #784: 残高チェーンに基づいて同一日内の時系列順を復元
            var ledgers = Services.LedgerOrderHelper.ReorderByBalanceChain(rawLedgers, precedingBalance);

            // Issue #1155: 1ページ目の先頭に繰越行を挿入（帳票と同じ表示）
            if (HistoryCurrentPage == 1)
            {
                var carryoverDto = BuildCarryoverRow(
                    HistoryCard.CardIdm, HistoryFromDate.Year, HistoryFromDate.Month, precedingBalance);
                if (carryoverDto != null)
                {
                    HistoryLedgers.Add(carryoverDto);
                }
            }

            foreach (var ledger in ledgers)
            {
                var dto = ledger.ToDto();

                // Issue #1907: 直前の返却で記録された行を強調する（一覧を作り直すたびに付け直す）
                dto.IsRecentlyRecorded = _recentlyRecordedLedgerIds.Contains(dto.Id);

                // Issue #1923: 退避したチェックを同じ台帳 ID の行へ戻す。
                // 他 PC が削除・統合した行は再取得結果に現れないため、そのチェックは自然に消える
                // （消えた行を選択対象として残しても統合は競合で失敗する）。
                if (checkedLedgerIds.Contains(dto.Id))
                {
                    dto.IsChecked = true;
                }

                SubscribeLedgerCheckedChanged(dto);
                HistoryLedgers.Add(dto);
            }

            // Issue #1923: 一覧を作り直すと選択の集合が変わり得る（引き継いだ／引き継がなかった／
            // 引き継ぐ対象の行が他 PC の削除・統合で消えた）。にもかかわらず、
            // 　・引き継ぎは SubscribeLedgerCheckedChanged より前に行うため個々の代入では通知されない
            // 　・引き継がない再読込では、古い DTO ごと捨てるので PropertyChanged 自体が起きない
            // ため、ここで通知しないと CanExecute が再評価されない（AsyncRelayCommand は
            // CommandManager の再問い合わせに乗らず、CanExecuteChanged だけがボタンを更新する）。
            // 結果、2 行チェック済みの時点で有効になった「統合」ボタンが、選択が消えた後も
            // 押せるまま残り、押しても MergeHistoryLedgers 冒頭の `checkedDtos.Count < 2` で
            // 無言のまま戻る（何も起きないボタン）。作り直しのたびに 1 回通知する。
            MergeHistoryLedgersCommand.NotifyCanExecuteChanged();

            // 最新の残高を取得
            var latestLedger = await _ledgerRepository.GetLatestBeforeDateAsync(
                HistoryCard.CardIdm, DateTime.Now.AddDays(1));
            HistoryCurrentBalance = latestLedger?.Balance ?? 0;

            // ステータスメッセージを更新
            var startIndex = (HistoryCurrentPage - 1) * HistoryPageSize + 1;
            var endIndex = Math.Min(HistoryCurrentPage * HistoryPageSize, totalCount);
            HistoryStatusMessage = totalCount > 0
                ? $"{startIndex}～{endIndex}件を表示（全{totalCount:N0}件）"
                : "該当する履歴がありません";

            // 統合取り消しボタンの有効/無効を更新
            await RefreshUndoMergeAvailabilityAsync();

            // Issue #1052: 残高不整合ハイライトの適用（ページ遷移時にも再適用される）
            ApplyBalanceInconsistencyMarkers();
        }
    }

    /// <summary>
    /// Issue #1155: 繰越行のDTOを生成する
    /// ReportDataBuilderと同じロジックで、4月は前年度繰越、それ以外は前月繰越を生成
    /// </summary>
    internal async Task<LedgerDto> BuildCarryoverRowAsync(string cardIdm, int year, int month)
    {
        var precedingBalance = await GetPrecedingBalanceAsync(cardIdm, year, month);
        return BuildCarryoverRow(cardIdm, year, month, precedingBalance);
    }

    /// <summary>
    /// 表示期間の直前の残高（＝繰越額）を取得する。null は「それ以前に履歴が無い」を表す。
    /// </summary>
    /// <remarks>
    /// Issue #1740: 残高チェーンの並べ替えシードと繰越行の生成の双方が同じ値を必要とするため、
    /// <see cref="BuildCarryoverRowAsync"/> から切り出した。呼び出し元は 1 回の取得で両方に使う。
    /// </remarks>
    internal async Task<int?> GetPrecedingBalanceAsync(string cardIdm, int year, int month)
    {
        if (month == 4)
        {
            return await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1);
        }

        // 前月末の最新残高を取得
        var firstDayOfMonth = new DateTime(year, month, 1);
        var lastLedger = await _ledgerRepository.GetLatestBeforeDateAsync(cardIdm, firstDayOfMonth);
        return lastLedger?.Balance;
    }

    /// <summary>
    /// Issue #1155: 取得済みの繰越額から繰越行のDTOを生成する（繰越額が無い場合は null）。
    /// </summary>
    internal LedgerDto BuildCarryoverRow(string cardIdm, int year, int month, int? precedingBalance)
    {
        if (!precedingBalance.HasValue)
        {
            return null;
        }

        string summary;
        int income;
        if (month == 4)
        {
            summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary();
            income = precedingBalance.Value;
        }
        else
        {
            int previousMonth = month == 1 ? 12 : month - 1;
            summary = SummaryGenerator.GetCarryoverFromPreviousMonthSummary(previousMonth);
            // 月次繰越の受入欄は空欄（受入金額を表示するのは4月の前年度繰越のみ）
            income = 0;
        }

        return new LedgerDto
        {
            Id = 0,
            CardIdm = cardIdm,
            Date = new DateTime(year, month, 1),
            DateDisplay = WarekiConverter.ToWareki(new DateTime(year, month, 1)),
            Summary = summary,
            Income = income,
            Expense = 0,
            Balance = precedingBalance.Value,
            StaffName = null,
            Note = null,
            IsLentRecord = false,
            IsCarryoverRow = true
        };
    }

    /// <summary>
    /// Issue #1052: 残高不整合のある行にハイライトマーカーを適用
    /// </summary>
    internal void ApplyBalanceInconsistencyMarkers()
    {
        foreach (var dto in HistoryLedgers)
        {
            if (_balanceInconsistencies.TryGetValue(dto.Id, out var info))
            {
                dto.HasBalanceInconsistency = true;
                // Issue #2007: 「導入時残高の誤り」として BuildInconsistencyMarkers が導入行へ付け替えた
                // マーカーは、期待値/実際ではなく、直すべき行と逆算した金額を案内する。
                // 分岐は摘要ではなくマーカー自身のフラグで行う（_balanceInconsistencies の remarks）。
                dto.BalanceInconsistencyMessage = info.IsInitialBalanceCorrection
                    ? InitialBalanceCorrectionMessage.ForHistoryRow(
                        recordedBalance: info.ActualBalance,
                        suggestedBalance: info.ExpectedBalance,
                        appliesToIncome: Ledger.InitialRecordCarriesIncome(dto.Summary))
                    : $"残高不整合: 期待値 {info.ExpectedBalance:N0}円 / 実際 {info.ActualBalance:N0}円";
            }
            else
            {
                dto.HasBalanceInconsistency = false;
                dto.BalanceInconsistencyMessage = string.Empty;
            }
        }
    }

    /// <summary>
    /// 履歴期間表示を更新
    /// </summary>
    private void UpdateHistoryPeriodDisplay()
    {
        HistoryPeriodDisplay = FormatHistoryPeriod(HistoryFromDate, HistoryToDate);
    }

    /// <summary>
    /// 履歴の期間ラベルを組み立てる。
    /// </summary>
    /// <remarks>
    /// 通常の表示期間は暦月（年月ピッカー）なので開始月だけを出す。Issue #2007 の警告クリックは
    /// 導入行の日付から今日までの複数月を表示するため、開始月と終了月が異なるときは範囲で出す
    /// （開始月だけ出すと「その月を表示中」と読まれ、画面に並ぶ数年分の行と食い違う）。
    /// </remarks>
    internal static string FormatHistoryPeriod(DateTime from, DateTime to)
    {
        var fromText = from.ToString("yyyy年M月", CultureInfo.InvariantCulture);
        if (from.Year == to.Year && from.Month == to.Month) return fromText;
        return $"{fromText}～{to.ToString("yyyy年M月", CultureInfo.InvariantCulture)}";
    }

    #region 履歴期間選択コマンド

    /// <summary>
    /// 履歴を今月に設定
    /// </summary>
    [RelayCommand]
    public async Task HistorySetThisMonth()
    {
        var today = DateTime.Today;
        await SetHistoryMonth(today.Year, today.Month);
    }

    /// <summary>
    /// 履歴を先月に設定
    /// </summary>
    [RelayCommand]
    public async Task HistorySetLastMonth()
    {
        var today = DateTime.Today;
        var lastMonth = today.AddMonths(-1);
        await SetHistoryMonth(lastMonth.Year, lastMonth.Month);
    }

    /// <summary>
    /// Issue #2030: 表示期間を 1 か月前へ移動する（表示期間の左の ◀）
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToPreviousMonth))]
    public async Task HistoryGoToPreviousMonth()
    {
        await MoveHistoryMonthAsync(-1);
    }

    /// <summary>
    /// Issue #2030: 表示期間を 1 か月後へ移動する（表示期間の右の ▶）
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToNextMonth))]
    public async Task HistoryGoToNextMonth()
    {
        await MoveHistoryMonthAsync(1);
    }

    /// <summary>
    /// 履歴: 前の月へ移動可能か
    /// </summary>
    public bool HistoryCanGoToPreviousMonth => ResolveAdjacentHistoryMonth(-1).HasValue;

    /// <summary>
    /// 履歴: 次の月へ移動可能か
    /// </summary>
    public bool HistoryCanGoToNextMonth => ResolveAdjacentHistoryMonth(1).HasValue;

    private async Task MoveHistoryMonthAsync(int deltaMonths)
    {
        // CanExecute の評価から実行までに日付が変わり得るため、実行時にも境界を確かめる
        var target = ResolveAdjacentHistoryMonth(deltaMonths);
        if (!target.HasValue) return;

        await SetHistoryMonth(target.Value.Year, target.Value.Month);
    }

    /// <summary>
    /// Issue #2030: 月選択ポップアップの年リストに無い年を補う（降順を保つ）。
    /// </summary>
    /// <remarks>
    /// 年リストは起動時に「今年から 6 年前まで」で作るだけなので、矢印で到達できる年がリストに無いことがある。
    /// ①警告クリック（#2007）で下限より前の年を表示してから ▶ で進んだ場合 ②起動したまま年を越してから ▶ で
    /// 新しい年へ進んだ場合。補わないと <see cref="HistorySelectedYear"/> がリスト外になり、ポップアップの年が空欄になる。
    /// 矢印の導入前は、リスト外の年が <c>SetHistoryMonth</c> へ渡る経路は無かった。
    /// </remarks>
    private void EnsureHistoryYearAvailable(int year)
    {
        if (HistoryAvailableYears.Contains(year)) return;

        var index = 0;
        while (index < HistoryAvailableYears.Count && HistoryAvailableYears[index] > year) index++;
        HistoryAvailableYears.Insert(index, year);
    }

    private DateTime? ResolveAdjacentHistoryMonth(int deltaMonths)
    {
        var oldestYear = HistoryAvailableYears.Count > 0 ? HistoryAvailableYears.Min() : DateTime.Today.Year;
        return GetAdjacentHistoryMonth(HistoryFromDate, deltaMonths, DateTime.Today, oldestYear);
    }

    /// <summary>
    /// Issue #2030: 表示期間の開始月から <paramref name="deltaMonths"/> か月ずらした月の 1 日を返す。
    /// 移動できないときは null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>基準は開始月</b>。警告クリック（#2007）で「2025年4月～2026年9月」のような範囲を表示しているときも、
    /// ラベルの先頭の月から前後へ 1 か月ずつ移動する（範囲の表示は解除され暦月表示に戻る）。
    /// </para>
    /// <para>
    /// <b>前へは月選択ポップアップで選べる最古の年の 1 月まで</b>（ポップアップと矢印で到達できる範囲を揃える。
    /// 台帳の保存期間は 6 年）。<b>次へは今月まで</b> — <c>ledger.date</c> は利用日なので未来の月に行は無い。
    /// 下限は後ろ向きの移動にだけ、上限は前向きの移動にだけ効かせる。警告クリックで下限より前の月を
    /// 表示しているとき、次の月へ進む操作まで塞がないため。
    /// </para>
    /// </remarks>
    internal static DateTime? GetAdjacentHistoryMonth(DateTime from, int deltaMonths, DateTime today, int oldestYear)
    {
        var target = new DateTime(from.Year, from.Month, 1).AddMonths(deltaMonths);

        if (deltaMonths < 0 && target < new DateTime(oldestYear, 1, 1)) return null;
        if (deltaMonths > 0 && target > new DateTime(today.Year, today.Month, 1)) return null;

        return target;
    }

    /// <summary>
    /// 月選択ポップアップを開く
    /// </summary>
    [RelayCommand]
    public void HistoryOpenMonthSelector()
    {
        IsHistoryMonthSelectorOpen = true;
    }

    /// <summary>
    /// 月選択ポップアップを閉じる
    /// </summary>
    [RelayCommand]
    public void HistoryCloseMonthSelector()
    {
        IsHistoryMonthSelectorOpen = false;
    }

    /// <summary>
    /// 選択した月を適用
    /// </summary>
    [RelayCommand]
    public async Task HistoryApplySelectedMonth()
    {
        await SetHistoryMonth(HistorySelectedYear, HistorySelectedMonth);
        IsHistoryMonthSelectorOpen = false;
    }

    /// <summary>
    /// 指定した年月に履歴期間を設定
    /// </summary>
    private async Task SetHistoryMonth(int year, int month)
    {
        HistoryFromDate = new DateTime(year, month, 1);
        HistoryToDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        EnsureHistoryYearAvailable(year);
        HistorySelectedYear = year;
        HistorySelectedMonth = month;
        HistoryCurrentPage = 1;
        _balanceInconsistencies.Clear(); // Issue #1052: 期間変更時にハイライトをクリア
        UpdateHistoryPeriodDisplay();
        await LoadHistoryLedgersAsync();
    }

    #endregion

    #region 履歴ページナビゲーションコマンド

    /// <summary>
    /// 履歴: 最初のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToFirstPage))]
    public async Task HistoryGoToFirstPage()
    {
        HistoryCurrentPage = 1;
        await LoadHistoryLedgersAsync();
    }

    /// <summary>
    /// 履歴: 前のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToPrevPage))]
    public async Task HistoryGoToPrevPage()
    {
        if (HistoryCurrentPage > 1)
        {
            HistoryCurrentPage--;
            await LoadHistoryLedgersAsync();
        }
    }

    /// <summary>
    /// 履歴: 次のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToNextPage))]
    public async Task HistoryGoToNextPage()
    {
        if (HistoryCurrentPage < HistoryTotalPages)
        {
            HistoryCurrentPage++;
            await LoadHistoryLedgersAsync();
        }
    }

    /// <summary>
    /// 履歴: 最後のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToLastPage))]
    public async Task HistoryGoToLastPage()
    {
        HistoryCurrentPage = HistoryTotalPages;
        await LoadHistoryLedgersAsync();
    }

    #endregion

    #region 履歴詳細・変更コマンド

    /// <summary>
    /// 履歴詳細を表示
    /// </summary>
    [RelayCommand]
    public async Task ShowLedgerDetail(LedgerDto ledger)
    {
        if (ledger == null || !ledger.HasDetails) return;

        // 詳細データを取得
        var ledgerWithDetails = await _ledgerRepository.GetByIdAsync(ledger.Id);
        if (ledgerWithDetails == null) return;

        var detailDto = ledgerWithDetails.ToDto();

        // 詳細ダイアログを表示
        var cardName = HistoryCard?.DisplayName;
        Views.Dialogs.LedgerDetailDialog capturedDialog = null;
        await _navigationService.ShowDialogAsync<Views.Dialogs.LedgerDetailDialog>(async d =>
        {
            await d.InitializeAsync(detailDto.Id, cardName: cardName);
            capturedDialog = d;
        });

        // Issue #548: 保存が行われた場合は履歴を再読み込み
        if (capturedDialog?.WasSaved == true)
        {
            await LoadHistoryLedgersAsync();
            // Issue #660: 分割等で摘要が変わった場合に警告を更新
            await Host.CheckWarningsAsync();
            // Issue #1739: 明細の金額編集は残高チェーンを変えるため、整合性も再判定する。
            // 他の履歴編集経路（行の追加・編集・削除）は既にこの組で呼んでいたが、本経路だけ
            // 抜けており、不整合を直しても古い件数の警告が残っていた。
            await CheckAndNotifyConsistencyAsync();
        }
    }

    #endregion
}
