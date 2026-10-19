#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Models;

namespace ICCardManager.ViewModels;

/// <summary>
/// 残額の食い違い（貸出中でないカード）を、カードの利用履歴から取り込んで復旧する導線（Issue #2293）
/// </summary>
public partial class HistoryPanelViewModel
{
    /// <summary>履歴の上に出す案内（取り込みを勧める理由）</summary>
    internal const string CardHistoryImportOfferMessage =
        "このカードは、カードの残額とピッすいの記録が食い違っています。「カードの履歴から取り込む...」を押し、カードに残る利用履歴から記録されていない利用を取り込んでください。";

    /// <summary>履歴の上に出す案内（XAML の束縛用）</summary>
    public string CardHistoryImportOfferMessageText => CardHistoryImportOfferMessage;

    /// <summary>
    /// 「カードの履歴から取り込む...」を案内しているか（残額の食い違い警告から、貸出中でないカードの履歴を開いたときだけ）
    /// </summary>
    /// <remarks>
    /// どの経路で履歴を開き直しても（<see cref="ShowHistoryAsync"/>）、閉じても（<see cref="CloseHistory"/>）取り下げる。
    /// 案内は警告をクリックした直後の、その警告のカードに限る（他の経路で開いた履歴に取り込みの操作を出さない）。
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFromCardHistoryCommand))]
    private bool _isCardHistoryImportOffered;

    /// <summary>
    /// 残額の食い違い警告のクリックから、カードの履歴を開く（Issue #2293）
    /// </summary>
    /// <remarks>
    /// 貸出中のカードは返却処理が利用履歴を記録する正規の経路なので、取り込みを案内しない（履歴を開くだけ）。
    /// 貸出状態はクリックした時点の DB の値で決める（警告を出した後に貸出・返却されていることがある）。
    /// </remarks>
    public async Task ShowCardHistoryForBalanceMismatchAsync(IcCard? card)
    {
        await ShowCardHistoryAsync(card);

        if (card != null && card.IsInOperation && !card.IsLent && HistoryCard?.CardIdm == card.CardIdm)
        {
            IsCardHistoryImportOffered = true;
        }
    }

    /// <summary>取り込みの案内を取り下げる</summary>
    private void EndCardHistoryImportOffer() => IsCardHistoryImportOffered = false;

    /// <summary>
    /// 「カードの履歴から取り込む...」: カードのタッチを求め、未記録の利用を取り込むダイアログを開く
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsCardHistoryImportOffered))]
    public async Task ImportFromCardHistoryAsync()
    {
        var card = HistoryCard;
        if (card == null)
        {
            return;
        }

        Views.Dialogs.CardHistoryImportDialog? dialog = null;
        await _navigationService.ShowDialogAsync<Views.Dialogs.CardHistoryImportDialog>(d =>
        {
            dialog = d;
            d.SetTarget(card.CardIdm, card.DisplayName);
            return Task.CompletedTask;
        });

        if (dialog != null)
        {
            await CompleteCardHistoryImportAsync(card.CardIdm, dialog.ViewModel.Outcome);
        }
    }

    /// <summary>
    /// 取り込んだ行が見えるように履歴を読み直す
    /// </summary>
    /// <remarks>
    /// 取り込みは前月以前の利用（ピッすいを通さずに使われ、そのまま戻されたもの）を記録し得る。今の表示期間のまま読み直すと、
    /// 「N件の行を記録しました」と案内したのに履歴に現れない。表示期間がその行を含まなければ（前の月を表示している場合も）、最も古い行の月の 1 日から
    /// 開き直す（返却確認 #1907 と同じ。期間を月単位で扱う画面に日付単位の値を渡さない）。開き直すと案内が取り下げられる
    /// （<see cref="ShowHistoryAsync"/>）ので立て直す（食い違いが解消したかは、この後の判定し直しで決める）。
    /// </remarks>
    private async Task ShowImportedRowsAsync(string cardIdm, CardHistoryImportOutcome outcome)
    {
        var oldest = outcome.RecordedLedgers.Count > 0
            ? outcome.RecordedLedgers.Min(l => l.Date).Date
            : (DateTime?)null;
        var newest = outcome.RecordedLedgers.Count > 0
            ? outcome.RecordedLedgers.Max(l => l.Date).Date
            : (DateTime?)null;
        var fromMonth = oldest.HasValue ? new DateTime(oldest.Value.Year, oldest.Value.Month, 1) : (DateTime?)null;

        // 前だけでなく後ろへはみ出す場合も開き直す（月送りで前の月を表示したまま取り込んだとき）
        var outsideShownPeriod = oldest < HistoryFromDate.Date || newest > HistoryToDate.Date;
        if (fromMonth.HasValue && HistoryCard?.CardIdm == cardIdm && outsideShownPeriod)
        {
            var card = await _cardRepository.GetByIdmAsync(cardIdm);
            if (card != null)
            {
                var offered = IsCardHistoryImportOffered;
                await ShowHistoryAsync(card, fromMonth.Value);
                IsCardHistoryImportOffered = offered && HistoryCard?.CardIdm == cardIdm;
                return;
            }
        }

        await LoadHistoryLedgersAsync();
    }

    /// <summary>
    /// 取り込みのダイアログを閉じた後の処理（Issue #2293）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 取り込んだ後は返却の後処理と同じ順に進める: 履歴（取り込んだ行が見える期間で）・ダッシュボードの再読み込み → バス停名の入力（バス利用があれば）→
    /// 残額の食い違いの判定し直し → データ系の警告（残額不足・バス停名未入力）→ 残高チェーンの検査。
    /// </para>
    /// <para>
    /// 読み取りまで進んでいれば、取り込んでいなくてもその実残額で食い違いを判定し直す（他のパソコンの取り込みで
    /// 解消していることがある）。解消したら案内を取り下げる。
    /// </para>
    /// <para>internal: ダイアログ（WPF のウィンドウ）を介さずにテストから呼ぶため。</para>
    /// </remarks>
    internal async Task CompleteCardHistoryImportAsync(string cardIdm, CardHistoryImportOutcome outcome)
    {
        if (outcome.HasImported)
        {
            await ShowImportedRowsAsync(cardIdm, outcome);
            await Host.RefreshDashboardAsync();

            // バス利用を含む行は、返却と同じくバス停名の入力へ進む（閉じても「バス（★）」のまま未入力の警告に残る）
            var busLedgers = BusStopInputViewModel.SelectBusStopInputTargets(outcome.RecordedLedgers);
            if (busLedgers.Count > 0)
            {
                await _navigationService.ShowDialogAsync<Views.Dialogs.BusStopInputDialog>(
                    async d => await d.InitializeWithLedgersAsync(busLedgers));
                await LoadHistoryLedgersAsync();
            }
        }
        else if (outcome.Completed)
        {
            // 一覧を見ている間に他の操作（返却・他のパソコンの取り込み）で記録されていた。その記録を見せるため読み直す
            await LoadHistoryLedgersAsync();
            await Host.RefreshDashboardAsync();
        }

        if (outcome.ActualBalance.HasValue)
        {
            var stillMismatched = await Host.ReevaluateCardBalanceMismatchAsync(cardIdm, outcome.ActualBalance.Value);
            if (!stillMismatched && HistoryCard?.CardIdm == cardIdm)
            {
                EndCardHistoryImportOffer();
            }
        }

        if (outcome.Completed)
        {
            await Host.CheckWarningsAsync();
            await CheckAndNotifyConsistencyAsync();
        }
    }
}
