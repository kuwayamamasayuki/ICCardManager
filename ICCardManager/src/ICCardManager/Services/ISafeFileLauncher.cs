namespace ICCardManager.Services
{
    /// <summary>
    /// Issue #1465: 「フォルダを開く」「ファイルを開く」UI コマンドからの
    /// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> 呼び出しを
    /// 一元化し、設定ファイル書換による任意コード実行を防ぐサービス。
    /// </summary>
    /// <remarks>
    /// <para>
    /// フォルダオープンは <c>explorer.exe</c> を <c>UseShellExecute=false</c> で直接起動して
    /// シェル関連付けを経由しない。ファイルオープンは
    /// <see cref="ICCardManager.Common.SafeFilePathValidator.AllowedFileExtensions"/> の
    /// 拡張子ホワイトリストを通過したものだけが <c>UseShellExecute=true</c> で起動される。
    /// </para>
    /// </remarks>
    public interface ISafeFileLauncher
    {
        /// <summary>
        /// 指定フォルダを explorer.exe で開く。
        /// </summary>
        SafeFileLaunchResult LaunchFolder(string folderPath);

        /// <summary>
        /// 指定ファイルを関連付けアプリで開く。拡張子は <c>.xlsx</c> / <c>.csv</c> のみ許可。
        /// </summary>
        SafeFileLaunchResult LaunchFile(string filePath);
    }

    /// <summary>
    /// <see cref="ISafeFileLauncher"/> の起動結果。失敗時は <see cref="ErrorMessage"/> に
    /// ユーザーへ表示可能な理由文字列が入る。
    /// </summary>
    public sealed class SafeFileLaunchResult
    {
        public bool Success { get; private set; }
        public string ErrorMessage { get; private set; }

        public static SafeFileLaunchResult Ok() => new() { Success = true };

        public static SafeFileLaunchResult Fail(string message) =>
            new() { Success = false, ErrorMessage = message };
    }
}
