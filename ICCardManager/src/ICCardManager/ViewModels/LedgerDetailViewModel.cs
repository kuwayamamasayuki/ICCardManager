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
using Microsoft.Extensions.Logging;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 利用履歴詳細表示用のアイテム
    /// </summary>
    public partial class LedgerDetailItemViewModel : ObservableObject
    {
        /// <summary>
        /// 元のLedgerDetail
        /// </summary>
        public LedgerDetail Detail { get; }

        /// <summary>
        /// リスト内のインデックス（選択操作用）
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 選択状態
        /// </summary>
        [ObservableProperty]
        private bool _isSelected;

        /// <summary>
        /// グループID
        /// </summary>
        [ObservableProperty]
        private int? _groupId;

        /// <summary>
        /// 利用日時表示
        /// </summary>
        public string UseDateDisplay => DisplayFormatters.FormatDateTime(Detail.UseDate);

        /// <summary>
        /// 区間表示
        /// </summary>
        /// <remarks>
        /// Issue #1023: RouteDisplayFormatter に委譲して LedgerDetailDto との重複を解消。
        /// 詳細表示用: 駅名区切り「 → 」、片方のみは非表示、フォールバック「-」
        /// </remarks>
        public string RouteDisplay =>
            RouteDisplayFormatter.Format(
                Detail.IsCharge, Detail.IsPointRedemption, Detail.IsBus, Detail.BusStops,
                Detail.EntryStation, Detail.ExitStation,
                stationSeparator: " → ",
                showPartialStations: false,
                fallback: "-");

        /// <summary>
        /// 金額表示
        /// </summary>
        public string AmountDisplay => DisplayFormatters.FormatAmountWithUnit(Detail.Amount, "-");

        /// <summary>
        /// 残高表示
        /// </summary>
        public string BalanceDisplay => DisplayFormatters.FormatAmountWithUnit(Detail.Balance, "-");

        /// <summary>
        /// チャージフラグ
        /// </summary>
        public bool IsCharge => Detail.IsCharge;

        /// <summary>
        /// バス利用フラグ
        /// </summary>
        public bool IsBus => Detail.IsBus;

        /// <summary>
        /// グループ表示色のインデックス（グループごとに異なる色を割り当てるため）
        /// </summary>
        [ObservableProperty]
        private int _groupColorIndex;

        /// <summary>
        /// グループラベル（A, B, C...）アクセシビリティ対応: Issue #548
        /// 色だけでなくアルファベットでもグループを識別可能に
        /// </summary>
        [ObservableProperty]
        private string _groupLabel = "-";

        /// <summary>
        /// このアイテムの下に分割線を表示するか（Issue #548: 分割線UI）
        /// </summary>
        [ObservableProperty]
        private bool _showDividerBelow;

        public LedgerDetailItemViewModel(LedgerDetail detail, int index)
        {
            Detail = detail;
            Index = index;
            _groupId = detail.GroupId;
        }
    }

    /// <summary>
    /// 利用履歴詳細ダイアログ用ViewModel（Issue #484: 統合・分割機能対応）
    /// Issue #548: 分割線クリック方式UIに変更
    /// </summary>
    public partial class LedgerDetailViewModel : ObservableObject, IBusyState
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly SummaryGenerator _summaryGenerator;
        private readonly LedgerDetailSaveService _saveService;
        private readonly ILogger<LedgerDetailViewModel> _logger;

        private Ledger _ledger = null!;

        /// <summary>
        /// DB に確定している状態の複製（読み込み時と、保存が確定したときに採る）。監査ログの「変更前」に使う（Issue #2177）。
        /// </summary>
        /// <remarks>
        /// 画面の明細（<c>item.Detail</c>）は <c>_ledger.Details</c> と同じインスタンスで、保存のたびに GroupId を書き換える。
        /// 保存が失敗したあとの再保存で <c>_ledger</c> から「変更前」を採ると、書き換え済みの GroupId を写して
        /// 監査ログの変更前と変更後が同じになる（#1979 の再発。コードレビューで検出）。
        /// </remarks>
        private Ledger _committedLedger = null!;

        /// <summary>
        /// 保存で競合（他 PC による削除・統合）を検出したか（Issue #2177）。
        /// </summary>
        private bool _conflictDetected;

        /// <summary>
        /// カード名（パンくず表示用）
        /// </summary>
        private string? _cardName;

        /// <summary>
        /// 詳細アイテムリスト
        /// </summary>
        public ObservableCollection<LedgerDetailItemViewModel> Items { get; } = new();

        /// <summary>
        /// 日付表示
        /// </summary>
        [ObservableProperty]
        private string _dateDisplay = string.Empty;

        /// <summary>
        /// 摘要表示
        /// </summary>
        [ObservableProperty]
        private string _summaryDisplay = string.Empty;

        /// <summary>
        /// 受入金額表示
        /// </summary>
        [ObservableProperty]
        private string _incomeDisplay = string.Empty;

        /// <summary>
        /// 払出金額表示
        /// </summary>
        [ObservableProperty]
        private string _expenseDisplay = string.Empty;

        /// <summary>
        /// 残高表示
        /// </summary>
        [ObservableProperty]
        private string _balanceDisplay = string.Empty;

        /// <summary>
        /// 利用者名
        /// </summary>
        [ObservableProperty]
        private string _staffName = string.Empty;

        /// <summary>
        /// 備考
        /// </summary>
        [ObservableProperty]
        private string _note = string.Empty;

        /// <summary>
        /// 詳細件数表示
        /// </summary>
        [ObservableProperty]
        private string _detailCountDisplay = string.Empty;

        /// <summary>
        /// 変更があるかどうか
        /// </summary>
        [ObservableProperty]
        private bool _hasChanges;

        /// <summary>
        /// 処理中かどうか
        /// </summary>
        [ObservableProperty]
        private bool _isBusy;

        /// <summary>
        /// ステータスメッセージ
        /// </summary>
        [ObservableProperty]
        private string _statusMessage = string.Empty;

        /// <summary>
        /// 保存完了時のコールバック
        /// </summary>
        public Action? OnSaveCompleted { get; set; }

        /// <summary>
        /// クローズ要求時のコールバック（Issue #1743: Escape キーの KeyBinding から Window.Close() へ届く経路）
        /// </summary>
        public Action? OnCloseRequested { get; set; }

        /// <summary>
        /// このダイアログで 1 件でも DB へ書き込みが確定したか（Issue #1743）
        /// </summary>
        /// <remarks>
        /// 呼び出し元はこのフラグを見て一覧を再読込する（画面の旧グループと DB の新 GroupId を食い違わせない）。
        /// Issue #2177 以降、保存（明細の置換・摘要の更新・監査ログ）は 1 つのトランザクションで確定するため、
        /// 保存に失敗したときは何も確定しておらず、このフラグも立たない（立つのは保存・分割が確定したときだけ）。
        /// </remarks>
        public bool HasPersistedChanges { get; private set; }

        /// <summary>
        /// ダイアログを閉じたあと、呼び出し元が履歴一覧を読み込み直す必要があるか（Issue #2177）
        /// </summary>
        /// <remarks>
        /// 保存・分割が確定したとき（<see cref="HasPersistedChanges"/>）に加え、保存で競合を検出したとき（この履歴が
        /// 他 PC で削除・統合された）も true。競合では何も保存していないが、一覧に残っている行はもう DB と一致しないため、
        /// 閉じたら読み込み直す（#1759「一覧を確認するよう案内するなら、案内する側が先に再読込する」）。
        /// </remarks>
        public bool NeedsHistoryReload => HasPersistedChanges || _conflictDetected;

        /// <summary>
        /// 複数グループがあるかどうか（Issue #634: ボタン切り替え用）
        /// </summary>
        [ObservableProperty]
        private bool _hasMultipleGroups;

        /// <summary>
        /// パンくずテキスト（Issue #1134）
        /// </summary>
        [ObservableProperty]
        private string _breadcrumbText = string.Empty;

        private readonly LedgerSplitService _ledgerSplitService;
        private readonly IStaffAuthService _staffAuthService;

        public LedgerDetailViewModel(
            ILedgerRepository ledgerRepository,
            SummaryGenerator summaryGenerator,
            LedgerDetailSaveService saveService,
            LedgerSplitService ledgerSplitService,
            IStaffAuthService staffAuthService,
            ILogger<LedgerDetailViewModel> logger)
        {
            _ledgerRepository = ledgerRepository;
            _summaryGenerator = summaryGenerator;
            _saveService = saveService;
            _ledgerSplitService = ledgerSplitService;
            _staffAuthService = staffAuthService;
            _logger = logger;
        }

        /// <summary>
        /// 初期化
        /// </summary>
        /// <param name="ledgerId">利用履歴ID</param>
        /// <param name="cardName">カード名（パンくず表示用、オプション）Issue #1134</param>
        /// <remarks>
        /// Issue #2177: 以前は操作者 IDm を受け取り、渡されたときだけ監査ログを記録していたが、画面は渡しておらず
        /// 明細の保存は監査ログに一度も残っていなかった。操作者は <c>ICurrentOperatorContext</c> から解決される（#1265）
        /// ため引数を外し、保存は常に監査ログを記録する（<see cref="LedgerDetailSaveService"/>）。
        /// </remarks>
        public async Task InitializeAsync(int ledgerId, string? cardName = null)
        {
            if (cardName != null)
            {
                _cardName = cardName;
            }
            _ledger = await _ledgerRepository.GetByIdAsync(ledgerId)
                ?? throw new InvalidOperationException($"Ledger ID {ledgerId} が見つかりません");

            _committedLedger = LedgerCloner.Clone(_ledger)!;

            // パンくず設定（Issue #1134）
            BreadcrumbText = !string.IsNullOrEmpty(_cardName)
                ? $"{_cardName} > 履歴詳細"
                : "履歴詳細";

            // ヘッダー情報を設定
            DateDisplay = WarekiConverter.ToWareki(_ledger.Date);
            SummaryDisplay = _ledger.Summary;
            IncomeDisplay = DisplayFormatters.FormatAmountWithUnitOrEmpty(_ledger.Income);
            ExpenseDisplay = DisplayFormatters.FormatAmountWithUnitOrEmpty(_ledger.Expense);
            BalanceDisplay = DisplayFormatters.FormatBalanceWithUnit(_ledger.Balance);
            StaffName = _ledger.StaffName ?? "-";
            Note = _ledger.Note ?? string.Empty;

            // 詳細アイテムを設定
            Items.Clear();
            var index = 0;
            foreach (var detail in _ledger.Details)
            {
                Items.Add(new LedgerDetailItemViewModel(detail, index++));
            }

            // 既存のGroupIdから分割線位置を設定
            InitializeDividersFromGroupIds();

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = false;
        }

        /// <summary>
        /// 既存のGroupIdから分割線位置を初期化
        /// </summary>
        private void InitializeDividersFromGroupIds()
        {
            for (int i = 0; i < Items.Count - 1; i++)
            {
                var current = Items[i];
                var next = Items[i + 1];

                bool currentHasGroup = current.GroupId.HasValue;
                bool nextHasGroup = next.GroupId.HasValue;

                if (currentHasGroup && nextHasGroup)
                {
                    // 両方グループに属している場合、グループIDが異なれば分割線
                    current.ShowDividerBelow = current.GroupId != next.GroupId;
                }
                else if (currentHasGroup || nextHasGroup)
                {
                    // 片方だけグループに属している場合は分割線
                    current.ShowDividerBelow = true;
                }
                else
                {
                    // 両方グループなしの場合は分割線なし
                    current.ShowDividerBelow = false;
                }
            }

            // 最後のアイテムには分割線なし
            if (Items.Count > 0)
            {
                Items[Items.Count - 1].ShowDividerBelow = false;
            }
        }

        /// <summary>
        /// 指定位置の分割線をトグル（挿入/削除）
        /// Issue #548: 分割線クリック方式UI
        /// </summary>
        /// <param name="index">分割線をトグルするアイテムのインデックス（この行の下の分割線）</param>
        public void ToggleDividerAt(int index)
        {
            if (index < 0 || index >= Items.Count - 1)
            {
                return; // 最後のアイテムの下には分割線を置けない
            }

            var item = Items[index];
            item.ShowDividerBelow = !item.ShowDividerBelow;

            // 分割線の状態からGroupIdを再計算
            RecalculateGroupsFromDividers();

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;

            if (item.ShowDividerBelow)
            {
                StatusMessage = "分割線を挿入しました（グループを分割）";
                _logger.LogDebug("Inserted divider after index {Index}", index);
            }
            else
            {
                StatusMessage = "分割線を削除しました（グループを統合）";
                _logger.LogDebug("Removed divider after index {Index}", index);
            }
        }

        /// <summary>
        /// 「すべて統合」で全項目へ付与するグループ番号（Issue #1816）
        /// </summary>
        internal const int MergedGroupId = 1;

        /// <summary>
        /// 分割線の状態からGroupIdを再計算
        /// 連続する分割線なしのアイテムは同じグループになる
        /// </summary>
        /// <remarks>
        /// Issue #633: 分割線が1つでも存在する場合、単独アイテムにもGroupIdを付与する。
        /// これにより、SummaryGeneratorがGroupIdベースの摘要生成パスを使用し、
        /// ユーザーの明示的な分割操作が摘要に正しく反映される。
        /// <para>
        /// Issue #1816: <b>分割線が 1 本も無い状態も「利用者が指定した単一グループ」として扱う</b>
        /// （全項目に <see cref="MergedGroupId"/>）。本メソッドを呼ぶのは分割線の操作
        /// （<c>ToggleDividerAt</c> / <c>SplitAll</c>）だけであり、そこへ至った時点で利用者は
        /// グループ分けを明示的に指定している。ここで null（自動検出）へ落とすと、
        /// 「すべて統合」の直後に分割線を 1 回 ON→OFF しただけで統合が黙って取り消され、
        /// 画面の見た目（分割線なし）は同じまま保存時の摘要だけが分かれる。
        /// 自動検出へ戻す唯一の経路は <see cref="ResetToAutoDetect"/> であり、
        /// 「分割線が無い」という 1 つの見た目に 2 つの意味を持たせない。
        /// </para>
        /// </remarks>
        private void RecalculateGroupsFromDividers()
        {
            if (Items.Count == 0)
            {
                return;
            }

            int currentGroupId = MergedGroupId;
            int groupStartIndex = 0;

            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];

                if (item.ShowDividerBelow || i == Items.Count - 1)
                {
                    // 全アイテムにGroupIdを付与する（単独アイテムも含む。これにより
                    // 摘要生成でGroupIdパスが使われる）。分割線が 1 本も無い場合は
                    // ループが最終行で 1 度だけ回り、全項目が MergedGroupId になる（Issue #1816）
                    for (int j = groupStartIndex; j <= i; j++)
                    {
                        Items[j].GroupId = currentGroupId;
                    }
                    currentGroupId++;

                    // 次のグループの開始位置
                    groupStartIndex = i + 1;
                }
            }
        }

        /// <summary>
        /// グループの色インデックスとラベルを更新（Issue #548: アクセシビリティ対応）
        /// </summary>
        private void UpdateGroupColors()
        {
            var groupIds = Items
                .Where(i => i.GroupId.HasValue)
                .Select(i => i.GroupId!.Value)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            foreach (var item in Items)
            {
                if (item.GroupId.HasValue)
                {
                    var groupIndex = groupIds.IndexOf(item.GroupId.Value);
                    item.GroupColorIndex = groupIndex % 5 + 1; // 1-5の色
                    // A, B, C... のラベルを設定（アクセシビリティ対応）
                    item.GroupLabel = ((char)('A' + groupIndex % 26)).ToString();
                }
                else
                {
                    item.GroupColorIndex = 0; // グループなし
                    item.GroupLabel = "-";
                }
            }
        }

        /// <summary>
        /// 詳細件数表示を更新
        /// </summary>
        private void UpdateDetailCountDisplay()
        {
            var groupCount = Items
                .Where(i => i.GroupId.HasValue)
                .Select(i => i.GroupId!.Value)
                .Distinct()
                .Count();

            HasMultipleGroups = groupCount >= 2;

            if (groupCount > 0)
            {
                DetailCountDisplay = $"{Items.Count}件の詳細（{groupCount}グループ）";
            }
            else
            {
                DetailCountDisplay = $"{Items.Count}件の詳細";
            }
        }

        /// <summary>
        /// 自動検出に戻す（すべての分割線を削除）
        /// </summary>
        [RelayCommand]
        private void ResetToAutoDetect()
        {
            foreach (var item in Items)
            {
                item.GroupId = null;
                item.ShowDividerBelow = false;
            }

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;
            StatusMessage = "自動検出モードに戻しました（すべての分割線を削除）";

            _logger.LogDebug("Reset all groups to auto-detect");
        }

        /// <summary>
        /// すべて統合（すべての分割線を削除し、全項目を1つのグループにする）
        /// </summary>
        /// <remarks>
        /// Issue #1816: 分割線を消したうえで <c>RecalculateGroupsFromDividers()</c> を呼ぶと、
        /// 「分割線なし＝自動検出モード」の分岐に落ちて全項目の <c>GroupId</c> が null になり、
        /// <see cref="ResetToAutoDetect"/> と同一の動作になっていた。
        /// 自動検出では非連続区間が「鉄道（A駅～B駅、C駅～D駅）」のまま分かれるため、
        /// 「1つのグループに統合しました」という案内と実際の摘要が食い違う。
        /// ここでは <c>GroupId = 1</c> を明示付与して「利用者が1グループを指定した」ことを表し、
        /// <c>SummaryGenerator</c> の GroupId パス（Issue #484）へ載せる。
        /// 自動検出へ戻したい場合は <see cref="ResetToAutoDetect"/>（GroupId = null）を使う。
        /// </remarks>
        [RelayCommand]
        private void MergeAll()
        {
            if (Items.Count < 2)
            {
                StatusMessage = "統合する項目がありません";
                return;
            }

            // すべての分割線を削除し、全項目を単一グループとして明示する
            foreach (var item in Items)
            {
                item.ShowDividerBelow = false;
                item.GroupId = MergedGroupId;
            }

            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;
            StatusMessage = "すべてを1つのグループに統合しました";

            _logger.LogDebug("Merged all items into one group");
        }

        /// <summary>
        /// すべて分割（すべての行の間に分割線を挿入）
        /// </summary>
        [RelayCommand]
        private void SplitAll()
        {
            if (Items.Count < 2)
            {
                StatusMessage = "分割する項目がありません";
                return;
            }

            // 最後以外のすべての行の下に分割線を挿入
            for (int i = 0; i < Items.Count - 1; i++)
            {
                Items[i].ShowDividerBelow = true;
            }

            // グループを再計算（すべてが個別になる）
            RecalculateGroupsFromDividers();
            UpdateGroupColors();
            UpdateDetailCountDisplay();
            HasChanges = true;
            StatusMessage = "すべてを個別に分割しました";

            _logger.LogDebug("Split all items into separate entries");
        }

        /// <summary>
        /// 変更を保存
        /// </summary>
        [RelayCommand]
        private async Task SaveAsync()
        {
            if (!HasChanges)
            {
                return;
            }

            IsBusy = true;
            StatusMessage = "保存中...";

            // Issue #2192: 完了の通知は処理中を解除してから行う（下の finally の後）。通知を受けた View は
            // 複数グループの保存なら Close() を呼び（#634）、OnClosing の CanClose は処理中なら閉じない（#1743）。
            // try の中で通知すると、保存は成功しているのにダイアログが開いたまま残る。
            var completed = false;
            try
            {
                // Issue #1979: 監査ログ用の「変更前」は、明細の書き換えより前に別インスタンスとして
                // 採る（#1959）。下の Select は item.Detail（＝ _ledger.Details と同一インスタンス）を
                // そのまま返して GroupId を書き換えるため、あとから採ると「変更前」が変更後の値を写す。
                // 同じ理由で _ledger.Details は差し替え不要（同一インスタンスが編集結果を持つ）。
                // Issue #2177: 失敗後の再保存でも DB に確定している状態を「変更前」にする（_committedLedger の remarks）
                var beforeLedger = LedgerCloner.Clone(_committedLedger)!;

                // 詳細のGroupIdを更新
                var updatedDetails = Items.Select(item =>
                {
                    var detail = item.Detail;
                    detail.GroupId = item.GroupId;
                    return detail;
                }).ToList();

                // 摘要を再生成（時系列昇順のまま渡す。DB へは LedgerDetailSaveService が新しい順で渡す。#1913）
                var newSummary = _summaryGenerator.Generate(updatedDetails);
                var summaryChanged = !string.IsNullOrEmpty(newSummary) && newSummary != _ledger.Summary;
                if (summaryChanged)
                {
                    _ledger.Summary = newSummary;
                }

                // Issue #2177: 明細の置換・摘要の更新・監査ログを 1 つのトランザクションで確定する。
                // 以前は明細の置換だけが先に確定し、摘要の更新が失敗すると「明細は新しいのに摘要は古い」
                // 食い違いが台帳に残った（摘要は物品出納簿にそのまま印字される）。
                LedgerDetailSaveResult result;
                try
                {
                    result = await _saveService.SaveAsync(beforeLedger, _ledger, updatedDetails, summaryChanged);
                }
                catch
                {
                    // 何も確定していないので、インメモリの摘要も元へ戻す（画面と DB を食い違わせない）
                    _ledger.Summary = beforeLedger.Summary;
                    throw;
                }

                if (result != LedgerDetailSaveResult.Saved)
                {
                    _ledger.Summary = beforeLedger.Summary;
                    // Issue #2177: 何も保存されていないので、編集内容は「未保存の変更」として残す（HasChanges は true のまま）。
                    // 閉じようとすれば破棄の確認が出て、保存し直せば反映される。
                    // このダイアログには再読込の手段が無いので、「更新してから」ではなく閉じて開き直すよう案内する
                    // （閉じるときは破棄の確認が出る。#1759「案内するなら案内する側が再読込できること」）
                    if (result == LedgerDetailSaveResult.Conflict)
                    {
                        // この履歴の行がもう無い（他 PC が削除した、または他の履歴へ統合した）。
                        // 閉じたら呼び出し元が一覧を読み込み直す（NeedsHistoryReload）。
                        // 何度保存しても同じ失敗になるので、編集内容を「未保存の変更」として残さない —
                        // 残すと閉じるときに破棄の確認が出て、「いいえ」で保存し直しても必ず失敗する（コードレビューで検出）
                        _conflictDetected = true;
                        HasChanges = false;
                        StatusMessage = "この履歴は他のパソコンや別の操作で削除されたか、他の履歴へ統合された可能性があるため保存できませんでした" +
                                        "（この画面の編集内容は保存できません）。このダイアログを閉じると履歴の一覧を読み込み直すので、" +
                                        "最新の履歴を確認してください。";
                    }
                    else
                    {
                        StatusMessage = "利用明細を保存できませんでした（明細・摘要とも変更されていません）。" +
                                        "しばらく待ってから再度保存してください。";
                    }
                    return;
                }

                HasPersistedChanges = true;
                _committedLedger = LedgerCloner.Clone(_ledger)!;
                if (summaryChanged)
                {
                    SummaryDisplay = newSummary;
                }

                HasChanges = false;
                StatusMessage = "保存しました";
                _logger.LogInformation("Saved ledger detail changes for ledger {LedgerId}", _ledger.Id);

                completed = true;
            }
            catch (Exception ex)
            {
                // Issue #2177: 保存は 1 つのトランザクションなので、失敗したら何も変更されていない。
                // 編集内容は「未保存の変更」として残る（HasChanges は true のまま）。
                _logger.LogError(ex, "Failed to save ledger detail changes");
                StatusMessage = ExceptionMessageFormatter.ToUserMessage(ex, "台帳の保存");
            }
            finally
            {
                IsBusy = false;
            }

            if (completed)
            {
                OnSaveCompleted?.Invoke();
            }
        }

        /// <summary>
        /// 台帳分割による保存（Issue #634）
        /// </summary>
        [RelayCommand]
        private async Task SaveWithFullSplitAsync()
        {
            if (!HasChanges)
            {
                return;
            }

            // 履歴分割は ledger を改変する監査対象の重要操作のため職員認証を要求する
            // （設計 06_シーケンス図 §11 / SEQ-AUTH-01。追加・削除・変更と同じゲート）
            var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の分割");
            if (authResult == null)
            {
                StatusMessage = "認証がキャンセルされたため分割を中止しました";
                return;
            }

            IsBusy = true;
            StatusMessage = "分割中...";

            // Issue #2192: 完了の通知は処理中を解除してから行う（SaveAsync と同じ理由）
            var completed = false;
            try
            {
                var updatedDetails = Items.Select(item =>
                {
                    var detail = item.Detail;
                    detail.GroupId = item.GroupId;
                    return detail;
                }).ToList();

                var result = await _ledgerSplitService.SplitAsync(
                    _ledger.Id, updatedDetails);

                if (!result.Success)
                {
                    StatusMessage = $"分割に失敗しました: {result.ErrorMessage}";
                    return;
                }

                HasPersistedChanges = true;
                HasChanges = false;
                StatusMessage = $"{result.CreatedLedgerIds.Count + 1}件の履歴に分割しました";
                _logger.LogInformation(
                    "Split ledger {LedgerId} into separate ledgers",
                    _ledger.Id);

                completed = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to split ledger {LedgerId}", _ledger.Id);
                StatusMessage = ExceptionMessageFormatter.ToUserMessage(ex, "台帳の分割");
            }
            finally
            {
                IsBusy = false;
            }

            if (completed)
            {
                OnSaveCompleted?.Invoke();
            }
        }

        /// <summary>
        /// ダイアログを閉じてよいか判定する（Issue #1743）
        /// </summary>
        /// <param name="confirmDiscard">
        /// 未保存の変更を破棄してよいかをユーザーに確認するコールバック。破棄してよい場合に true を返す。
        /// 未保存の変更が無い場合は呼ばれない。
        /// </param>
        /// <returns>閉じてよい場合 true、閉じる操作を中止すべき場合 false</returns>
        /// <remarks>
        /// <para>
        /// タイトルバーの ✕ / Alt+F4 / Escape / 「閉じる」ボタンのどの経路で閉じても
        /// View 側の OnClosing が本メソッドを通るため、破棄確認はここに一元化される。
        /// </para>
        /// <para>
        /// 保存・分割の DB トランザクション実行中（<see cref="IsBusy"/>）は破棄確認を出さずに閉じない。
        /// 処理中オーバーレイはマウスのヒットテストしか塞がず ✕ / Alt+F4 / Escape は生きているため、
        /// この間に閉じられると①「破棄しますか」という事実に反する確認が出る②書き込みは成功するのに
        /// 呼び出し元が保存済みと認識できず履歴一覧が更新されない、の 2 つが同時に起きる。
        /// </para>
        /// </remarks>
        public bool CanClose(Func<bool> confirmDiscard)
        {
            if (IsBusy)
            {
                return false;
            }

            if (!HasChanges)
            {
                return true;
            }

            return confirmDiscard();
        }

        /// <summary>
        /// ダイアログのクローズを要求する（Issue #1743）
        /// </summary>
        /// <remarks>
        /// Escape キーの KeyBinding から実行される。View 側が <see cref="OnCloseRequested"/> に
        /// Window.Close() を設定するため、この経路も OnClosing の破棄確認を通る。
        /// Button.IsCancel は Click 処理の後に無条件で DialogResult=false を設定し、破棄確認で
        /// 「いいえ」を選んでも DialogResult が false のまま残って以後の操作が無反応になるため使わない。
        /// </remarks>
        [RelayCommand]
        private void RequestClose()
        {
            OnCloseRequested?.Invoke();
        }
    }
}
