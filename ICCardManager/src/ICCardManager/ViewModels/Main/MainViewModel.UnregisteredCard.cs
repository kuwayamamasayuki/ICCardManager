using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Common.Messages;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Views.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ICCardManager.ViewModels;

public partial class MainViewModel
{
    // === 未登録カード・残高照合 ===

    /// <summary>
    /// 未登録カードの処理
    /// </summary>
    /// <remarks>
    /// Issue #312: IDmからカード種別（Suica/PASMO等）や職員証かどうかを判別することは
    /// 技術的に不可能なため、常にユーザーに選択させる。
    /// </remarks>
    private async Task HandleUnregisteredCardAsync(string idm)
    {
        // 抑制中は処理をスキップ（登録モード中は StaffManageViewModel / CardManageViewModel が処理する）。
        // HandleCardReadAsync の入口ゲート（_suppressionSources.Count > 0）から本メソッドへ到達するまでには
        // 呼び出し元の GetByIdmAsync（職員・カードの判定）の await が挟まる。その待機中に届いた 2 件目の
        // タッチは入口ゲートを通過済みなので、ここで改めて判定しないと 1 件目が下で取得する抑制
        // （UnregisteredCardDialog）をすり抜けて種別選択ダイアログが重なり、Error ハンドラも二重購読になる。
        // 特定のソースを列挙せず「何かが抑制中なら処理しない」で判定する（新しいソースの追随漏れを防ぐ）。
        if (IsCardReadingSuppressed)
        {
            return;
        }

        // Issue #1807: 以降の全区間（残高・履歴の事前読み取り〜種別選択ダイアログ〜登録ダイアログ）で
        // 自身のカード読み取りを抑制する。ShowDialog は入れ子のメッセージポンプなので、抑制しないと
        // 表示中の別カードタッチが HandleCardReadAsync に届き、種別選択ダイアログが多重に開いたり
        // 背後で貸出・返却が進んだりする。事前読み取り中の再入も同じ経路で防ぐ
        // （再入すると Error ハンドラの -= が no-op になり finally の += が 2 回走って二重購読になる）。
        // 解放は Dispose（finally 相当）で保証する（Issue #1725 と同じ判断）。
        using var suppression = BeginCardReadingSuppression(CardReadingSource.UnregisteredCardDialog);

        _soundPlayer.Play(SoundType.Warning);
        // メイン画面は変更しない（Issue #186）

        // Issue #482対応: カード種別選択の前に残高を読み取っておく
        // 選択中にカードを離しても正しい残高で登録できる
        // Issue #596対応: 履歴も事前に読み取っておく（カード登録時に当月分をインポートするため）
        // エラーイベントを一時的に抑制（ユーザーに混乱を与えるエラーメッセージを防止）
        int? preReadBalance = null;
        List<LedgerDetail> preReadHistory = null;
        _cardReader.Error -= OnCardReaderError;
        try
        {
            preReadBalance = await _cardReader.ReadBalanceAsync(idm);
            preReadHistory = (await _cardReader.ReadHistoryAsync(idm))?.ToList();
        }
        catch
        {
            // 残高・履歴読み取りエラーは無視（カード登録は続行可能）
        }
        finally
        {
            _cardReader.Error += OnCardReaderError;
        }

        // Issue #312: IDmからカード種別を判別することは技術的に不可能なため、
        // カスタムダイアログでユーザーに職員証か交通系ICカードかを選択させる
        Views.Dialogs.CardTypeSelectionDialog capturedSelectionDialog = null;
        _navigationService.ShowDialog<Views.Dialogs.CardTypeSelectionDialog>(
            d => capturedSelectionDialog = d);

        switch (capturedSelectionDialog?.SelectionResult)
        {
            case Views.Dialogs.CardTypeSelectionResult.StaffCard:
                // 職員管理画面を開いて新規登録モードで開始
                _navigationService.ShowDialog<Views.Dialogs.StaffManageDialog>(
                    d => d.InitializeWithIdm(idm));
                break;

            case Views.Dialogs.CardTypeSelectionResult.IcCard:
                // カード管理画面を開いて新規登録モードで開始
                // Issue #482: 事前に読み取った残高を渡す
                // Issue #596: 事前に読み取った履歴も渡す
                _navigationService.ShowDialog<Views.Dialogs.CardManageDialog>(
                    d => d.InitializeWithIdmBalanceAndHistory(idm, preReadBalance, preReadHistory));

                // ダイアログを閉じた後、貸出中カード一覧とダッシュボードを更新
                // Issue #483: RefreshDashboardAsync を追加してカード一覧を更新
                await RefreshLentCardsAsync();
                await RefreshDashboardAsync();
                break;

            case Views.Dialogs.CardTypeSelectionResult.Cancel:
            default:
                // キャンセル - 何もしない
                break;
        }

        ResetState();
    }

    /// <summary>
    /// Issue #1908: 登録済みの交通系ICカードを単独でタッチしたときに、
    /// カードの実残額とピッすいが記録している残額の食い違いを判定して警告を入れ替える。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ピッすいを通さずに利用・返却されたカードを庶務担当者が見つけられるようにするための検出。
    /// 判定できるのは<b>カードがリーダーに載っている瞬間だけ</b>のため、他の履歴表示経路
    /// （残額警告のクリック・ダッシュボードからの表示）では判定しない。
    /// </para>
    /// <para>
    /// <b>読み取れなかったときは前回の判定を残す。</b> 残額の読み取り失敗（カードを早く離した・
    /// リーダー断）や台帳の読み取り失敗は「差異が無い」ことを意味しないため、既存の警告行を消さない。
    /// 「判定できなかった」ことをその場で通知もしない（カードを早く離す運用では毎回出て
    /// 本当の警告が埋もれるため。判断材料はログに残す）。
    /// </para>
    /// <para>
    /// internal: カードリーダー経由でしか到達しない経路のため、テストから直接呼び出して検証する。
    /// </para>
    /// </remarks>
    internal async Task CheckCardBalanceMismatchAsync(IcCard card)
    {
        if (card == null)
        {
            return;
        }

        // Issue #1947: 母集団は「運用中のカード」（IcCard.IsInOperation）。除去側の
        // RefreshDashboardAsync は残額ダッシュボードに居ないカードの警告を取り除くため、
        // 運用から外れたカード（払戻済み・削除済み）で警告を立てると、次のダッシュボード更新
        // （貸出・返却／共有モードの定期更新）で誰の操作にも紐づかず黙って消える。
        // 生成側と除去側の判定条件を揃える（.claude/rules/business-logic.md #1739 / #1947）。
        // 判定は呼び出し元へ配らず本メソッドの内側に置く — 分岐先に配ると、経路が増えるたびに
        // 配り忘れる形が残る（#1842）。上の抑制判定（#1946）を内側に置いたのと同じ理由。
        if (!card.IsInOperation)
        {
            return;
        }

        // Issue #1946: 呼び出し元（HandleCardInStaffWaitingStateAsync）の再判定と本メソッドの抑制取得の間に
        // await は無いが、抑制中に本メソッドへ到達する経路が将来増えても再入しないための backstop として
        // ここでも判定する（HandleUnregisteredCardAsync と同じ形）。特定のソースを列挙せず
        // 「何かが抑制中なら判定しない」で書く（新しいソースの追随漏れを防ぐ）。
        if (IsCardReadingSuppressed)
        {
            return;
        }

        // Issue #1946: 本メソッドは AppState.WaitingForStaffCard のまま ReadBalanceAsync（実機で数百ミリ秒）を
        // 待つため、抑制も Processing 状態も無いままでは 2 枚目のタッチが入口ゲートを通過して再入する。
        // 再入すると下の -= が no-op になり finally の += が 2 回走って Error が二重購読になり、
        // 以後リーダーエラー 1 件ごとに WarningItem.OccurrenceCount が 2 ずつ増える（Issue #1807 と同型）。
        // 解放は Dispose（finally 相当）で保証する（Issue #1725 の「解除は finally で保証する」と同じ判断）。
        using var suppression = BeginCardReadingSuppression(CardReadingSource.BalanceMismatchCheck);

        int? actualBalance = null;

        // Issue #656 と同じ理由でエラーイベントを一時的に抑制する
        // （カードを離したことによるリーダーエラーを警告エリアへ出さない）。
        _cardReader.Error -= OnCardReaderError;
        try
        {
            actualBalance = await _cardReader.ReadBalanceAsync(card.CardIdm);
        }
        catch (Exception ex)
        {
            // IDm はログへ生で出さない（IdmMasker を通す。Issue #1852）
            _logger?.LogWarning(ex,
                "残額の食い違い判定: カードから残額を読み取れませんでした。カード={CardIdm}（管理番号={CardNumber}）",
                IdmMasker.Mask(card.CardIdm), card.CardNumber);
        }
        finally
        {
            _cardReader.Error += OnCardReaderError;
        }

        if (actualBalance == null)
        {
            // 障害調査で「判定したのに一致した」と「判定できなかった」を区別できるようにする（#1716）
            _logger?.LogInformation(
                "残額の食い違い判定: 実残額を取得できなかったため判定を見送りました。カード={CardIdm}（管理番号={CardNumber}）",
                IdmMasker.Mask(card.CardIdm), card.CardNumber);
            return;
        }

        Models.Ledger latestLedger;
        try
        {
            latestLedger = await _ledgerRepository.GetLatestLedgerAsync(card.CardIdm);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "残額の食い違い判定: 台帳の最新残額を取得できませんでした。カード={CardIdm}（管理番号={CardNumber}）",
                IdmMasker.Mask(card.CardIdm), card.CardNumber);
            return;
        }

        if (latestLedger == null)
        {
            // 記録が 1 行も無い（＝比較対象が無い）。「一致した」ではないので既存の警告は残す。
            _logger?.LogInformation(
                "残額の食い違い判定: 台帳に記録が無いため判定を見送りました。カード={CardIdm}（管理番号={CardNumber}）",
                IdmMasker.Mask(card.CardIdm), card.CardNumber);
            return;
        }

        var warning = _warningService.CheckCardBalanceMismatchWarning(
            card.CardIdm, card.CardType, card.CardNumber,
            actualBalance.Value, latestLedger.Balance, card.IsLent);

        // 自分が生成する種別の、しかも同一カード分だけを入れ替える（04_機能設計書 §7.4）
        ReplaceWarnings(
            w => w.Type == WarningType.CardBalanceMismatch && w.CardIdm == card.CardIdm,
            warning == null ? null : new[] { warning });

        if (warning != null)
        {
            // 色・アイコン・テキスト・音の4要素で伝える。トーストは文字数制約があるため簡潔にし、
            // 「なぜ／どうすれば」は警告エリアの行が担う（error-messages.md）。
            _soundPlayer.Play(SoundType.Warning);
            _toastNotificationService.ShowWarning(
                "残額の食い違い",
                $"{card.CardType} {card.CardNumber}\n" +
                $"カード {DisplayFormatters.FormatBalanceWithUnit(actualBalance.Value)} / " +
                $"記録 {DisplayFormatters.FormatBalanceWithUnit(latestLedger.Balance)}");
        }
    }

    /// <summary>
    /// Issue #1908: 貸出・返却が記録されたカードの残額食い違い警告を取り除く。
    /// </summary>
    /// <remarks>
    /// 貸出・返却はどちらもカードから読み取った実残額を台帳へ書くため、
    /// その記録が確定した時点で食い違いは解消している。
    /// <b>実残額を読み取れなかった処理では呼ばない</b> — その場合は台帳に入った残額が
    /// 現物と一致する保証が無く、警告を消すと「解消した」という誤った表示になる。
    /// </remarks>
    private void ClearCardBalanceMismatchWarning(string cardIdm)
    {
        ReplaceWarnings(w => w.Type == WarningType.CardBalanceMismatch && w.CardIdm == cardIdm);
    }
}
