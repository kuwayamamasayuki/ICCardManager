#nullable enable

namespace ICCardManager.Common
{
    /// <summary>
    /// 実際に開くデータベースパス（解決済み）を DbContext へ運ぶ入れ物
    /// </summary>
    /// <remarks>
    /// 値は <c>Data.DatabaseLocationResolver</c> が database_config.txt から解決して設定する（形式の検証も済み）。
    /// appsettings.json にはバインドしない（Issue #2389 で <c>DatabaseOptions:Path</c> を廃止。旧版の値は
    /// 起動時に一度だけ database_config.txt へ移す）。
    /// 空文字の場合、既定のローカルパス（C:\ProgramData\ICCardManager\iccard.db）を使用します。
    /// </remarks>
    public class DatabaseOptions
    {
        /// <summary>
        /// データベースファイルのパス（空文字 = ローカルデフォルト）
        /// </summary>
        public string Path { get; set; } = "";
    }
}
