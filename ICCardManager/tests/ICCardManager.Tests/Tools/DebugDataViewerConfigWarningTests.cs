using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DebugDataViewer;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Tests.Views.Helpers;
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

            viewModel.ConfigPathWarningMessage.Should().EndWith("してください。");

            // Issue #2102: 旧版は文言に「選択...」が含まれるかしか見ておらず、画面（XAML）の側で
            // ボタンの表記を変えても、ボタンを消しても緑だった。文言が名指すボタンを実際の XAML から探し、
            // 名指したとおりの場所（画面右下＝最下段の右寄せのフッター）にあって、DB を選び直す
            // コマンドへつながっていることを確かめる。
            var buttonNames = Regex.Matches(viewModel.ConfigPathWarningMessage, "「(?<name>[^」]+)」ボタン")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups["name"].Value)
                .ToList();
            buttonNames.Should().ContainSingle("行動指示はボタンを 1 つ名指す");

            var xaml = XamlElementInspection.StripXmlComments(File.ReadAllText(Path.Combine(
                TestPaths.GetSolutionRoot(), "tools", "DebugDataViewer", "MainWindow.xaml")));
            var buttons = XamlElementInspection.EnumerateElements(xaml, "Button")
                .Where(b => XamlElementInspection.GetAttribute(b.StartTag, "Content") == buttonNames[0])
                .ToList();
            buttons.Should().ContainSingle($"文言が名指す「{buttonNames[0]}」ボタンが画面に 1 つだけ実在する");

            XamlElementInspection.GetBindingPropertyName(
                    XamlElementInspection.GetAttribute(buttons[0].StartTag, "Command"))
                .Should().Be(nameof(MainViewModel.SelectDatabaseCommand),
                    "名指したボタンが、正しいデータベースファイルを選び直す操作につながっていること");

            var footer = XamlElementInspection.EnumerateElements(xaml, "StackPanel")
                .Where(p => p.Body.Contains(buttons[0].StartTag))
                .OrderBy(p => p.Body.Length)
                .First();
            var rootRowCount = Regex.Matches(
                XamlElementInspection.EnumerateElements(xaml, "Grid.RowDefinitions").First().Body,
                @"<RowDefinition\b").Count;
            XamlElementInspection.GetAttribute(footer.StartTag, "Grid.Row")
                .Should().Be((rootRowCount - 1).ToString(), "文言は「画面右下」と述べている（最下段）");
            XamlElementInspection.GetAttribute(footer.StartTag, "HorizontalAlignment")
                .Should().Be("Right", "文言は「画面右下」と述べている（右寄せ）");
        }
    }
}
