using DebugDataViewer;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Infrastructure.CardReader;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// <c>database_config.txt</c> を採用できなかったときの警告の所在（Issue #2012）。
    /// </summary>
    /// <remarks>
    /// <c>DbStatusMessage</c> はテーブルを読み込むたびに上書きされる一時的なステータス欄であり、
    /// <c>InitializeAsync</c> の初回読み込み（<c>LoadTableDataAsync</c>）が起動直後に必ず上書きする。
    /// そこへ起動時の警告を入れると一度も表示されない
    /// （<c>error-messages.md</c>「文言を長くしたら、その表示領域が『その状態で生きているか』を
    /// 必ず確認する」#1727 / #1759）。
    /// </remarks>
    [Trait("Category", "Unit")]
    public class DebugDataViewerConfigWarningTests
    {
        private static readonly string[] NoArgs = { @"C:\tools\DebugDataViewer.exe" };
        private const string ExeDir = @"C:\tools";

        private static MainViewModel CreateViewModel(DatabasePathResolution resolution)
        {
            var cardReader = new Mock<ICardReader>();
            return new MainViewModel(cardReader.Object, new DbContext(":memory:"), resolution);
        }

        [Fact]
        public void 棄却された設定値の警告は上書きされない専用の領域へ出すこと()
        {
            var resolution = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => @"..\iccard.db", _ => false);

            var viewModel = CreateViewModel(resolution);

            viewModel.HasConfigPathWarning.Should().BeTrue();
            viewModel.ConfigPathWarningMessage.Should().Contain(@"..\iccard.db");
            // 一時的なステータス欄へ入れると初回のテーブル読み込みで消える
            viewModel.DbStatusMessage.Should().BeEmpty();
        }

        [Fact]
        public void 設定値が正常なら警告を出さないこと()
        {
            // 対の表明: これが無いと「常に警告を出す」実装でも上の 1 件は緑になる
            var resolution = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => @"\\server\share\iccard.db", _ => false);

            var viewModel = CreateViewModel(resolution);

            viewModel.HasConfigPathWarning.Should().BeFalse();
            viewModel.ConfigPathWarningMessage.Should().BeEmpty();
        }

        [Fact]
        public void 警告の行動指示は実在するボタンを指すこと()
        {
            // 「どうすれば」が画面に無い操作を指すと、利用者は指示を実行できない
            // （error-messages.md「UI 操作の場所を示す」）
            var resolution = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => @"..\iccard.db", _ => false);

            var viewModel = CreateViewModel(resolution);

            viewModel.ConfigPathWarningMessage.Should().Contain("選択...");
            viewModel.ConfigPathWarningMessage.Should().EndWith("してください。");
        }
    }
}
