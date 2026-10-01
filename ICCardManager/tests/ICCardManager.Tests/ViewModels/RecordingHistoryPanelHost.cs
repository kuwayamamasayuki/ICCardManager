using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Dtos;
using ICCardManager.ViewModels;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// 履歴パネル（<see cref="HistoryPanelViewModel"/>）の単体テスト用のホスト（Issue #2159）。
/// </summary>
/// <remarks>
/// <para>
/// メイン画面を組み立てずに「履歴パネルが親へ何を頼んだか」を表明するための記録係。
/// 要求はすべて <see cref="Calls"/> に<b>呼ばれた順</b>で残る（履歴削除は「再読込 → ダッシュボード → 警告」の
/// 順序が仕様のため、順序まで見られるようにしている。#1753）。
/// </para>
/// <para>
/// 残高不整合警告は本番（<c>MainViewModel</c>）と同じ規則で <see cref="WarningMessages"/> を入れ替える
/// （種別とカードで絞って取り除き、あれば追加する）。親との実配線は <c>MainViewModelTests</c> の
/// 「履歴パネルとの連携」で別に表明する。
/// </para>
/// <para>
/// 本番と違う点: 本番の <c>RefreshDashboardAsync</c> は、ダッシュボードから消えたカードの残高不整合警告も取り除く（#1739）が、
/// ここでは再現しない（呼ばれたことを記録するだけ）。ダッシュボード更新と警告の入れ替えの<b>相互作用</b>
/// （呼び出し順を入れ替えると警告が消える等）は、本物の <c>MainViewModel</c> を使う連携テストでしか検出できない。
/// </para>
/// </remarks>
internal sealed class RecordingHistoryPanelHost : IHistoryPanelHost
{
    public const string BeginBusyCall = nameof(IHistoryPanelHost.BeginBusy);
    public const string RefreshDashboardCall = nameof(IHistoryPanelHost.RefreshDashboardAsync);
    public const string CheckWarningsCall = nameof(IHistoryPanelHost.CheckWarningsAsync);
    public const string RefreshLentCardsCall = nameof(IHistoryPanelHost.RefreshLentCardsAsync);

    /// <summary>親が持つ警告エリアの代役</summary>
    public ObservableCollection<WarningItem> WarningMessages { get; } = new();

    /// <summary>受けた要求の名前（呼ばれた順）</summary>
    public List<string> Calls { get; } = new();

    /// <summary><see cref="IHistoryPanelHost.BeginBusy"/> へ渡されたメッセージ</summary>
    public List<string> BusyMessages { get; } = new();

    /// <summary>開いたまま閉じていない処理中スコープの数</summary>
    public int OpenBusyScopes { get; private set; }

    /// <summary>
    /// 設定すると <see cref="RefreshDashboardAsync"/> がこの例外で失敗する（記録は失敗の前に残す）。
    /// 共有フォルダーの切断・DB ロックのように、履歴の再読込とダッシュボード更新が同じ原因で失敗する状況の再現用（#1727 / #1954）。
    /// </summary>
    public Exception? RefreshDashboardFailure { get; set; }

    public int CountOf(string call) => Calls.Count(c => c == call);

    public IDisposable BeginBusy(string message)
    {
        Calls.Add(BeginBusyCall);
        BusyMessages.Add(message);
        OpenBusyScopes++;
        return new Scope(this);
    }

    public void ReplaceBalanceInconsistencyWarning(string cardIdm, WarningItem? warning)
    {
        foreach (var stale in WarningMessages
                     .Where(w => w.Type == WarningType.BalanceInconsistency && w.CardIdm == cardIdm)
                     .ToList())
        {
            WarningMessages.Remove(stale);
        }

        if (warning != null)
        {
            WarningMessages.Add(warning);
        }
    }

    public Task RefreshDashboardAsync()
    {
        Calls.Add(RefreshDashboardCall);
        return RefreshDashboardFailure == null
            ? Task.CompletedTask
            : Task.FromException(RefreshDashboardFailure);
    }

    public Task CheckWarningsAsync()
    {
        Calls.Add(CheckWarningsCall);
        return Task.CompletedTask;
    }

    public Task RefreshLentCardsAsync()
    {
        Calls.Add(RefreshLentCardsCall);
        return Task.CompletedTask;
    }

    private sealed class Scope : IDisposable
    {
        private RecordingHistoryPanelHost? _owner;

        public Scope(RecordingHistoryPanelHost owner) => _owner = owner;

        public void Dispose()
        {
            if (_owner == null) return;
            _owner.OpenBusyScopes--;
            _owner = null;
        }
    }
}
