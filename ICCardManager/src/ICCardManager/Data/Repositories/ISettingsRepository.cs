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
