using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// バス停名未入力一覧ダイアログのViewModel（Issue #672）
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
        /// 確認完了フラグ（ダイアログ結果用）
        /// </summary>
        [ObservableProperty]
        private bool _isConfirmed;

        /// <summary>
        /// 選択されたカードのIDm
        /// </summary>
        public string SelectedCardIdm => SelectedItem?.CardIdm;

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

                CardFilterOptions.Clear();
                CardFilterOptions.Add("すべて");
                foreach (var cardName in _allItems.Select(i => i.CardDisplayName).Distinct().OrderBy(n => n))
                {
                    CardFilterOptions.Add(cardName);
                }

                ApplyFilter();
            }
        }

        partial void OnSelectedCardFilterChanged(string value) => ApplyFilter();

        /// <summary>
        /// フィルタ適用
        /// </summary>
        internal void ApplyFilter()
        {
            var filtered = _allItems.AsEnumerable();

            if (!string.IsNullOrEmpty(SelectedCardFilter) && SelectedCardFilter != "すべて")
            {
                filtered = filtered.Where(i => i.CardDisplayName == SelectedCardFilter);
            }

            Items = new ObservableCollection<IncompleteBusStopItem>(filtered);
        }

        /// <summary>
        /// 選択確定
        /// </summary>
        [RelayCommand]
        public void Confirm()
        {
            if (SelectedItem != null)
            {
                IsConfirmed = true;
            }
        }
    }
}
