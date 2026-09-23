using System;
using System.IO;

namespace ICCardManager.Common
{
    /// <summary>
    /// 全ユーザーで共有するアプリケーションデータの置き場所（<c>C:\ProgramData\ICCardManager</c>）を
    /// 1 か所で解決する（Issue #2098）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// DB の既定の保存先・バックアップの既定の保存先・<c>database_config.txt</c> 等の設定ファイル・
    /// エラーログ・ファイルログはすべてこのフォルダーの配下に置く。以前は各クラスが
    /// <c>Environment.SpecialFolder.CommonApplicationData</c> から個別にパスを組み立てていたため、
    /// テストから差し替える手段が無く、<b>テストを実行するだけで開発機の本物の DB 設定・バックアップ・
    /// エラーログが書き換わっていた</b>（共有モードの PC では共有 DB のパス設定が消えた）。
    /// </para>
    /// <para>
    /// 置き場所の解決をここへ寄せたことで、単体テストのアセンブリは起動時に 1 回
    /// <see cref="RedirectRootDirectory"/> を呼ぶだけで、すべての経路を一時フォルダーへ向けられる。
    /// 本番コードで <c>CommonApplicationData</c> を直接参照してよいのは本クラスだけ
    /// （静的検査 <c>AppDataPathsConventionTests</c> が固定する）。
    /// </para>
    /// </remarks>
    public static class AppDataPaths
    {
        /// <summary>
        /// アプリケーションデータのフォルダー名。
        /// </summary>
        public const string ApplicationFolderName = "ICCardManager";

        private static volatile string _redirectedRootDirectory;

        /// <summary>
        /// 本番の置き場所（<c>C:\ProgramData\ICCardManager</c>）。差し替えの影響を受けない。
        /// </summary>
        internal static string DefaultRootDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ApplicationFolderName);

        /// <summary>
        /// 現在有効なアプリケーションデータのフォルダー。
        /// </summary>
        /// <remarks>
        /// 呼ばれるたびに解決する。静的フィールドへキャッシュすると、差し替えより前に
        /// 型が初期化された場合に本物のフォルダーを握ったままになる。
        /// </remarks>
        public static string RootDirectory => _redirectedRootDirectory ?? DefaultRootDirectory;

        /// <summary>
        /// アプリケーションデータの置き場所を差し替える。
        /// </summary>
        /// <param name="rootDirectory">差し替え先の絶対パス</param>
        /// <remarks>
        /// 単体テストのアセンブリが、どのテストよりも先に（モジュール初期化子で）1 回だけ呼ぶ。
        /// テストごとに切り替える用途には使わない ― 並列に走る他のテストの置き場所まで変わる。
        /// テストごとに独立させたい設定ファイルは、<c>SettingsViewModel</c> のように
        /// 置き場所を受け取るコンストラクタで注入する。
        /// </remarks>
        internal static void RedirectRootDirectory(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathRooted(rootDirectory))
            {
                throw new ArgumentException(
                    "アプリケーションデータの差し替え先には絶対パスを指定してください。",
                    nameof(rootDirectory));
            }

            _redirectedRootDirectory = rootDirectory;
        }
    }
}
