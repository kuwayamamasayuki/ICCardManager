using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// <see cref="ILedgerRepository.GetLatestBeforeDateAsync"/> のフェイク（Issue #2043）。
/// </summary>
/// <remarks>
/// <para>
/// 帳票のデータ準備（<c>ReportDataBuilder</c>）は前月末残高を、リポジトリの確定済み単票クエリ
/// <see cref="ILedgerRepository.GetLatestBeforeDateAsync"/>（指定日より前の最終稼働日の全行を
/// 残高チェーンで解決した最終行を返す。Issue #1731 / #1999）から取る。
/// </para>
/// <para>
/// 固定値を返す <c>Setup</c> ではシードの有無で結果が変わる日（同額のポイント還元と利用で残高が
/// 循環する Issue #1004 形状）を再現できないため、テストが既に仕込んでいる
/// <see cref="ILedgerQueryService.GetByMonthAsync"/> /
/// <see cref="ILedgerQueryService.GetCarryoverBalanceAsync"/> のモックを**データ源として**読み、
/// 実装と同じ規則（古い日から順にチェーン解決し、その日の最終残高を次の日のシードにする）で
/// 最終行を組み立てる。これにより各テストのモック構成を書き換えずに済む。
/// </para>
/// <para>
/// 貸出中レコードは除外しない（実装と同じ）。月をさかのぼる範囲は
/// <see cref="LookbackMonths"/> 月で、それより前は年度繰越のモックへ委ねる。
/// </para>
/// </remarks>
internal static class PrecedingLedgerBalanceFake
{
    /// <summary>台帳をさかのぼって集める月数（実 DB の全期間走査に相当する近似）</summary>
    private const int LookbackMonths = 24;

    /// <summary>
    /// 指定したモックへ <see cref="ILedgerRepository.GetLatestBeforeDateAsync"/> のフェイクを仕込む。
    /// </summary>
    public static void Install(Mock<ILedgerRepository> ledgerRepositoryMock)
    {
        ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns((string idm, DateTime beforeDate) =>
                ResolveAsync(ledgerRepositoryMock.Object, idm, beforeDate));
    }

    private static async Task<Ledger> ResolveAsync(
        ILedgerRepository repository, string cardIdm, DateTime beforeDate)
    {
        // 直前日が属する年度の「前年度繰越」をチェーン開始点のシードにする
        var previousDay = beforeDate.AddDays(-1);
        var fiscalYear = FiscalYearHelper.GetFiscalYear(previousDay.Year, previousDay.Month);
        var seed = await repository.GetCarryoverBalanceAsync(cardIdm, fiscalYear - 1);

        var ledgers = new List<Ledger>();
        var cursor = new DateTime(beforeDate.Year, beforeDate.Month, 1);
        for (var i = 0; i < LookbackMonths; i++)
        {
            var monthly = await repository.GetByMonthAsync(cardIdm, cursor.Year, cursor.Month);
            if (monthly != null)
            {
                ledgers.AddRange(monthly);
            }

            cursor = cursor.AddMonths(-1);
        }

        var before = ledgers.Where(l => l.Date < beforeDate).ToList();
        if (before.Count == 0)
        {
            // 台帳が 1 件も無いカードでも、前年度繰越があればその残高が「当月 1 日より前の最終残高」になる
            return seed.HasValue
                ? new Ledger
                {
                    CardIdm = cardIdm,
                    Date = new DateTime(fiscalYear, 3, 31),
                    Summary = "前年度末残高（テスト用フェイク）",
                    Balance = seed.Value
                }
                : null;
        }

        Ledger last = null;
        foreach (var day in before.GroupBy(l => l.Date.Date).OrderBy(g => g.Key))
        {
            last = LedgerOrderHelper.ReorderByBalanceChain(day.ToList(), seed).Last();
            seed = last.Balance;
        }

        return last;
    }
}
