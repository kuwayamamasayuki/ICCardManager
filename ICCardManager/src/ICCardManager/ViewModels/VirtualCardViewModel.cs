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
/// 仮想タッチの実行結果（ダイアログからMainViewModelへの受け渡し用）
/// </summary>
internal class VirtualTouchResult
{
    public string StaffIdm { get; set; }
    public string CardIdm { get; set; }
    public int CurrentBalance { get; set; }
    public List<LedgerDetail> HistoryDetails { get; set; }
    public bool HasEntries => HistoryDetails?.Count > 0;
}

/// <summary>
/// 仮想ICカードタッチ設定ダイアログのViewModel（Issue #640）
/// </summary>
public partial class VirtualCardViewModel : ObservableObject
{
    private const int MaxEntries = 20;

    private readonly ILedgerRepository _ledgerRepository;

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

    /// <summary>
    /// メッセージ表示用デリゲート（テストでの差し替え用）
    /// </summary>
    internal Action<string, string> ShowMessage { get; set; } =
        (message, title) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// タッチ実行結果（ダイアログ閉鎖後にMainViewModelが参照する）
    /// nullの場合はキャンセル扱い
    /// </summary>
    internal VirtualTouchResult TouchResult { get; private set; }

    /// <summary>
    /// DI用コンストラクタ
    /// </summary>
    public VirtualCardViewModel(
        ILedgerRepository ledgerRepository)
    {
        _ledgerRepository = ledgerRepository;

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
    /// ダイアログ表示時の初期化（選択されたカードの現在残高をDBから読み取る）
    /// </summary>
    public async Task InitializeAsync()
    {
        if (SelectedCard != null)
        {
            await LoadCurrentBalanceAsync(SelectedCard.Idm);
        }
    }

    /// <summary>
    /// 指定IDmのカード残高をDBから読み取って CurrentBalance に反映
    /// </summary>
    private async Task LoadCurrentBalanceAsync(string idm)
    {
        try
        {
            var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(idm);
            if (latestLedger != null)
            {
                CurrentBalance = latestLedger.Balance;
                return;
            }
        }
        catch
        {
            // DB読み取り失敗は無視
        }

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
    /// 入力データをTouchResultに格納してダイアログを閉じる。
    /// 実際の貸出・返却処理はMainViewModelがShowDialog()後に実行する。
    /// </summary>
    [RelayCommand]
    public void ApplyAndTouch()
    {
        if (SelectedCard == null || SelectedStaff == null)
        {
            ShowMessage("カードと職員を選択してください。", "仮想タッチ");
            return;
        }

        var cardIdm = SelectedCard.Idm;
        var staffIdm = SelectedStaff.Idm;

        // 履歴データを LedgerDetail に変換し、残高を金額から自動計算
        // UI上は上＝最古、下＝最新の順で入力されるが、
        // FeliCa履歴の慣例（index 0＝最新）に合わせて逆順にする
        var historyDetails = new List<LedgerDetail>();
        var balance = CurrentBalance;

        foreach (var e in Entries.Reverse())
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
                balance -= e.Amount;
            else
                balance += e.Amount;
        }

        // 結果を格納（実処理はMainViewModelが行う）
        TouchResult = new VirtualTouchResult
        {
            StaffIdm = staffIdm,
            CardIdm = cardIdm,
            CurrentBalance = CurrentBalance,
            HistoryDetails = historyDetails
        };

        CloseAction?.Invoke();
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
