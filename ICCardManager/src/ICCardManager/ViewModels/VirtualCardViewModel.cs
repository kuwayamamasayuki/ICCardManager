#if DEBUG
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels;

/// <summary>
/// カード/職員の選択肢（表示名 + IDm）
/// </summary>
public class IdmSelectItem
{
    public string Idm { get; set; }
    public string DisplayName { get; set; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// 仮想ICカードタッチ設定ダイアログのViewModel（Issue #640）
/// </summary>
public partial class VirtualCardViewModel : ObservableObject
{
    private const int MaxEntries = 20;

    private readonly HybridCardReader _hybridCardReader;
    private readonly ICardRepository _cardRepository;

    /// <summary>
    /// 履歴エントリの一覧
    /// </summary>
    public ObservableCollection<VirtualHistoryEntry> Entries { get; } = new();

    /// <summary>
    /// 選択可能なカードリスト（表示名付き）
    /// </summary>
    public List<IdmSelectItem> CardSelectItems { get; }

    /// <summary>
    /// 選択可能な職員リスト（表示名付き）
    /// </summary>
    public List<IdmSelectItem> StaffSelectItems { get; }

    /// <summary>
    /// 選択されたカード
    /// </summary>
    [ObservableProperty]
    private IdmSelectItem _selectedCard;

    /// <summary>
    /// 選択された職員
    /// </summary>
    [ObservableProperty]
    private IdmSelectItem _selectedStaff;

    /// <summary>
    /// 選択されたエントリ
    /// </summary>
    [ObservableProperty]
    private VirtualHistoryEntry _selectedEntry;

    /// <summary>
    /// 現在のカード残高（履歴の残高はこの値から自動計算される）
    /// </summary>
    [ObservableProperty]
    private int _currentBalance;

    /// <summary>
    /// ダイアログを閉じるためのAction（Viewから設定）
    /// </summary>
    public Action CloseAction { get; set; }

    public VirtualCardViewModel(HybridCardReader hybridCardReader, ICardRepository cardRepository)
    {
        _hybridCardReader = hybridCardReader;
        _cardRepository = cardRepository;

        // DebugDataService のテストデータから表示名を構築
        CardSelectItems = DebugDataService.TestCardList
            .Select(c => new IdmSelectItem { Idm = c.CardIdm, DisplayName = $"{c.CardType} {c.CardNumber}" })
            .ToList();

        StaffSelectItems = DebugDataService.TestStaffList
            .Select(s => new IdmSelectItem { Idm = s.StaffIdm, DisplayName = $"{s.Name}（{s.Number}）" })
            .ToList();

        SelectedCard = CardSelectItems.FirstOrDefault();
        SelectedStaff = StaffSelectItems.FirstOrDefault();

        Entries.CollectionChanged += (_, _) => AddEntryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// ダイアログ表示時の初期化（選択されたカードの現在残高を読み取る）
    /// </summary>
    public async Task InitializeAsync()
    {
        if (SelectedCard != null)
        {
            await LoadCurrentBalanceAsync(SelectedCard.Idm);
        }
    }

    /// <summary>
    /// 指定IDmのカード残高を読み取って CurrentBalance に反映
    /// </summary>
    private async Task LoadCurrentBalanceAsync(string idm)
    {
        try
        {
            var balance = await _hybridCardReader.ReadBalanceAsync(idm);
            if (balance.HasValue)
            {
                CurrentBalance = balance.Value;
                return;
            }
        }
        catch
        {
            // 残高読み取り失敗は無視
        }

        // 読み取れなかった場合はデフォルト値
        CurrentBalance = 0;
    }

    /// <summary>
    /// カード選択変更時に残高を再読み取り
    /// </summary>
    partial void OnSelectedCardChanged(IdmSelectItem value)
    {
        if (value != null)
        {
            _ = LoadCurrentBalanceAsync(value.Idm);
        }
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
            Amount = 200
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
        if (SelectedCard == null || SelectedStaff == null)
        {
            MessageBox.Show("カードと職員を選択してください。", "仮想タッチ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cardIdm = SelectedCard.Idm;
        var staffIdm = SelectedStaff.Idm;

        // 履歴データを LedgerDetail に変換し、残高を金額から自動計算
        // エントリは新しい順（index 0 が最新）で、各エントリの Balance は取引後の残高
        var historyDetails = new List<LedgerDetail>();
        var balance = CurrentBalance;

        foreach (var e in Entries)
        {
            historyDetails.Add(new LedgerDetail
            {
                UseDate = e.UseDate,
                EntryStation = string.IsNullOrWhiteSpace(e.EntryStation) ? null : e.EntryStation,
                ExitStation = string.IsNullOrWhiteSpace(e.ExitStation) ? null : e.ExitStation,
                Amount = e.Amount,
                Balance = balance,
                IsCharge = e.IsCharge,
                IsBus = !e.IsCharge &&
                        string.IsNullOrWhiteSpace(e.EntryStation) &&
                        string.IsNullOrWhiteSpace(e.ExitStation)
            });

            // 次のエントリ（1つ前の取引）の残高を逆算
            if (e.IsCharge)
                balance -= e.Amount; // チャージ前の残高 = チャージ後 - チャージ額
            else
                balance += e.Amount; // 利用前の残高 = 利用後 + 利用額
        }

        // エントリがある場合のみカスタム履歴・残高を設定
        if (historyDetails.Count > 0)
        {
            _hybridCardReader.SetCustomHistory(cardIdm, historyDetails);
            _hybridCardReader.SetCustomBalance(cardIdm, CurrentBalance);

            // カードが貸出中でない場合は、先に貸出してから返却する
            // （履歴は返却処理時にのみ読み取られるため）
            var card = await _cardRepository.GetByIdmAsync(cardIdm);
            if (card != null && !card.IsLent)
            {
                CloseAction?.Invoke();

                // 1. 貸出: 職員証タッチ → ICカードタッチ
                _hybridCardReader.SimulateCardRead(staffIdm);
                await Task.Delay(500);
                _hybridCardReader.SimulateCardRead(cardIdm);
                await Task.Delay(1500); // 貸出処理の完了を待つ

                // 2. 返却: 職員証タッチ → ICカードタッチ（ここで履歴が読み取られる）
                _hybridCardReader.SimulateCardRead(staffIdm);
                await Task.Delay(500);
                _hybridCardReader.SimulateCardRead(cardIdm);
                return;
            }
        }

        // ダイアログを閉じる
        CloseAction?.Invoke();

        // 職員証タッチ → 少し待機 → ICカードタッチ
        _hybridCardReader.SimulateCardRead(staffIdm);
        await Task.Delay(500);
        _hybridCardReader.SimulateCardRead(cardIdm);
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

    /// <summary>
    /// 金額（チャージの場合は受入額、利用の場合は払出額）
    /// </summary>
    [ObservableProperty]
    private int _amount;

    [ObservableProperty]
    private bool _isCharge;
}
#endif
