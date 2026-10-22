#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
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
        Task<string?> GetAsync(string key);

        /// <summary>
        /// 呼び出し元が開いたトランザクションの中で設定値を取得する（Issue #2264）
        /// </summary>
        /// <param name="key">設定キー</param>
        /// <param name="transaction">呼び出し元のトランザクション</param>
        /// <returns>設定値。未保存なら null</returns>
        /// <remarks>
        /// 監査ログの「変更前」を、書き換えと同じトランザクションで DB から読むために使う
        /// （画面が読み込んだ時点の値は、共有モードでは他 PC の変更より古い）。
        /// </remarks>
        Task<string?> GetAsync(string key, SQLiteTransaction transaction);

        /// <summary>
        /// バックアップ先を変更し、変更日時と変更した PC 名を同じトランザクションで記録する（Issue #2264）
        /// </summary>
        /// <param name="backupPath">新しいバックアップ先（空文字は既定の場所）</param>
        /// <param name="machineName">変更した PC 名</param>
        /// <param name="changedAt">変更日時</param>
        /// <param name="transaction">呼び出し元のトランザクション（監査ログも同じトランザクションで書く）</param>
        /// <remarks>
        /// <c>backup_path</c> は共有 DB に保存され、全 PC が起動のたびにこの場所へ DB 全体をコピーする。
        /// 職員証の認証と操作ログを伴う経路（<c>Services.SettingsChangeService</c>）だけが書く。
        /// 一括保存（<see cref="SaveAppSettingsAsync"/>）には載せない。
        /// トランザクションを渡した書き込みはキャッシュを破棄しない。呼び出し元が完了後に <see cref="InvalidateCache"/> を呼ぶ。
        /// </remarks>
        Task ChangeBackupPathAsync(string backupPath, string machineName, DateTime changedAt, SQLiteTransaction transaction);

        /// <summary>
        /// 設定のキャッシュを破棄する（トランザクションを渡した書き込みの後に呼び出し元が呼ぶ。Issue #2264）
        /// </summary>
        void InvalidateCache();

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
        /// <para>
        /// <see cref="AppSettings.BackupPath"/> も<b>設定しても DB へは反映されない</b>（Issue #2264）。
        /// バックアップ先の変更は職員証の認証と操作ログを伴う <see cref="ChangeBackupPathAsync"/> だけが書く。
        /// 一括保存は渡された値を条件なしで書くため、載せると、古い値を持つ PC の保存が
        /// 他 PC で変更したバックアップ先を認証も記録も無いまま巻き戻す。
        /// </para>
        /// <para>
        /// <see cref="AppSettings.MainWindowSettings"/> も<b>設定しても DB へは反映されない</b>（Issue #2305）。
        /// ウィンドウの位置・サイズは設定画面で編集しない値で、終了時に <see cref="SaveMainWindowSettingsAsync"/> だけが書く。
        /// </para>
        /// <para>
        /// 一括保存は条件を持たない UPSERT で、渡された値を<b>すべて</b>書く。<see cref="GetAppSettingsAsync"/>
        /// （TTL キャッシュ）で読んだ値の一部だけを変えて渡すと、触っていない値も読んだ時点の値で書き戻し、
        /// 共有モードでは他 PC がその後に変えた設定（部署種別・残額警告など）を巻き戻す（Issue #2305）。
        /// 呼び出し元は画面で編集した値から組み立てる設定画面（F5）だけにすること
        /// （静的検査 <c>SettingsMaintenanceKeyConventionTests</c> が固定する）。
        /// </para>
        /// </remarks>
        Task<bool> SaveAppSettingsAsync(AppSettings settings);

        /// <summary>
        /// メインウィンドウの位置・サイズだけを保存する（終了時の保存。Issue #2305）
        /// </summary>
        /// <param name="windowSettings">保存する位置・サイズ。<see cref="WindowSettings.Left"/> 等が null の項目は書かない</param>
        /// <returns>すべてのキーを書けたら true</returns>
        /// <remarks>
        /// <c>window_left</c> / <c>window_top</c> / <c>window_width</c> / <c>window_height</c> / <c>window_maximized</c>
        /// 以外のキーは書かない。以前は終了時に <see cref="SaveAppSettingsAsync"/> へキャッシュ由来の
        /// <see cref="AppSettings"/> を丸ごと渡しており、共有モードで他 PC が変えた設定を古い値へ巻き戻していた。
        /// </remarks>
        Task<bool> SaveMainWindowSettingsAsync(WindowSettings windowSettings);

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
