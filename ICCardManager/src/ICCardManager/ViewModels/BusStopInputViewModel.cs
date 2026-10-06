#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;


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
    /// Issue #1945: バス停名（<c>ledger_detail.bus_stops</c>）と摘要（<c>ledger.summary</c>）の
    /// 書き込みを 1 つのトランザクションに束ねるために保持する（Issue #1806）。
    /// 分けると、明細だけが確定して摘要が「バス（★）」のまま残る鏡像の不整合を作る。
    /// </summary>
    private readonly DbContext _dbContext;

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
    /// Issue #2251: バス停名 × 金額 × 「この返却の貸出者の利用か」の利用実績。
    /// 明細ごとの候補の並び（<see cref="BusStopInputAssistant.Rank"/>）と既定値（<see cref="BusStopInputAssistant.SelectDefault"/>）に使う。
    /// 読み込みに失敗したら空（候補は従来の全体の並びになり、既定値は入らない）。
    /// </summary>
    private List<BusStopUsageStatRow> _usageStats = new();

    /// <summary>
    /// Issue #2251: 往復の復路の自動補完を止めているか（スキップで全欄を「★」へ置き換える間と、その失敗時の復元の間）。
    /// </summary>
    private bool _suppressRoundTripAutoFill;

    /// <summary>
    /// 保存完了フラグ（ダイアログ結果用）
    /// </summary>
    [ObservableProperty]
    private bool _isSaved;

    public BusStopInputViewModel(
        ILedgerRepository ledgerRepository,
        ISettingsRepository settingsRepository,
        IDialogService dialogService,
        DbContext dbContext)
    {
        _ledgerRepository = ledgerRepository;
        _settingsRepository = settingsRepository;
        _dialogService = dialogService;
        _dbContext = dbContext;
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

            // サジェスト候補を読み込み
            // Issue #2251: 未入力一覧から開いた場合も、その行の貸出者を「同じ職員」とする
            await LoadBusStopSuggestionsAsync(Ledger.LenderIdm);

            // バス利用のみを抽出
            ClearBusUsages();
            foreach (var detail in Ledger.Details.Where(d => d.IsBus))
            {
                AddBusUsage(detail);
            }
            LinkPreviousItems();

            if (BusUsages.Count == 0)
            {
                StatusMessage = "バス利用の履歴がありません";
            }
            else
            {
                StatusMessage = $"{BusUsages.Count}件のバス利用があります。";
            }

            CapturePersistedState();
            HasUnsavedChanges = false;
            ApplyInitialAutoFill();
        }
    }

    /// <summary>
    /// バス利用詳細を直接設定して初期化（返却時用）
    /// </summary>
    public async Task InitializeWithDetailsAsync(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
    {
        // サジェスト候補を読み込み
        await LoadBusStopSuggestionsAsync(ledger.LenderIdm);

        Ledger = ledger;

        ClearBusUsages();
        foreach (var detail in busDetails.Where(d => d.IsBus))
        {
            AddBusUsage(detail);
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

        CapturePersistedState();
        HasUnsavedChanges = false;
        ApplyInitialAutoFill();
    }

    /// <summary>
    /// Issue #1203: 複数の Ledger のバス利用をまとめて1つのダイアログで編集するための初期化。
    /// 返却処理でバス利用が複数日にまたがる場合に、1件ずつダイアログを出さずまとめて入力させる用途。
    /// </summary>
    public async Task InitializeWithLedgersAsync(IEnumerable<Ledger> ledgers)
    {
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

        await LoadBusStopSuggestionsAsync(ResolveSingleLenderIdm(_ledgers));

        ClearBusUsages();
        foreach (var ledger in _ledgers)
        {
            foreach (var detail in ledger.Details.Where(d => d.IsBus))
            {
                // LedgerId が未設定の場合は親 Ledger を参照できるよう補完
                if (detail.LedgerId == 0)
                {
                    detail.LedgerId = ledger.Id;
                }
                AddBusUsage(detail);
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

        CapturePersistedState();
        HasUnsavedChanges = false;
        ApplyInitialAutoFill();
    }

    /// <summary>
    /// バス利用詳細を直接設定して初期化（返却時用・同期版）
    /// </summary>
    public void InitializeWithDetails(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
    {
        // Issue #2251: この経路は利用実績を読み込まない。前回の初期化で読んだ別の職員の実績で既定値を入れないよう捨てる
        _usageStats = new List<BusStopUsageStatRow>();
        Ledger = ledger;

        ClearBusUsages();
        foreach (var detail in busDetails.Where(d => d.IsBus))
        {
            AddBusUsage(detail);
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

        CapturePersistedState();
        HasUnsavedChanges = false;
        ApplyInitialAutoFill();
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
    /// Issue #2251: 明細 1 件分の入力欄を作って <see cref="BusUsages"/> へ加える。
    /// 候補はその明細の金額で並べ（同じ職員×同じ金額 → 同じ金額 → 同じ職員 → 全体）、
    /// 欄の値の変化を往復の復路の自動補完へつなぐ。
    /// </summary>
    private void AddBusUsage(LedgerDetail detail)
    {
        var item = new BusStopInputItem(detail);
        item.SetSuggestions(BusStopInputAssistant.Rank(_usageStats, BusStopSuggestions, detail.Amount));
        item.PropertyChanged += OnBusUsagePropertyChanged;
        BusUsages.Add(item);
    }

    /// <summary>
    /// Issue #2251: 入力欄をすべて取り除く。作り直す前の欄の値の変化を、往復の自動補完へ流さないよう購読を外す。
    /// </summary>
    private void ClearBusUsages()
    {
        foreach (var item in BusUsages)
        {
            item.PropertyChanged -= OnBusUsagePropertyChanged;
        }
        BusUsages.Clear();
    }

    /// <summary>
    /// Issue #2251: 複数の台帳をまとめて開いたときの「同じ職員」。貸出者が 1 人に決まるときだけその IDm、
    /// 決まらない（貸出者が無い・複数いる）ときは null（職員で並べず、既定値も入れない）。
    /// </summary>
    /// <remarks>
    /// 1 回の返却で作られる台帳の貸出者は同じ職員なので、通常は 1 人に決まる。
    /// 比較は SQL（<c>l.lender_idm = @lenderIdm</c>）と同じく大文字小文字を区別する（「同じ職員」の判断を 2 通りにしない）。
    /// </remarks>
    internal static string? ResolveSingleLenderIdm(IEnumerable<Ledger> ledgers)
    {
        var lenders = ledgers
            .Select(l => l.LenderIdm)
            .Where(idm => !string.IsNullOrEmpty(idm))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        return lenders.Count == 1 ? lenders[0] : null;
    }

    /// <summary>
    /// Issue #2251: 開いた時点の自動入力。上から順に、往復の続き（同じ金額・同じ利用日）の行には上の行の復路を、
    /// それ以外の未入力の行には既定値（同じ職員×同じ金額の単独 1 位が 2 回以上）を入れる。
    /// 自動で入れた欄があればステータス欄で知らせる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 保存済みの値（履歴から開いた行）と職員が触った欄には入れない。往復の続きの行には既定値を入れない
    /// （上の行を職員が入力したときに復路で入れ直せるよう、行の役割を 1 つに決める）。
    /// </para>
    /// <para>
    /// <see cref="CapturePersistedState"/> の後で呼ぶこと。自動で入れた値は明細（<see cref="LedgerDetail.BusStops"/>）にも
    /// 書き込まれるため、先に入れると「DB と同じ値」として退避され、保存に失敗したときの復元（#2103）が自動入力の値へ戻してしまう。
    /// </para>
    /// </remarks>
    internal void ApplyInitialAutoFill()
    {
        foreach (var item in BusUsages)
        {
            var previous = item.PreviousItem;
            if (previous != null && IsRoundTripContinuation(previous, item))
            {
                ApplyRoundTripAutoFill(previous, item);
            }
            else if (item.CanReceiveDefault)
            {
                var value = BusStopInputAssistant.SelectDefault(_usageStats, item.Amount);
                if (value != null)
                {
                    item.ApplyAutoFill(value, BusStopAutoFillKind.Suggested);
                }
            }
        }

        var autoFilledCount = BusUsages.Count(b => b.IsAutoFilled);
        if (autoFilledCount > 0)
        {
            // 開いた時点の件数として述べる（以後の書き換え・往復の入れ直しでは数え直さない。注記は欄ごとに出ている）
            StatusMessage += Environment.NewLine +
                             $"開いた時点で{autoFilledCount}件の欄に自動で入れました（欄の下に「自動入力」と表示）。内容を確かめ、違う場合は書き換えてください。";
        }
    }

    private static bool IsRoundTripContinuation(BusStopInputItem previous, BusStopInputItem item)
        => BusStopInputAssistant.IsRoundTripContinuation(previous.Amount, previous.UseDate, item.Amount, item.UseDate);

    /// <summary>
    /// Issue #2251: 上の行の値から、下の行へ往復の復路を入れる（入れ直す）。下の行を職員が触っていたら何もしない。
    /// 上の行が「A～B」の形でなくなったら、以前に入れた復路を取り除く。
    /// </summary>
    private static void ApplyRoundTripAutoFill(BusStopInputItem previous, BusStopInputItem item)
    {
        if (!item.CanReceiveRoundTrip)
        {
            return;
        }

        var reversed = BusStopInputAssistant.ReverseRoute(previous.BusStops);
        if (reversed != null)
        {
            item.ApplyAutoFill(reversed, BusStopAutoFillKind.RoundTrip);
        }
        else if (item.AutoFillKind == BusStopAutoFillKind.RoundTrip)
        {
            item.ClearAutoFill();
        }
    }

    /// <summary>
    /// Issue #2251: ある行の値が変わったら、往復の続きの次の行へ復路を入れ直す。
    /// 次の行の値が変わればさらに次の行へ伝わる（A～B, B～A, A～B …）。
    /// </summary>
    private void OnBusUsagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressRoundTripAutoFill
            || e.PropertyName != nameof(BusStopInputItem.BusStops)
            || sender is not BusStopInputItem item)
        {
            return;
        }

        var index = BusUsages.IndexOf(item);
        if (index < 0 || index + 1 >= BusUsages.Count)
        {
            return;
        }

        var next = BusUsages[index + 1];
        if (IsRoundTripContinuation(item, next))
        {
            ApplyRoundTripAutoFill(item, next);
        }
    }

    /// <summary>
    /// バス停名サジェスト候補を読み込み
    /// </summary>
    /// <param name="lenderIdm">Issue #2251: 「同じ職員」とみなす貸出者の IDm（決まらなければ null）</param>
    private async Task LoadBusStopSuggestionsAsync(string? lenderIdm)
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

        // Issue #2251: 失敗しても入力は続けられるよう、全体の候補とは別に受け止める（候補は全体の並びになり、既定値は入らない）
        try
        {
            var stats = await _ledgerRepository.GetBusStopUsageStatsAsync(
                SummaryGenerator.BusPlaceholder, lenderIdm);
            _usageStats = stats?.ToList() ?? new List<BusStopUsageStatRow>();
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.LogException(ex, "バス停名の利用実績の読み込み");
            _usageStats = new List<BusStopUsageStatRow>();
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
            {
                continue;
            }

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
        {
            return false;
        }

        var aParts = a.Split('～');
        var bParts = b.Split('～');
        if (aParts.Length != 2 || bParts.Length != 2)
        {
            return false;
        }

        return aParts[0].Trim() == bParts[1].Trim()
            && aParts[1].Trim() == bParts[0].Trim();
    }

    /// <summary>
    /// Issue #1133: 2つのバス停名が類似しているか判定
    /// </summary>
    internal static bool IsSimilar(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        // 一方が他方を含む場合（「天神」vs「天神南」、「博多駅」vs「博多駅前」等）
        if (a.Contains(b) || b.Contains(a))
        {
            return true;
        }

        // 「～」区切りの場合、乗車・降車バス停をそれぞれ比較
        var aParts = a.Split('～');
        var bParts = b.Split('～');
        if (aParts.Length == 2 && bParts.Length == 2)
        {
            // 乗車と降車が入れ替わっている場合（「天神～博多」vs「博多～天神」）
            if (aParts[0].Trim() == bParts[1].Trim() && aParts[1].Trim() == bParts[0].Trim())
            {
                return true;
            }
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

        // Issue #1914: 摘要は「ラベル＋全角括弧」の区切り書式のため、バス停名に
        // 対応の取れない全角括弧が入ると摘要からバス停名を取り出せなくなる
        //（履歴統合・摘要の直接編集での明細同期が働かない）。保存はブロックしないが、
        // 入力し直せる今のうちに気付けるよう確認へ載せる。
        var unbalancedCount = BusUsages.Count(b =>
            !string.IsNullOrWhiteSpace(b.BusStops)
            && !SummaryGenerator.HasBalancedFullWidthParentheses(b.BusStops));
        if (unbalancedCount > 0)
        {
            warnings.Add(
                $"全角括弧「（」「）」の対応が取れていない入力が{unbalancedCount}件あります" +
                "（摘要からバス停名を読み取れなくなります）。" +
                "括弧を対にするか、半角「(」「)」に置き換えてください");
        }

        // Issue #1133: 類似バス停名の検出（取り違え・表記ゆれの疑い）
        // Issue #2251: 本システムが自動で入れた値（既定値・往復の復路）は過去の入力そのもの（またはその乗降の入れ替え）なので、
        // 類似の確認から外す。外さないと、過去に「天神～博多駅」と「天神～博多駅前」の両方がある職員は、
        // 何も入力していなくても返却のたびに保存前の確認が出る（アプリ自身が生成した入力を自分で警告しない。#1811）
        var newEntries = BusUsages
            .Where(b => !b.IsAutoFilled)
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
        if (Ledger == null)
        {
            return;
        }

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
            // Issue #2103: 保存に失敗したら、メモリ上の明細（バス停名）と摘要を DB と同じ値へ戻す
            // （RestorePersistedState の remarks）。入力欄は戻さないので、職員はそのまま保存をやり直せる。
            var success = false;
            try
            {
                // 各バス利用のバス停名を更新
                foreach (var item in BusUsages)
                {
                    item.Detail.BusStops = string.IsNullOrWhiteSpace(item.BusStops)
                        ? SummaryGenerator.BusPlaceholder // 未入力の場合はプレースホルダ
                        : item.BusStops;
                }

                success = await PersistBusStopsAsync();
            }
            finally
            {
                if (!success)
                {
                    RestorePersistedState();
                }
            }

            if (success)
            {
                CapturePersistedState();
                StatusMessage = "保存しました";
                HasUnsavedChanges = false;
                IsSaved = true;
            }
            else
            {
                StatusMessage = HasBusStopUpdateConflict ? BusStopConflictMessage : "保存に失敗しました";
            }
        }
    }

    /// <summary>
    /// Issue #1203: 単一 Ledger / 複数 Ledger の両モードに対応した保存処理。
    /// <see cref="_ledgers"/> が設定されていれば Ledger ごとにグルーピングして更新する。
    /// </summary>
    private async Task<bool> PersistBusStopsAsync()
    {
        HasBusStopUpdateConflict = false;
        var settings = await _settingsRepository.GetAppSettingsAsync();
        var summaryGenerator = new SummaryGenerator(settings.DepartmentType);

        var targetLedgers = GetTargetLedgers();

        if (targetLedgers.Count == 0)
        {
            return false;
        }

        var itemsByLedgerId = BusUsages.GroupBy(i => i.Detail.LedgerId).ToDictionary(g => g.Key, g => g.ToList());

        // Issue #1945 / #1806: バス停名（ledger_detail.bus_stops）と摘要（ledger.summary）は
        // 同じ事実を 2 か所に持つため、片方だけ確定すると 6 年保存の台帳が自己矛盾する
        // （摘要は「バス（天神～博多）」なのに明細は★／その鏡像で明細だけが新しい）。
        // 摘要の再生成はこの明細から行われるので、次の統合で摘要が古い値へ巻き戻る。
        // 複数 Ledger をまとめて 1 つのトランザクションで書き、1 件でも失敗したら全部巻き戻す。
        //
        // 設定の読み取り（GetAppSettingsAsync）はスコープを開く前に済ませてある。
        // スコープ内で別のリポジトリが LeaseConnectionAsync を呼ぶと、DbContext のセマフォを
        // 二重に取って自己デッドロックするため（Issue #1575）。
        //
        // Issue #2202: トランザクションの本体ごと UI スレッドの外へ移す（DbContext.RunOffUiThreadAsync の remarks）。
        // トランザクションを渡したリポジトリの SQL は DbContext の入口を通らないため、ここで移さないと
        // UI スレッドの上で走り、開いたトランザクションの途中でほかの UI 起点の処理が割り込み得る。
        return await _dbContext.RunOffUiThreadAsync(async () =>
        {
            using var scope = await _dbContext.BeginTransactionAsync();

            foreach (var ledger in targetLedgers)
            {
                if (itemsByLedgerId.TryGetValue(ledger.Id, out var items))
                {
                    var updates = items
                        .Select(item => (item.Detail.SequenceNumber, item.Detail.BusStops))
                        .ToList();

                    // Issue #1945: 戻り値を握りつぶさない。履歴詳細の全置換（ReplaceDetailsAsync の
                    // DELETE + INSERT）で id が振り直されていると 0 行になる。
                    var detailsOk = await _ledgerRepository.UpdateDetailBusStopsAsync(
                        ledger.Id, updates, scope.Transaction);
                    if (!detailsOk)
                    {
                        // commit せずに抜ける（scope の Dispose で巻き戻る）
                        HasBusStopUpdateConflict = true;
                        return false;
                    }
                }

                ledger.Summary = summaryGenerator.Generate(ledger.Details);
                // Issue #2212: 摘要だけを SET する。履歴から開いたときの ledger は画面を開いた時点の値なので、
                // 全列を SET する UpdateAsync を使うと、その間に他 PC が直した備考・同行者数を巻き戻す。
                if (!await _ledgerRepository.UpdateSummaryAsync(ledger.Id, ledger.Summary, scope.Transaction))
                {
                    return false;
                }
            }

            scope.Commit();
            return true;
        });
    }

    /// <summary>
    /// 保存対象の Ledger（Issue #1203: 複数 Ledger モードならその全件、単一モードなら <see cref="Ledger"/>）。
    /// </summary>
    private List<Ledger> GetTargetLedgers()
    {
        if (_ledgers != null && _ledgers.Count > 0)
        {
            return _ledgers;
        }
        return Ledger != null ? new List<Ledger> { Ledger } : new List<Ledger>();
    }

    /// <summary>
    /// Issue #2103: DB に保存されているのと同じメモリ上の値（明細のバス停名・摘要）。
    /// 初期化時と保存成功時に取り直す。
    /// </summary>
    private InMemoryStateSnapshot? _persistedState;

    /// <summary>
    /// Issue #2103: 現在のメモリ上の値を「DB と同じ値」として退避する。
    /// </summary>
    /// <remarks>
    /// 初期化の時点で取る。入力欄のバス停名は入力のたびに <see cref="LedgerDetail.BusStops"/> へ
    /// そのまま書き込まれる（<see cref="BusStopInputItem"/> の <c>OnBusStopsChanged</c>）ため、
    /// 保存を始めた時点で取っても、既に保存前の値は失われている。
    /// </remarks>
    private void CapturePersistedState()
    {
        var details = BusUsages
            .Select(item => (item.Detail, item.Detail.BusStops))
            .ToList();
        var summaries = GetTargetLedgers()
            .Select(ledger => (ledger, ledger.Summary))
            .ToList();
        _persistedState = new InMemoryStateSnapshot(details, summaries);
    }

    /// <summary>
    /// Issue #2103: 保存に失敗したとき、メモリ上の明細と摘要を DB と同じ値へ戻す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// DB はトランザクションで巻き戻るが、書き換えた <see cref="Ledger"/> / <see cref="LedgerDetail"/> は巻き戻らない。
    /// 戻さないと、メモリ上は「バス（天神～博多）」なのに台帳は「バス（★）」のままという食い違いが残る。
    /// </para>
    /// <para>
    /// とくに <see cref="InitializeWithLedgersAsync"/> で DB から読み直せなかった Ledger（Id が 0、
    /// または他のパソコンで削除された）は呼び出し元のインスタンスをそのまま書き換えており、
    /// 返却フローでは同じインスタンスが直後の同行者数入力ダイアログへ渡る。「他のパソコンで削除された」は
    /// 摘要の更新が失敗する典型的な原因でもあるため、失敗と共有はそろって起きる。
    /// 読み直せた Ledger はこの ViewModel 専用のインスタンスで、呼び出し元とは共有しない。
    /// </para>
    /// <para>
    /// 入力欄（<see cref="BusStopInputItem.BusStops"/>）は戻さないので、職員はそのまま保存をやり直せる
    /// （保存のたびに入力欄の値を明細へ書き直すため）。保存せずに閉じた場合の明細の書き込み
    /// （入力のたびに書き込まれる）は、この復元の対象外である。Issue #2251 の自動入力（既定値・往復の復路）も
    /// 開いた時点で明細へ書き込まれるので同じ扱いになる（読み直せなかった Ledger では呼び出し元のインスタンスに残る。
    /// 後続の同行者数入力は <c>companion_count</c> しか書かないため、現状は台帳へは届かない）。
    /// </para>
    /// </remarks>
    private void RestorePersistedState()
    {
        _persistedState?.Restore();
    }

    /// <summary>
    /// Issue #2103: 退避したメモリ上の値。<see cref="Restore"/> で書き戻す。
    /// </summary>
    private sealed class InMemoryStateSnapshot
    {
        private readonly List<(LedgerDetail Detail, string BusStops)> _details;
        private readonly List<(Ledger Ledger, string Summary)> _summaries;

        public InMemoryStateSnapshot(
            List<(LedgerDetail Detail, string BusStops)> details,
            List<(Ledger Ledger, string Summary)> summaries)
        {
            _details = details;
            _summaries = summaries;
        }

        public void Restore()
        {
            foreach (var (detail, busStops) in _details)
            {
                detail.BusStops = busStops;
            }
            foreach (var (ledger, summary) in _summaries)
            {
                ledger.Summary = summary;
            }
        }
    }

    /// <summary>
    /// Issue #1945: 直近の保存でバス停名の更新が競合（影響行数 0）したかどうか。
    /// 失敗の理由が「行が見つからない」ことに特定できるため、汎用の失敗文言と区別して案内する。
    /// </summary>
    private bool HasBusStopUpdateConflict { get; set; }

    /// <summary>
    /// Issue #1945: 保存失敗時の案内文言（「何が」「なぜ」「どうすれば」の 3 要素）。
    /// </summary>
    internal const string BusStopConflictMessage =
        "バス停名を保存できませんでした。この履歴の明細が、他のパソコンや履歴の編集操作で" +
        "変更された可能性があります。画面を閉じて履歴一覧を再読み込みし、" +
        "最新の内容を確認してから入力し直してください。";

    /// <summary>
    /// Issue #2142: スキップで破棄される入力がある場合の確認ダイアログのタイトル。
    /// </summary>
    internal const string SkipDiscardConfirmationTitle = "入力内容の破棄の確認";

    /// <summary>
    /// Issue #2142: スキップすると失われる入力（空欄でも「★」でもないバス停名）が 1 つ以上あるか。
    /// </summary>
    /// <remarks>
    /// Issue #2251: 本システムが自動で入れた値（既定値・往復の復路）は職員の入力ではないので数えない。
    /// 数えると、何も入力していないのに Esc（スキップ）のたびに破棄の確認が出る。保存済みの値（履歴から開いた行）は数える。
    /// </remarks>
    internal bool HasInputDiscardedBySkip()
        => BusUsages.Any(b => !b.IsAutoFilled
                              && !string.IsNullOrWhiteSpace(b.BusStops)
                              && !SummaryGenerator.IsBusStopPlaceholder(b.BusStops));

    /// <summary>
    /// Issue #2142: Esc キーによる閉じる要求。このダイアログを閉じる手段はスキップ（★で保存）なので、確認つきの
    /// <see cref="SkipAsync"/> へ委譲する。
    /// </summary>
    /// <remarks>
    /// スキップボタンに <c>IsCancel</c> を付けると、WPF は Click 処理の後に無条件で <c>DialogResult=false</c> を設定するため、
    /// 破棄の確認で「いいえ」を選んでもダイアログが閉じる（★も保存されず入力も失われる）。Esc はこのコマンドへ
    /// <c>KeyBinding</c> で結線し、閉じるのは <see cref="IsSaved"/> を契機にするだけにする。
    /// </remarks>
    [RelayCommand]
    private Task RequestCloseAsync() => SkipAsync();

    /// <summary>
    /// スキップ（★マークを付けて保存）
    /// </summary>
    /// <remarks>
    /// Issue #2142: スキップは入力済みの内容も「★」へ置き換える（#1156）ため、入力済みの欄があるときだけ
    /// 確認を挟む。スキップは Esc にも割り当たっており（当初はスキップボタンの <c>IsCancel</c>、現在は <see cref="RequestCloseCommand"/>）、元に戻せない破棄が
    /// Esc 1 回で起きていた。確認はボタンと Esc の両方が通るこのメソッドに置く（片方にだけ置くと
    /// 同じ判断が 2 か所に分かれる。ui-conventions #2080）。確認ダイアログは同期モーダルなので
    /// 処理中スコープの外で出す（#1793）。
    /// </remarks>
    [RelayCommand]
    public async Task SkipAsync()
    {
        if (Ledger == null)
        {
            return;
        }

        if (HasInputDiscardedBySkip())
        {
            var message = "入力したバス停名は保存されず、すべて「" + SummaryGenerator.BusPlaceholder +
                          "」（後で入力が必要）になります。" + Environment.NewLine + Environment.NewLine +
                          "入力内容を破棄してスキップしますか？" + Environment.NewLine +
                          "入力内容を残す場合は「いいえ」を選び、「保存」を押してください。";
            if (!_dialogService.ShowWarningConfirmation(message, SkipDiscardConfirmationTitle))
            {
                return;
            }
        }

        using (BeginBusy("保存中..."))
        {
            // Issue #2103: スキップが失敗したら入力欄も元へ戻す（★で上書きしたまま残すと、
            // 職員の入力が失われ、そのまま「保存」をやり直すと★で保存される）
            // Issue #2251: 自動入力の理由・操作の有無も一緒に退避する（値だけ戻すと、自動で入れた欄が職員の入力に化ける）
            var inputsBeforeSkip = BusUsages.Select(item => (item, item.CaptureInputState())).ToList();
            var success = false;
            // Issue #2251: 全欄を「★」へ置き換える間とその復元の間は、往復の復路の自動補完を止める
            _suppressRoundTripAutoFill = true;
            try
            {
                // Issue #1156: スキップ時は入力済みの内容も破棄し、すべてプレースホルダにする
                foreach (var item in BusUsages)
                {
                    item.BusStops = SummaryGenerator.BusPlaceholder;
                    item.Detail.BusStops = SummaryGenerator.BusPlaceholder;
                }

                success = await PersistBusStopsAsync();
            }
            finally
            {
                // Issue #2103: 保存と同じく、失敗したらメモリ上の明細と摘要を DB と同じ値へ戻す。
                // 入力欄の復元は明細へも書き込む（OnBusStopsChanged）ため、明細の復元より先に行う
                if (!success)
                {
                    foreach (var (item, state) in inputsBeforeSkip)
                    {
                        item.RestoreInputState(state);
                    }
                    RestorePersistedState();
                }
                _suppressRoundTripAutoFill = false;
            }

            if (success)
            {
                CapturePersistedState();
                StatusMessage = "スキップしました（後で入力が必要です）";
                IsSaved = true;
            }
            else
            {
                StatusMessage = HasBusStopUpdateConflict ? BusStopConflictMessage : "保存に失敗しました";
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
    /// Issue #2072: キーボード（↓↑）で選択中の候補の位置。-1 は「どの候補も選んでいない」。
    /// </summary>
    /// <remarks>
    /// 候補の並びが変わる（入力で再フィルターされる）か候補が閉じたら -1 へ戻す。
    /// 前の並びの位置を引き継ぐと、Enter で職員が見ていない候補を確定してしまう。
    /// </remarks>
    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

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

    /// <summary>
    /// Issue #2251: この欄の値を本システムが自動で入れたか（入れた理由）。職員が欄を書き換えたら <see cref="BusStopAutoFillKind.None"/> へ戻る。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoFilled))]
    [NotifyPropertyChangedFor(nameof(AutoFillNote))]
    private BusStopAutoFillKind _autoFillKind;

    /// <summary>
    /// Issue #2251: 自動で入れた値か。入力欄の下の注記の表示に使う（色だけに頼らず文言で区別する）。
    /// </summary>
    public bool IsAutoFilled => AutoFillKind != BusStopAutoFillKind.None;

    /// <summary>
    /// Issue #2251: 自動で入れた理由の注記。自動で入れていなければ空文字。
    /// </summary>
    public string AutoFillNote => AutoFillKind switch
    {
        BusStopAutoFillKind.Suggested => SuggestedAutoFillNote,
        BusStopAutoFillKind.RoundTrip => RoundTripAutoFillNote,
        _ => string.Empty,
    };

    /// <summary>Issue #2251: 既定値（同じ職員×同じ金額の実績）で入れた欄の注記。</summary>
    internal const string SuggestedAutoFillNote =
        "自動入力：これまでの入力（同じ職員・同じ金額）から入れました。違う場合は書き換えてください";

    /// <summary>Issue #2251: 往復の復路として入れた欄の注記。</summary>
    internal const string RoundTripAutoFillNote =
        "自動入力：上の行の帰り（往復）として入れました。違う場合は書き換えてください";

    /// <summary>
    /// Issue #2251: 職員がこの欄を操作したか（入力・候補の選択・「↑往復」ボタン）。
    /// 一度でも操作した欄には、空に戻した後も自動で値を入れない（#1729「無い値の補完」と「有る値の上書き」を分ける）。
    /// </summary>
    public bool IsTouchedByUser { get; private set; }

    /// <summary>
    /// Issue #2251: 自動入力の最中か。<c>OnBusStopsChanged</c> が職員の操作と区別するために見る。
    /// </summary>
    private bool _isApplyingAutoFill;

    /// <summary>
    /// Issue #2251: 既定値を入れてよいか（未入力で、職員がまだ触っていない）。
    /// </summary>
    internal bool CanReceiveDefault
        => !IsTouchedByUser && AutoFillKind == BusStopAutoFillKind.None && string.IsNullOrWhiteSpace(BusStops);

    /// <summary>
    /// Issue #2251: 往復の復路を入れてよいか（未入力で職員がまだ触っていない、または前回の復路の補完のまま）。
    /// 復路の補完のままの欄は、上の行が変わったら入れ直す。
    /// </summary>
    internal bool CanReceiveRoundTrip
        => !IsTouchedByUser
           && (AutoFillKind == BusStopAutoFillKind.RoundTrip
               || (AutoFillKind == BusStopAutoFillKind.None && string.IsNullOrWhiteSpace(BusStops)));

    /// <summary>
    /// Issue #2251: 値を自動で入れる。職員の操作としては数えない（<see cref="IsTouchedByUser"/> を立てない）。
    /// 候補も開かない（入れる先は、職員が今入力している欄ではない）。
    /// </summary>
    internal void ApplyAutoFill(string value, BusStopAutoFillKind kind)
    {
        SetBusStopsWithoutUserInput(value);
        AutoFillKind = kind;
    }

    /// <summary>
    /// Issue #2251: 自動で入れた値を取り除き、未入力・未操作の状態へ戻す。
    /// </summary>
    internal void ClearAutoFill()
    {
        SetBusStopsWithoutUserInput(string.Empty);
        AutoFillKind = BusStopAutoFillKind.None;
    }

    /// <summary>
    /// Issue #2251: 入力欄の状態（値・自動入力の理由・操作の有無）を退避する。スキップの失敗時に戻すため。
    /// </summary>
    internal (string BusStops, BusStopAutoFillKind AutoFillKind, bool IsTouchedByUser) CaptureInputState()
        => (BusStops, AutoFillKind, IsTouchedByUser);

    /// <summary>
    /// Issue #2251: <see cref="CaptureInputState"/> で退避した状態へ戻す。
    /// </summary>
    internal void RestoreInputState((string BusStops, BusStopAutoFillKind AutoFillKind, bool IsTouchedByUser) state)
    {
        SetBusStopsWithoutUserInput(state.BusStops);
        AutoFillKind = state.AutoFillKind;
        IsTouchedByUser = state.IsTouchedByUser;
    }

    private void SetBusStopsWithoutUserInput(string value)
    {
        _isApplyingAutoFill = true;
        try
        {
            BusStops = value;
        }
        finally
        {
            _isApplyingAutoFill = false;
        }
    }

    /// <summary>
    /// Issue #2251: 職員がこの欄の値を決めたことを記録する。自動で入れた値と同じ値を選び直した場合も、確認済みとして扱う。
    /// </summary>
    private void MarkAsUserInput()
    {
        IsTouchedByUser = true;
        AutoFillKind = BusStopAutoFillKind.None;
    }

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

        // Issue #2251: 自動入力・状態の復元は職員の操作ではないので、操作済みの印を付けず候補も開かない
        if (_isApplyingAutoFill)
        {
            ShowSuggestions = false;
            return;
        }

        MarkAsUserInput();
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
        SelectedSuggestionIndex = -1;
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
            // 大文字小文字は区別しない。比較は文化圏に依存させない（CA1310 / CA1862）。
            var startsWithMatches = _allSuggestions
                .Where(s => s.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                .Take(5);

            var containsMatches = _allSuggestions
                .Where(s => !s.StartsWith(input, StringComparison.OrdinalIgnoreCase) &&
                            s.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
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
        // Issue #2251: 自動で入れた値と同じ候補を選んだときは値が変わらず OnBusStopsChanged が走らないので、ここでも記録する
        MarkAsUserInput();
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

    partial void OnShowSuggestionsChanged(bool value)
    {
        // 候補が閉じたら選択も捨てる（Popup の StaysOpen=False で外側クリックにより閉じた場合を含む）
        if (!value)
        {
            SelectedSuggestionIndex = -1;
        }
    }

    /// <summary>
    /// Issue #2072: 入力欄で押されたキーを候補リストの操作として処理する。
    /// </summary>
    /// <param name="key">押されたキー（IME 変換中は <see cref="Key.ImeProcessed"/> が来るため処理しない）</param>
    /// <returns>
    /// 候補リストの操作として消費した場合 true。呼び出し側はキーを処理済みにし、
    /// ダイアログの既定ボタン（Enter＝保存）・Esc の KeyBinding（スキップ。#2142）へ届かないようにする。
    /// </returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>↓: 次の候補を選ぶ。候補が閉じていれば開いて先頭を選ぶ</item>
    /// <item>↑: 前の候補を選ぶ。先頭で押すと選択を外す（入力中の文字列へ戻る）</item>
    /// <item>Enter: 選択中の候補を確定する。候補が開いているが未選択なら候補を閉じるだけで保存しない</item>
    /// <item>Esc: 候補を閉じる（スキップしない）</item>
    /// </list>
    /// 候補が閉じているときの Enter / Esc は消費しない（従来どおり保存 / スキップ）。
    /// 候補が開いている間の Enter で保存すると、入力途中の文字列がそのまま 6 年保存の台帳へ入る。
    /// </remarks>
    public bool HandleSuggestionKey(Key key)
    {
        switch (key)
        {
            case Key.Down:
                if (!ShowSuggestions)
                {
                    UpdateFilteredSuggestions(BusStops);
                    if (!ShowSuggestions)
                    {
                        return false;
                    }
                }
                if (FilteredSuggestions.Count == 0)
                {
                    return false;
                }
                SelectedSuggestionIndex = Math.Min(SelectedSuggestionIndex + 1, FilteredSuggestions.Count - 1);
                return true;

            case Key.Up:
                if (!ShowSuggestions)
                {
                    return false;
                }
                SelectedSuggestionIndex = Math.Max(SelectedSuggestionIndex - 1, -1);
                return true;

            case Key.Enter:
                if (!ShowSuggestions)
                {
                    return false;
                }
                if (SelectedSuggestionIndex >= 0 && SelectedSuggestionIndex < FilteredSuggestions.Count)
                {
                    SelectSuggestion(FilteredSuggestions[SelectedSuggestionIndex]);
                }
                else
                {
                    ShowSuggestions = false;
                }
                return true;

            case Key.Escape:
                if (!ShowSuggestions)
                {
                    return false;
                }
                ShowSuggestions = false;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Issue #1570: 一つ前の行の起点と終点を入れ替えた値を当該行にセットする（往復ボタン）。
    /// 前の行が空欄／プレースホルダ（既定「★」）のみ／「～」を含まない／
    /// 「～」で分割して2要素にならない場合は何もしない。
    /// </summary>
    [RelayCommand]
    public void ApplyRoundTrip()
    {
        if (PreviousItem == null)
        {
            return;
        }

        // Issue #2251: 入れ替えの判断は往復の自動補完と共有する（同じ判断を 2 か所に書かない。#1763）
        var reversed = BusStopInputAssistant.ReverseRoute(PreviousItem.BusStops);
        if (reversed is null)
        {
            return;
        }

        BusStops = reversed;
        // 自動で入れた復路と同じ値なら OnBusStopsChanged が走らないので、ボタンを押したことをここでも記録する
        MarkAsUserInput();
    }

    /// <summary>
    /// Issue #1133: テキストボックスフォーカス時にサジェスト候補を表示
    /// </summary>
    public void OnTextBoxGotFocus()
    {
        UpdateFilteredSuggestions(BusStops);
    }
}

/// <summary>
/// Issue #2251: バス停名の入力欄に本システムが自動で値を入れた理由。
/// </summary>
public enum BusStopAutoFillKind
{
    /// <summary>自動で入れていない（未入力・職員の入力・保存済みの値）</summary>
    None,

    /// <summary>同じ職員×同じ金額の実績から既定値として入れた</summary>
    Suggested,

    /// <summary>上の行の往復の復路として入れた</summary>
    RoundTrip,
}
