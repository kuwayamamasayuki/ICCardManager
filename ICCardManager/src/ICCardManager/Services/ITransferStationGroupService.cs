using System.Collections.Generic;
using System.Threading.Tasks;

namespace ICCardManager.Services
{
    /// <summary>
    /// 同一とみなす駅・バス停のグループを永続化する（Issue #1905）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「天神日銀前」と「天神中央郵便局前」のように道路を挟んで向かい合う実質同一の停留所や、
    /// 「天神」と「西鉄福岡(天神)」のように事業者違いで名前が異なる駅を登録する。
    /// 登録した名前どうしは <see cref="SummaryGenerator"/> の乗継・往復・循環の各判定で同一視される。
    /// </para>
    /// <para>
    /// 汎用/固有の別: 交通系固有（<c>domain-boundaries.md</c> の 3 リングでは外側）。
    /// </para>
    /// </remarks>
    public interface ITransferStationGroupService
    {
        /// <summary>
        /// 現在有効なグループを取得する
        /// </summary>
        /// <remarks>
        /// DB（<c>settings</c> テーブル）に保存されていればそれを、無ければ
        /// appsettings.json の <c>OrganizationOptions:SummaryRules:TransferStationGroups</c>
        /// （未指定なら C# の既定値）を返す。
        /// </remarks>
        Task<List<List<string>>> GetGroupsAsync();

        /// <summary>
        /// グループを保存し、実行中の <see cref="SummaryGenerator"/> へ即時反映する
        /// </summary>
        /// <param name="groups">保存するグループ。空白のみの名前と 2 件未満のグループは捨てられる</param>
        /// <returns>保存できた場合 true。競合・DB エラーで書き込めなかった場合 false</returns>
        Task<bool> SaveGroupsAsync(IEnumerable<IEnumerable<string>> groups);
    }
}
