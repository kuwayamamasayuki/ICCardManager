#if DEBUG
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;

namespace ICCardManager.ViewModels;

/// <summary>
/// 仮想ICカードタッチ設定ダイアログのViewModel（Issue #640）
/// </summary>
public partial class VirtualCardViewModel : ObservableObject
{
    private const int MaxEntries = 20;

    private readonly MockCardReader _mockCardReader;

    /// <summary>
    /// 履歴エントリの一覧
    /// </summary>
    public ObservableCollection<VirtualHistoryEntry> Entries { get; } = new();

    /// <summary>
    /// 選択可能なカードIDmリスト
    /// </summary>
    public List<string> CardIdmList { get; }

    /// <summary>
    /// 選択可能な職員IDmリスト
    /// </summary>
    public List<string> StaffIdmList { get; }

    /// <summary>
    /// 選択されたカードIDm
    /// </summary>
    [ObservableProperty]
    private string _selectedCardIdm;

    /// <summary>
    /// 選択された職員IDm
    /// </summary>
    [ObservableProperty]
    private string _selectedStaffIdm;

    /// <summary>
    /// 選択されたエントリ
    /// </summary>
    [ObservableProperty]
    private VirtualHistoryEntry _selectedEntry;

    /// <summary>
    /// ダイアログを閉じるためのAction（Viewから設定）
    /// </summary>
    public Action CloseAction { get; set; }

    public VirtualCardViewModel(MockCardReader mockCardReader)
    {
        _mockCardReader = mockCardReader;

        CardIdmList = mockCardReader.MockCards.ToList();
        StaffIdmList = mockCardReader.MockStaffCards.ToList();
        SelectedCardIdm = CardIdmList.FirstOrDefault();
        SelectedStaffIdm = StaffIdmList.FirstOrDefault();

        Entries.CollectionChanged += (_, _) => AddEntryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 履歴エントリを追加
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddEntry))]
    public void AddEntry()
    {
        Entries.Add(new VirtualHistoryEntry
        {
            UseDate = DateTime.Today,
            Balance = Entries.Count > 0 ? Entries.Last().Balance : 5000
        });
    }

    /// <summary>
    /// エントリ追加可能か（最大20件）
    /// </summary>
    public bool CanAddEntry() => Entries.Count < MaxEntries;

    /// <summary>
    /// 選択されたエントリを削除
    /// </summary>
    [RelayCommand]
    public void RemoveEntry(VirtualHistoryEntry entry)
    {
        if (entry != null)
        {
            Entries.Remove(entry);
        }
    }

    /// <summary>
    /// 設定を適用してカードタッチをシミュレート
    /// </summary>
    [RelayCommand]
    public async Task ApplyAndTouchAsync()
    {
        if (string.IsNullOrEmpty(SelectedCardIdm) || string.IsNullOrEmpty(SelectedStaffIdm))
        {
            MessageBox.Show("カードと職員を選択してください。", "仮想タッチ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 履歴データを LedgerDetail に変換
        var historyDetails = Entries.Select(e => new LedgerDetail
        {
            UseDate = e.UseDate,
            EntryStation = string.IsNullOrWhiteSpace(e.EntryStation) ? null : e.EntryStation,
            ExitStation = string.IsNullOrWhiteSpace(e.ExitStation) ? null : e.ExitStation,
            Balance = e.Balance,
            IsCharge = e.IsCharge,
            IsBus = !e.IsCharge &&
                    string.IsNullOrWhiteSpace(e.EntryStation) &&
                    string.IsNullOrWhiteSpace(e.ExitStation)
        }).ToList();

        // 金額を残高差分から計算
        for (int i = 0; i < historyDetails.Count; i++)
        {
            if (i < historyDetails.Count - 1)
            {
                var current = historyDetails[i];
                var next = historyDetails[i + 1];
                if (current.IsCharge)
                {
                    current.Amount = current.Balance - next.Balance;
                }
                else
                {
                    current.Amount = next.Balance - current.Balance;
                }
            }
        }

        // MockCardReader に設定
        _mockCardReader.SetCustomHistory(SelectedCardIdm, historyDetails);

        // 残高: エントリがあれば先頭（最新）の残高、なければデフォルト
        var balance = historyDetails.Count > 0 ? historyDetails.First().Balance ?? 5000 : 5000;
        _mockCardReader.SetCustomBalance(SelectedCardIdm, balance);

        // ダイアログを閉じる
        CloseAction?.Invoke();

        // 職員証タッチ → 少し待機 → ICカードタッチ
        _mockCardReader.SimulateCardRead(SelectedStaffIdm);
        await Task.Delay(500);
        _mockCardReader.SimulateCardRead(SelectedCardIdm);
    }
}

/// <summary>
/// 仮想履歴エントリ（1行分のデータ）
/// </summary>
public partial class VirtualHistoryEntry : ObservableObject
{
    [ObservableProperty]
    private DateTime _useDate = DateTime.Today;

    [ObservableProperty]
    private string _entryStation = "";

    [ObservableProperty]
    private string _exitStation = "";

    [ObservableProperty]
    private int _balance;

    [ObservableProperty]
    private bool _isCharge;
}
#endif
