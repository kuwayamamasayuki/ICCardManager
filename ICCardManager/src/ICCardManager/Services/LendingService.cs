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
/// 貸出・返却処理サービス
/// </summary>
public class LendingService
{
    private readonly DbContext _dbContext;
    private readonly ICardRepository _cardRepository;
    private readonly IStaffRepository _staffRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly SummaryGenerator _summaryGenerator;

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

    public LendingService(
        DbContext dbContext,
        ICardRepository cardRepository,
        IStaffRepository staffRepository,
        ILedgerRepository ledgerRepository,
        ISettingsRepository settingsRepository,
        SummaryGenerator summaryGenerator)
    {
        _dbContext = dbContext;
        _cardRepository = cardRepository;
        _staffRepository = staffRepository;
        _ledgerRepository = ledgerRepository;
        _settingsRepository = settingsRepository;
        _summaryGenerator = summaryGenerator;
    }

    /// <summary>
    /// 貸出処理を実行
    /// </summary>
    /// <param name="staffIdm">職員証IDm</param>
    /// <param name="cardIdm">交通系ICカードIDm</param>
    public async Task<LendingResult> LendAsync(string staffIdm, string cardIdm)
    {
        var result = new LendingResult { OperationType = LendingOperationType.Lend };

        try
        {
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

        return result;
    }

    /// <summary>
    /// 返却処理を実行
    /// </summary>
    /// <param name="staffIdm">返却者の職員証IDm</param>
    /// <param name="cardIdm">交通系ICカードIDm</param>
    /// <param name="usageDetails">ICカードから読み取った利用履歴詳細</param>
    public async Task<LendingResult> ReturnAsync(string staffIdm, string cardIdm, IEnumerable<LedgerDetail> usageDetails)
    {
        var result = new LendingResult { OperationType = LendingOperationType.Return };

        try
        {
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
    /// 30秒ルールが適用されるかチェック
    /// </summary>
    /// <param name="cardIdm">確認するカードIDm</param>
    /// <returns>30秒以内の再タッチかどうか</returns>
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
}
