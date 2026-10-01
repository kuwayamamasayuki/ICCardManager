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
    // === DEBUG 仮想タッチ（#if DEBUG はこのファイルに閉じる） ===

#if DEBUG
    /// <summary>
    /// デバッグ用: 職員証タッチをシミュレート
    /// </summary>
    [RelayCommand]
    public void SimulateStaffCard()
    {
        if (_cardReader is HybridCardReader hybridReader)
        {
            hybridReader.SimulateCardRead("FFFF000000000001");
        }
    }

    /// <summary>
    /// デバッグ用: ICカードタッチをシミュレート
    /// </summary>
    [RelayCommand]
    public void SimulateIcCard()
    {
        if (_cardReader is HybridCardReader hybridReader)
        {
            hybridReader.SimulateCardRead("07FE112233445566");
        }
    }

    /// <summary>
    /// デバッグ用: 仮想タッチ設定ダイアログを開く（Issue #640）
    /// </summary>
    [RelayCommand]
    public async Task OpenVirtualCardAsync()
    {
        Views.Dialogs.VirtualCardDialog capturedVirtualDialog = null;
        _navigationService.ShowDialog<Views.Dialogs.VirtualCardDialog>(
            d => capturedVirtualDialog = d);

        // ダイアログを閉じた後、TouchResult を参照して処理を実行
        if (capturedVirtualDialog?.DataContext is VirtualCardViewModel vm && vm.TouchResult != null)
        {
            await ProcessVirtualTouchAsync(vm.TouchResult);
        }

        await RefreshLentCardsAsync();
        await RefreshDashboardAsync();
    }

    /// <summary>
    /// 仮想タッチの結果を処理する（ShowDialog後に呼び出される）
    /// </summary>
    private async Task ProcessVirtualTouchAsync(VirtualTouchResult touchResult)
    {
        try
        {
            var staffIdm = touchResult.StaffIdm;
            var cardIdm = touchResult.CardIdm;

            if (touchResult.HasEntries)
            {
                // エントリがある場合: LendAsync → ReturnAsync で履歴を直接DBに反映
                var card = await _cardRepository.GetByIdmAsync(cardIdm);

                if (card == null)
                {
                    _navigationService.ShowError(
                        // Issue #1986: IDm はマスクを通す（#1852）。末尾 4 文字が残るためカードの識別はできる。
                        $"カードがデータベースに登録されていません。\nIDm: {IdmMasker.Mask(cardIdm)}", "仮想タッチ");
                    return;
                }

                if (!card.IsLent)
                {
                    var lendResult = await _lendingService.LendAsync(staffIdm, cardIdm, touchResult.CurrentBalance);
                    if (!lendResult.Success)
                    {
                        _navigationService.ShowError(
                            $"貸出処理に失敗しました: {lendResult.ErrorMessage}", "仮想タッチ");
                        return;
                    }
                }

                // 仮想タッチは物理カード読み取りではないため、重複チェックをスキップ
                var returnResult = await _lendingService.ReturnAsync(staffIdm, cardIdm, touchResult.HistoryDetails, skipDuplicateCheck: true);
                if (!returnResult.Success)
                {
                    _navigationService.ShowError(
                        $"返却処理に失敗しました: {returnResult.ErrorMessage}", "仮想タッチ");
                    return;
                }

                // 返却成功: 通常の返却と同じ後処理を呼び出す（バス停入力ダイアログ等。Issue #1577）
                await HandleReturnSuccessAsync(card, returnResult);
            }
            else
            {
                // エントリなし: SimulateCardRead で通常の貸出タッチをシミュレート
                if (_cardReader is HybridCardReader hybridReader)
                {
                    hybridReader.SimulateCardRead(staffIdm);
                    await Task.Delay(500);
                    hybridReader.SimulateCardRead(cardIdm);
                }
            }
        }
        catch (Exception ex)
        {
            _navigationService.ShowError(
                $"仮想タッチ処理でエラーが発生しました:\n{ex.Message}", "仮想タッチエラー");
        }
    }
#endif
}
