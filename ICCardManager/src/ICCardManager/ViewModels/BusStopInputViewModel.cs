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
    private readonly ISettingsRepository _settingsRepository;

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

    public BusStopInputViewModel(ILedgerRepository ledgerRepository, ISettingsRepository settingsRepository)
    {
        _ledgerRepository = ledgerRepository;
        _settingsRepository = settingsRepository;
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
    /// Issue #1133: 保存時に類似バス停名を検出して警告メッセージを返す
    /// </summary>
    internal static List<string> DetectSimilarBusStops(IEnumerable<string> existingSuggestions, IEnumerable<string> newEntries)
    {
        var warnings = new List<string>();
        var existing = existingSuggestions.ToList();

        foreach (var entry in newEntries)
        {
            if (string.IsNullOrWhiteSpace(entry) || entry == "★")
                continue;

            // 完全一致は除外（既存エントリと同じなら問題なし）
            var similar = existing
                .Where(s => !s.Equals(entry, StringComparison.Ordinal))
                .Where(s => IsSimilar(entry, s))
                .ToList();

            foreach (var s in similar)
            {
                warnings.Add($"「{entry}」は既存の「{s}」と類似しています");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Issue #1133: 2つのバス停名が類似しているか判定
    /// </summary>
    internal static bool IsSimilar(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        // 一方が他方を含む場合（「天神」vs「天神南」、「博多駅」vs「博多駅前」等）
        if (a.Contains(b) || b.Contains(a))
            return true;

        // 「～」区切りの場合、乗車・降車バス停をそれぞれ比較
        var aParts = a.Split('～');
        var bParts = b.Split('～');
        if (aParts.Length == 2 && bParts.Length == 2)
        {
            // 乗車と降車が入れ替わっている場合（「天神～博多」vs「博多～天神」）
            if (aParts[0].Trim() == bParts[1].Trim() && aParts[1].Trim() == bParts[0].Trim())
                return true;
        }

        return false;
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

        // ソフトバリデーション: 「～」区切りの形式チェック
        var missingTildeCount = BusUsages.Count(b =>
            !string.IsNullOrWhiteSpace(b.BusStops) && !b.BusStops.Contains("～"));
        if (missingTildeCount > 0)
        {
            StatusMessage = "「○○～△△」の形式での入力を推奨します";
            // 警告のみ — 保存はブロックしない
        }

        // Issue #1133: 類似バス停名の検出（警告のみ）
        var newEntries = BusUsages
            .Where(b => !string.IsNullOrWhiteSpace(b.BusStops) && b.BusStops != "★")
            .Select(b => b.BusStops)
            .ToList();
        var similarWarnings = DetectSimilarBusStops(BusStopSuggestions, newEntries);
        if (similarWarnings.Count > 0)
        {
            StatusMessage = similarWarnings.First();
            // 警告のみ — 保存はブロックしない
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

            // Issue #593: バス停名をledger_detailに保存（サジェスト候補の蓄積に必要）
            var busStopUpdates = BusUsages
                .Select(item => (item.Detail.SequenceNumber, item.Detail.BusStops))
                .ToList();
            await _ledgerRepository.UpdateDetailBusStopsAsync(Ledger.Id, busStopUpdates);

            // 摘要を再生成（バス停名を反映）
            var settings = await _settingsRepository.GetAppSettingsAsync();
            var summaryGenerator = new SummaryGenerator(settings.DepartmentType);
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

            // Issue #593: バス停名をledger_detailに保存
            var busStopUpdates = BusUsages
                .Select(item => (item.Detail.SequenceNumber, item.Detail.BusStops))
                .ToList();
            await _ledgerRepository.UpdateDetailBusStopsAsync(Ledger.Id, busStopUpdates);

            // 摘要を再生成（★マークを反映）
            var skipSettings = await _settingsRepository.GetAppSettingsAsync();
            var summaryGenerator = new SummaryGenerator(skipSettings.DepartmentType);
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
    public string AmountDisplay => DisplayFormatters.FormatAmountWithUnit(Amount);

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
    /// <remarks>
    /// Issue #1133: 空入力時も直近利用のバス停を表示（ワンタッチ入力対応）
    /// </remarks>
    internal void UpdateFilteredSuggestions(string input)
    {
        FilteredSuggestions.Clear();

        if (_allSuggestions.Count == 0)
        {
            ShowSuggestions = false;
            return;
        }

        List<string> matches;

        if (string.IsNullOrWhiteSpace(input))
        {
            // Issue #1133: 空入力時は直近利用順（=スコア順）のトップ候補を表示
            matches = _allSuggestions.Take(8).ToList();
        }
        else
        {
            // 入力文字列を含む候補を抽出（先頭一致優先、次に部分一致）
            var inputLower = input.ToLowerInvariant();

            var startsWithMatches = _allSuggestions
                .Where(s => s.ToLowerInvariant().StartsWith(inputLower))
                .Take(5);

            var containsMatches = _allSuggestions
                .Where(s => !s.ToLowerInvariant().StartsWith(inputLower) &&
                            s.ToLowerInvariant().Contains(inputLower))
                .Take(5);

            matches = startsWithMatches.Concat(containsMatches).Take(8).ToList();

            // 入力値と完全一致する候補のみの場合は表示しない
            if (matches.Count > 0 && matches.All(m => m.Equals(input, StringComparison.OrdinalIgnoreCase)))
            {
                ShowSuggestions = false;
                return;
            }
        }

        if (matches.Count > 0)
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

    /// <summary>
    /// Issue #1133: テキストボックスフォーカス時にサジェスト候補を表示
    /// </summary>
    public void OnTextBoxGotFocus()
    {
        UpdateFilteredSuggestions(BusStops);
    }
}
