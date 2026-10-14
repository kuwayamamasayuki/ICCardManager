#nullable enable
using System.Threading.Tasks;

namespace ICCardManager.Services
{
    /// <summary>
    /// 共有されるセキュリティ関連の設定（バックアップ先・データベース保存先）を、操作ログとともに変更する（Issue #2264）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 呼び出し元（設定画面）は職員証の認証のスコープ（Issue #2260）の中で呼ぶ。操作ログの操作者は
    /// <see cref="ICurrentOperatorContext"/> から解決されるため、認証した職員の操作として記録される。
    /// </para>
    /// <para>
    /// どちらのメソッドも、変更と操作ログを<b>まとめて確定させるか、どちらも残さない</b>。
    /// 操作ログの記録に失敗したら変更も行わない（記録の無い変更を作らない）。
    /// </para>
    /// </remarks>
    public interface ISettingsChangeService
    {
        /// <summary>
        /// バックアップ先・データベース保存先の変更に、職員証の認証を求めるか
        /// </summary>
        /// <returns>
        /// 登録済みの職員が 1 人以上いれば true。0 人なら false（誰も認証できないため免除し、操作ログは「GUI操作」になる）
        /// </returns>
        /// <remarks>
        /// 判定は職員の登録・復元の免除（<see cref="StaffManagementService.RequiresOperatorAuthenticationAsync"/>。Issue #2263）に委ねる。
        /// 2 台目以降の PC は職員 0 人のローカルの既定 DB で起動し、設定画面で共有 DB を指すため、免除が無いと共有 DB へ移れない。
        /// 現在の DB を開けないときは例外を投げる。呼び出し元は保存先の変更も「デフォルトに戻す」も断る
        /// （認証なしで職員 0 人の DB へ戻せる経路を作らない。04_機能設計書 §17.1a）。
        /// </remarks>
        Task<bool> RequiresOperatorAuthenticationAsync();

        /// <summary>
        /// バックアップ先（共有 DB の <c>settings.backup_path</c>）を変更する
        /// </summary>
        /// <param name="backupPath">新しいバックアップ先（正規化済み。空文字は既定の場所）</param>
        /// <returns>変更したら true。DB の現在値と同じなら何も書かずに false</returns>
        /// <remarks>
        /// 値・変更日時・変更した PC 名・操作ログ（変更前後の値）を 1 つのトランザクションで書く。
        /// 変更前の値は画面が読み込んだ値ではなく、同じトランザクションで DB から読み直した値を記録する。
        /// </remarks>
        Task<bool> ChangeBackupPathAsync(string backupPath);

        /// <summary>
        /// この PC のデータベース保存先（<c>database_config.txt</c>）を変更する
        /// </summary>
        /// <param name="configFilePath"><c>database_config.txt</c> のパス</param>
        /// <param name="databaseFilePath">
        /// 新しいデータベースファイルのパス。空文字は「既定の場所」を書き込み、null は設定ファイルを削除する（「デフォルトに戻す」）
        /// </param>
        /// <returns>変更したら true。設定ファイルが既に同じ内容（null なら既に存在しない）なら何もせず false</returns>
        /// <remarks>
        /// 設定ファイルは PC ごとのファイル、操作ログは現在接続している DB にあり、1 つのトランザクションにまとめられない。
        /// 設定ファイルの書き換えを DB のトランザクションの中（コミットの前）で行い、操作ログの記録・コミットに
        /// 失敗したら設定ファイルを元の内容へ戻してから例外を投げ直す。
        /// </remarks>
        Task<bool> ChangeDatabaseConfigAsync(string configFilePath, string? databaseFilePath);
    }
}
