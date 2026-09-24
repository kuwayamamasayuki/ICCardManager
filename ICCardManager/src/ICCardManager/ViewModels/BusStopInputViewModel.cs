using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;


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

            CapturePersistedState();
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

        CapturePersistedState();
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

        CapturePersistedState();
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

        CapturePersistedState();
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

        if (targetLedgers.Count == 0) return false;

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
            if (!await _ledgerRepository.UpdateAsync(ledger, scope.Transaction))
            {
                return false;
            }
        }

        scope.Commit();
        return true;
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
    private InMemoryStateSnapshot _persistedState;

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
    /// （入力のたびに書き込まれる）は、この復元の対象外である。
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
    /// スキップ（★マークを付けて保存）
    /// </summary>
    [RelayCommand]
    public async Task SkipAsync()
    {
        if (Ledger == null) return;

        using (BeginBusy("保存中..."))
        {
            // Issue #2103: スキップが失敗したら入力欄も元へ戻す（★で上書きしたまま残すと、
            // 職員の入力が失われ、そのまま「保存」をやり直すと★で保存される）
            var inputsBeforeSkip = BusUsages.Select(item => (item, item.BusStops)).ToList();
            var success = false;
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
                    foreach (var (item, busStops) in inputsBeforeSkip)
                    {
                        item.BusStops = busStops;
                    }
                    RestorePersistedState();
                }
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
    /// ダイアログの既定ボタン（Enter＝保存）・キャンセルボタン（Esc＝スキップ）へ届かないようにする。
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
