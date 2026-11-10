#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
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

        private static volatile string? _redirectedRootDirectory;

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
            if (!IsFullyQualified(rootDirectory))
            {
                throw new ArgumentException(
                    "アプリケーションデータの差し替え先には絶対パスを指定してください。",
                    nameof(rootDirectory));
            }

            _redirectedRootDirectory = rootDirectory;
        }

#if DEBUG
        /// <summary>
        /// 撮影用に差し替えた置き場所（<see cref="RedirectRootDirectoryForScreenshot"/>）。表示のときだけ本番の置き場所へ読み替える。
        /// </summary>
        private static volatile string? _screenshotRootDirectory;

        /// <summary>
        /// マニュアル用スクリーンショットの撮影のために置き場所を差し替え、画面に出すパスを本番の置き場所で表示させる
        /// （Issue #2435。DEBUG ビルド限定）。
        /// </summary>
        /// <param name="rootDirectory">撮影用の置き場所（UI テストが用意して毎回空にするフォルダー）の絶対パス</param>
        /// <remarks>
        /// <para>
        /// 撮影で起動したアプリが、開発機の本物の置き場所（DB・バックアップ・ログ・更新通知ファイル）に一切触れないようにする。
        /// あわせて <see cref="ToDisplayPath"/> が、この置き場所の配下のパスを本番の置き場所（<see cref="DefaultRootDirectory"/>）の
        /// 配下として表示する。システム管理画面の「保存先」は実際に使っている保存先を表示するため、読み替えなければ
        /// 撮影機にしか無いフォルダーがマニュアルの画像に写る。
        /// </para>
        /// <para>
        /// 読み替えるのは表示だけで、保存・検証・フォルダーを開く操作は実際のパスを使う。Release ビルドにはこのメソッドも
        /// 読み替えも無い（<see cref="ToDisplayPath"/> は受け取った値をそのまま返す）。呼ぶのは起動の入口（<c>App.OnStartup</c>）だけ。
        /// </para>
        /// </remarks>
        internal static void RedirectRootDirectoryForScreenshot(string rootDirectory)
        {
            RedirectRootDirectory(rootDirectory);
            _screenshotRootDirectory = rootDirectory;
        }
#endif

        /// <summary>
        /// 画面に表示するパスを返す。撮影用に置き場所を差し替えた DEBUG ビルドの起動（Issue #2435）でだけ、
        /// 差し替えた置き場所の配下を本番の置き場所の配下として表示する。それ以外は受け取った値をそのまま返す。
        /// </summary>
        /// <param name="path">実際に使っているパス</param>
        /// <remarks>
        /// 表示専用。編集できる欄（設定画面の入力欄）に使うと、読み替えた値がそのまま保存される。
        /// 単体テストの差し替え（<see cref="RedirectRootDirectory"/>）では読み替えない。
        /// </remarks>
        [return: NotNullIfNotNull("path")]
        public static string? ToDisplayPath(string? path)
        {
#if DEBUG
            return MapToDisplayPath(path, _screenshotRootDirectory, DefaultRootDirectory);
#else
            return path;
#endif
        }

        /// <summary>
        /// <paramref name="aliasedRoot"/> の配下（それ自身を含む）のパスを、<paramref name="displayRoot"/> の配下として読み替える（純粋関数）。
        /// </summary>
        /// <param name="path">読み替える対象。null・空はそのまま返す。</param>
        /// <param name="aliasedRoot">読み替え元のフォルダー。null なら読み替えない。</param>
        /// <param name="displayRoot">表示に使うフォルダー。</param>
        /// <remarks>
        /// 大文字小文字を区別しない（Windows のパス）。フォルダー名の途中では区切らない —
        /// <c>C:\Data\ICCardManager.UITests</c> は <c>C:\Data\ICCardManager</c> の配下ではない。
        /// </remarks>
        [return: NotNullIfNotNull("path")]
        internal static string? MapToDisplayPath(string? path, string? aliasedRoot, string displayRoot)
        {
            if (path is null || path.Length == 0 || aliasedRoot is null || aliasedRoot.Length == 0)
            {
                return path;
            }

            var root = aliasedRoot.TrimEnd('\\', '/');
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var rest = path.Substring(root.Length);
            if (rest.Length > 0 && rest[0] != '\\' && rest[0] != '/')
            {
                return path;
            }

            return displayRoot.TrimEnd('\\', '/') + rest;
        }

        /// <summary>
        /// ドライブ付きの絶対パス（<c>C:\…</c>）または UNC（<c>\\server\share…</c>）か。
        /// </summary>
        /// <remarks>
        /// <see cref="Path.IsPathRooted"/> は <c>\foo</c>（カレントドライブ相対）や <c>C:foo</c>
        /// （ドライブのカレントディレクトリ相対）も真とするため使わない。
        /// </remarks>
        internal static bool IsFullyQualified(string? path)
        {
            if (path is null || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return path.Length > 2;
            }

            return path.Length >= 3
                && char.IsLetter(path[0])
                && path[1] == ':'
                && (path[2] == '\\' || path[2] == '/');
        }
    }
}
