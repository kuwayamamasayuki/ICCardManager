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

        public WarningService(ILedgerRepository ledgerRepository, IDatabaseInfo databaseInfo)
        {
            _ledgerRepository = ledgerRepository;
            _databaseInfo = databaseInfo;
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
    }
}
