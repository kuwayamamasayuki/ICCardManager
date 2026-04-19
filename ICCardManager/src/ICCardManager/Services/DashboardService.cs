using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.ViewModels;

namespace ICCardManager.Services
{
    /// <summary>
    /// カード残高ダッシュボードのデータ構築とソートを担当するサービス
    /// </summary>
    /// <remarks>
    /// MainViewModelから抽出。複数Repositoryからのデータ取得・結合・ソートを一元化。
    /// </remarks>
    public class DashboardService
    {
        private readonly ICardRepository _cardRepository;
        private readonly ILedgerRepository _ledgerRepository;
        private readonly IStaffRepository _staffRepository;
        private readonly ISettingsRepository _settingsRepository;

        public DashboardService(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository,
            IStaffRepository staffRepository,
            ISettingsRepository settingsRepository)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
            _staffRepository = staffRepository;
            _settingsRepository = settingsRepository;
        }

        /// <summary>
        /// ダッシュボードデータを構築して返す
        /// </summary>
        /// <param name="sortOrder">ソート順</param>
        /// <returns>ソート済みのダッシュボードアイテムと警告しきい値</returns>
        public async Task<DashboardResult> BuildDashboardAsync(DashboardSortOrder sortOrder)
        {
            // Issue #504: データ取得を並列化して高速化
            var settingsTask = _settingsRepository.GetAppSettingsAsync();
            var cardsTask = _cardRepository.GetAllAsync();
            var balancesTask = _ledgerRepository.GetAllLatestBalancesAsync();
            var staffTask = _staffRepository.GetAllAsync();

            await Task.WhenAll(settingsTask, cardsTask, balancesTask, staffTask).ConfigureAwait(false);

            var settings = await settingsTask.ConfigureAwait(false);
            var cards = await cardsTask.ConfigureAwait(false);
            var balances = await balancesTask.ConfigureAwait(false);
            var staffDict = (await staffTask.ConfigureAwait(false)).ToDictionary(s => s.StaffIdm, s => s.Name);

            var dashboardItems = new List<CardBalanceDashboardItem>();

            foreach (var card in cards)
            {
                var (balance, lastUsageDate) = balances.TryGetValue(card.CardIdm, out var info)
                    ? info
                    : (0, (DateTime?)null);

                var staffName = card.IsLent && card.LastLentStaff != null && staffDict.TryGetValue(card.LastLentStaff, out var name)
                    ? name
                    : null;

                dashboardItems.Add(new CardBalanceDashboardItem
                {
                    CardIdm = card.CardIdm,
                    CardType = card.CardType,
                    CardNumber = card.CardNumber,
                    CurrentBalance = balance,
                    IsBalanceWarning = balance <= settings.WarningBalance,
                    LastUsageDate = lastUsageDate,
                    IsLent = card.IsLent,
                    LentStaffName = staffName
                });
            }

            var sortedItems = SortItems(dashboardItems, sortOrder);

            return new DashboardResult
            {
                Items = sortedItems,
                WarningBalance = settings.WarningBalance
            };
        }

        /// <summary>
        /// ダッシュボードアイテムをソートする
        /// </summary>
        public IReadOnlyList<CardBalanceDashboardItem> SortItems(
            IEnumerable<CardBalanceDashboardItem> items,
            DashboardSortOrder sortOrder)
        {
            IEnumerable<CardBalanceDashboardItem> sorted = sortOrder switch
            {
                DashboardSortOrder.CardName => items.OrderByCardDefault(x => x.CardType, x => x.CardNumber),
                DashboardSortOrder.BalanceAscending => items.OrderBy(x => x.CurrentBalance).ThenByCardDefault(x => x.CardType, x => x.CardNumber),
                DashboardSortOrder.BalanceDescending => items.OrderByDescending(x => x.CurrentBalance).ThenByCardDefault(x => x.CardType, x => x.CardNumber),
                DashboardSortOrder.LastUsageDate => items.OrderByDescending(x => x.LastUsageDate ?? DateTime.MinValue).ThenByCardDefault(x => x.CardType, x => x.CardNumber),
                _ => items
            };
            return sorted.ToList();
        }
    }

    /// <summary>
    /// ダッシュボードデータ構築結果
    /// </summary>
    public class DashboardResult
    {
        /// <summary>ソート済みダッシュボードアイテム</summary>
        public IReadOnlyList<CardBalanceDashboardItem> Items { get; set; }

        /// <summary>残額警告しきい値（警告チェック用）</summary>
        public int WarningBalance { get; set; }
    }
}
