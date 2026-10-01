using System;
using System.Threading.Tasks;
using ICCardManager.Dtos;

namespace ICCardManager.ViewModels;

/// <summary>
/// 履歴パネル（<see cref="HistoryPanelViewModel"/>）を載せる画面へ、履歴パネルから送る要求（Issue #2159）。
/// </summary>
/// <remarks>
/// <para>
/// 履歴パネルはメイン画面の一部で、警告エリア・残高ダッシュボード・貸出中一覧・処理中オーバーレイは
/// メイン画面（<see cref="MainViewModel"/>）が持つ。履歴の操作がそれらを変えるときに、ここを通して頼む。
/// </para>
/// <para>
/// <c>IMessenger</c> ではなくインターフェースにしているのは、<b>順序が仕様だから</b>。
/// 履歴削除は「一覧の再読込 → ダッシュボード → 警告 → 整合性 → 競合の案内」の順で、
/// 案内文が「一覧を再読み込みしました」と述べる以上、親の処理の完了を待ってから次へ進む（#1753）。
/// <c>IMessenger.Send</c> は受け手の非同期処理の完了を待てない。また 1 つのインターフェースにまとめると、
/// 実装漏れをコンパイラが止める（デリゲートを個別に渡すと 1 つの配線漏れが潜在化する。#1820）。
/// </para>
/// </remarks>
public interface IHistoryPanelHost
{
    /// <summary>
    /// メイン画面の処理中オーバーレイを表示するスコープを開く。
    /// </summary>
    /// <remarks>
    /// オーバーレイはメイン画面の <c>IsBusy</c> に束縛されているため、履歴パネルが自前で処理中状態を持っても表示されない。
    /// </remarks>
    IDisposable BeginBusy(string message);

    /// <summary>
    /// 指定カードの残高不整合警告（<see cref="WarningType.BalanceInconsistency"/>）を入れ替える。
    /// <paramref name="warning"/> が null なら取り除くだけ。
    /// </summary>
    /// <remarks>
    /// 警告の<b>中身</b>は履歴パネルが組み立て、<b>入れ替えの規約</b>（種別ごとに自分の行だけを入れ替える。#1739）は
    /// メイン画面が持つ。種別を固定したメソッドにして、履歴パネルが他の種別の警告を消せないようにしている。
    /// </remarks>
    void ReplaceBalanceInconsistencyWarning(string cardIdm, WarningItem? warning);

    /// <summary>残高ダッシュボードを読み直す（履歴の追加・変更・削除・統合で残高が変わるため）。</summary>
    Task RefreshDashboardAsync();

    /// <summary>データ系の警告（残額不足・バス停名未入力）を再判定する。</summary>
    Task CheckWarningsAsync();

    /// <summary>貸出中一覧を読み直す（貸出中レコードの削除で <c>ic_card.is_lent</c> を戻したとき）。</summary>
    Task RefreshLentCardsAsync();
}
