using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services;

/// <summary>
/// 貸出・返却処理結果
/// </summary>
public class LendingResult
{
    /// <summary>
    /// 成功したかどうか
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 処理種別（Lend: 貸出, Return: 返却）
    /// </summary>
    public LendingOperationType OperationType { get; set; }

    /// <summary>
    /// エラーメッセージ
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 残額
    /// </summary>
    public int Balance { get; set; }

    /// <summary>
    /// 残額が警告閾値未満かどうか
    /// </summary>
    public bool IsLowBalance { get; set; }

    /// <summary>
    /// バス利用があったかどうか（返却時のみ）
    /// </summary>
    public bool HasBusUsage { get; set; }

    /// <summary>
    /// 作成された履歴レコード
    /// </summary>
    public List<Ledger> CreatedLedgers { get; set; } = new();
}

/// <summary>
/// 貸出・返却の処理種別
/// </summary>
public enum LendingOperationType
{
    /// <summary>
    /// 貸出
    /// </summary>
    Lend,

    /// <summary>
    /// 返却
    /// </summary>
    Return
}

/// <summary>
/// ICカードの貸出・返却処理を行うサービスです。
/// </summary>
/// <remarks>
/// <para>
/// このサービスは以下の機能を提供します：
/// </para>
/// <list type="bullet">
/// <item><description>ICカードの貸出処理（<see cref="LendAsync"/>）</description></item>
/// <item><description>ICカードの返却処理と利用履歴の記録（<see cref="ReturnAsync"/>）</description></item>
/// <item><description>30秒ルールによる誤操作修正（<see cref="IsRetouchWithinTimeout"/>）</description></item>
/// </list>
/// <para>
/// <strong>30秒ルール:</strong>
/// 同一カードが30秒以内に再度タッチされた場合、直前の処理と逆の処理が実行されます。
/// これにより、誤って貸出/返却した場合に即座に取り消すことができます。
/// </para>
/// <para>
/// <strong>排他制御:</strong>
/// 同一カードへの同時アクセスは <see cref="CardLockManager"/> により排他制御されます。
/// ロック取得のタイムアウトは5秒で、タイムアウト時は処理が拒否されます。
/// </para>
/// </remarks>
public class LendingService
{
    private readonly DbContext _dbContext;
    private readonly ICardRepository _cardRepository;
    private readonly IStaffRepository _staffRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly CardLockManager _lockManager;

    /// <summary>
    /// 最後に処理したカードのIDm
    /// </summary>
    public string? LastProcessedCardIdm { get; private set; }

    /// <summary>
    /// 最後に処理した時刻
    /// </summary>
    public DateTime? LastProcessedTime { get; private set; }

    /// <summary>
    /// 最後の処理種別
    /// </summary>
    public LendingOperationType? LastOperationType { get; private set; }

    /// <summary>
    /// 30秒ルール適用の時間（秒）
    /// </summary>
    private const int RetouchTimeoutSeconds = 30;

    /// <summary>
    /// ロック取得のタイムアウト（ミリ秒）
    /// </summary>
    private const int LockTimeoutMs = 5000;

    public LendingService(
        DbContext dbContext,
        ICardRepository cardRepository,
        IStaffRepository staffRepository,
        ILedgerRepository ledgerRepository,
        ISettingsRepository settingsRepository,
        SummaryGenerator summaryGenerator,
        CardLockManager lockManager)
    {
        _dbContext = dbContext;
        _cardRepository = cardRepository;
        _staffRepository = staffRepository;
        _ledgerRepository = ledgerRepository;
        _settingsRepository = settingsRepository;
        _summaryGenerator = summaryGenerator;
        _lockManager = lockManager;
    }

    /// <summary>
    /// ICカードの貸出処理を実行します。
    /// </summary>
    /// <param name="staffIdm">貸出者の職員証IDm（16桁の16進数文字列）</param>
    /// <param name="cardIdm">貸出対象のICカードIDm（16桁の16進数文字列）</param>
    /// <returns>貸出結果。成功時は <see cref="LendingResult.Success"/> が true</returns>
    /// <remarks>
    /// <para>処理フロー：</para>
    /// <list type="number">
    /// <item><description>カードごとの排他ロックを取得（タイムアウト: 5秒）</description></item>
    /// <item><description>カードと職員の存在確認</description></item>
    /// <item><description>貸出中でないことを確認</description></item>
    /// <item><description>トランザクション内で貸出レコード作成とカード状態更新</description></item>
    /// <item><description>30秒ルール用の処理情報を記録</description></item>
    /// </list>
    /// <para>
    /// エラー時は <see cref="LendingResult.ErrorMessage"/> にエラー内容が設定されます。
    /// </para>
    /// </remarks>
    public async Task<LendingResult> LendAsync(string staffIdm, string cardIdm)
    {
        var result = new LendingResult { OperationType = LendingOperationType.Lend };

        // カードごとのロックを取得
        var cardLock = _lockManager.GetLock(cardIdm);
        var lockAcquired = false;

        try
        {
            // タイムアウト付きでロックを取得
            lockAcquired = await cardLock.WaitAsync(GetLockTimeoutMs());
            if (!lockAcquired)
            {
                result.ErrorMessage = "他の処理が実行中です。しばらく待ってから再度お試しください。";
                return result;
            }

            // カードを取得
            var card = await _cardRepository.GetByIdmAsync(cardIdm);
            if (card == null)
            {
                result.ErrorMessage = "カードが登録されていません。";
                return result;
            }

            // 貸出中チェック
            if (card.IsLent)
            {
                result.ErrorMessage = "このカードは既に貸出中です。";
                return result;
            }

            // 職員を取得
            var staff = await _staffRepository.GetByIdmAsync(staffIdm);
            if (staff == null)
            {
                result.ErrorMessage = "職員証が登録されていません。";
                return result;
            }

            var now = DateTime.Now;

            // トランザクション開始
            using var transaction = _dbContext.BeginTransaction();

            try
            {
                // 貸出レコードを作成
                var ledger = new Ledger
                {
                    CardIdm = cardIdm,
                    LenderIdm = staffIdm,
                    Date = now.Date,
                    Summary = SummaryGenerator.GetLendingSummary(),
                    Income = 0,
                    Expense = 0,
                    Balance = 0,
                    StaffName = staff.Name,
                    LentAt = now,
                    IsLentRecord = true
                };

                var ledgerId = await _ledgerRepository.InsertAsync(ledger);
                ledger.Id = ledgerId;
                result.CreatedLedgers.Add(ledger);

                // カードの貸出状態を更新
                await _cardRepository.UpdateLentStatusAsync(cardIdm, true, now, staffIdm);

                transaction.Commit();

                // 処理情報を記録
                LastProcessedCardIdm = cardIdm;
                LastProcessedTime = now;
                LastOperationType = LendingOperationType.Lend;

                result.Success = true;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"貸出処理でエラーが発生しました: {ex.Message}";
        }
        finally
        {
            // ロックを解放
            if (lockAcquired)
            {
                cardLock.Release();
            }
            // ロック参照カウントをデクリメント
            _lockManager.ReleaseLockReference(cardIdm);
        }

        return result;
    }

    /// <summary>
    /// ICカードの返却処理を実行し、利用履歴を記録します。
    /// </summary>
    /// <param name="staffIdm">返却者の職員証IDm（16桁の16進数文字列）</param>
    /// <param name="cardIdm">返却対象のICカードIDm（16桁の16進数文字列）</param>
    /// <param name="usageDetails">ICカードから読み取った利用履歴詳細（貸出時刻以降のみ使用）</param>
    /// <returns>返却結果。成功時は残額や警告情報も含まれます</returns>
    /// <remarks>
    /// <para>処理フロー：</para>
    /// <list type="number">
    /// <item><description>カードごとの排他ロックを取得（タイムアウト: 5秒）</description></item>
    /// <item><description>カード・職員・貸出レコードの存在確認</description></item>
    /// <item><description>貸出時刻以降の利用履歴のみを抽出</description></item>
    /// <item><description>日付ごとに利用履歴レコードを作成（<see cref="SummaryGenerator"/> で摘要生成）</description></item>
    /// <item><description>貸出レコードを更新（返却者・返却時刻を記録）</description></item>
    /// <item><description>カードの貸出状態を解除</description></item>
    /// <item><description>残額警告チェック</description></item>
    /// </list>
    /// <para>
    /// <see cref="LendingResult.HasBusUsage"/> でバス利用の有無を確認できます。
    /// バス利用がある場合は、呼び出し元でバス停名入力ダイアログを表示してください。
    /// </para>
    /// </remarks>
    public async Task<LendingResult> ReturnAsync(string staffIdm, string cardIdm, IEnumerable<LedgerDetail> usageDetails)
    {
        var result = new LendingResult { OperationType = LendingOperationType.Return };

        // カードごとのロックを取得
        var cardLock = _lockManager.GetLock(cardIdm);
        var lockAcquired = false;

        try
        {
            // タイムアウト付きでロックを取得
            lockAcquired = await cardLock.WaitAsync(GetLockTimeoutMs());
            if (!lockAcquired)
            {
                result.ErrorMessage = "他の処理が実行中です。しばらく待ってから再度お試しください。";
                return result;
            }

            // カードを取得
            var card = await _cardRepository.GetByIdmAsync(cardIdm);
            if (card == null)
            {
                result.ErrorMessage = "カードが登録されていません。";
                return result;
            }

            // 貸出中チェック
            if (!card.IsLent)
            {
                result.ErrorMessage = "このカードは貸出されていません。";
                return result;
            }

            // 返却者を取得
            var returner = await _staffRepository.GetByIdmAsync(staffIdm);
            if (returner == null)
            {
                result.ErrorMessage = "職員証が登録されていません。";
                return result;
            }

            // 貸出レコードを取得
            var lentRecord = await _ledgerRepository.GetLentRecordAsync(cardIdm);
            if (lentRecord == null)
            {
                result.ErrorMessage = "貸出レコードが見つかりません。";
                return result;
            }

            var now = DateTime.Now;
            var detailList = usageDetails.ToList();

            // 貸出時刻以降の利用履歴のみを抽出
            var lentAt = lentRecord.LentAt ?? now.AddDays(-1);
            var usageSinceLent = detailList
                .Where(d => d.UseDate == null || d.UseDate >= lentAt)
                .ToList();

            // トランザクション開始
            using var transaction = _dbContext.BeginTransaction();

            try
            {
                // 利用日ごとにグループ化して履歴を作成
                var createdLedgers = await CreateUsageLedgersAsync(
                    cardIdm, lentRecord.StaffName ?? string.Empty, usageSinceLent);

                result.CreatedLedgers.AddRange(createdLedgers);

                // バス利用の有無をチェック
                result.HasBusUsage = usageSinceLent.Any(d => d.IsBus);

                // 貸出レコードを更新（貸出中フラグをOFFに）
                lentRecord.IsLentRecord = false;
                lentRecord.ReturnerIdm = staffIdm;
                lentRecord.ReturnedAt = now;
                await _ledgerRepository.UpdateAsync(lentRecord);

                // カードの貸出状態を更新
                await _cardRepository.UpdateLentStatusAsync(cardIdm, false, null, null);

                transaction.Commit();

                // 残額チェック
                var latestLedger = createdLedgers.LastOrDefault();
                if (latestLedger != null)
                {
                    result.Balance = latestLedger.Balance;
                    var settings = await _settingsRepository.GetAppSettingsAsync();
                    result.IsLowBalance = result.Balance < settings.WarningBalance;
                }

                // 処理情報を記録
                LastProcessedCardIdm = cardIdm;
                LastProcessedTime = now;
                LastOperationType = LendingOperationType.Return;

                result.Success = true;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"返却処理でエラーが発生しました: {ex.Message}";
        }
        finally
        {
            // ロックを解放
            if (lockAcquired)
            {
                cardLock.Release();
            }
            // ロック参照カウントをデクリメント
            _lockManager.ReleaseLockReference(cardIdm);
        }

        return result;
    }

    /// <summary>
    /// 利用履歴詳細からledgerレコードを作成
    /// </summary>
    private async Task<List<Ledger>> CreateUsageLedgersAsync(
        string cardIdm, string staffName, List<LedgerDetail> details)
    {
        var createdLedgers = new List<Ledger>();

        // 日付でグループ化
        var groupedByDate = details
            .Where(d => d.UseDate.HasValue)
            .GroupBy(d => d.UseDate!.Value.Date)
            .OrderBy(g => g.Key);

        // 前回の残高を取得
        var lastBalance = await GetLastBalanceAsync(cardIdm);

        foreach (var dateGroup in groupedByDate)
        {
            var date = dateGroup.Key;
            var dailyDetails = dateGroup.ToList();

            // チャージとそれ以外を分ける
            var chargeDetails = dailyDetails.Where(d => d.IsCharge).ToList();
            var usageDetails = dailyDetails.Where(d => !d.IsCharge).ToList();

            // チャージがある場合、別レコードとして作成
            foreach (var charge in chargeDetails)
            {
                var income = charge.Amount ?? 0;
                lastBalance += income;

                var chargeLedger = new Ledger
                {
                    CardIdm = cardIdm,
                    Date = date,
                    Summary = SummaryGenerator.GetChargeSummary(),
                    Income = income,
                    Expense = 0,
                    Balance = lastBalance,
                    StaffName = staffName
                };

                var ledgerId = await _ledgerRepository.InsertAsync(chargeLedger);
                chargeLedger.Id = ledgerId;

                // 詳細を登録
                charge.LedgerId = ledgerId;
                await _ledgerRepository.InsertDetailAsync(charge);

                createdLedgers.Add(chargeLedger);
            }

            // 利用がある場合
            if (usageDetails.Count > 0)
            {
                var expense = usageDetails.Sum(d => d.Amount ?? 0);
                lastBalance -= expense;

                var summary = _summaryGenerator.Generate(usageDetails);

                var usageLedger = new Ledger
                {
                    CardIdm = cardIdm,
                    Date = date,
                    Summary = summary,
                    Income = 0,
                    Expense = expense,
                    Balance = lastBalance,
                    StaffName = staffName
                };

                var ledgerId = await _ledgerRepository.InsertAsync(usageLedger);
                usageLedger.Id = ledgerId;

                // 詳細を登録
                await _ledgerRepository.InsertDetailsAsync(ledgerId, usageDetails);

                createdLedgers.Add(usageLedger);
            }
        }

        return createdLedgers;
    }

    /// <summary>
    /// カードの最終残高を取得
    /// </summary>
    private async Task<int> GetLastBalanceAsync(string cardIdm)
    {
        var lastLedger = await _ledgerRepository.GetLatestBeforeDateAsync(cardIdm, DateTime.Now.AddDays(1));
        return lastLedger?.Balance ?? 0;
    }

    /// <summary>
    /// 30秒ルールが適用されるかチェックします。
    /// </summary>
    /// <param name="cardIdm">確認するカードIDm（16桁の16進数文字列）</param>
    /// <returns>
    /// 30秒以内に同一カードが処理されていた場合は <c>true</c>。
    /// 適用される場合、<see cref="LastOperationType"/> で前回の処理種別を確認できます。
    /// </returns>
    /// <remarks>
    /// <para>
    /// このメソッドは誤操作修正のための「30秒ルール」の判定に使用します。
    /// </para>
    /// <para>
    /// <strong>使用例:</strong>
    /// </para>
    /// <code>
    /// if (_lendingService.IsRetouchWithinTimeout(cardIdm))
    /// {
    ///     // 逆の処理を実行
    ///     if (_lendingService.LastOperationType == LendingOperationType.Lend)
    ///         await ProcessReturnAsync(card);  // 貸出直後 → 返却
    ///     else
    ///         await ProcessLendAsync(card);    // 返却直後 → 貸出
    /// }
    /// </code>
    /// </remarks>
    public bool IsRetouchWithinTimeout(string cardIdm)
    {
        if (LastProcessedCardIdm != cardIdm || !LastProcessedTime.HasValue)
        {
            return false;
        }

        var elapsed = DateTime.Now - LastProcessedTime.Value;
        return elapsed.TotalSeconds <= RetouchTimeoutSeconds;
    }

    /// <summary>
    /// 処理履歴をクリア
    /// </summary>
    public void ClearHistory()
    {
        LastProcessedCardIdm = null;
        LastProcessedTime = null;
        LastOperationType = null;
    }

    /// <summary>
    /// ロック取得のタイムアウト値を取得（テスト用にオーバーライド可能）
    /// </summary>
    protected virtual int GetLockTimeoutMs() => LockTimeoutMs;
}
