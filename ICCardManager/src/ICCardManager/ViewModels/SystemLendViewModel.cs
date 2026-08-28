using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;

namespace ICCardManager.ViewModels;

/// <summary>
/// システム操作による貸出記録作成ダイアログの ViewModel（Issue #1909）
/// </summary>
/// <remarks>
/// <para>
/// ピッすいに読み取らせずに交通系ICカードを持ち出したことに後から気付いた場合に、
/// 庶務担当者等が貸出中の状態を作るための画面。物理タッチを伴わないため、
/// 残額はカードから読めず直近の履歴残高で補完される（<c>LendingService.ResolveInitialBalanceAsync</c>）。
/// </para>
/// <para>
/// 借用者は <c>ledger.LenderIdm</c> / <c>staff_name</c> に、操作者（この画面を開く前に
/// 職員証で認証した庶務担当者）は <c>operation_log.operator_idm</c> に、それぞれ別々に残る。
/// 操作者は <see cref="ICurrentOperatorContext"/> 経由で解決されるため（Issue #1265）、
/// ここで操作者を引数として渡すことはしない（渡せる形にすると監査ログのなりすまし経路になる）。
/// </para>
/// <para>
/// 30 秒ルールは武装しない（<c>armRetouchWindow: false</c>）。物理タッチが 1 度も
/// 起きていないため再タッチ窓を開く根拠が無く、武装すると借用者が 30 秒以内に戻って
/// タッチしたときに「返却」ではなく「作成した貸出記録の取り消し」が走ってしまう。
/// </para>
/// </remarks>
public partial class SystemLendViewModel : ViewModelBase
{
    private readonly IStaffRepository _staffRepository;
    private readonly LendingService _lendingService;
    private readonly OperationLogger _operationLogger;
    private readonly ISystemClock _clock;
    private readonly ILogger<SystemLendViewModel> _logger;

    /// <summary>貸出日時の時刻欄が受け付ける書式（24 時間制）</summary>
    private static readonly string[] TimeFormats = { "H:mm", "HH:mm", "H:m", "HH:m" };

    private string _cardIdm = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Staff> _staffList = new();

    [ObservableProperty]
    private Staff _selectedStaff;

    /// <summary>貸出日（時刻は <see cref="LentTimeText"/> と合成する）</summary>
    [ObservableProperty]
    private DateTime _lentDate;

    /// <summary>
    /// 貸出時刻。数値以外の入力を保存時に検出できるよう文字列で保持する
    /// （<c>CompanionCountInputViewModel</c> と同じ方針）
    /// </summary>
    [ObservableProperty]
    private string _lentTimeText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    /// <summary>作成完了フラグ。ダイアログはこれを購読して閉じる</summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>対象カードの表示名（「はやかけん C001」）</summary>
    public string CardDisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// 呼び出し元（カード管理画面）が完了メッセージとして表示する文言。
    /// 作成が確定したときだけ設定される。
    /// </summary>
    public string ResultMessage { get; private set; } = string.Empty;

    public SystemLendViewModel(
        IStaffRepository staffRepository,
        LendingService lendingService,
        OperationLogger operationLogger,
        ISystemClock clock,
        ILogger<SystemLendViewModel> logger)
    {
        _staffRepository = staffRepository ?? throw new ArgumentNullException(nameof(staffRepository));
        _lendingService = lendingService ?? throw new ArgumentNullException(nameof(lendingService));
        _operationLogger = operationLogger ?? throw new ArgumentNullException(nameof(operationLogger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger;
    }

    /// <summary>
    /// 対象カードを指定して初期化する。借用者候補には在籍職員（論理削除されていない職員）だけを並べる。
    /// </summary>
    /// <param name="card">対象カード。呼び出し元が DB から読み直した最新の状態を渡すこと</param>
    public async Task InitializeAsync(IcCard card)
    {
        if (card == null) throw new ArgumentNullException(nameof(card));

        _cardIdm = card.CardIdm;
        CardDisplayName = $"{card.CardType} {card.CardNumber}";
        OnPropertyChanged(nameof(CardDisplayName));

        var now = _clock.Now;
        LentDate = now.Date;
        LentTimeText = now.ToString("HH:mm", CultureInfo.InvariantCulture);

        var staff = await _staffRepository.GetAllAsync();
        StaffList = new ObservableCollection<Staff>(staff ?? Enumerable.Empty<Staff>());
        StatusMessage =
            "カードを持ち出した職員と、持ち出した日時を指定してください。" +
            "残額はカードから読み取れないため、直近の履歴の残額が引き継がれます。";
        IsStatusError = false;
    }

    /// <summary>
    /// 貸出中レコードを作成する。失敗時はダイアログを閉じず、原因と対処を画面に残す。
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        var borrower = SelectedStaff;
        if (borrower == null)
        {
            SetError("カードを持ち出した職員が選択されていません。" +
                     "貸出記録には借用者が必要です。一覧から職員を選択してください。");
            return;
        }

        if (!TryBuildLentAt(out var lentAt))
        {
            SetError($"貸出時刻「{LentTimeText}」を時刻として読み取れません。" +
                     "24時間制で「9:30」「17:05」のように時と分をコロンで区切って入力してください。");
            return;
        }

        using (BeginBusy("貸出記録を作成中..."))
        {
            LendingResult result;
            try
            {
                result = await _lendingService.LendAsync(
                    borrower.StaffIdm, _cardIdm, balance: null, lentAt: lentAt, armRetouchWindow: false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "システム操作による貸出記録の作成に失敗しました（CardIdm={CardIdm}）",
                    IdmMasker.Mask(_cardIdm));
                SetError(ExceptionMessageFormatter.ToUserMessage(ex, "貸出記録の作成"));
                return;
            }

            if (!result.Success)
            {
                SetError(result.ErrorMessage);
                return;
            }

            // Issue #1727 / #1805: ここから先はコミット確定後の後処理。失敗しても作成は取り消さない。
            ResultMessage =
                $"{CardDisplayName} を {borrower.Name} への貸出中として記録しました" +
                $"（貸出日時: {lentAt:yyyy/MM/dd HH:mm}）。";

            try
            {
                await _operationLogger.LogLedgerInsertAsync(result.CreatedLedgers[0]);
            }
            catch (Exception ex)
            {
                // 操作ログが残らなかっただけで、貸出記録そのものは確定している。
                // ここで IsCompleted を落とすと、案内どおりの再操作が「既に貸出中です」に行き着く。
                _logger?.LogError(ex, "貸出記録の操作ログ記録に失敗しました（CardIdm={CardIdm}）",
                    IdmMasker.Mask(_cardIdm));
                ResultMessage += "（操作ログの記録には失敗しました。ログファイルを確認してください。）";
            }

            IsCompleted = true;
        }
    }

    /// <summary>
    /// 貸出日と貸出時刻を合成する。時刻が読み取れない場合は false を返す。
    /// </summary>
    /// <remarks>
    /// <see cref="TimeSpan.TryParse(string, out TimeSpan)"/> は「3」を 3 日、「1.02:03」を
    /// 1 日 2 時間 3 分として受け入れるため使わない。時と分だけを受け付ける
    /// <see cref="DateTime.TryParseExact(string, string[], IFormatProvider, DateTimeStyles, out DateTime)"/>
    /// で定義域を絞る（<c>development-conventions.md</c>「定義域外の入力を黙って別の値に丸めない」）。
    /// </remarks>
    internal bool TryBuildLentAt(out DateTime lentAt)
    {
        lentAt = default;

        var text = (LentTimeText ?? string.Empty).Trim();
        if (!DateTime.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        lentAt = LentDate.Date + parsed.TimeOfDay;
        return true;
    }

    private void SetError(string message)
    {
        StatusMessage = message;
        IsStatusError = true;
    }
}
