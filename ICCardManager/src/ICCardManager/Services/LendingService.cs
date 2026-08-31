using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;

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

        /// <summary>
        /// 台帳への記録は確定した（<see cref="Success"/> = true）が、コミット確定後の付帯情報の取得に
        /// 失敗したかどうか（Issue #1805。返却時のみ）
        /// </summary>
        /// <remarks>
        /// <para>
        /// true のとき <see cref="Balance"/> / <see cref="IsLowBalance"/> / <see cref="WarningBalance"/> は
        /// 信頼できない（既定値のまま、または途中まで解決された値）。呼び出し元は残額を表示せず、
        /// 「記録済み」であることと「再タッチしないこと」を案内する（再タッチは30秒ルールの逆処理として扱われる）。
        /// </para>
        /// <para>
        /// <see cref="Success"/> は「台帳への記録が確定した」ことだけを表す。コミット後の後処理の失敗で
        /// <see cref="Success"/> を落とすと、記録済みなのに「失敗・再タッチ」と案内され、案内どおりの
        /// 再タッチが貸出として新規に記録される（<c>.claude/rules/development-conventions.md</c>
        /// 「コミット確定後の後処理を、成否の判定に巻き込まない」）。
        /// </para>
        /// </remarks>
        public bool HasPostCommitFailure { get; set; }
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

        /// <summary>
        /// 失敗した理由（Issue #1727。<see cref="Success"/> が false のときのみ設定される）
        /// </summary>
        /// <remarks>
        /// <para>
        /// エラーメッセージの「なぜ」だけを保持する。「何が」「どうすれば」を含まないのは、
        /// 復旧手段を知っているのが呼び出し元だから。カード登録直後であれば
        /// 「CSVインポートで補完する」が正解だが、「しばらく待ってから再度実行してください」は
        /// 誤り（カード行は既に登録済みで、同じ操作をやり直せない）。
        /// 呼び出し元が 3 要素（何が／なぜ／どうすれば）に組み立てて表示すること。
        /// </para>
        /// <para>
        /// 生の <see cref="Exception.Message"/> は含めない（Issue #1614）。
        /// </para>
        /// </remarks>
        public string FailureReason { get; set; }
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
        private readonly ISystemClock _clock;
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
            ILogger<LendingService> logger,
            ISystemClock clock = null)
        {
            _dbContext = dbContext;
            _cardRepository = cardRepository;
            _staffRepository = staffRepository;
            _ledgerRepository = ledgerRepository;
            _settingsRepository = settingsRepository;
            _summaryGenerator = summaryGenerator;
            _lockManager = lockManager;
            // 既定はシステム時計（DateTime.Now）。テストでは固定時計を注入して
            // 30秒ルール（IsRetouchWithinTimeout）の境界を決定論的に検証する（Issue #1626）
            _clock = clock ?? new SystemClock();
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
            // Issue #1239: 共有モードで他PCの同時操作と競合しないよう、
            // READ（カード一覧 + 貸出レコード）と UPDATE を同一トランザクション内で実行する。
            // トランザクション内ではSQLiteのスナップショット分離により一貫した状態が読める。
            var repairCount = 0;

            await _dbContext.ExecuteWithRetryAsync(async () =>
            {
                using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);

                try
                {
                    var cards = await _cardRepository.GetAllAsync().ConfigureAwait(false);
                    var lentRecords = await _ledgerRepository.GetAllLentRecordsAsync().ConfigureAwait(false);

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

                    repairCount = 0;

                    foreach (var card in cards)
                    {
                        var hasLentRecord = lentRecordMap.TryGetValue(card.CardIdm, out var lentRecord);

                        if (hasLentRecord && !card.IsLent)
                        {
                            // 貸出中レコードがあるのにis_lent=0 → is_lent=1に修復
                            // Issue #1953: 影響行数 0（他 PC がこのカードを論理削除した）は修復ではない。
                            // 数えると DB が変わっていないのに「N件修復しました」と報告し、
                            // 不整合が残ったまま解決済みに見える。
                            var repaired = await _cardRepository.UpdateLentStatusAsync(
                                card.CardIdm, true, lentRecord.LentAt, lentRecord.LenderIdm).ConfigureAwait(false);
                            if (!repaired)
                            {
                                LogRepairConflict(IdmMasker.Mask(card.CardIdm), "0→1");
                                continue;
                            }
                            _logger.LogWarning(
                                "Issue #790: 貸出状態の不整合を修復しました（is_lent: 0→1）: CardIdm={CardIdm}, LentAt={LentAt}",
                                IdmMasker.Mask(card.CardIdm), lentRecord.LentAt);
                            repairCount++;
                        }
                        else if (!hasLentRecord && card.IsLent)
                        {
                            // 貸出中レコードがないのにis_lent=1 → is_lent=0に修復
                            // Issue #1953: 影響行数 0 は修復ではない（0→1 の分岐と同じ理由）。
                            var repaired = await _cardRepository.UpdateLentStatusAsync(
                                card.CardIdm, false, null, null).ConfigureAwait(false);
                            if (!repaired)
                            {
                                LogRepairConflict(IdmMasker.Mask(card.CardIdm), "1→0");
                                continue;
                            }
                            _logger.LogWarning(
                                "Issue #790: 貸出状態の不整合を修復しました（is_lent: 1→0）: CardIdm={CardIdm}",
                                IdmMasker.Mask(card.CardIdm));
                            repairCount++;
                        }
                    }

                    scope.Commit();
                }
                catch
                {
                    // Issue #1831: 素の Rollback() を呼ばない（二次例外が本来の SQLITE_BUSY を
                    // 置き換えると ExecuteWithRetryAsync のリトライが効かなくなる）
                    SafeRollback.TryRollback(() => scope.Rollback(), _logger, "貸出状態の整合性修復");
                    throw;
                }
            }).ConfigureAwait(false);

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
        /// <param name="lentAt">
        /// 貸出日時。null の場合は現在時刻（＝物理タッチ経路）。
        /// Issue #1909: システム操作による貸出記録作成では、実際にカードを持ち出した日時を指定できる。
        /// 指定した場合は <see cref="ValidateSystemLendDateTime"/> で妥当性を検証する。
        /// </param>
        /// <param name="armRetouchWindow">
        /// 30秒ルール（<see cref="IsRetouchWithinTimeout"/>）を武装するかどうか。既定は true（物理タッチ経路）。
        /// Issue #1909: システム操作による貸出記録作成では false を指定する。物理タッチが 1 度も起きていないため
        /// 再タッチ窓を開く根拠が無く、武装すると借用者が 30 秒以内に戻ってタッチしたときに
        /// 「返却」ではなく「貸出の逆処理（＝作成した記録の取り消し）」が走ってしまう。
        /// </param>
        public async Task<LendingResult> LendAsync(
            string staffIdm,
            string cardIdm,
            int? balance = null,
            DateTime? lentAt = null,
            bool armRetouchWindow = true)
        {
            var result = new LendingResult { OperationType = LendingOperationType.Lend };

            // カードごとのロックを取得
            var cardLock = _lockManager.GetLock(cardIdm);
            var lockAcquired = false;

            try
            {
                // タイムアウト付きでロックを取得
                lockAcquired = await cardLock.WaitAsync(GetLockTimeoutMs()).ConfigureAwait(false);
                if (!lockAcquired)
                {
                    result.ErrorMessage = "他の処理が実行中です。しばらく待ってから再度お試しください。";
                    return result;
                }

                var (card, staff, validationError) = await ValidateLendPreconditionsAsync(staffIdm, cardIdm).ConfigureAwait(false);
                if (validationError != null)
                {
                    result.ErrorMessage = validationError;
                    return result;
                }

                var now = _clock.Now;

                // Issue #1909: 貸出日時を任意指定する経路（システム操作）だけ妥当性を検証する。
                // 物理タッチ経路（lentAt = null）は現在時刻がそのまま使われるため検証不要で、
                // 直近履歴の追加クエリもここでは発行しない。
                var effectiveLentAt = lentAt ?? now;
                Ledger latestLedger = null;
                if (lentAt.HasValue)
                {
                    latestLedger = await _ledgerRepository.GetLatestLedgerAsync(cardIdm).ConfigureAwait(false);
                    var dateError = ValidateSystemLendDateTime(lentAt.Value, now, latestLedger?.Date);
                    if (dateError != null)
                    {
                        result.ErrorMessage = dateError;
                        return result;
                    }
                }

                // Issue #656: カードから残高を読み取れなかった場合、直近の履歴から残高を取得
                // READ操作はリトライ範囲の外で実行（不要な再クエリを防止）
                var currentBalance = await ResolveInitialBalanceAsync(cardIdm, balance, latestLedger).ConfigureAwait(false);

                // トランザクション内で貸出ledger作成 + カード状態更新
                // 共有モード時のSQLITE_BUSY対策としてリトライでラップ（WRITE操作のみ）
                var ledger = await InsertLendLedgerAsync(cardIdm, staffIdm, staff.Name, currentBalance, effectiveLentAt).ConfigureAwait(false);
                result.CreatedLedgers.Add(ledger);

                // 処理情報を記録（Issue #1909: システム操作では武装しない。
                // 記録の基準時刻は「操作が行われた現在時刻」であって、遡って指定された貸出日時ではない）
                if (armRetouchWindow)
                {
                    LastProcessedCardIdm = cardIdm;
                    LastProcessedTime = now;
                    LastOperationType = LendingOperationType.Lend;
                }

                result.Success = true;
                result.Balance = currentBalance;
            }
            catch (Exception ex)
            {
                // Issue #1734: トースト通知は数秒で消えるため、失敗の事実と原因（例外種別・スタックトレース）を
                // 本番ログへ必ず残す。LogDebug では既定の Logging:LogLevel=Information によりファイル出力されない
                _logger.LogError(ex, "貸出処理に失敗しました（CardIdm={CardIdm}）", IdmMasker.Mask(cardIdm));
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
                using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);

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

                    // Issue #1481: ledger 書込みは scope の SQLiteTransaction に「暗黙参加」する。
                    // 同一 SQLiteConnection 上で BEGIN 発行後のコマンドは autocommit にならず、
                    // 当該トランザクションに参加するため SMB 切断時にも ALL OR NOTHING が保たれる。
                    // 明示的な tx 引数渡しは新オーバーロード（Issue #1481）として整備済みだが、
                    // 既存テスト（Mock<ILedgerRepository> の引数1版 Setup）との互換のため当面は引数1版経由で呼ぶ。
                    var ledgerId = await _ledgerRepository.InsertAsync(ledger).ConfigureAwait(false);
                    ledger.Id = ledgerId;

                    // Issue #1953: 影響行数 0（WHERE is_deleted = 0 に一致しない）は競合であり、
                    // 握りつぶすと ledger に貸出中レコードだけが入り is_lent = 0 のままコミットされる。
                    // 手元に無いカードが次のタッチで新規貸出として再記録されるため、
                    // 例外でトランザクションごと巻き戻す（BusinessException は SQLITE_BUSY ではないので
                    // ExecuteWithRetryAsync のリトライ対象にならず、そのまま LendAsync の catch へ届く）。
                    var lentStatusUpdated = await _cardRepository
                        .UpdateLentStatusAsync(cardIdm, true, now, staffIdm).ConfigureAwait(false);
                    if (!lentStatusUpdated)
                    {
                        throw BusinessException.LentStatusUpdateConflict(cardIdm, "貸出");
                    }

                    scope.Commit();
                    createdLedger = ledger;
                }
                catch
                {
                    // Issue #1831: 素の Rollback() を呼ばない（二次例外が本来の SQLITE_BUSY を
                    // 置き換えると ExecuteWithRetryAsync のリトライが効かず、共有モードの一過性の
                    // 競合で貸出が一発失敗する。#1734 で足した LogError も実行されない）
                    SafeRollback.TryRollback(() => scope.Rollback(), _logger, "貸出の記録");
                    throw;
                }
            }).ConfigureAwait(false);

            return createdLedger;
        }

        /// <summary>
        /// Issue #656: カードから残高を読み取れなかった場合、直近の ledger 残高を fallback として使用。
        /// </summary>
        internal async Task<int> ResolveInitialBalanceAsync(
            string cardIdm, int? balance, Ledger prefetchedLatestLedger = null)
        {
            if (balance.HasValue)
            {
                return balance.Value;
            }

            // Issue #1909: 呼び出し元が直近履歴を既に読んでいる場合（貸出日時の検証）は
            // 同じクエリを 2 度発行しない。共有モードでは 1 往復が SMB のレイテンシ分だけ効く。
            var latestLedger = prefetchedLatestLedger
                ?? await _ledgerRepository.GetLatestLedgerAsync(cardIdm).ConfigureAwait(false);
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
            var card = await _cardRepository.GetByIdmAsync(cardIdm).ConfigureAwait(false);
            if (card == null)
            {
                return (null, null, "カードが登録されていません。");
            }

            if (card.IsLent)
            {
                return (card, null, "このカードは既に貸出中です。");
            }

            var staff = await _staffRepository.GetByIdmAsync(staffIdm).ConfigureAwait(false);
            if (staff == null)
            {
                return (card, null, "職員証が登録されていません。");
            }

            return (card, staff, null);
        }

        /// <summary>
        /// Issue #1909: システム操作で指定された貸出日時の妥当性を検証する。
        /// </summary>
        /// <param name="lentAt">利用者が指定した貸出日時</param>
        /// <param name="now">現在時刻</param>
        /// <param name="latestLedgerDate">対象カードの直近履歴の日付。履歴が 1 件も無い場合は null</param>
        /// <returns>問題が無ければ null。問題があれば「何が／なぜ／どうすれば」を含む案内文言</returns>
        /// <remarks>
        /// <para>
        /// 下限を「直近履歴の日付」に置くのは、貸出中レコードが残高チェーンの途中へ古い残額のまま
        /// 割り込むのを防ぐため。貸出中レコードは <c>Income = Expense = 0</c> で
        /// 直近の残額をそのまま持つため、履歴の途中に入ると利用者には残額が戻ったように見える。
        /// </para>
        /// <para>
        /// DB もモックも介さない純関数にしてあるのは、境界（現在時刻ちょうど・直近履歴ちょうど）を
        /// 決定論的に固定するため（<c>development-conventions.md</c>「判断を純関数へ切り出す」）。
        /// </para>
        /// </remarks>
        internal static string ValidateSystemLendDateTime(DateTime lentAt, DateTime now, DateTime? latestLedgerDate)
        {
            if (lentAt > now)
            {
                return $"貸出日時に未来の日時（{lentAt.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}）が指定されています。" +
                       $"カードを持ち出した日時が現在時刻（{now.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}）より後になることはありません。" +
                       "現在時刻以前の日時を入力してください。";
            }

            if (latestLedgerDate.HasValue && lentAt < latestLedgerDate.Value)
            {
                return $"貸出日時（{lentAt.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}）が、このカードの直近の履歴の日付" +
                       $"（{latestLedgerDate.Value.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}）より前です。" +
                       "貸出中の記録が履歴の途中に入ると、残額の並びが実際と食い違って表示されます。" +
                       $"{latestLedgerDate.Value.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)} 以降の日時を入力してください。";
            }

            return null;
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
            // Issue #1733: ExecuteWithRetryAsync はラムダ全体を SQLITE_BUSY/LOCKED で再実行するため、
            // ラムダ内で result へ AddRange するとロールバック済みの試行分が累積する（バス停入力ダイアログの
            // 二重表示につながる）。ローカル変数は試行ごとに代入で上書きされる（冪等）ため、
            // result への反映はリトライ境界の外で成功した最終試行の分だけを行う（LendAsync と同じ配置）。
            List<Ledger> createdLedgers = null;

            await _dbContext.ExecuteWithRetryAsync(async () =>
            {
                using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);

                try
                {
                    // Issue #1481: ledger ヘッダ＋複数 detail ＋貸出レコード削除＋カード状態解除を単一トランザクションに束ねる。
                    // 内部の Insert/Update は同一 SQLiteConnection 上で BEGIN 後に発行されるため暗黙参加する。
                    // 注意: Issue #1575 で LedgerRepository.InsertDetailsAsync(1 引数版) が外側 tx 中の再入を
                    // 自己検知する設計（DbContext.HasActiveTransactionScope）に変更されたため、ここで明示的に
                    // transaction を伝搬しなくてもデッドロックしない。
                    createdLedgers = await CreateUsageLedgersAsync(
                        cardIdm, lentRecord.LenderIdm, lentRecord.StaffName ?? string.Empty, usageSinceLent, skipDuplicateCheck).ConfigureAwait(false);

                    // 貸出レコードをすべて削除（履歴に「（貸出中）」が残らないようにする）
                    // 共有モードで重複した貸出中レコードがある場合にも対応
                    await _ledgerRepository.DeleteAllLentRecordsAsync(cardIdm).ConfigureAwait(false);

                    // Issue #1953: 影響行数 0 は競合。握りつぶすと貸出中レコードだけが消えて
                    // is_lent = 1 が残り、返却済みカードが長期未返却として督促され続ける。
                    var lentStatusUpdated = await _cardRepository
                        .UpdateLentStatusAsync(cardIdm, false, null, null).ConfigureAwait(false);
                    if (!lentStatusUpdated)
                    {
                        throw BusinessException.LentStatusUpdateConflict(cardIdm, "返却");
                    }

                    scope.Commit();
                }
                catch
                {
                    // Issue #1831: 素の Rollback() を呼ばない（二次例外が本来の SQLITE_BUSY を
                    // 置き換えるとリトライが効かず、返却が一発失敗する。職員は案内どおり再タッチし、
                    // is_lent=0 のため手元に無いカードが新規の貸出として記録される）
                    SafeRollback.TryRollback(() => scope.Rollback(), _logger, "返却の記録");
                    throw;
                }
            }).ConfigureAwait(false);

            result.CreatedLedgers.AddRange(createdLedgers);
            result.HasBusUsage = usageSinceLent.Any(d => d.IsBus);
        }

        /// <summary>
        /// 低残高警告情報を result にセットする。
        /// </summary>
        internal async Task ApplyBalanceWarningAsync(LendingResult result)
        {
            var settings = await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false);
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

            var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(cardIdm).ConfigureAwait(false);
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
            var lentRecord = await _ledgerRepository.GetLentRecordAsync(cardIdm).ConfigureAwait(false);
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
            var card = await _cardRepository.GetByIdmAsync(cardIdm).ConfigureAwait(false);
            if (card == null)
            {
                return (null, null, "カードが登録されていません。");
            }

            if (!card.IsLent)
            {
                return (card, null, "このカードは貸出されていません。");
            }

            var returner = await _staffRepository.GetByIdmAsync(staffIdm).ConfigureAwait(false);
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
        /// <item><description>カードの貸出状態を解除（ここまでが単一トランザクション。コミットで返却が確定）</description></item>
        /// <item><description>30秒ルール用の処理情報を記録し <see cref="LendingResult.Success"/> を確定（Issue #1805）</description></item>
        /// <item><description>残額の解決と残額警告チェック（コミット後の付帯情報）</description></item>
        /// </list>
        /// <para>
        /// <see cref="LendingResult.HasBusUsage"/> でバス利用の有無を確認できます。
        /// バス利用がある場合は、呼び出し元でバス停名入力ダイアログを表示してください。
        /// </para>
        /// <para>
        /// Issue #1805: コミット後の付帯情報の取得（残額・残額警告）で例外が出ても
        /// <see cref="LendingResult.Success"/> は true のまま返り、
        /// <see cref="LendingResult.HasPostCommitFailure"/> が true になります。
        /// 呼び出し元は残額を表示せず「記録済み・再タッチしない」ことを案内してください。
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
                lockAcquired = await cardLock.WaitAsync(GetLockTimeoutMs()).ConfigureAwait(false);
                if (!lockAcquired)
                {
                    result.ErrorMessage = "他の処理が実行中です。しばらく待ってから再度お試しください。";
                    return result;
                }

                var (card, returner, validationError) = await ValidateReturnPreconditionsAsync(staffIdm, cardIdm).ConfigureAwait(false);
                if (validationError != null)
                {
                    result.ErrorMessage = validationError;
                    return result;
                }

                var (lentRecord, lentRecordError) = await ResolveLentRecordAsync(cardIdm).ConfigureAwait(false);
                if (lentRecordError != null)
                {
                    result.ErrorMessage = lentRecordError;
                    return result;
                }

                var now = _clock.Now;
                var detailList = usageDetails.ToList();

                _logger.LogDebug("LendingService: 返却処理 - 受け取った履歴件数={Count}", detailList.Count);

                // 貸出タッチを忘れた場合でも履歴が正しく記録されるよう、日付フィルタを緩和
                // 重複チェックは CreateUsageLedgersAsync 内の既存履歴照合（Issue #326）で行う
                var usageSinceLent = FilterUsageSinceLent(detailList, lentRecord, now);

                var lentAt = lentRecord.LentAt ?? now.AddDays(-1);
                _logger.LogDebug("LendingService: 貸出時刻={LentAt}, フィルタ開始日={FilterStart}, 抽出後の履歴件数={Count}",
                    SqliteDateTimeFormat.ToText(lentAt), SqliteDateTimeFormat.ToDateText(lentAt.Date.AddDays(-7)), usageSinceLent.Count);

                // 履歴データの詳細をログ出力
                foreach (var detail in usageSinceLent.Take(5))
                {
                    _logger.LogDebug("LendingService: 履歴詳細 - 日付={Date}, 残高={Balance}, 金額={Amount}, チャージ={IsCharge}",
                        SqliteDateTimeFormat.ToDateText(detail.UseDate), detail.Balance, detail.Amount, detail.IsCharge);
                }

                // Issue #596: 今月の履歴完全性チェック（トランザクション前に既存レコードを確認）
                var currentMonthStart = new DateTime(now.Year, now.Month, 1);
                var existingMonthRecords = await _ledgerRepository.GetByMonthAsync(cardIdm, now.Year, now.Month).ConfigureAwait(false);
                var hadExistingCurrentMonthRecords = existingMonthRecords
                    .Any(l => !l.IsLentRecord);

                // トランザクション内で履歴作成 + 貸出レコード削除 + カード状態更新
                await PersistReturnAsync(cardIdm, lentRecord, usageSinceLent, skipDuplicateCheck, result).ConfigureAwait(false);

                // Issue #1805: ここから先は台帳への記録が確定している。
                // 30秒ルール用の処理情報と Success は後処理（残高解決・残額警告の DB I/O）より前に確定させる。
                // 後処理より後に置くと、後処理の例外で「返却失敗・再タッチ」と案内され、案内どおりの
                // 再タッチが（is_lent=0 のため）貸出として新規に記録される。以降で例外が出ても
                // 下の catch (when result.Success) が付帯情報の欠落として扱い、Success は落とさない。
                LastProcessedCardIdm = cardIdm;
                LastProcessedTime = now;
                LastOperationType = LendingOperationType.Return;

                result.Success = true;

                // Issue #1819: 返却は記録されたのに台帳行が 1 行も作られなかったことを本番ログへ残す。
                // 内訳（重複除外・貸出後フィルタ）は LogDebug で本番に出ないため、
                // 「返却したのに履歴が増えない」という問い合わせの切り分けに必要な値をここへ集約する。
                if (result.CreatedLedgers.Count == 0)
                {
                    _logger.LogInformation(
                        "LendingService: 返却を記録しましたが台帳行は作成されませんでした" +
                        "（CardIdm={CardIdm}, 受け取った履歴件数={ReceivedCount}, 貸出後の抽出件数={FilteredCount}, 重複チェック省略={SkipDuplicateCheck}）",
                        IdmMasker.Mask(cardIdm), detailList.Count, usageSinceLent.Count, skipDuplicateCheck);
                }

                // Issue #596: 今月の履歴が不完全な可能性をチェック（純粋計算。DB I/O なし）
                if (!hadExistingCurrentMonthRecords)
                {
                    result.MayHaveIncompleteHistory = CheckHistoryCompleteness(detailList, currentMonthStart);
                }

                // 残額チェック（トランザクション外）
                // カードから直接読み取った残高を優先（履歴の先頭が最新）
                // FelicaCardReaderで読み取った場合、各LedgerDetail.Balanceには実際の残高が設定されている
                result.Balance = await ResolveReturnBalanceAsync(detailList, result.CreatedLedgers, cardIdm).ConfigureAwait(false);

                await ApplyBalanceWarningAsync(result).ConfigureAwait(false);
            }
            catch (Exception ex) when (result.Success)
            {
                // Issue #1805: 台帳への記録は確定済み。付帯情報（残額・残額警告）が得られなかっただけとして扱い、
                // Success と ErrorMessage は変えない（呼び出し元は HasPostCommitFailure で残額表示を抑止する）。
                // 返却自体は成功しているため Error ではなく Warning。本番の Logging:LogLevel=Information でも出力される
                _logger.LogWarning(ex,
                    "返却は記録済みですが、コミット後の付帯情報（残額・残額警告）の取得に失敗しました（CardIdm={CardIdm}）",
                    IdmMasker.Mask(cardIdm));
                result.HasPostCommitFailure = true;
            }
            catch (Exception ex)
            {
                // Issue #1734: トースト通知は数秒で消えるため、失敗の事実と原因（例外種別・スタックトレース）を
                // 本番ログへ必ず残す。LogDebug では既定の Logging:LogLevel=Information によりファイル出力されない
                _logger.LogError(ex, "返却処理に失敗しました（CardIdm={CardIdm}）", IdmMasker.Mask(cardIdm));
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
        /// <summary>
        /// Issue #1481: transaction が非 null なら新オーバーロード、null なら既存オーバーロードを呼ぶ。
        /// </summary>
        /// <remarks>
        /// 既存テストの <c>Mock&lt;ILedgerRepository&gt;</c> は引数1版のみ <c>Setup</c> 済みのため、
        /// テスト経路（tx=null 想定）では引数1版を呼んで Setup と一致させる。
        /// </remarks>
        private Task<int> InsertLedgerInTransactionAsync(Ledger ledger, SQLiteTransaction transaction)
            => transaction != null ? _ledgerRepository.InsertAsync(ledger, transaction) : _ledgerRepository.InsertAsync(ledger);

        private Task<bool> UpdateLedgerInTransactionAsync(Ledger ledger, SQLiteTransaction transaction)
            => transaction != null ? _ledgerRepository.UpdateAsync(ledger, transaction) : _ledgerRepository.UpdateAsync(ledger);

        private Task<bool> InsertDetailInTransactionAsync(LedgerDetail detail, SQLiteTransaction transaction)
            => transaction != null ? _ledgerRepository.InsertDetailAsync(detail, transaction) : _ledgerRepository.InsertDetailAsync(detail);

        private Task<bool> InsertDetailsInTransactionAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction)
            => transaction != null ? _ledgerRepository.InsertDetailsAsync(ledgerId, details, transaction) : _ledgerRepository.InsertDetailsAsync(ledgerId, details);

        private async Task<List<Ledger>> CreateUsageLedgersAsync(
            string cardIdm, string staffIdm, string staffName, List<LedgerDetail> details, bool skipDuplicateCheck = false,
            SQLiteTransaction transaction = null)
        {
            // Issue #1481: transaction を内部 Repository 呼び出し全てに伝搬してトランザクション境界を明示。
            // tx=null の経路（テスト等）では引数1版にフォールバックする。
            Task<int> InsertLedger(Ledger l) => InsertLedgerInTransactionAsync(l, transaction);
            Task<bool> InsertDetail(LedgerDetail d) => InsertDetailInTransactionAsync(d, transaction);
            Task<bool> InsertDetails(int lid, IEnumerable<LedgerDetail> ds) => InsertDetailsInTransactionAsync(lid, ds, transaction);
            Task<bool> UpdateLedger(Ledger l) => UpdateLedgerInTransactionAsync(l, transaction);

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

                var existingKeys = await _ledgerRepository.GetExistingDetailKeysAsync(cardIdm, oldestDate).ConfigureAwait(false);

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
                dateGroups.Count, string.Join(", ", dateGroups.Select(g => SqliteDateTimeFormat.ToDateText(g.Key))));

            // カードから読み取った残高を優先的に使用
            // 履歴データには各取引後の残高が含まれているため、これを直接使用する
            // フォールバック: データベースの最終残高（履歴が取得できなかった場合用）
            var useCardBalance = details.Any(d => d.Balance.HasValue && d.Balance.Value > 0);
            _logger.LogDebug("LendingService: カード残高使用={UseCardBalance}", useCardBalance);

            // フォールバック用: データベースから前回の残高を取得
            var lastBalance = await GetLastBalanceAsync(cardIdm).ConfigureAwait(false);

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
                    // Issue #1735: 駅名が解決できず摘要を生成できない場合も、摘要が空欄の台帳行を保存しない
                    var summary = _summaryGenerator.Generate(new List<LedgerDetail> { usage });
                    if (string.IsNullOrEmpty(summary))
                    {
                        summary = SummaryGenerator.GetUnknownUsageSummary();
                    }
                    var note = SummaryGenerator.GetInsufficientBalanceNote(totalFare, shortfall);

                    var mergedLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = usage.UseDate ?? date,
                        Summary = summary,
                        Income = 0,
                        Expense = expense,   // 運賃 - チャージ額（カードから充当した金額）
                        Balance = usage.Balance ?? 0,  // 利用後の実残高（端数チャージの場合は端数が残る）
                        LenderIdm = staffIdm,  // Issue #1303
                        StaffName = staffName,
                        Note = note
                    };

                    var ledgerId = await InsertLedger(mergedLedger).ConfigureAwait(false);
                    mergedLedger.Id = ledgerId;

                    // Issue #978: チャージ詳細と利用詳細の両方を登録
                    // チャージ詳細も登録しないと重複チェック（GetExistingDetailKeysAsync）で
                    // 検出されず、次回返却時にチャージが再処理されてしまう
                    //
                    // Issue #1822: 挿入順は「チャージ → 利用」で、rowid ベースの SequenceNumber は
                    // 利用側が大きくなる。これは LedgerDetail.SequenceNumber の既定規約
                    // （FeliCa 互換で小さい値ほど新しい。下の通常経路が Reverse() で維持している）の
                    // 例外にあたる。残高不足マージで作る台帳の残高は上の mergedLedger.Balance で
                    // 利用後の実残高を直接持たせており、明細の並びからは決めていないため実害はない。
                    //
                    // Issue #1932: この台帳を履歴統合の対象にしたときの残額選択は
                    // LedgerMergeService.OrderChronologically が担う。同メソッドは同一日内の順序を
                    // まず残高チェーンで解き、解けないときだけ SequenceNumber の規約へ倒すため、
                    // 規約の例外であるこの並び（チャージ → 利用）でも利用後の実残高が選ばれる。
                    // ここの挿入順を変えるときは同メソッドの対のテストを確認すること。
                    charge.LedgerId = ledgerId;
                    await InsertDetail(charge).ConfigureAwait(false);
                    usage.LedgerId = ledgerId;
                    await InsertDetail(usage).ConfigureAwait(false);

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
                // Issue #1723: 繰越レコード（「新規購入」「○月から繰越」）は統合対象から除外する。
                //   年度途中繰越の繰越行は Income=0・Note=null・StaffName=null で importFromDate と
                //   同日に作成されるため、登録時インポート（staffName=null）で他条件を全て満たして
                //   しまい、統合すると期首残高行が利用行に上書きされて消滅する
                List<Ledger> existingUsageLedgers = null;
                var hasUsageSegment = segments.Any(s => !s.IsCharge);
                if (hasUsageSegment)
                {
                    var existingLedgers = await _ledgerRepository.GetByDateRangeAsync(cardIdm, date, date).ConfigureAwait(false);
                    existingUsageLedgers = existingLedgers
                        .Where(l => !l.IsLentRecord && !l.IsCarryover && l.Income == 0
                                    && string.IsNullOrEmpty(l.Note)
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
                        var appSettings = await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false);
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

                        var ledgerId = await InsertLedger(chargeLedger).ConfigureAwait(false);
                        chargeLedger.Id = ledgerId;

                        charge.LedgerId = ledgerId;
                        await InsertDetail(charge).ConfigureAwait(false);

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

                        var ledgerId = await InsertLedger(pointLedger).ConfigureAwait(false);
                        pointLedger.Id = ledgerId;

                        pointDetail.LedgerId = ledgerId;
                        await InsertDetail(pointDetail).ConfigureAwait(false);

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
                            await InsertDetails(existingUsageLedger.Id, usageDetails.AsEnumerable().Reverse()).ConfigureAwait(false);

                            // 2. 全詳細を再読み込み
                            var fullLedger = await _ledgerRepository.GetByIdAsync(existingUsageLedger.Id).ConfigureAwait(false);
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
                            // Issue #1735: 再生成した摘要が空なら既存の摘要を維持する（CsvImportService.Detail と
                            // 同じガード）。既存も空なら代替文言で補い、摘要が空欄の台帳行を残さない
                            fullLedger.Summary = !string.IsNullOrEmpty(summary) ? summary
                                : !string.IsNullOrEmpty(fullLedger.Summary) ? fullLedger.Summary
                                : SummaryGenerator.GetUnknownUsageSummary();
                            fullLedger.Expense = expense;
                            fullLedger.Balance = balance;
                            // Issue #1303: 既存レコードの利用者情報が欠落していれば現在のタッチ者で補完
                            if (fullLedger.StaffName == null && staffName != null)
                            {
                                fullLedger.StaffName = staffName;
                            }
                            if (string.IsNullOrEmpty(fullLedger.LenderIdm) && !string.IsNullOrEmpty(staffIdm))
                            {
                                fullLedger.LenderIdm = staffIdm;
                            }
                            await UpdateLedger(fullLedger).ConfigureAwait(false);

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
                            // Issue #1735: 駅名が解決できず摘要を生成できない場合も、摘要が空欄の台帳行を保存しない
                            if (string.IsNullOrEmpty(summary))
                            {
                                summary = SummaryGenerator.GetUnknownUsageSummary();
                            }

                            var usageLedger = new Ledger
                            {
                                CardIdm = cardIdm,
                                Date = usageDetails.FirstOrDefault()?.UseDate ?? date,
                                Summary = summary,
                                Income = 0,
                                Expense = expense,
                                Balance = balance,
                                // Issue #1303: ポイント還元のみは機械操作扱いで LenderIdm/StaffName ともに null
                                LenderIdm = usageDetails.All(d => d.IsPointRedemption) ? null : staffIdm,
                                StaffName = usageDetails.All(d => d.IsPointRedemption) ? null : staffName
                            };

                            var ledgerId = await InsertLedger(usageLedger).ConfigureAwait(false);
                            usageLedger.Id = ledgerId;

                            // Issue #880互換: 挿入順を逆にしてFeliCa互換のrowid順序を維持
                            await InsertDetails(ledgerId, usageDetails.AsEnumerable().Reverse()).ConfigureAwait(false);

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
            var lastLedger = await _ledgerRepository.GetLatestBeforeDateAsync(cardIdm, DateTime.Now.AddDays(1)).ConfigureAwait(false);
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

            var elapsed = _clock.Now - LastProcessedTime.Value;
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
        /// <para>
        /// カード登録直後に呼び出され、カード内の履歴から対象期間（importFromDate以降）の
        /// レコードをledgerに登録する。既存の <see cref="CreateUsageLedgersAsync"/> を
        /// 内部で利用し、重複チェック・チャージ分離・残高不足パターン検出等を再利用する。
        /// </para>
        /// <para>
        /// Issue #1763: <b>取り込む履歴が無い場合もこのメソッドを通す</b>。
        /// <paramref name="historyDetails"/> に空リスト、<paramref name="initialLedger"/> に
        /// 初期残高行を渡すと「初期残高行だけをリトライ＋トランザクションで登録する」経路になる。
        /// 呼び出し元がリポジトリを直接叩くと、リトライも失敗通知も無い書込みが 1 経路だけ残り、
        /// そのカード唯一の受入行が無言で欠落する。
        /// </para>
        /// </remarks>
        /// <param name="cardIdm">カードのIDm</param>
        /// <param name="historyDetails">
        /// カードから読み取った履歴詳細。空リストを渡した場合、
        /// <paramref name="initialLedger"/> のみが登録される（Issue #1763）。
        /// </param>
        /// <param name="importFromDate">インポート対象の開始日</param>
        /// <param name="initialLedger">
        /// 初期残高レコード（「新規購入」/「○月から繰越」）。Issue #1727。
        /// 指定すると履歴行と同一トランザクションで登録される。null の場合は履歴行のみを登録する。
        /// </param>
        /// <returns>
        /// インポート結果。<see cref="HistoryImportResult.Success"/> が false の場合、
        /// <paramref name="initialLedger"/> を含めて **1 行も登録されていない**。
        /// 呼び出し元は必ず <see cref="HistoryImportResult.Success"/> を確認し、
        /// 失敗をユーザーへ通知すること。
        /// </returns>
        public async Task<HistoryImportResult> ImportHistoryForRegistrationAsync(
            string cardIdm, List<LedgerDetail> historyDetails, DateTime importFromDate,
            Ledger initialLedger = null)
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

                if (filtered.Count == 0 && initialLedger == null)
                {
                    result.Success = true;
                    result.ImportedCount = 0;
                    return result;
                }

                var importedCount = 0;

                // Issue #1727: 他の書込み経路（貸出・返却・整合性修復）と同様にリトライで包む。
                // 共有モードでは他PCの書込みと競合して SQLITE_BUSY になり得るが、
                // ここは busy_timeout でカバーできない接続レベルのロックも起こり得る。
                await _dbContext.ExecuteWithRetryAsync(async () =>
                {
                    // トランザクション開始
                    using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);

                    try
                    {
                        // Issue #1727: 初期残高行は「この後に履歴が入る」前提で履歴最古エントリから
                        // 逆算した値（CardManageViewModel.CalculatePreHistoryBalance）である。
                        // 別トランザクションで先に確定させると、履歴インポートだけが失敗したときに
                        // 実カードと合わない残高の行だけが台帳に残り、以降の残高チェーンがずれ続ける。
                        // リポジトリは同一接続を借りるため、ここでの Insert は本スコープに暗黙参加する。
                        if (initialLedger != null)
                        {
                            await _ledgerRepository.InsertAsync(initialLedger).ConfigureAwait(false);
                        }

                        // 既存のCreateUsageLedgersAsyncを利用（staffIdm/staffNameはnull: 登録時には利用者情報がないため）
                        // Issue #1481: ledger ヘッダ＋複数 detail 書込みを単一トランザクションに束ねる（暗黙参加）
                        var createdLedgers = filtered.Count > 0
                            ? await CreateUsageLedgersAsync(cardIdm, null, null, filtered).ConfigureAwait(false)
                            : new List<Ledger>();

                        scope.Commit();

                        importedCount = createdLedgers.Count;
                    }
                    catch
                    {
                        // Issue #1745: 素の Rollback() を呼ばない。COMMIT が SQLITE_BUSY 等で
                        // 失敗した後は SQLiteTransaction が無効化されており Rollback() 自体が
                        // 例外になる。その二次例外が本来の失敗要因を置き換えて抜けると、
                        // ①ExecuteWithRetryAsync の `when (ex is SQLiteException Busy/Locked)` に
                        // 一致せずリトライが働かない、②GetHistoryImportFailureReason が
                        // 既定分岐に落ちて「なぜ」が「データベースへの書き込み中に問題が発生しました。」
                        // に退化する、という二重の害になる。
                        TryRollbackRegistrationImport(scope, cardIdm);
                        throw;
                    }
                }).ConfigureAwait(false);

                result.Success = true;
                result.ImportedCount = importedCount;
            }
            catch (Exception ex)
            {
                // Issue #1704: IDm は認証クレデンシャルのためログにはマスクして出力する
                // Issue #1763: 履歴が無い登録（初期残高行のみ）もこのメソッドを通るため、
                // 「履歴インポート」と決め打ちせず、実際に何を書こうとしたかを値で残す
                // （ログは調査を先に進める値を載せる。development-conventions.md 参照）
                _logger.LogError(ex,
                    "カード登録時の台帳書き込みでエラーが発生しました（CardIdm={CardIdm}, 履歴件数={HistoryCount}, 初期残高行={HasInitialLedger}）",
                    IdmMasker.Mask(cardIdm), historyDetails?.Count ?? 0, initialLedger != null);
                result.Success = false;
                // ロールバック済みなので、途中まで作られた行数は残さない
                result.ImportedCount = 0;
                result.FailureReason = GetHistoryImportFailureReason(ex, _dbContext.IsSharedMode);
            }

            // Issue #1727: コミット確定後の後処理は、取込の成否に影響させない。
            // ここで例外を通して Success=false にすると、呼び出し元は「台帳には1行も
            // 記録されていません。CSVインポートで取り込んでください」と**事実に反する**
            // 案内をし、職員がそれに従うとコミット済みの行の上に同じ利用が二重計上される。
            // 完全性チェックは「不足しているかもしれない」という助言でしかないため、
            // 判定できなかった場合は助言を出さない（＝false）扱いで十分。
            if (result.Success)
            {
                try
                {
                    // 完全性チェック: 元の履歴（フィルタ前）を使用
                    result.MayHaveIncompleteHistory = CheckHistoryCompleteness(historyDetails, importFromDate);

                    // Issue #664: 不完全な場合、履歴の最古日付をメッセージ用に記録
                    if (result.MayHaveIncompleteHistory)
                    {
                        // UseDate を持つ要素が無い場合、Min は例外ではなく null を返す
                        // （DateTime? のセレクタを渡しているため）
                        result.EarliestHistoryDate = historyDetails
                            .Where(d => d.UseDate.HasValue)
                            .Min(d => d.UseDate);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "履歴の完全性チェックに失敗しました（取込自体は成功しています。CardIdm={CardIdm}）",
                        IdmMasker.Mask(cardIdm));
                    result.MayHaveIncompleteHistory = false;
                    result.EarliestHistoryDate = null;
                }
            }

            return result;
        }

        /// <summary>
        /// Issue #1745: <see cref="ImportHistoryForRegistrationAsync"/> のロールバックを
        /// 二次例外で本来の失敗要因を潰さずに試みる
        /// </summary>
        /// <remarks>
        /// 書き込みが確定するのは <c>Commit()</c> の成功時だけで、未コミットのトランザクションは
        /// <c>TransactionScope.Dispose()</c> でも巻き戻る（二重の巻き戻し）。したがってここでの
        /// 失敗を握りつぶしてもデータは確定しない。ログは <c>LogDebug</c> では本番のファイルに
        /// 出力されないため Warning に置く（development-conventions.md 参照）。
        /// </remarks>
        private void TryRollbackRegistrationImport(TransactionScope scope, string cardIdm)
        {
            // Issue #1831: 巻き戻しの手段は SafeRollback へ寄せる（クラスごとに同じヘルパーを
            // 増やすと、次に規約を変える人が一部を取りこぼす）。
            // Issue #1704: IDm は認証クレデンシャルのためログにはマスクして出力する
            SafeRollback.TryRollback(
                () => scope.Rollback(),
                _logger,
                $"カード登録時の台帳書き込み（CardIdm={IdmMasker.Mask(cardIdm)}）");
        }

        /// <summary>
        /// Issue #1727: <see cref="ImportHistoryForRegistrationAsync"/> の失敗の「なぜ」を
        /// ユーザー向け文言に変換する
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="GetUserFriendlyErrorMessage"/>（Issue #1110）を流用しないのは、
        /// あちらが「再度○○をお試しください」で終わるため。カード登録直後の履歴インポートは
        /// 同じ操作をやり直せない（カード行は既に登録済み）ので、行動指示としては誤りになる。
        /// </para>
        /// <para>
        /// 「どうすれば」は復旧手段を知っている呼び出し元が付ける
        /// （<c>Common/RegistrationLedgerFailureMessage</c>。取り込む履歴の有無で
        /// 取れる行動が変わるため、経路ごとにファクトリが分かれている。Issue #1763）。
        /// </para>
        /// <para>
        /// <paramref name="isSharedMode"/> で文言を分けるのは、**ローカルモードには「他のPC」が
        /// 存在しない**ため。単一 PC でも VACUUM・バックアップ・接続ヘルスチェックといった
        /// 自プロセス内の別接続と競合して SQLITE_BUSY は起こり得る。そこで「他のPCが使用中」と
        /// 案内すると、職員は存在しない相手を探して原因究明が止まる。
        /// </para>
        /// </remarks>
        /// <param name="ex">捕捉した例外</param>
        /// <param name="isSharedMode">共有フォルダモードで動作しているか（<c>DbContext.IsSharedMode</c>）</param>
        internal static string GetHistoryImportFailureReason(Exception ex, bool isSharedMode)
        {
            // ネットワーク共有が絡まない環境では、原因をネットワークに帰さない
            var ioReason = isSharedMode
                ? "ネットワーク共有フォルダーへの接続が切れました。"
                : "データベースファイルの読み書きに失敗しました。";

            if (ex is System.Data.SQLite.SQLiteException sqliteEx)
            {
                switch (sqliteEx.ResultCode)
                {
                    case System.Data.SQLite.SQLiteErrorCode.Busy:
                    case System.Data.SQLite.SQLiteErrorCode.Locked:
                        return isSharedMode
                            ? "他のPCがデータベースを使用中で、書き込みが競合しました。"
                            : "データベースが他の処理（バックアップや最適化など）で使用中で、書き込みが競合しました。";
                    case System.Data.SQLite.SQLiteErrorCode.IoErr:
                        return ioReason;
                }
            }

            if (ex is System.IO.IOException)
            {
                return ioReason;
            }

            return "データベースへの書き込み中に問題が発生しました。";
        }

        /// <summary>
        /// 整合性修復の <c>UPDATE</c> が影響行数 0 になった（競合）ことを記録する。
        /// </summary>
        /// <remarks>
        /// Issue #1953: 起動時に毎回走る処理であり例外にはしない（他 PC がカードを論理削除した
        /// だけで、そのカードの不整合はもはや運用に影響しない）。ただし
        /// <c>.claude/rules/development-conventions.md</c>「『ログには出ている』は無言失敗の
        /// 免罪符にならない」の趣旨に沿い、修復件数に数えなかった事実は本番ログへ残す
        /// （<c>LogDebug</c> は既定の <c>Logging:LogLevel=Information</c> ではファイル出力されない）。
        /// </remarks>
        /// <param name="maskedCardIdm">
        /// <see cref="IdmMasker.Mask"/> 済みの IDm。呼び出し地点でマスクを通すのは、
        /// 静的検査（<c>IdmLoggingMaskConventionTests</c>）が <c>Log</c> で始まるメソッド呼び出しの
        /// 引数を見るため（Issue #1852。自前のログヘルパーへ生の IDm を渡す形も同じ欠陥）。
        /// </param>
        /// <param name="direction">修復の向き（<c>0→1</c> / <c>1→0</c>）</param>
        private void LogRepairConflict(string maskedCardIdm, string direction)
        {
            _logger.LogWarning(
                "Issue #1953: 貸出状態の修復が競合しました（is_lent: {Direction}）。" +
                "他のパソコンでこのカードが削除された可能性があるため修復件数に数えません: CardIdm={CardIdm}",
                direction, maskedCardIdm);
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
        /// <para>
        /// 既定分岐（SQLite / IO 以外の例外）は生の <see cref="Exception.Message"/> を返さず
        /// （Issue #1614、#1817）、トーストに収まる簡潔な行動指示を返す。
        /// <see cref="AppException"/> は整備済みの <see cref="AppException.UserFriendlyMessage"/> を尊重する。
        /// </para>
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

            // Issue #1817: 既定分岐で生の ex.Message を返すと、.NET／SQLite の英語文言が
            // そのままトーストへ出る（Issue #1614 違反）。技術的詳細は呼び出し元
            // （LendAsync / ReturnAsync）の LogError が残しているため、ここでは文言だけを返す。

            // AppException は整備済みの UserFriendlyMessage を尊重する。
            if (ex is AppException appException &&
                !string.IsNullOrWhiteSpace(appException.UserFriendlyMessage))
            {
                return appException.UserFriendlyMessage;
            }

            // ExceptionMessageFormatter.ToUserMessage のフル文言を使わないのは2つの理由による
            // （#1817 のコードレビュー指摘）。
            // ① この戻り値は LendingResult.ErrorMessage を経て
            //    MainViewModel の _toastNotificationService.ShowError へ渡る。
            //    error-messages.md は「トースト通知は文字数制約があるため、ToUserMessage の
            //    フル文言ではなく簡潔な行動指示（「もう一度タッチしてください」等）を優先してよい」
            //    と定めている。ToUserMessage 版は 58 文字で、文字サイズ「大」以上では末尾が切れる。
            // ② ToUserMessage の InvalidOperationException 分岐は「画面を最新の状態に更新してから
            //    再度実行してください」と案内するが、カードをタッチした職員に実行できる操作ではない。
            //    取れる行動が違う経路には専用の文言を置く（#1757）。
            // Success=false は「台帳へ記録されていない」ことだけを意味する（#1805。コミット後の
            // 後処理の失敗は HasPostCommitFailure で別に伝える）ため、再タッチは安全。
            // 文言は MainViewModel の null フォールバックと同一に揃える。
            return $"{operationName}処理に失敗しました。もう一度タッチしてください。";
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
