#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// テストで記録済みの操作ログを読み返すための問い合わせ（Issue #2330）
/// </summary>
/// <remarks>
/// <para>
/// 以前は <see cref="IOperationLogRepository"/> に <c>GetByDateRangeAsync</c> / <c>GetByOperatorAsync</c> /
/// <c>GetByTargetAsync</c> があったが、本番のどこからも呼ばれず、テストだけが使っていた。
/// テストの検証が本番の検索経路を通らないため削除し、読み返しは本番の操作ログ画面と同じ
/// <see cref="IOperationLogRepository.SearchAllAsync"/> を通す。
/// </para>
/// <para>
/// <see cref="OperationLogSearchCriteria.TargetId"/> は部分一致で、操作者の IDm は条件に持たないため、
/// 完全一致・IDm での絞り込みはここで行う。並びは <see cref="IOperationLogRepository.SearchAllAsync"/> の
/// 「時刻の昇順・同時刻は ID の昇順」のまま返す。
/// </para>
/// </remarks>
internal static class OperationLogTestQueries
{
    /// <summary>対象テーブル・対象 ID（完全一致）の操作ログを古い順に返す</summary>
    public static async Task<IEnumerable<OperationLog>> GetByTargetAsync(
        this IOperationLogRepository repository, string targetTable, string targetId)
    {
        var logs = await repository.SearchAllAsync(new OperationLogSearchCriteria
        {
            TargetTable = targetTable,
            TargetId = targetId
        });
        return logs.Where(l => l.TargetTable == targetTable && l.TargetId == targetId).ToList();
    }

    /// <summary>操作者の IDm が一致する操作ログを古い順に返す</summary>
    public static async Task<IEnumerable<OperationLog>> GetByOperatorAsync(
        this IOperationLogRepository repository, string operatorIdm)
    {
        var logs = await repository.SearchAllAsync(new OperationLogSearchCriteria());
        return logs.Where(l => l.OperatorIdm == operatorIdm).ToList();
    }

    /// <summary>期間（両端の日を含む）の操作ログを古い順に返す</summary>
    public static Task<IEnumerable<OperationLog>> GetByDateRangeAsync(
        this IOperationLogRepository repository, DateTime fromDate, DateTime toDate)
        => repository.SearchAllAsync(new OperationLogSearchCriteria
        {
            FromDate = fromDate,
            ToDate = toDate
        });
}
