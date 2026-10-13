#nullable enable

using System.IO;
using System.Text;

namespace ICCardManager.Common
{
    /// <summary>
    /// <c>C:\ProgramData\ICCardManager\</c> 配下の設定ファイル（<c>*_config.txt</c>）の置き場所と読み書き（Issue #2266 で集約）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// インストーラー（Inno Setup）と本アプリが共有する PC ごとの設定ファイルで、インストーラーは BOM なしの
    /// Shift_JIS、本アプリは UTF-8（BOM 付き）で書くため、読み取りは BOM で文字コードを判定する。
    /// </para>
    /// <para>
    /// 以前は <c>ViewModels.SettingsViewModel</c> の静的メソッドにあり、起動処理（<c>App</c>）や
    /// サービス層（<c>Services.ReportOutputFolderStore</c>）が ViewModel を参照する逆向きの依存になっていた。
    /// 置き場所の解決は <see cref="AppDataPaths.RootDirectory"/> に委ねる（テストプロセスでは一時フォルダー）。
    /// </para>
    /// </remarks>
    public static class AppConfigFiles
    {
        /// <summary>データベースの保存先（<c>database_config.txt</c>）</summary>
        public const string DatabaseConfigFileName = "database_config.txt";

        /// <summary>部署種別（<c>department_config.txt</c>）</summary>
        public const string DepartmentConfigFileName = "department_config.txt";

        /// <summary>帳票の出力先（<c>report_output_config.txt</c>。この PC の設定が正。Issue #2266）</summary>
        public const string ReportOutputConfigFileName = "report_output_config.txt";

        /// <summary>
        /// データベース設定ファイルのフルパス。呼ばれるたびに解決する（置き場所の差し替えに追随するため）。
        /// </summary>
        public static string DatabaseConfigPath => GetPath(DatabaseConfigFileName);

        /// <summary>部署種別設定ファイルのフルパス</summary>
        public static string DepartmentConfigPath => GetPath(DepartmentConfigFileName);

        /// <summary>帳票出力先設定ファイルのフルパス</summary>
        public static string ReportOutputConfigPath => GetPath(ReportOutputConfigFileName);

        /// <summary>
        /// アプリケーションデータのフォルダー配下にある設定ファイルのフルパス
        /// </summary>
        public static string GetPath(string fileName) => Path.Combine(AppDataPaths.RootDirectory, fileName);

        /// <summary>
        /// 設定ファイルからテキストを読み込む（BOM 自動判定）。ファイルが無い・読めない場合は空文字。
        /// </summary>
        /// <remarks>
        /// Inno Setup の SaveStringToFile は Shift_JIS（ANSI）で書き込むが、
        /// アプリの File.WriteAllText は UTF-8 BOM 付きで書き込む。
        /// どちらでも正しく読めるよう、BOM で判定する。前後の空白は取り除く。
        /// </remarks>
        public static string Read(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return string.Empty;
            }

            try
            {
                var bytes = File.ReadAllBytes(filePath);
                string value;
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    // UTF-8 BOM
                    value = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                }
                else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    // UTF-16 LE BOM
                    value = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                }
                else
                {
                    // BOMなし: Shift_JIS（Inno Setupが書いた場合）として読む
                    value = Encoding.GetEncoding(932).GetString(bytes);
                }

                return value.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 設定ファイルにテキストを書き込む（アトミック書き込み）
        /// </summary>
        /// <remarks>
        /// 一時ファイルに書き出してから <see cref="File.Replace(string, string, string)"/> で
        /// 原子的に置換することで、書き込み途中のクラッシュによるファイル破損・消失を防止する。
        /// File.Replace は対象ファイルが存在しないと例外を投げるため、初回作成時のみ
        /// File.Move へフォールバックする（Issue #1598）。
        ///
        /// 旧実装は File.Delete → File.Move の2段階で、その間にクラッシュすると
        /// database_config.txt が消失し、共有モード運用中の PC が警告なくローカル DB へ
        /// フォールバックして台帳が分裂する実害があった。
        /// </remarks>
        /// <param name="filePath">書き込み先</param>
        /// <param name="value">内容。null の場合は空のファイルを書く</param>
        public static void Write(string filePath, string? value)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (directory is not null && directory.Length > 0)
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, value ?? string.Empty, Encoding.UTF8);
            if (File.Exists(filePath))
            {
                // 原子的置換: Delete→Move の「対象ファイルが存在しない窓」を排除する。
                // 第3引数（バックアップファイル名）は null = バックアップを作らない。
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                // 初回作成時は対象が存在しないため File.Replace が使えない
                File.Move(tempPath, filePath);
            }
        }
    }
}
