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
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Issue #1811: 保存前の確認ダイアログに列挙する類似警告の上限件数。
    /// 「天神」のような短い入力は「天神」を含む既存候補すべてに一致するため、
    /// 超過分は「ほか N 件」に要約してダイアログが画面からはみ出さないようにする。
    /// </summary>
    internal const int MaxListedSimilarWarnings = 5;

    [ObservableProperty]
    private Ledger? _ledger;

    /// <summary>
    /// Issue #1203: 複数 Ledger を一括で扱う場合の対象 Ledger リスト。
    /// 単一 Ledger 初期化時は null のまま（<see cref="Ledger"/> を使用）。
    /// </summary>
    private List<Ledger>? _ledgers;

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

    public BusStopInputViewModel(
        ILedgerRepository ledgerRepository,
        ISettingsRepository settingsRepository,
        IDialogService dialogService)
    {
        _ledgerRepository = ledgerRepository;
        _settingsRepository = settingsRepository;
        _dialogService = dialogService;
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
            LinkPreviousItems();

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
        LinkPreviousItems();

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
    /// Issue #1203: 複数の Ledger のバス利用をまとめて1つのダイアログで編集するための初期化。
    /// 返却処理でバス利用が複数日にまたがる場合に、1件ずつダイアログを出さずまとめて入力させる用途。
    /// </summary>
    public async Task InitializeWithLedgersAsync(IEnumerable<Ledger> ledgers)
    {
        await LoadBusStopSuggestionsAsync();

        // 入力された Ledger は LendingService から返される in-memory インスタンスで
        // Details コレクションが populate されていない場合があるため、ID で DB から再取得する。
        // Id が 0（永続化前）または GetByIdAsync が null を返す場合は入力インスタンスをそのまま使う。
        var loaded = new List<Ledger>();
        foreach (var src in ledgers ?? Enumerable.Empty<Ledger>())
        {
            Ledger? full = null;
            if (src.Id > 0)
            {
                full = await _ledgerRepository.GetByIdAsync(src.Id);
            }
            loaded.Add(full ?? src);
        }

        _ledgers = loaded;
        // UI 表示互換のため Ledger プロパティには先頭を設定
        Ledger = _ledgers.FirstOrDefault();

        BusUsages.Clear();
        foreach (var ledger in _ledgers)
        {
            foreach (var detail in ledger.Details.Where(d => d.IsBus))
            {
                // LedgerId が未設定の場合は親 Ledger を参照できるよう補完
                if (detail.LedgerId == 0)
                {
                    detail.LedgerId = ledger.Id;
                }
                var item = new BusStopInputItem(detail);
                item.SetSuggestions(BusStopSuggestions);
                BusUsages.Add(item);
            }
        }
        LinkPreviousItems();

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
        LinkPreviousItems();

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
    /// Issue #1570: <see cref="BusUsages"/> の各アイテムに直前アイテムへの参照を設定する。
    /// 先頭は null。「往復」ボタンの活性制御に使用。
    /// </summary>
    private void LinkPreviousItems()
    {
        for (int i = 0; i < BusUsages.Count; i++)
        {
            BusUsages[i].PreviousItem = i == 0 ? null : BusUsages[i - 1];
        }
    }

    /// <summary>
    /// バス停名サジェスト候補を読み込み
    /// </summary>
    private async Task LoadBusStopSuggestionsAsync()
    {
        try
        {
            // Issue #1818: 除外するプレースホルダは組織設定由来のため、Data 層へ値として渡す
            //（永続化層に交通系固有の判断を持ち込まないため。設計書 05 §2a.5）
            var suggestions = await _ledgerRepository.GetBusStopSuggestionsAsync(
                SummaryGenerator.BusPlaceholder);
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
            if (string.IsNullOrWhiteSpace(entry) || SummaryGenerator.IsBusStopPlaceholder(entry))
                continue;

            // 完全一致は除外（既存エントリと同じなら問題なし）
            // 完全な逆順（「A～B」⇔「B～A」）も除外する（Issue #1811）:
            // 「↑往復」ボタン（Issue #1570）が前行の値を反転して生成する正当な入力であり、
            // 取り違えではない。含めると往復入力のたびに保存前の確認ダイアログが出て、
            // 本来見せたい取り違え警告（「天神」と「天神南」）が埋もれる。
            // なお逆順の2文字列は長さが等しいため、部分包含による類似と同時に成立することはない
            // （等しい長さで互いを含むのは完全一致のときだけで、それは上で除外済み）。
            var similar = existing
                .Where(s => !s.Equals(entry, StringComparison.Ordinal))
                .Where(s => IsSimilar(entry, s))
                .Where(s => !IsRoundTripReversal(entry, s))
                .ToList();

            foreach (var s in similar)
            {
                warnings.Add($"「{entry}」は既存の「{s}」と類似しています");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Issue #1811: 2つのバス停名が「A～B」と「B～A」の完全な逆順の関係にあるか判定する。
    /// </summary>
    /// <remarks>
    /// <see cref="IsSimilar"/> は乗降逆転を類似とみなす（Issue #1133）が、
    /// 「↑往復」ボタン（Issue #1570）はこの逆転値を意図的に生成する。
    /// 判定は <see cref="IsSimilar"/> の乗降逆転分岐と同じ（前後空白をトリムして比較）。
    /// </remarks>
    internal static bool IsRoundTripReversal(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        var aParts = a.Split('～');
        var bParts = b.Split('～');
        if (aParts.Length != 2 || bParts.Length != 2)
            return false;

        return aParts[0].Trim() == bParts[1].Trim()
            && aParts[1].Trim() == bParts[0].Trim();
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
    /// Issue #1811: 保存前に利用者へ提示する警告（未入力・形式・類似）を集める。
    /// いずれも保存をブロックしない「確認してほしい点」であり、空なら確認なしで保存してよい。
    /// </summary>
    /// <remarks>
    /// 以前はこれらを順に <see cref="StatusMessage"/> へ代入していたため、後の警告が前を上書きし、
    /// 保存成功時はさらに「保存しました」で上書きされた直後に <see cref="IsSaved"/> でダイアログが閉じ、
    /// 3 つのうち少なくとも 2 つは一度も職員の目に触れないまま台帳へ確定していた。
    /// </remarks>
    internal List<string> CollectSaveWarnings()
    {
        var warnings = new List<string>();

        // 未入力は保存可能（★マークが付き、後でバス停名未入力警告から入力する）
        var emptyCount = BusUsages.Count(b => string.IsNullOrWhiteSpace(b.BusStops));
        if (emptyCount > 0)
        {
            warnings.Add(
                $"未入力のバス停が{emptyCount}件あります" +
                $"（「{SummaryGenerator.BusPlaceholder}」として保存され、後で入力が必要になります）");
        }

        // ソフトバリデーション: 「～」区切りの形式チェック
        var missingTildeCount = BusUsages.Count(b =>
            !string.IsNullOrWhiteSpace(b.BusStops) && !b.BusStops.Contains("～"));
        if (missingTildeCount > 0)
        {
            warnings.Add($"「○○～△△」の形式になっていない入力が{missingTildeCount}件あります（乗車バス停～降車バス停の形式を推奨します）");
        }

        // Issue #1133: 類似バス停名の検出（取り違え・表記ゆれの疑い）
        var newEntries = BusUsages
            .Where(b => !string.IsNullOrWhiteSpace(b.BusStops)
                && !SummaryGenerator.IsBusStopPlaceholder(b.BusStops))
            .Select(b => b.BusStops)
            .ToList();
        // 同じバス停名を複数行に入力した場合（同一路線を1日に2回利用する等）、
        // DetectSimilarBusStops は行ごとに同じ文言を返す。重複したまま列挙すると
        // 確認ダイアログに同じ行が並び、上限（MaxListedSimilarWarnings）と
        // 「ほか N 件」の件数も重複分で水増しされるため、ここで一意化する。
        var similarWarnings = DetectSimilarBusStops(BusStopSuggestions, newEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        warnings.AddRange(similarWarnings.Take(MaxListedSimilarWarnings));
        if (similarWarnings.Count > MaxListedSimilarWarnings)
        {
            warnings.Add($"類似するバス停名がほか{similarWarnings.Count - MaxListedSimilarWarnings}件あります");
        }

        return warnings;
    }

    /// <summary>
    /// 保存
    /// </summary>
    /// <remarks>
    /// Issue #1811: 警告があるときは保存の<b>前</b>に確認ダイアログで全件を提示し、続行するかを職員に委ねる。
    /// 「いいえ」なら何も書かずに入力画面へ戻り、修正の手掛かりとして警告の全文をステータス欄に残す。
    /// 確認ダイアログは同期モーダルのため、処理中スコープ（<see cref="ViewModelBase.BeginBusy"/>）の
    /// 外で出す（Issue #1793）。
    /// </remarks>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (Ledger == null) return;

        var warnings = CollectSaveWarnings();
        if (warnings.Count > 0)
        {
            // 確認ダイアログを閉じた後も見直せるよう、先にステータス欄へ全件を出しておく
            StatusMessage = string.Join(Environment.NewLine, warnings);

            var message = "入力内容に確認が必要な点があります。" + Environment.NewLine + Environment.NewLine +
                          string.Join(Environment.NewLine, warnings.Select(w => "・" + w)) +
                          Environment.NewLine + Environment.NewLine +
                          "このまま保存しますか？" + Environment.NewLine +
                          "「いいえ」を選ぶと入力画面に戻って修正できます。";
            if (!_dialogService.ShowWarningConfirmation(message, "バス停名の確認"))
            {
                return;
            }
        }

        using (BeginBusy("保存中..."))
        {
            // 各バス利用のバス停名を更新
            foreach (var item in BusUsages)
            {
                item.Detail.BusStops = string.IsNullOrWhiteSpace(item.BusStops)
                    ? SummaryGenerator.BusPlaceholder // 未入力の場合はプレースホルダ
                    : item.BusStops;
            }

            var success = await PersistBusStopsAsync();

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
    /// Issue #1203: 単一 Ledger / 複数 Ledger の両モードに対応した保存処理。
    /// <see cref="_ledgers"/> が設定されていれば Ledger ごとにグルーピングして更新する。
    /// </summary>
    private async Task<bool> PersistBusStopsAsync()
    {
        var settings = await _settingsRepository.GetAppSettingsAsync();
        var summaryGenerator = new SummaryGenerator(settings.DepartmentType);

        var targetLedgers = _ledgers != null && _ledgers.Count > 0
            ? _ledgers
            : (Ledger != null ? new List<Ledger> { Ledger } : new List<Ledger>());

        if (targetLedgers.Count == 0) return false;

        var itemsByLedgerId = BusUsages.GroupBy(i => i.Detail.LedgerId).ToDictionary(g => g.Key, g => g.ToList());

        var allSuccess = true;
        foreach (var ledger in targetLedgers)
        {
            if (itemsByLedgerId.TryGetValue(ledger.Id, out var items))
            {
                var updates = items
                    .Select(item => (item.Detail.SequenceNumber, item.Detail.BusStops))
                    .ToList();
                await _ledgerRepository.UpdateDetailBusStopsAsync(ledger.Id, updates);
            }

            ledger.Summary = summaryGenerator.Generate(ledger.Details);
            var ok = await _ledgerRepository.UpdateAsync(ledger);
            if (!ok) allSuccess = false;
        }

        return allSuccess;
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
            // Issue #1156: スキップ時は入力済みの内容も破棄し、すべてプレースホルダにする
            foreach (var item in BusUsages)
            {
                item.BusStops = SummaryGenerator.BusPlaceholder;
                item.Detail.BusStops = SummaryGenerator.BusPlaceholder;
            }

            var success = await PersistBusStopsAsync();

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

    /// <summary>
    /// Issue #1570: 一つ前の行のアイテム。「往復」ボタンで参照する。
    /// 先頭行では null。<see cref="BusStopInputViewModel"/> の初期化処理で
    /// <see cref="BusStopInputViewModel.BusUsages"/> 構築後に直前のアイテムが設定される。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousItem))]
    private BusStopInputItem? _previousItem;

    /// <summary>
    /// Issue #1570: 「往復」ボタンを表示すべきか（前の行が存在するか）。
    /// XAML で BooleanToVisibilityConverter と組み合わせて表示制御に使う。
    /// </summary>
    public bool HasPreviousItem => PreviousItem != null;

    public DateTime? UseDate => Detail.UseDate;
    public string UseDateDisplay => Detail.UseDate.HasValue
        ? WarekiConverter.ToWareki(Detail.UseDate.Value)
        : "不明";
    public int? Amount => Detail.Amount;
    public string AmountDisplay => DisplayFormatters.FormatAmountWithUnit(Amount);

    public BusStopInputItem(LedgerDetail detail)
    {
        Detail = detail;
        // Issue #1205: 既存値が未入力プレースホルダー（既定「★」）のみの場合は、
        // ユーザーがわざわざ削除しなくても入力できるよう空欄として初期化する。
        // backing field への直接代入のため Detail.BusStops には書き戻さず、
        // 保存時の「空欄→プレースホルダ」変換ロジック（SaveAsync）で元の状態が維持される。
        // Issue #1818: プレースホルダは組織設定（SummaryText.BusPlaceholder）由来のため直書きしない。
        var initial = detail.BusStops ?? string.Empty;
        _busStops = SummaryGenerator.IsBusStopPlaceholder(initial) ? string.Empty : initial;
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
    /// Issue #1570: 一つ前の行の起点と終点を入れ替えた値を当該行にセットする（往復ボタン）。
    /// 前の行が空欄／プレースホルダ（既定「★」）のみ／「～」を含まない／
    /// 「～」で分割して2要素にならない場合は何もしない。
    /// </summary>
    [RelayCommand]
    public void ApplyRoundTrip()
    {
        if (PreviousItem == null) return;

        var source = PreviousItem.BusStops;
        if (string.IsNullOrWhiteSpace(source)) return;

        var parts = source.Split('～');
        if (parts.Length != 2) return;

        var from = parts[0].Trim();
        var to = parts[1].Trim();
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;

        BusStops = $"{to}～{from}";
    }

    /// <summary>
    /// Issue #1133: テキストボックスフォーカス時にサジェスト候補を表示
    /// </summary>
    public void OnTextBoxGotFocus()
    {
        UpdateFilteredSuggestions(BusStops);
    }
}
