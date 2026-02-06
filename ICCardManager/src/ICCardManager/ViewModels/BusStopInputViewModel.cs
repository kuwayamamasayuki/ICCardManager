using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


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
    /// バス停名サジェストのマスターリスト（使用頻度順）
    /// </summary>
    [ObservableProperty]
    private List<string> _busStopSuggestions = new();

    /// <summary>
    /// 保存完了フラグ（ダイアログ結果用）
    /// </summary>
    [ObservableProperty]
    private bool _isSaved;

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
            // サジェスト候補を読み込み
            await LoadBusStopSuggestionsAsync();

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
                var item = new BusStopInputItem(detail);
                item.SetSuggestions(BusStopSuggestions);
                BusUsages.Add(item);
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
    public async Task InitializeWithDetailsAsync(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
    {
        // サジェスト候補を読み込み
        await LoadBusStopSuggestionsAsync();

        Ledger = ledger;

        BusUsages.Clear();
        foreach (var detail in busDetails.Where(d => d.IsBus))
        {
            var item = new BusStopInputItem(detail);
            item.SetSuggestions(BusStopSuggestions);
            BusUsages.Add(item);
        }

        if (BusUsages.Count == 0)
        {
            StatusMessage = "バス利用の履歴がありません";
        }
        else
        {
            var suggestionCount = BusStopSuggestions.Count;
            var suggestionInfo = suggestionCount > 0 ? $"（{suggestionCount}件の候補あり）" : "";
            StatusMessage = $"{BusUsages.Count}件のバス利用があります。バス停名を入力してください。{suggestionInfo}";
        }

        HasUnsavedChanges = false;
    }

    /// <summary>
    /// バス利用詳細を直接設定して初期化（返却時用・同期版）
    /// </summary>
    public void InitializeWithDetails(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
    {
        Ledger = ledger;

        BusUsages.Clear();
        foreach (var detail in busDetails.Where(d => d.IsBus))
        {
            var item = new BusStopInputItem(detail);
            item.SetSuggestions(BusStopSuggestions);
            BusUsages.Add(item);
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
    /// バス停名サジェスト候補を読み込み
    /// </summary>
    private async Task LoadBusStopSuggestionsAsync()
    {
        try
        {
            var suggestions = await _ledgerRepository.GetBusStopSuggestionsAsync();
            BusStopSuggestions = suggestions.Select(s => s.BusStops).ToList();
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[BusStopInput] {BusStopSuggestions.Count}件のバス停名候補を読み込みました");
#endif
        }
        catch (Exception ex)
        {
            _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[BusStopInput] サジェスト候補の読み込みに失敗: {ex.Message}");
#endif
            BusStopSuggestions = new List<string>();
        }
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

            // 摘要を再生成（バス停名を反映）
            var summaryGenerator = new SummaryGenerator();
            Ledger.Summary = summaryGenerator.Generate(Ledger.Details);

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

            // 摘要を再生成（★マークを反映）
            var summaryGenerator = new SummaryGenerator();
            Ledger.Summary = summaryGenerator.Generate(Ledger.Details);

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

    /// <summary>
    /// 全サジェスト候補（マスター）
    /// </summary>
    private List<string> _allSuggestions = new();

    /// <summary>
    /// 現在のフィルター済みサジェスト候補
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string> _filteredSuggestions = new();

    /// <summary>
    /// サジェストポップアップを表示するか
    /// </summary>
    [ObservableProperty]
    private bool _showSuggestions;

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

    /// <summary>
    /// サジェスト候補を設定
    /// </summary>
    public void SetSuggestions(List<string> suggestions)
    {
        _allSuggestions = suggestions;
    }

    partial void OnBusStopsChanged(string value)
    {
        Detail.BusStops = value;
        UpdateFilteredSuggestions(value);
    }

    /// <summary>
    /// 入力値でサジェストをフィルター
    /// </summary>
    private void UpdateFilteredSuggestions(string input)
    {
        FilteredSuggestions.Clear();

        if (string.IsNullOrWhiteSpace(input) || _allSuggestions.Count == 0)
        {
            ShowSuggestions = false;
            return;
        }

        // 入力文字列を含む候補を抽出（先頭一致優先、次に部分一致）
        var inputLower = input.ToLowerInvariant();

        var startsWithMatches = _allSuggestions
            .Where(s => s.ToLowerInvariant().StartsWith(inputLower))
            .Take(5);

        var containsMatches = _allSuggestions
            .Where(s => !s.ToLowerInvariant().StartsWith(inputLower) &&
                        s.ToLowerInvariant().Contains(inputLower))
            .Take(5);

        var matches = startsWithMatches.Concat(containsMatches).Take(8).ToList();

        if (matches.Count > 0 && !matches.Any(m => m.Equals(input, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var match in matches)
            {
                FilteredSuggestions.Add(match);
            }
            ShowSuggestions = true;
        }
        else
        {
            ShowSuggestions = false;
        }
    }

    /// <summary>
    /// サジェストを選択
    /// </summary>
    [RelayCommand]
    public void SelectSuggestion(string suggestion)
    {
        BusStops = suggestion;
        ShowSuggestions = false;
    }

    /// <summary>
    /// サジェストを非表示
    /// </summary>
    [RelayCommand]
    public void HideSuggestions()
    {
        ShowSuggestions = false;
    }
}
