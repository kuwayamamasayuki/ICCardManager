using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;

namespace ICCardManager.Services
{
    /// <summary>
    /// データ系の警告チェックを担当するサービス
    /// </summary>
    /// <remarks>
    /// MainViewModelから抽出。残額警告とバス停未入力チェックを一元化。
    /// インフラ系の警告（接続断・カードリーダー）はMainViewModelに残す。
    /// </remarks>
    public class WarningService
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly IDatabaseInfo _databaseInfo;
        private readonly IUpdateNotificationService _updateNotificationService;

        /// <param name="ledgerRepository">台帳リポジトリ</param>
        /// <param name="databaseInfo">DB接続情報</param>
        /// <param name="updateNotificationService">
        /// 更新通知チェック（Issue #1687）。null の場合、更新通知警告は常に生成されない
        /// （既存テストの構築コードとの互換のため省略可能にしている。DI経由では常に注入される）
        /// </param>
        public WarningService(
            ILedgerRepository ledgerRepository,
            IDatabaseInfo databaseInfo,
            IUpdateNotificationService updateNotificationService = null)
        {
            _ledgerRepository = ledgerRepository;
            _databaseInfo = databaseInfo;
            _updateNotificationService = updateNotificationService;
        }

        /// <summary>
        /// ダッシュボードデータから残額警告を生成
        /// </summary>
        /// <param name="dashboardItems">ダッシュボードアイテム一覧</param>
        /// <param name="warningBalance">警告しきい値（円）</param>
        /// <returns>残額警告のリスト</returns>
        public IReadOnlyList<WarningItem> CheckLowBalanceWarnings(
            IEnumerable<CardBalanceDashboardItem> dashboardItems,
            int warningBalance)
        {
            var warnings = new List<WarningItem>();
            foreach (var item in dashboardItems)
            {
                // Issue: DashboardService.BuildDashboardAsync と判定条件を統一する。
                // DashboardService側は IsBalanceWarning = balance <= warningBalance (≤) で
                // 警告アイコンを出しているため、警告一覧も同じ条件 (≤) でないと
                // 「アイコンは出るが一覧に載らない」という不整合が発生する。
                if (item.CurrentBalance <= warningBalance)
                {
                    warnings.Add(new WarningItem
                    {
                        DisplayText = $"⚠️ {item.CardType} {item.CardNumber}: 残額 {DisplayFormatters.FormatBalanceWithUnit(item.CurrentBalance)}（しきい値: {warningBalance:N0}円）",
                        Type = WarningType.LowBalance,
                        CardIdm = item.CardIdm
                    });
                }
            }
            return warnings;
        }

        /// <summary>
        /// バス停名未入力の件数をチェック
        /// </summary>
        /// <returns>未入力件数がある場合はWarningItem、ない場合はnull</returns>
        public async Task<WarningItem> CheckIncompleteBusStopsAsync()
        {
            var ledgers = await _ledgerRepository.GetByDateRangeAsync(
                null, DateTime.Now.AddYears(-1), DateTime.Now).ConfigureAwait(false);

            var incompleteCount = ledgers.Count(l => l.Summary?.Contains("★") == true);
            if (incompleteCount > 0)
            {
                return new WarningItem
                {
                    DisplayText = $"⚠️ バス停名が未入力の履歴が{incompleteCount}件あります",
                    Type = WarningType.IncompleteBusStop
                };
            }
            return null;
        }

        /// <summary>
        /// ジャーナルモード警告を生成
        /// </summary>
        /// <returns>ジャーナルモードが低下している場合はWarningItem、正常な場合はnull</returns>
        public WarningItem CheckJournalModeWarning()
        {
            if (!_databaseInfo.IsJournalModeDegraded)
                return null;

            return new WarningItem
            {
                Type = WarningType.DatabaseJournalModeDegraded,
                DisplayText = $"⚠️ データベースのクラッシュ耐性が低下しています（journal_mode={_databaseInfo.CurrentJournalMode}）。" +
                              "ファイルサーバ管理者にご相談ください。"
            };
        }

        /// <summary>
        /// 更新通知警告を生成（Issue #1687）
        /// </summary>
        /// <remarks>
        /// DBと同じフォルダの latest_version.txt に自バージョンより新しいバージョンが
        /// 記載されている場合、更新を促す通知を生成する。ファイル読み取りを伴うため、
        /// UI スレッドから呼ぶ場合は Task.Run 経由を推奨（SMB遅延対策）。
        /// </remarks>
        /// <returns>新しいバージョンがある場合はWarningItem、ない場合はnull</returns>
        public WarningItem CheckUpdateNotificationWarning()
        {
            var result = _updateNotificationService?.CheckForNewerVersion();
            if (result == null)
                return null;

            return new WarningItem
            {
                Type = WarningType.NewVersionAvailable,
                DisplayText = $"ℹ️ 新しいバージョン {result.LatestVersion} が公開されています" +
                              $"（このPCは {result.CurrentVersion}）。管理者に更新をご確認ください。"
            };
        }
    }
}
