using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
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
        public string ErrorMessage { get; set; }

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

        /// <summary>
        /// 今月の利用履歴が不完全な可能性があるか（返却時のみ）
        /// </summary>
        /// <remarks>
        /// Issue #596対応: カード内の20件の履歴がすべて今月以降の場合、
        /// 今月初日から読み取れなかった履歴がある可能性がある。
        /// trueの場合、CSVインポートで不足分を補完する必要がある旨をユーザーに通知する。
        /// </remarks>
        public bool MayHaveIncompleteHistory { get; set; }
    }

    /// <summary>
    /// カード登録時の履歴インポート結果
    /// </summary>
    /// <remarks>
    /// Issue #596対応: カード登録時に当月履歴を自動読み取りした結果を格納する。
    /// </remarks>
    public class HistoryImportResult
    {
        /// <summary>
        /// インポートが成功したかどうか
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// インポートされた履歴レコード数
        /// </summary>
        public int ImportedCount { get; set; }

        /// <summary>
        /// 今月の履歴が不完全な可能性があるか
        /// </summary>
        /// <remarks>
        /// カード内の20件の履歴がすべて対象期間内の場合、
        /// 月初めからの履歴が不足している可能性がある。
        /// trueの場合、CSVインポートで不足分を補完する必要がある旨をユーザーに通知する。
        /// </remarks>
        public bool MayHaveIncompleteHistory { get; set; }

        /// <summary>
        /// カード内の履歴の最古日付（Issue #664: 不完全履歴の場合のみ有効）
        /// </summary>
        public DateTime? EarliestHistoryDate { get; set; }
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
        private readonly ILogger<LendingService> _logger;

        /// <summary>
        /// 最後に処理したカードのIDm
        /// </summary>
        public string LastProcessedCardIdm { get; private set; }

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
            CardLockManager lockManager,
            ILogger<LendingService> logger)
        {
            _dbContext = dbContext;
            _cardRepository = cardRepository;
            _staffRepository = staffRepository;
            _ledgerRepository = ledgerRepository;
            _settingsRepository = settingsRepository;
            _summaryGenerator = summaryGenerator;
            _lockManager = lockManager;
            _logger = logger;
        }

        /// <summary>
        /// 起動時にic_card.is_lentとledger.is_lent_recordの整合性をチェックし、
        /// 不整合があれば修復します。
        /// </summary>
        /// <remarks>
        /// <para>Issue #790対応: 何らかの原因でic_card.is_lentフラグと
        /// ledgerテーブルの貸出中レコード（is_lent_record=1）が不整合になるケースへの対策。</para>
        /// <para>貸出中レコードの有無を正（source of truth）として、is_lentフラグを修復する：</para>
        /// <list type="bullet">
        /// <item><description>貸出中レコードあり＋is_lent=0 → is_lent=1に修復</description></item>
        /// <item><description>貸出中レコードなし＋is_lent=1 → is_lent=0に修復</description></item>
        /// </list>
        /// </remarks>
        /// <returns>修復件数</returns>
        public async Task<int> RepairLentStatusConsistencyAsync()
        {
            var cards = await _cardRepository.GetAllAsync();
            var lentRecords = await _ledgerRepository.GetAllLentRecordsAsync();

            // カードIDm → 貸出中レコードのマッピング
            var lentRecordMap = new Dictionary<string, Ledger>();
            foreach (var record in lentRecords)
            {
                // 同一カードに複数の貸出中レコードがある場合は最新を採用
                if (!lentRecordMap.ContainsKey(record.CardIdm))
                {
                    lentRecordMap[record.CardIdm] = record;
                }
            }

            var repairCount = 0;

            foreach (var card in cards)
            {
                var hasLentRecord = lentRecordMap.TryGetValue(card.CardIdm, out var lentRecord);

                if (hasLentRecord && !card.IsLent)
                {
                    // 貸出中レコードがあるのにis_lent=0 → is_lent=1に修復
                    await _cardRepository.UpdateLentStatusAsync(
                        card.CardIdm, true, lentRecord.LentAt, lentRecord.LenderIdm);
                    _logger.LogWarning(
                        "Issue #790: 貸出状態の不整合を修復しました（is_lent: 0→1）: CardIdm={CardIdm}, LentAt={LentAt}",
                        card.CardIdm, lentRecord.LentAt);
                    repairCount++;
                }
                else if (!hasLentRecord && card.IsLent)
                {
                    // 貸出中レコードがないのにis_lent=1 → is_lent=0に修復
                    await _cardRepository.UpdateLentStatusAsync(
                        card.CardIdm, false, null, null);
                    _logger.LogWarning(
                        "Issue #790: 貸出状態の不整合を修復しました（is_lent: 1→0）: CardIdm={CardIdm}",
                        card.CardIdm);
                    repairCount++;
                }
            }

            if (repairCount > 0)
            {
                _logger.LogInformation("Issue #790: 貸出状態の整合性チェック完了: {Count}件修復", repairCount);
            }

            return repairCount;
        }

        /// <summary>
        /// ICカードの貸出処理を実行します。
        /// </summary>
        /// <param name="staffIdm">貸出者の職員証IDm（16桁の16進数文字列）</param>
        /// <param name="cardIdm">貸出対象のICカードIDm（16桁の16進数文字列）</param>
        /// <param name="balance">カードの現在残高（読み取れなかった場合はnull）</param>
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
        public async Task<LendingResult> LendAsync(string staffIdm, string cardIdm, int? balance = null)
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
                    // Issue #656: カードから残高を読み取れなかった場合、直近の履歴から残高を取得
                    var currentBalance = balance ?? 0;
                    if (!balance.HasValue)
                    {
                        var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(cardIdm);
                        if (latestLedger != null)
                        {
                            currentBalance = latestLedger.Balance;
                            _logger.LogInformation(
                                "LendAsync: カード残高を読み取れなかったため、直近の履歴残高を使用: {Balance}円", currentBalance);
                        }
                    }
                    var ledger = new Ledger
                    {
                        CardIdm = cardIdm,
                        LenderIdm = staffIdm,
                        Date = now,
                        Summary = SummaryGenerator.GetLendingSummary(),
                        Income = 0,
                        Expense = 0,
                        Balance = currentBalance,
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
                    result.Balance = currentBalance;
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
        /// <param name="skipDuplicateCheck">重複チェックをスキップするかどうか（既定値: false）</param>
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
        public async Task<LendingResult> ReturnAsync(string staffIdm, string cardIdm, IEnumerable<LedgerDetail> usageDetails, bool skipDuplicateCheck = false)
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

                _logger.LogDebug("LendingService: 返却処理 - 受け取った履歴件数={Count}", detailList.Count);

                // 貸出タッチを忘れた場合でも履歴が正しく記録されるよう、日付フィルタを緩和
                // 重複チェックは CreateUsageLedgersAsync 内の既存履歴照合（Issue #326）で行う
                // 注意: FeliCa履歴の日付は時刻を含まないため、日付部分のみで比較する
                var lentAt = lentRecord.LentAt ?? now.AddDays(-1);
                var lentDate = lentAt.Date;  // 時刻を切り捨てて日付のみにする
                // 貸出日の1週間前までの履歴を対象とする（貸出タッチ忘れへの対応）
                var filterStartDate = lentDate.AddDays(-7);
                var usageSinceLent = detailList
                    .Where(d => d.UseDate == null || d.UseDate.Value.Date >= filterStartDate)
                    .ToList();

                _logger.LogDebug("LendingService: 貸出時刻={LentAt}, フィルタ開始日={FilterStart}, 抽出後の履歴件数={Count}",
                    lentAt.ToString("yyyy-MM-dd HH:mm:ss"), filterStartDate.ToString("yyyy-MM-dd"), usageSinceLent.Count);

                // 履歴データの詳細をログ出力
                foreach (var detail in usageSinceLent.Take(5))
                {
                    _logger.LogDebug("LendingService: 履歴詳細 - 日付={Date}, 残高={Balance}, 金額={Amount}, チャージ={IsCharge}",
                        detail.UseDate?.ToString("yyyy-MM-dd"), detail.Balance, detail.Amount, detail.IsCharge);
                }

                // Issue #596: 今月の履歴完全性チェック（トランザクション前に既存レコードを確認）
                var currentMonthStart = new DateTime(now.Year, now.Month, 1);
                var existingMonthRecords = await _ledgerRepository.GetByMonthAsync(cardIdm, now.Year, now.Month);
                var hadExistingCurrentMonthRecords = existingMonthRecords
                    .Any(l => !l.IsLentRecord);

                // トランザクション開始
                using var transaction = _dbContext.BeginTransaction();

                try
                {
                    // 利用日ごとにグループ化して履歴を作成
                    var createdLedgers = await CreateUsageLedgersAsync(
                        cardIdm, lentRecord.StaffName ?? string.Empty, usageSinceLent, skipDuplicateCheck);

                    result.CreatedLedgers.AddRange(createdLedgers);

                    // バス利用の有無をチェック
                    result.HasBusUsage = usageSinceLent.Any(d => d.IsBus);

                    // 貸出レコードを削除（履歴に「（貸出中）」が残らないようにする）
                    await _ledgerRepository.DeleteAsync(lentRecord.Id);

                    // カードの貸出状態を更新
                    await _cardRepository.UpdateLentStatusAsync(cardIdm, false, null, null);

                    transaction.Commit();

                    // 残額チェック
                    // カードから直接読み取った残高を優先（履歴の先頭が最新）
                    // FelicaCardReaderで読み取った場合、各LedgerDetail.Balanceには実際の残高が設定されている
                    var cardBalance = detailList.FirstOrDefault()?.Balance;
                    if (cardBalance.HasValue && cardBalance.Value > 0)
                    {
                        result.Balance = cardBalance.Value;
                        _logger.LogDebug("LendingService: カードから直接読み取った残高を使用: {Balance}円", result.Balance);
                    }
                    else
                    {
                        // フォールバック: 作成したledgerレコードの残高を使用
                        var latestLedger = createdLedgers.LastOrDefault();
                        if (latestLedger != null)
                        {
                            result.Balance = latestLedger.Balance;
                            _logger.LogDebug("LendingService: ledgerレコードの残高を使用: {Balance}円", result.Balance);
                        }
                    }

                    // 低残高チェック
                    var settings = await _settingsRepository.GetAppSettingsAsync();
                    result.IsLowBalance = result.Balance < settings.WarningBalance;

                    // 処理情報を記録
                    LastProcessedCardIdm = cardIdm;
                    LastProcessedTime = now;
                    LastOperationType = LendingOperationType.Return;

                    result.Success = true;

                    // Issue #596: 今月の履歴が不完全な可能性をチェック
                    // 条件: このカードの今月の既存レコードがなく（月途中導入の可能性）、
                    //       かつカード内に20件の履歴があり、すべて今月以降の場合
                    if (!hadExistingCurrentMonthRecords)
                    {
                        result.MayHaveIncompleteHistory = CheckHistoryCompleteness(detailList, currentMonthStart);
                    }
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
        /// <remarks>
        /// <para>
        /// カードから読み取った履歴データを元に、ledgerレコードを作成します。
        /// FelicaCardReaderで読み取った場合、各 <see cref="LedgerDetail.Balance"/> には
        /// カードから直接読み取った残高が設定されているため、これを優先的に使用します。
        /// </para>
        /// <para>
        /// Issue #326対応: 同じ履歴を二回以上登録しないため、
        /// 既存の履歴詳細と照合して重複を除外します。
        /// </para>
        /// </remarks>
        private async Task<List<Ledger>> CreateUsageLedgersAsync(
            string cardIdm, string staffName, List<LedgerDetail> details, bool skipDuplicateCheck = false)
        {
            var createdLedgers = new List<Ledger>();

            _logger.LogDebug("LendingService: CreateUsageLedgersAsync開始 - 履歴件数={Count}, skipDuplicateCheck={Skip}", details.Count, skipDuplicateCheck);

            if (details.Count == 0)
            {
                _logger.LogDebug("LendingService: 履歴データがありません");
                return createdLedgers;
            }

            // Issue #326: 既存の履歴詳細と照合して重複を除外
            // 仮想タッチの場合はスキップ（物理カード読み取りではないため重複は発生しない）
            if (!skipDuplicateCheck)
            {
                // 最も古い履歴の日付を基準に既存データを取得
                var oldestDate = details
                    .Where(d => d.UseDate.HasValue)
                    .Select(d => d.UseDate!.Value)
                    .DefaultIfEmpty(DateTime.Today)
                    .Min();

                var existingKeys = await _ledgerRepository.GetExistingDetailKeysAsync(cardIdm, oldestDate);

                if (existingKeys.Count > 0)
                {
                    var originalCount = details.Count;
                    details = details
                        .Where(d => !existingKeys.Contains((d.UseDate, d.Balance, d.IsCharge)))
                        .ToList();

                    var removedCount = originalCount - details.Count;
                    if (removedCount > 0)
                    {
                        _logger.LogInformation(
                            "LendingService: 重複履歴を除外しました（除外件数={RemovedCount}, 残り件数={RemainingCount}）",
                            removedCount, details.Count);
                    }
                }

                if (details.Count == 0)
                {
                    _logger.LogDebug("LendingService: 重複除外後、登録対象の履歴がありません");
                    return createdLedgers;
                }
            }

            // 日付でグループ化
            var groupedByDate = details
                .Where(d => d.UseDate.HasValue)
                .GroupBy(d => d.UseDate!.Value.Date)
                .OrderBy(g => g.Key);

            var dateGroups = groupedByDate.ToList();
            _logger.LogDebug("LendingService: 日付グループ数={Count}, 日付一覧={Dates}",
                dateGroups.Count, string.Join(", ", dateGroups.Select(g => g.Key.ToString("yyyy-MM-dd"))));

            // カードから読み取った残高を優先的に使用
            // 履歴データには各取引後の残高が含まれているため、これを直接使用する
            // フォールバック: データベースの最終残高（PcScCardReader等、残高が読めない場合用）
            var useCardBalance = details.Any(d => d.Balance.HasValue && d.Balance.Value > 0);
            _logger.LogDebug("LendingService: カード残高使用={UseCardBalance}", useCardBalance);

            // フォールバック用: データベースから前回の残高を取得
            var lastBalance = await GetLastBalanceAsync(cardIdm);

            foreach (var dateGroup in groupedByDate)
            {
                var date = dateGroup.Key;
                var dailyDetails = dateGroup.ToList();

                // Issue #380: 残高不足パターンの検出とマージ処理
                // パターン: 小額チャージ → 利用（残高0）の連続で、チャージ後残高 = 利用額の場合
                // 例: 残高200円、運賃210円 → 10円チャージ → 210円支払い → 残高0円
                var insufficientBalancePairs = DetectInsufficientBalancePattern(dailyDetails);

                foreach (var pair in insufficientBalancePairs)
                {
                    var charge = pair.Charge;
                    var usage = pair.Usage;
                    var originalBalance = (charge.Balance ?? 0) - (charge.Amount ?? 0);
                    var shortfall = charge.Amount ?? 0;
                    var totalFare = usage.Amount ?? 0;

                    _logger.LogDebug("LendingService: 残高不足パターン検出 - 元残高={OriginalBalance}, 不足額={Shortfall}, 運賃={Fare}",
                        originalBalance, shortfall, totalFare);

                    // マージしたLedgerを作成
                    var summary = _summaryGenerator.Generate(new List<LedgerDetail> { usage });
                    var note = SummaryGenerator.GetInsufficientBalanceNote(totalFare, shortfall);

                    var mergedLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = usage.UseDate ?? date,
                        Summary = summary,
                        Income = 0,
                        Expense = originalBalance,  // 実際にカードから支払った金額（元の残高）
                        Balance = 0,
                        StaffName = staffName,
                        Note = note
                    };

                    var ledgerId = await _ledgerRepository.InsertAsync(mergedLedger);
                    mergedLedger.Id = ledgerId;

                    // 利用詳細のみを登録（チャージ詳細はスキップ）
                    usage.LedgerId = ledgerId;
                    await _ledgerRepository.InsertDetailAsync(usage);

                    createdLedgers.Add(mergedLedger);

                    // 処理済みの項目をdailyDetailsから除外
                    dailyDetails.Remove(charge);
                    dailyDetails.Remove(usage);

                    // lastBalanceを更新
                    lastBalance = 0;
                }

                // チャージとそれ以外を分ける（残高不足パターンで処理済みのものは除外されている）
                var chargeDetails = dailyDetails.Where(d => d.IsCharge).ToList();
                var usageDetails = dailyDetails.Where(d => !d.IsCharge).ToList();

                // チャージがある場合、別レコードとして作成
                foreach (var charge in chargeDetails)
                {
                    int balance;
                    int income;

                    if (useCardBalance && charge.Balance.HasValue)
                    {
                        // カードから読み取った残高を使用
                        balance = charge.Balance.Value;
                        income = charge.Amount ?? (balance - lastBalance);
                        lastBalance = balance;
                    }
                    else
                    {
                        // フォールバック: Amountから計算
                        income = charge.Amount ?? 0;
                        lastBalance += income;
                        balance = lastBalance;
                    }

                    var chargeLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = charge.UseDate ?? date,
                        Summary = SummaryGenerator.GetChargeSummary(_settingsRepository.GetAppSettings().DepartmentType),
                        Income = income,
                        Expense = 0,
                        Balance = balance,
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
                    int balance;
                    int expense;

                    if (useCardBalance)
                    {
                        // カードから読み取った残高を使用（日付内の最新レコードの残高）
                        // 利用履歴は時刻順にソートされていないので、残高が最小のものを選ぶ
                        // （利用後なので残高は減っている）
                        var latestDetail = usageDetails
                            .Where(d => d.Balance.HasValue)
                            .OrderBy(d => d.Balance)
                            .FirstOrDefault();

                        if (latestDetail?.Balance != null)
                        {
                            balance = latestDetail.Balance.Value;
                            expense = usageDetails.Sum(d => d.Amount ?? 0);
                            if (expense == 0)
                            {
                                // Amountが設定されていない場合、残高差から計算
                                expense = lastBalance - balance;
                                if (expense < 0) expense = 0;
                            }
                            lastBalance = balance;
                        }
                        else
                        {
                            // フォールバック
                            expense = usageDetails.Sum(d => d.Amount ?? 0);
                            lastBalance -= expense;
                            balance = lastBalance;
                        }
                    }
                    else
                    {
                        // フォールバック: Amountから計算
                        expense = usageDetails.Sum(d => d.Amount ?? 0);
                        lastBalance -= expense;
                        balance = lastBalance;
                    }

                    var summary = _summaryGenerator.Generate(usageDetails);

                    var usageLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = usageDetails.FirstOrDefault()?.UseDate ?? date,
                        Summary = summary,
                        Income = 0,
                        Expense = expense,
                        Balance = balance,
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
        /// 残高不足パターンを検出
        /// </summary>
        /// <remarks>
        /// Issue #380対応: 残高が不足して不足分だけを現金でチャージした場合のパターンを検出。
        ///
        /// パターン:
        /// 1. 小額のチャージ（不足分）
        /// 2. 直後の利用（運賃）で残高が0になる
        /// 3. チャージ後の残高 = 利用額（運賃）
        ///
        /// 例: 残高200円、運賃210円の場合
        /// - チャージ: 10円（残高 → 210円）
        /// - 利用: 210円（残高 → 0円）
        /// この場合、チャージ後残高(210) = 利用額(210) となる。
        /// </remarks>
        /// <param name="dailyDetails">日付グループ内の履歴詳細リスト</param>
        /// <returns>検出されたペアのリスト</returns>
        internal static List<(LedgerDetail Charge, LedgerDetail Usage)> DetectInsufficientBalancePattern(
            List<LedgerDetail> dailyDetails)
        {
            var result = new List<(LedgerDetail Charge, LedgerDetail Usage)>();
            var processedIndices = new HashSet<int>();

            for (int i = 0; i < dailyDetails.Count; i++)
            {
                if (processedIndices.Contains(i)) continue;

                var current = dailyDetails[i];

                // チャージレコードを探す
                if (!current.IsCharge) continue;
                if (!current.Balance.HasValue || !current.Amount.HasValue) continue;

                // 対応する利用レコードを探す
                for (int j = 0; j < dailyDetails.Count; j++)
                {
                    if (i == j || processedIndices.Contains(j)) continue;

                    var candidate = dailyDetails[j];

                    // 利用レコード（チャージでもポイント還元でもない）
                    if (candidate.IsCharge || candidate.IsPointRedemption) continue;
                    if (!candidate.Balance.HasValue || !candidate.Amount.HasValue) continue;

                    // パターン検出条件:
                    // 1. チャージ後の残高 = 利用額（運賃）
                    // 2. 利用後の残高 = 0
                    var chargeAfterBalance = current.Balance.Value;
                    var usageAmount = candidate.Amount.Value;
                    var usageAfterBalance = candidate.Balance.Value;

                    if (chargeAfterBalance == usageAmount && usageAfterBalance == 0)
                    {
                        // パターン検出！
                        result.Add((current, candidate));
                        processedIndices.Add(i);
                        processedIndices.Add(j);
                        break;
                    }
                }
            }

            return result;
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
        /// カード登録時に履歴をインポート（Issue #596）
        /// </summary>
        /// <remarks>
        /// カード登録直後に呼び出され、カード内の履歴から対象期間（importFromDate以降）の
        /// レコードをledgerに登録する。既存の <see cref="CreateUsageLedgersAsync"/> を
        /// 内部で利用し、重複チェック・チャージ分離・残高不足パターン検出等を再利用する。
        /// </remarks>
        /// <param name="cardIdm">カードのIDm</param>
        /// <param name="historyDetails">カードから読み取った履歴詳細</param>
        /// <param name="importFromDate">インポート対象の開始日</param>
        /// <returns>インポート結果</returns>
        public async Task<HistoryImportResult> ImportHistoryForRegistrationAsync(
            string cardIdm, List<LedgerDetail> historyDetails, DateTime importFromDate)
        {
            var result = new HistoryImportResult();

            try
            {
                // importFromDate以降の履歴のみをフィルタ（呼び出し元で既にフィルタ済みだが安全のため再チェック）
                var filtered = historyDetails
                    .Where(d => d.UseDate.HasValue && d.UseDate.Value.Date >= importFromDate.Date)
                    .OrderBy(d => d.UseDate)
                    .ThenByDescending(d => d.Balance)
                    .ToList();

                if (filtered.Count == 0)
                {
                    result.Success = true;
                    result.ImportedCount = 0;
                    return result;
                }

                // トランザクション開始
                using var transaction = _dbContext.BeginTransaction();

                try
                {
                    // 既存のCreateUsageLedgersAsyncを利用（staffNameはnull: 登録時には利用者情報がないため）
                    var createdLedgers = await CreateUsageLedgersAsync(cardIdm, null, filtered);

                    transaction.Commit();

                    result.Success = true;
                    result.ImportedCount = createdLedgers.Count;

                    // 完全性チェック: 元の履歴（フィルタ前）を使用
                    result.MayHaveIncompleteHistory = CheckHistoryCompleteness(historyDetails, importFromDate);

                    // Issue #664: 不完全な場合、履歴の最古日付をメッセージ用に記録
                    if (result.MayHaveIncompleteHistory)
                    {
                        result.EarliestHistoryDate = historyDetails
                            .Where(d => d.UseDate.HasValue)
                            .Min(d => d.UseDate.Value);
                    }
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "カード登録時の履歴インポートでエラーが発生しました（CardIdm={CardIdm}）", cardIdm);
                result.Success = false;
            }

            return result;
        }

        /// <summary>
        /// ロック取得のタイムアウト値を取得（テスト用にオーバーライド可能）
        /// </summary>
        protected virtual int GetLockTimeoutMs() => LockTimeoutMs;

        /// <summary>
        /// カードから読み取った履歴の完全性をチェック
        /// </summary>
        /// <remarks>
        /// Issue #596対応: カード内の履歴20件がすべて今月以降の場合、
        /// 今月初日以降の古い履歴がカードから押し出されている可能性がある。
        /// </remarks>
        /// <param name="rawDetails">カードから読み取った生の履歴（最大20件）</param>
        /// <param name="currentMonthStart">今月1日</param>
        /// <returns>今月の履歴が不完全な可能性がある場合true</returns>
        internal static bool CheckHistoryCompleteness(IList<LedgerDetail> rawDetails, DateTime currentMonthStart)
        {
            // 20件未満の場合はカード内の全履歴を取得済み
            if (rawDetails.Count < 20)
            {
                return false;
            }

            // 日付のある履歴のうち、今月より前のものがあれば今月分は全件カバー
            var hasPreCurrentMonth = rawDetails
                .Where(d => d.UseDate.HasValue)
                .Any(d => d.UseDate.Value.Date < currentMonthStart);

            // 先月以前の履歴がなければ → 今月分が押し出されている可能性あり
            return !hasPreCurrentMonth;
        }
    }
}
