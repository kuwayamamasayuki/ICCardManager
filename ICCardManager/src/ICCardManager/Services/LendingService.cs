using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        /// Issue #1132: 残額警告しきい値（設定値）
        /// </summary>
        public int WarningBalance { get; set; }

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
        /// <summary>
        /// 残高不足パターン検出時に許容するチャージ超過額の閾値（円）。
        /// 精算機でのチャージは不足額ちょうどか10円単位の端数切り上げのため、
        /// 利用後残高（= チャージ額 - 不足額）がこの値未満であれば残高不足パターンとみなす。
        /// </summary>
        internal const int InsufficientBalanceExcessThreshold = LendingHistoryAnalyzer.InsufficientBalanceExcessThreshold;

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
        private readonly int _retouchTimeoutSeconds;

        /// <summary>
        /// ロック取得のタイムアウト（ミリ秒）
        /// </summary>
        private readonly int _lockTimeoutMs;

        public LendingService(
            DbContext dbContext,
            ICardRepository cardRepository,
            IStaffRepository staffRepository,
            ILedgerRepository ledgerRepository,
            ISettingsRepository settingsRepository,
            SummaryGenerator summaryGenerator,
            CardLockManager lockManager,
            IOptions<AppOptions> appOptions,
            ILogger<LendingService> logger)
        {
            _dbContext = dbContext;
            _cardRepository = cardRepository;
            _staffRepository = staffRepository;
            _ledgerRepository = ledgerRepository;
            _settingsRepository = settingsRepository;
            _summaryGenerator = summaryGenerator;
            _lockManager = lockManager;
            _retouchTimeoutSeconds = appOptions.Value.RetouchWindowSeconds;
            _lockTimeoutMs = appOptions.Value.CardLockTimeoutSeconds * 1000;
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
            // Issue #1196: 同一カードに複数の貸出中レコードがある場合は明示的に最新を採用する。
            // 以前はリポジトリ側 ORDER BY lent_at DESC に依存していたが、層間の暗黙契約を排除し、
            // サービス層自身が並び順を保証する。LentAt が null のレコードは末尾に並ぶ
            // （Comparer<DateTime?>.Default は null を最小値として扱うため）。
            var lentRecordMap = new Dictionary<string, Ledger>();
            foreach (var record in lentRecords.OrderByDescending(r => r.LentAt))
            {
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

                var (card, staff, validationError) = await ValidateLendPreconditionsAsync(staffIdm, cardIdm);
                if (validationError != null)
                {
                    result.ErrorMessage = validationError;
                    return result;
                }

                var now = DateTime.Now;

                // Issue #656: カードから残高を読み取れなかった場合、直近の履歴から残高を取得
                // READ操作はリトライ範囲の外で実行（不要な再クエリを防止）
                var currentBalance = await ResolveInitialBalanceAsync(cardIdm, balance);

                // トランザクション内で貸出ledger作成 + カード状態更新
                // 共有モード時のSQLITE_BUSY対策としてリトライでラップ（WRITE操作のみ）
                var ledger = await InsertLendLedgerAsync(cardIdm, staffIdm, staff.Name, currentBalance, now);
                result.CreatedLedgers.Add(ledger);

                // 処理情報を記録
                LastProcessedCardIdm = cardIdm;
                LastProcessedTime = now;
                LastOperationType = LendingOperationType.Lend;

                result.Success = true;
                result.Balance = currentBalance;
            }
            catch (Exception ex)
            {
                // Issue #1110: SQLiteエラーをユーザー向けメッセージに変換
                result.ErrorMessage = GetUserFriendlyErrorMessage(ex, "貸出");
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
        /// 貸出ledgerレコードを作成し、カードの貸出状態を更新する。
        /// 共有モード時のSQLITE_BUSY対策として ExecuteWithRetryAsync でラップ。
        /// </summary>
        internal async Task<Ledger> InsertLendLedgerAsync(
            string cardIdm, string staffIdm, string staffName, int balance, DateTime now)
        {
            Ledger createdLedger = null;

            await _dbContext.ExecuteWithRetryAsync(async () =>
            {
                using var scope = await _dbContext.BeginTransactionAsync();

                try
                {
                    var ledger = new Ledger
                    {
                        CardIdm = cardIdm,
                        LenderIdm = staffIdm,
                        Date = now,
                        Summary = SummaryGenerator.GetLendingSummary(),
                        Income = 0,
                        Expense = 0,
                        Balance = balance,
                        StaffName = staffName,
                        LentAt = now,
                        IsLentRecord = true
                    };

                    var ledgerId = await _ledgerRepository.InsertAsync(ledger);
                    ledger.Id = ledgerId;

                    await _cardRepository.UpdateLentStatusAsync(cardIdm, true, now, staffIdm);

                    scope.Commit();
                    createdLedger = ledger;
                }
                catch
                {
                    scope.Rollback();
                    throw;
                }
            });

            return createdLedger;
        }

        /// <summary>
        /// Issue #656: カードから残高を読み取れなかった場合、直近の ledger 残高を fallback として使用。
        /// </summary>
        internal async Task<int> ResolveInitialBalanceAsync(string cardIdm, int? balance)
        {
            if (balance.HasValue)
            {
                return balance.Value;
            }

            var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(cardIdm);
            if (latestLedger != null)
            {
                _logger.LogInformation(
                    "LendAsync: カード残高を読み取れなかったため、直近の履歴残高を使用: {Balance}円", latestLedger.Balance);
                return latestLedger.Balance;
            }

            return 0;
        }

        /// <summary>
        /// 貸出処理の事前検証。カード・貸出状態・職員の存在を順次チェックする。
        /// </summary>
        /// <returns>(Card, Staff, ErrorMessage)。ErrorMessage が非 null の場合は検証失敗。</returns>
        internal async Task<(IcCard Card, Staff Staff, string ErrorMessage)> ValidateLendPreconditionsAsync(
            string staffIdm, string cardIdm)
        {
            var card = await _cardRepository.GetByIdmAsync(cardIdm);
            if (card == null)
            {
                return (null, null, "カードが登録されていません。");
            }

            if (card.IsLent)
            {
                return (card, null, "このカードは既に貸出中です。");
            }

            var staff = await _staffRepository.GetByIdmAsync(staffIdm);
            if (staff == null)
            {
                return (card, null, "職員証が登録されていません。");
            }

            return (card, staff, null);
        }

        /// <summary>
        /// 返却時のトランザクション内処理: 履歴ledger作成 + 貸出レコード削除 + カード状態解除。
        /// 共有モード時のSQLITE_BUSY対策として ExecuteWithRetryAsync でラップ。
        /// </summary>
        internal async Task PersistReturnAsync(
            string cardIdm,
            Ledger lentRecord,
            List<LedgerDetail> usageSinceLent,
            bool skipDuplicateCheck,
            LendingResult result)
        {
            await _dbContext.ExecuteWithRetryAsync(async () =>
            {
                using var scope = await _dbContext.BeginTransactionAsync();

                try
                {
                    var createdLedgers = await CreateUsageLedgersAsync(
                        cardIdm, lentRecord.StaffName ?? string.Empty, usageSinceLent, skipDuplicateCheck);

                    result.CreatedLedgers.AddRange(createdLedgers);

                    result.HasBusUsage = usageSinceLent.Any(d => d.IsBus);

                    // 貸出レコードをすべて削除（履歴に「（貸出中）」が残らないようにする）
                    // 共有モードで重複した貸出中レコードがある場合にも対応
                    await _ledgerRepository.DeleteAllLentRecordsAsync(cardIdm);

                    await _cardRepository.UpdateLentStatusAsync(cardIdm, false, null, null);

                    scope.Commit();
                }
                catch
                {
                    scope.Rollback();
                    throw;
                }
            });
        }

        /// <summary>
        /// 低残高警告情報を result にセットする。
        /// </summary>
        internal async Task ApplyBalanceWarningAsync(LendingResult result)
        {
            var settings = await _settingsRepository.GetAppSettingsAsync();
            result.WarningBalance = settings.WarningBalance;
            result.IsLowBalance = result.Balance < settings.WarningBalance;
        }

        /// <summary>
        /// 返却時の残高解決カスケード。
        /// 優先順位: (1)カード直接読取値 > (2)作成ledger末尾 > (3)DB 直近 ledger(Issue #1139)。
        /// </summary>
        internal async Task<int> ResolveReturnBalanceAsync(
            List<LedgerDetail> detailList, List<Ledger> createdLedgers, string cardIdm)
        {
            var cardBalance = detailList.FirstOrDefault()?.Balance;
            if (cardBalance.HasValue && cardBalance.Value > 0)
            {
                _logger.LogDebug("LendingService: カードから直接読み取った残高を使用: {Balance}円", cardBalance.Value);
                return cardBalance.Value;
            }

            var latestCreatedLedger = createdLedgers.LastOrDefault();
            if (latestCreatedLedger != null)
            {
                _logger.LogDebug("LendingService: ledgerレコードの残高を使用: {Balance}円", latestCreatedLedger.Balance);
                return latestCreatedLedger.Balance;
            }

            var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(cardIdm);
            if (latestLedger != null)
            {
                _logger.LogInformation(
                    "ReturnAsync: カード残高を読み取れなかったため、直近の履歴残高を使用: {Balance}円", latestLedger.Balance);
                return latestLedger.Balance;
            }

            return 0;
        }

        /// <summary>
        /// 貸出日以降の履歴を抽出する。貸出タッチ忘れに備え貸出日の1週間前から遡る。
        /// 注意: FeliCa履歴の日付は時刻を含まないため、日付部分のみで比較する。
        /// </summary>
        internal static List<LedgerDetail> FilterUsageSinceLent(
            List<LedgerDetail> detailList, Ledger lentRecord, DateTime now)
        {
            var lentAt = lentRecord.LentAt ?? now.AddDays(-1);
            var lentDate = lentAt.Date;
            var filterStartDate = lentDate.AddDays(-7);
            return detailList
                .Where(d => d.UseDate == null || d.UseDate.Value.Date >= filterStartDate)
                .ToList();
        }

        /// <summary>
        /// 貸出レコードを取得。見つからない場合はエラーメッセージを返す。
        /// </summary>
        /// <returns>(LentRecord, ErrorMessage)。ErrorMessage が非 null の場合は失敗。</returns>
        internal async Task<(Ledger LentRecord, string ErrorMessage)> ResolveLentRecordAsync(string cardIdm)
        {
            var lentRecord = await _ledgerRepository.GetLentRecordAsync(cardIdm);
            if (lentRecord == null)
            {
                return (null, "貸出レコードが見つかりません。");
            }
            return (lentRecord, null);
        }

        /// <summary>
        /// 返却処理の事前検証。カード・貸出状態・職員の存在を順次チェックする。
        /// </summary>
        /// <returns>(Card, Returner, ErrorMessage)。ErrorMessage が非 null の場合は検証失敗。</returns>
        internal async Task<(IcCard Card, Staff Returner, string ErrorMessage)> ValidateReturnPreconditionsAsync(
            string staffIdm, string cardIdm)
        {
            var card = await _cardRepository.GetByIdmAsync(cardIdm);
            if (card == null)
            {
                return (null, null, "カードが登録されていません。");
            }

            if (!card.IsLent)
            {
                return (card, null, "このカードは貸出されていません。");
            }

            var returner = await _staffRepository.GetByIdmAsync(staffIdm);
            if (returner == null)
            {
                return (card, null, "職員証が登録されていません。");
            }

            return (card, returner, null);
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

                var (card, returner, validationError) = await ValidateReturnPreconditionsAsync(staffIdm, cardIdm);
                if (validationError != null)
                {
                    result.ErrorMessage = validationError;
                    return result;
                }

                var (lentRecord, lentRecordError) = await ResolveLentRecordAsync(cardIdm);
                if (lentRecordError != null)
                {
                    result.ErrorMessage = lentRecordError;
                    return result;
                }

                var now = DateTime.Now;
                var detailList = usageDetails.ToList();

                _logger.LogDebug("LendingService: 返却処理 - 受け取った履歴件数={Count}", detailList.Count);

                // 貸出タッチを忘れた場合でも履歴が正しく記録されるよう、日付フィルタを緩和
                // 重複チェックは CreateUsageLedgersAsync 内の既存履歴照合（Issue #326）で行う
                var usageSinceLent = FilterUsageSinceLent(detailList, lentRecord, now);

                var lentAt = lentRecord.LentAt ?? now.AddDays(-1);
                _logger.LogDebug("LendingService: 貸出時刻={LentAt}, フィルタ開始日={FilterStart}, 抽出後の履歴件数={Count}",
                    lentAt.ToString("yyyy-MM-dd HH:mm:ss"), lentAt.Date.AddDays(-7).ToString("yyyy-MM-dd"), usageSinceLent.Count);

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

                // トランザクション内で履歴作成 + 貸出レコード削除 + カード状態更新
                await PersistReturnAsync(cardIdm, lentRecord, usageSinceLent, skipDuplicateCheck, result);

                // 残額チェック（トランザクション外）
                // カードから直接読み取った残高を優先（履歴の先頭が最新）
                // FelicaCardReaderで読み取った場合、各LedgerDetail.Balanceには実際の残高が設定されている
                result.Balance = await ResolveReturnBalanceAsync(detailList, result.CreatedLedgers, cardIdm);

                await ApplyBalanceWarningAsync(result);

                // 処理情報を記録
                LastProcessedCardIdm = cardIdm;
                LastProcessedTime = now;
                LastOperationType = LendingOperationType.Return;

                result.Success = true;

                // Issue #596: 今月の履歴が不完全な可能性をチェック
                if (!hadExistingCurrentMonthRecords)
                {
                    result.MayHaveIncompleteHistory = CheckHistoryCompleteness(detailList, currentMonthStart);
                }
            }
            catch (Exception ex)
            {
                // Issue #1110: SQLiteエラーをユーザー向けメッセージに変換
                result.ErrorMessage = GetUserFriendlyErrorMessage(ex, "返却");
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
            // フォールバック: データベースの最終残高（履歴が取得できなかった場合用）
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
                    var chargeAmount = charge.Amount ?? 0;
                    var totalFare = usage.Amount ?? 0;
                    // Issue #978: 会計上の処理
                    // 運賃210円 = カードから払出(70円) + 現金で支払(140円=チャージ額)
                    // 不足額 = チャージ額（実際に現金で支払った金額）
                    // 払出額 = 運賃 - チャージ額（カードの元残高から充当した金額）
                    // 残額 = 利用後の実残高（ぴったりチャージなら0、端数チャージなら端数が残る）
                    var shortfall = chargeAmount;
                    var expense = totalFare - chargeAmount;

                    _logger.LogDebug("LendingService: 残高不足パターン検出 - 払出額={Expense}, 不足額={Shortfall}, 運賃={Fare}, チャージ額={ChargeAmount}",
                        expense, shortfall, totalFare, chargeAmount);

                    // マージしたLedgerを作成
                    var summary = _summaryGenerator.Generate(new List<LedgerDetail> { usage });
                    var note = SummaryGenerator.GetInsufficientBalanceNote(totalFare, shortfall);

                    var mergedLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = usage.UseDate ?? date,
                        Summary = summary,
                        Income = 0,
                        Expense = expense,   // 運賃 - チャージ額（カードから充当した金額）
                        Balance = usage.Balance ?? 0,  // 利用後の実残高（端数チャージの場合は端数が残る）
                        StaffName = staffName,
                        Note = note
                    };

                    var ledgerId = await _ledgerRepository.InsertAsync(mergedLedger);
                    mergedLedger.Id = ledgerId;

                    // Issue #978: チャージ詳細と利用詳細の両方を登録
                    // チャージ詳細も登録しないと重複チェック（GetExistingDetailKeysAsync）で
                    // 検出されず、次回返却時にチャージが再処理されてしまう
                    // チャージを先に挿入し、利用を後に挿入することで
                    // rowidベースのSequenceNumberが利用側で大きくなり、
                    // LedgerMergeService等の「最新Detail＝最大SequenceNumber」ロジックと整合する
                    charge.LedgerId = ledgerId;
                    await _ledgerRepository.InsertDetailAsync(charge);
                    usage.LedgerId = ledgerId;
                    await _ledgerRepository.InsertDetailAsync(usage);

                    createdLedgers.Add(mergedLedger);

                    // 処理済みの項目をdailyDetailsから除外
                    dailyDetails.Remove(charge);
                    dailyDetails.Remove(usage);

                    // lastBalanceを更新（利用後の実残高）
                    lastBalance = usage.Balance ?? 0;
                }

                // チャージ境界で利用グループを分割（残高不足パターンで処理済みのものは除外されている）
                var segments = SplitAtChargeBoundaries(dailyDetails);

                // Issue #837: 同一カード・同一日の既存利用レコードを取得（統合用）
                // 最初の利用セグメント処理時に既存レコードとの統合を試みる
                // Issue #1147: 利用者（StaffName）が一致するレコードのみ統合対象とする
                //   異なる職員が同日に同じカードを使った場合は別レコードとして作成
                List<Ledger> existingUsageLedgers = null;
                var hasUsageSegment = segments.Any(s => !s.IsCharge);
                if (hasUsageSegment)
                {
                    var existingLedgers = await _ledgerRepository.GetByDateRangeAsync(cardIdm, date, date);
                    existingUsageLedgers = existingLedgers
                        .Where(l => !l.IsLentRecord && l.Income == 0 && string.IsNullOrEmpty(l.Note)
                                    && l.StaffName == staffName)  // Issue #1147: 同一利用者のみ統合
                        .OrderByDescending(l => l.Balance)  // 残高降順（高い=古い）
                        .ToList();
                }
                var isFirstUsageSegment = true;

                // 各セグメントを時系列順に処理（lastBalanceを引き継いで残高チェーンを維持）
                foreach (var segment in segments)
                {
                    if (segment.IsCharge)
                    {
                        // チャージLedger作成
                        var charge = segment.Details[0];
                        int balance;
                        int income;

                        if (useCardBalance && charge.Balance.HasValue)
                        {
                            balance = charge.Balance.Value;
                            income = charge.Amount ?? (balance - lastBalance);
                            lastBalance = balance;
                        }
                        else
                        {
                            income = charge.Amount ?? 0;
                            lastBalance += income;
                            balance = lastBalance;
                        }

                        // Issue #1281: 非同期版を使い UI スレッドブロックを回避
                        var appSettings = await _settingsRepository.GetAppSettingsAsync();
                        var chargeLedger = new Ledger
                        {
                            CardIdm = cardIdm,
                            Date = charge.UseDate ?? date,
                            Summary = SummaryGenerator.GetChargeSummary(appSettings.DepartmentType),
                            Income = income,
                            Expense = 0,
                            Balance = balance,
                            StaffName = null  // チャージは機械操作のため氏名不要
                        };

                        var ledgerId = await _ledgerRepository.InsertAsync(chargeLedger);
                        chargeLedger.Id = ledgerId;

                        charge.LedgerId = ledgerId;
                        await _ledgerRepository.InsertDetailAsync(charge);

                        createdLedgers.Add(chargeLedger);
                    }
                    else if (segment.IsPointRedemption)
                    {
                        // Issue #942: ポイント還元Ledger作成（チャージと同様に個別レコード）
                        var pointDetail = segment.Details[0];
                        int balance;
                        int income;

                        // ポイント還元の金額は負値（カードへの入金）なので絶対値をIncomeとする
                        var rawAmount = pointDetail.Amount ?? 0;
                        income = Math.Abs(rawAmount);

                        if (useCardBalance && pointDetail.Balance.HasValue)
                        {
                            balance = pointDetail.Balance.Value;
                            lastBalance = balance;
                        }
                        else
                        {
                            lastBalance += income;
                            balance = lastBalance;
                        }

                        var pointLedger = new Ledger
                        {
                            CardIdm = cardIdm,
                            Date = pointDetail.UseDate ?? date,
                            Summary = SummaryGenerator.GetPointRedemptionSummary(),
                            Income = income,
                            Expense = 0,
                            Balance = balance,
                            StaffName = null  // ポイント還元は自動処理のため氏名不要
                        };

                        var ledgerId = await _ledgerRepository.InsertAsync(pointLedger);
                        pointLedger.Id = ledgerId;

                        pointDetail.LedgerId = ledgerId;
                        await _ledgerRepository.InsertDetailAsync(pointDetail);

                        createdLedgers.Add(pointLedger);
                    }
                    else
                    {
                        // 利用グループLedger作成
                        var usageDetails = segment.Details;
                        if (usageDetails.Count == 0) continue;

                        // 最初の利用セグメントのみ既存レコードとの統合を試みる
                        var existingUsageLedger = isFirstUsageSegment
                            ? existingUsageLedgers?.LastOrDefault()  // 残高最小（時系列最新）
                            : null;
                        isFirstUsageSegment = false;

                        if (existingUsageLedger != null)
                        {
                            _logger.LogDebug("LendingService: 同一日の既存利用レコードを検出（LedgerId={Id}）、統合します", existingUsageLedger.Id);

                            // 1. 新しい詳細を既存レコードに追加
                            // Issue #880互換: SplitAtChargeBoundariesが時系列順（古い順）で返すため、
                            // 逆順にしてFeliCa互換のrowid順序を維持（小さいrowid＝新しい）
                            await _ledgerRepository.InsertDetailsAsync(existingUsageLedger.Id, usageDetails.AsEnumerable().Reverse());

                            // 2. 全詳細を再読み込み
                            var fullLedger = await _ledgerRepository.GetByIdAsync(existingUsageLedger.Id);
                            var allUsageDetails = fullLedger.Details.Where(d => !d.IsCharge).ToList();

                            // 3. 摘要を再生成（往復検出・乗継統合が全詳細に対して実行される）
                            var summary = _summaryGenerator.Generate(allUsageDetails);

                            // 4. 残高・支出を再計算
                            int balance;
                            int expense;

                            if (useCardBalance)
                            {
                                var latestDetail = allUsageDetails
                                    .Where(d => d.Balance.HasValue)
                                    .OrderBy(d => d.Balance)
                                    .FirstOrDefault();

                                if (latestDetail?.Balance != null)
                                {
                                    balance = latestDetail.Balance.Value;
                                    expense = allUsageDetails.Sum(d => d.Amount ?? 0);
                                    if (expense == 0)
                                    {
                                        expense = lastBalance - balance;
                                        if (expense < 0) expense = 0;
                                    }
                                    lastBalance = balance;
                                }
                                else
                                {
                                    expense = allUsageDetails.Sum(d => d.Amount ?? 0);
                                    lastBalance -= expense;
                                    balance = lastBalance;
                                }
                            }
                            else
                            {
                                expense = allUsageDetails.Sum(d => d.Amount ?? 0);
                                lastBalance -= (expense - existingUsageLedger.Expense);
                                balance = lastBalance;
                            }

                            // 5. 既存レコードを更新
                            fullLedger.Summary = summary;
                            fullLedger.Expense = expense;
                            fullLedger.Balance = balance;
                            if (fullLedger.StaffName == null && staffName != null)
                            {
                                fullLedger.StaffName = staffName;
                            }
                            await _ledgerRepository.UpdateAsync(fullLedger);

                            createdLedgers.Add(fullLedger);
                        }
                        else
                        {
                            // 新規作成
                            int balance;
                            int expense;

                            if (useCardBalance)
                            {
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
                                        expense = lastBalance - balance;
                                        if (expense < 0) expense = 0;
                                    }
                                    lastBalance = balance;
                                }
                                else
                                {
                                    expense = usageDetails.Sum(d => d.Amount ?? 0);
                                    lastBalance -= expense;
                                    balance = lastBalance;
                                }
                            }
                            else
                            {
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
                                StaffName = usageDetails.All(d => d.IsPointRedemption) ? null : staffName
                            };

                            var ledgerId = await _ledgerRepository.InsertAsync(usageLedger);
                            usageLedger.Id = ledgerId;

                            // Issue #880互換: 挿入順を逆にしてFeliCa互換のrowid順序を維持
                            await _ledgerRepository.InsertDetailsAsync(ledgerId, usageDetails.AsEnumerable().Reverse());

                            createdLedgers.Add(usageLedger);
                        }
                    }
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
        /// <see cref="LendingHistoryAnalyzer.DetectInsufficientBalancePattern"/> に委譲。
        /// </remarks>
        internal static List<(LedgerDetail Charge, LedgerDetail Usage)> DetectInsufficientBalancePattern(
            List<LedgerDetail> dailyDetails)
            => LendingHistoryAnalyzer.DetectInsufficientBalancePattern(dailyDetails);

        /// <summary>
        /// 同一日の履歴を時系列順に並べ、チャージの位置で利用グループを分割する。
        /// </summary>
        /// <remarks>
        /// <see cref="LendingHistoryAnalyzer.SplitAtChargeBoundaries"/> に委譲。
        /// </remarks>
        internal static List<LendingHistoryAnalyzer.DailySegment> SplitAtChargeBoundaries(List<LedgerDetail> dailyDetails)
            => LendingHistoryAnalyzer.SplitAtChargeBoundaries(dailyDetails);

        /// <summary>
        /// 残高チェーンに基づいて詳細を時系列順（古い順）に並べ替える。
        /// </summary>
        /// <remarks>
        /// <see cref="LendingHistoryAnalyzer.SortChronologically"/> に委譲。
        /// </remarks>
        internal static List<LedgerDetail> SortChronologically(List<LedgerDetail> details)
            => LendingHistoryAnalyzer.SortChronologically(details);

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
            return elapsed.TotalSeconds <= _retouchTimeoutSeconds;
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
                using var scope = await _dbContext.BeginTransactionAsync();

                try
                {
                    // 既存のCreateUsageLedgersAsyncを利用（staffNameはnull: 登録時には利用者情報がないため）
                    var createdLedgers = await CreateUsageLedgersAsync(cardIdm, null, filtered);

                    scope.Commit();

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
                    scope.Rollback();
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
        protected virtual int GetLockTimeoutMs() => _lockTimeoutMs;

        /// <summary>
        /// Issue #1110: 例外をユーザー向けエラーメッセージに変換
        /// </summary>
        /// <remarks>
        /// SQLiteの技術的なエラーメッセージ（SQLITE_BUSY等）をユーザーが理解できる
        /// メッセージに変換する。共有モードでの一般的なエラーシナリオをカバーする。
        /// </remarks>
        internal static string GetUserFriendlyErrorMessage(Exception ex, string operationName)
        {
            if (ex is System.Data.SQLite.SQLiteException sqliteEx)
            {
                switch (sqliteEx.ResultCode)
                {
                    case System.Data.SQLite.SQLiteErrorCode.Busy:
                        return $"他のPCと処理が競合しています。しばらく待ってから再度{operationName}をお試しください。";
                    case System.Data.SQLite.SQLiteErrorCode.Locked:
                        return $"データベースがロックされています。しばらく待ってから再度{operationName}をお試しください。";
                    case System.Data.SQLite.SQLiteErrorCode.IoErr:
                        return $"ネットワーク共有フォルダへの接続に失敗しました。ネットワーク接続を確認してください。";
                }
            }

            if (ex is System.IO.IOException)
            {
                return $"ネットワーク共有フォルダへの接続に失敗しました。ネットワーク接続を確認してください。";
            }

            return $"{operationName}処理でエラーが発生しました: {ex.Message}";
        }

        /// <summary>
        /// カードから読み取った履歴の完全性をチェック
        /// </summary>
        /// <remarks>
        /// <see cref="LendingHistoryAnalyzer.CheckHistoryCompleteness"/> に委譲。
        /// </remarks>
        internal static bool CheckHistoryCompleteness(IList<LedgerDetail> rawDetails, DateTime currentMonthStart)
            => LendingHistoryAnalyzer.CheckHistoryCompleteness(rawDetails, currentMonthStart);
    }
}
