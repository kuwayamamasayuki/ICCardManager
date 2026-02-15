using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// バス停名未入力一覧ダイアログのViewModel（Issue #672, #703）
    /// </summary>
    public partial class IncompleteBusStopViewModel : ViewModelBase
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly ICardRepository _cardRepository;

        private List<IncompleteBusStopItem> _allItems = new();

        [ObservableProperty]
        private ObservableCollection<IncompleteBusStopItem> _items = new();

        [ObservableProperty]
        private IncompleteBusStopItem _selectedItem;

        /// <summary>
        /// 利用日フィルタの選択肢
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<string> _dateFilterOptions = new();

        /// <summary>
        /// 選択中の利用日フィルタ
        /// </summary>
        [ObservableProperty]
        private string _selectedDateFilter = "すべて";

        /// <summary>
        /// カード名フィルタの選択肢
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<string> _cardFilterOptions = new();

        /// <summary>
        /// 選択中のカード名フィルタ
        /// </summary>
        [ObservableProperty]
        private string _selectedCardFilter = "すべて";

        /// <summary>
        /// 選択されたカードのIDm
        /// </summary>
        public string SelectedCardIdm => SelectedItem?.CardIdm;

        /// <summary>
        /// 選択された履歴のID
        /// </summary>
        public int? SelectedLedgerId => SelectedItem?.LedgerId;

        public IncompleteBusStopViewModel(
            ILedgerRepository ledgerRepository,
            ICardRepository cardRepository)
        {
            _ledgerRepository = ledgerRepository;
            _cardRepository = cardRepository;
        }

        /// <summary>
        /// 初期化（バス停未入力履歴を読み込み）
        /// </summary>
        public async Task InitializeAsync()
        {
            using (BeginBusy("読み込み中..."))
            {
                // フィルタ条件を保持（バス停名入力後の再読み込み時に復元するため）
                var previousDateFilter = SelectedDateFilter;
                var previousCardFilter = SelectedCardFilter;

                var ledgers = await _ledgerRepository.GetByDateRangeAsync(
                    null, DateTime.Now.AddYears(-1), DateTime.Now);
                var incompleteLedgers = ledgers.Where(l => l.Summary.Contains("★")).ToList();

                var cards = await _cardRepository.GetAllAsync();
                var cardMap = cards.ToDictionary(c => c.CardIdm, c => $"{c.CardType} {c.CardNumber}");

                _allItems = incompleteLedgers.Select(l => new IncompleteBusStopItem
                {
                    LedgerId = l.Id,
                    CardIdm = l.CardIdm,
                    CardDisplayName = cardMap.TryGetValue(l.CardIdm, out var name) ? name : l.CardIdm,
                    Date = l.Date,
                    Summary = l.Summary,
                    Expense = l.Expense,
                    StaffName = l.StaffName ?? ""
                }).OrderByDescending(i => i.Date).ToList();

                // 利用日フィルタの選択肢を構築
                DateFilterOptions.Clear();
                DateFilterOptions.Add("すべて");
                foreach (var date in _allItems.Select(i => i.DateDisplay).Distinct().OrderByDescending(d => d))
                {
                    DateFilterOptions.Add(date);
                }

                // カード名フィルタの選択肢を構築
                CardFilterOptions.Clear();
                CardFilterOptions.Add("すべて");
                foreach (var cardName in _allItems.Select(i => i.CardDisplayName).Distinct().OrderBy(n => n))
                {
                    CardFilterOptions.Add(cardName);
                }

                // 以前の選択値が新しい選択肢に存在する場合は復元、なければ「すべて」に戻す
                SelectedDateFilter = DateFilterOptions.Contains(previousDateFilter) ? previousDateFilter : "すべて";
                SelectedCardFilter = CardFilterOptions.Contains(previousCardFilter) ? previousCardFilter : "すべて";

                ApplyFilter();
            }
        }

        /// <summary>
        /// バス停名保存後にアイテムの摘要を更新する（Issue #709）
        /// </summary>
        /// <returns>更新後のアイテム。見つからない場合はnull</returns>
        internal async Task<IncompleteBusStopItem> UpdateItemSummaryAsync(int ledgerId)
        {
            var ledger = await _ledgerRepository.GetByIdAsync(ledgerId);
            if (ledger == null) return null;

            // Items内の該当アイテムを検索
            var index = -1;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].LedgerId == ledgerId) { index = i; break; }
            }
            if (index < 0) return null;

            var oldItem = Items[index];
            var newItem = new IncompleteBusStopItem
            {
                LedgerId = oldItem.LedgerId,
                CardIdm = oldItem.CardIdm,
                CardDisplayName = oldItem.CardDisplayName,
                Date = oldItem.Date,
                Summary = ledger.Summary,
                Expense = oldItem.Expense,
                StaffName = oldItem.StaffName
            };

            // ObservableCollection の要素差し替え → DataGrid が自動更新
            Items[index] = newItem;

            // _allItems も更新
            var allIndex = _allItems.FindIndex(i => i.LedgerId == ledgerId);
            if (allIndex >= 0) _allItems[allIndex] = newItem;

            return newItem;
        }

        partial void OnSelectedDateFilterChanged(string value) => ApplyFilter();
        partial void OnSelectedCardFilterChanged(string value) => ApplyFilter();

        /// <summary>
        /// フィルタ適用
        /// </summary>
        internal void ApplyFilter()
        {
            var filtered = _allItems.AsEnumerable();

            if (!string.IsNullOrEmpty(SelectedDateFilter) && SelectedDateFilter != "すべて")
            {
                filtered = filtered.Where(i => i.DateDisplay == SelectedDateFilter);
            }

            if (!string.IsNullOrEmpty(SelectedCardFilter) && SelectedCardFilter != "すべて")
            {
                filtered = filtered.Where(i => i.CardDisplayName == SelectedCardFilter);
            }

            Items = new ObservableCollection<IncompleteBusStopItem>(filtered);
        }
    }
}
