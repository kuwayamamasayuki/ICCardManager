using System;
using System.IO;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// XAML テキスト上の静的検証テストが、検査対象の View ソースを解決するための共通ヘルパー。
/// </summary>
/// <remarks>
/// <para>
/// レイアウト規約テスト（<c>*LayoutTests</c> 等）は実描画ではなく XAML のテキストを検査するため、
/// テスト実行ディレクトリからソースツリーを遡って対象ファイルを見つける必要がある。
/// この解決処理は各テストクラスに1文字違わぬ形で複製されていたため、1か所へ集約した（Issue #1740）。
/// </para>
/// <para>
/// 集約しないと、テスト出力ディレクトリの構成やソースレイアウトが変わったときに全クラスを
/// 個別に直すことになり、直し漏れたクラスだけが静かに検査対象を見失う。
/// </para>
/// <para>
/// 解決処理自体は拡張子を問わないため、XAML 以外のソーステキスト検査からも利用してよい
/// （Issue #1785 の <c>DataExportImportViewModelImportPathSharingTests</c> が ViewModel の .cs を検査する）。
/// </para>
/// </remarks>
public static class ViewSourceLocator
{
    /// <summary>
    /// <c>src/ICCardManager/</c> 配下の相対パスから、View ソースの絶対パスを解決する。
    /// </summary>
    /// <param name="relativePath">
    /// <c>src/ICCardManager/</c> からの相対パス（例: <c>Path.Combine("Views", "Dialogs", "Foo.xaml")</c>）
    /// </param>
    /// <exception cref="InvalidOperationException">親階層を遡っても見つからない場合</exception>
    public static string Resolve(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"{relativePath} を {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
