using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories
{
/// <summary>
    /// 設定リポジトリインターフェース
    /// </summary>
    public interface ISettingsRepository
    {
        /// <summary>
        /// 設定値を取得
        /// </summary>
        /// <param name="key">設定キー</param>
        Task<string> GetAsync(string key);

        /// <summary>
        /// 設定値を保存
        /// </summary>
        /// <param name="key">設定キー</param>
        /// <param name="value">設定値</param>
        Task<bool> SetAsync(string key, string value);

        /// <summary>
        /// 全設定をAppSettingsオブジェクトとして取得
        /// </summary>
        Task<AppSettings> GetAppSettingsAsync();

        /// <summary>
        /// 全設定をAppSettingsオブジェクトとして取得（同期版）
        /// </summary>
        /// <remarks>
        /// アプリケーション起動時など、非同期が使用できない場面で使用。
        /// 通常はGetAppSettingsAsync()を使用すること。
        /// </remarks>
        AppSettings GetAppSettings();

        /// <summary>
        /// AppSettingsオブジェクトを保存
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="AppSettings.LastVacuumDate"/> は<b>設定しても DB へは反映されない</b>（Issue #1997）。
        /// <c>last_vacuum_date</c> は月次 VACUUM の先勝ちロック（Issue #1482）そのものであり、
        /// 更新経路は <see cref="TryAcquireMonthlyVacuumLockAsync"/> の CAS だけに限る。
        /// 一括保存は月ガードを持たないため、TTL キャッシュ由来の古い値を書き戻して
        /// 当月のロックを巻き戻し、同じ月に VACUUM が複数回走る。
        /// </para>
        /// <para>
        /// 同じ理由で、保守処理が記録する <c>last_backup_success_at</c> / <c>last_backup_machine</c> /
        /// <c>last_vacuum_machine</c>（Issue #1689）も一括保存の対象外（<see cref="AppSettings"/> に持たない）。
        /// 後から更新要件が出たら専用メソッドを新設すること（一括保存へ戻さない、Issue #1726）。
        /// </para>
        /// </remarks>
        Task<bool> SaveAppSettingsAsync(AppSettings settings);

        /// <summary>
        /// 当月の VACUUM 実行権を先勝ちで獲得する（Issue #1482）。
        /// </summary>
        /// <param name="today">基準日。</param>
        /// <returns>
        /// 自 PC が VACUUM を実行すべきなら <c>true</c>、
        /// 既に他 PC が当月分を確保済みなら <c>false</c>。
        /// </returns>
        /// <remarks>
        /// 共有モードで複数 PC が同時に呼び出しても、原子的 UPSERT により正確に 1 つだけが
        /// <c>true</c> を返す。<c>true</c> を受け取った PC は VACUUM 失敗時も再試行しない
        /// （来月まで誰も試行しない）。
        /// </remarks>
        Task<bool> TryAcquireMonthlyVacuumLockAsync(DateTime today);
    }
}
