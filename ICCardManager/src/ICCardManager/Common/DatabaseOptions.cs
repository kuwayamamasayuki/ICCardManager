namespace ICCardManager.Common
{
    /// <summary>
    /// データベース設定オプション
    /// </summary>
    /// <remarks>
    /// appsettings.json の "DatabaseOptions" セクションにバインドされます。
    /// 共有フォルダ上のDBを使用する場合、UNCパス（例: \\server\share\ICCardManager\iccard.db）を指定します。
    /// 空文字またはnullの場合、従来のローカルパス（C:\ProgramData\ICCardManager\iccard.db）を使用します。
    /// </remarks>
    public class DatabaseOptions
    {
        /// <summary>
        /// データベースファイルのパス（空文字 = ローカルデフォルト）
        /// </summary>
        public string Path { get; set; } = "";
    }
}
