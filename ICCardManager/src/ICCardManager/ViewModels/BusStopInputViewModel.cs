using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.ViewModels;

/// <summary>
/// バス停入力画面のViewModel
/// </summary>
public partial class BusStopInputViewModel : ViewModelBase
{
    private readonly ILedgerRepository _ledgerRepository;

    [ObservableProperty]
    private Ledger? _ledger;

    [ObservableProperty]
    private ObservableCollection<BusStopInputItem> _busUsages = new();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>
    /// 保存完了フラグ（ダイアログ結果用）
    /// </summary>
    public bool IsSaved { get; private set; }

    public BusStopInputViewModel(ILedgerRepository ledgerRepository)
    {
        _ledgerRepository = ledgerRepository;
    }

    /// <summary>
    /// 利用履歴を指定して初期化
    /// </summary>
    public async Task InitializeAsync(int ledgerId)
    {
        using (BeginBusy("読み込み中..."))
        {
            // 履歴詳細を取得
            Ledger = await _ledgerRepository.GetByIdAsync(ledgerId);
            if (Ledger == null)
            {
                StatusMessage = "履歴データが見つかりません";
                return;
            }

            // バス利用のみを抽出
            BusUsages.Clear();
            foreach (var detail in Ledger.Details.Where(d => d.IsBus))
            {
                BusUsages.Add(new BusStopInputItem(detail));
            }

            if (BusUsages.Count == 0)
            {
                StatusMessage = "バス利用の履歴がありません";
            }
            else
            {
                StatusMessage = $"{BusUsages.Count}件のバス利用があります";
            }

            HasUnsavedChanges = false;
        }
    }

    /// <summary>
    /// バス利用詳細を直接設定して初期化（返却時用）
    /// </summary>
    public void InitializeWithDetails(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
    {
        Ledger = ledger;

        BusUsages.Clear();
        foreach (var detail in busDetails.Where(d => d.IsBus))
        {
            BusUsages.Add(new BusStopInputItem(detail));
        }

        if (BusUsages.Count == 0)
        {
            StatusMessage = "バス利用の履歴がありません";
        }
        else
        {
            StatusMessage = $"{BusUsages.Count}件のバス利用があります。バス停名を入力してください。";
        }

        HasUnsavedChanges = false;
    }

    /// <summary>
    /// 保存
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (Ledger == null) return;

        // 入力検証
        var emptyCount = BusUsages.Count(b => string.IsNullOrWhiteSpace(b.BusStops));
        if (emptyCount > 0)
        {
            StatusMessage = $"未入力のバス停が{emptyCount}件あります";
            // 未入力でも保存は可能（★マークが付く）
        }

        using (BeginBusy("保存中..."))
        {
            // 各バス利用のバス停名を更新
            foreach (var item in BusUsages)
            {
                item.Detail.BusStops = string.IsNullOrWhiteSpace(item.BusStops)
                    ? "★" // 未入力の場合は★マーク
                    : item.BusStops;
            }

            // 履歴を更新
            var success = await _ledgerRepository.UpdateAsync(Ledger);

            if (success)
            {
                StatusMessage = "保存しました";
                HasUnsavedChanges = false;
                IsSaved = true;
            }
            else
            {
                StatusMessage = "保存に失敗しました";
            }
        }
    }

    /// <summary>
    /// スキップ（★マークを付けて保存）
    /// </summary>
    [RelayCommand]
    public async Task SkipAsync()
    {
        if (Ledger == null) return;

        using (BeginBusy("保存中..."))
        {
            // 未入力のバス停に★マークを付ける
            foreach (var item in BusUsages)
            {
                if (string.IsNullOrWhiteSpace(item.BusStops))
                {
                    item.Detail.BusStops = "★";
                }
            }

            var success = await _ledgerRepository.UpdateAsync(Ledger);

            if (success)
            {
                StatusMessage = "スキップしました（後で入力が必要です）";
                IsSaved = true;
            }
            else
            {
                StatusMessage = "保存に失敗しました";
            }
        }
    }
}

/// <summary>
/// バス停入力アイテム
/// </summary>
public partial class BusStopInputItem : ObservableObject
{
    public LedgerDetail Detail { get; }

    [ObservableProperty]
    private string _busStops;

    public DateTime? UseDate => Detail.UseDate;
    public string UseDateDisplay => Detail.UseDate.HasValue
        ? WarekiConverter.ToWareki(Detail.UseDate.Value)
        : "不明";
    public int? Amount => Detail.Amount;
    public string AmountDisplay => Amount.HasValue ? $"{Amount:N0}円" : "";

    public BusStopInputItem(LedgerDetail detail)
    {
        Detail = detail;
        _busStops = detail.BusStops ?? string.Empty;
    }

    partial void OnBusStopsChanged(string value)
    {
        Detail.BusStops = value;
    }
}
