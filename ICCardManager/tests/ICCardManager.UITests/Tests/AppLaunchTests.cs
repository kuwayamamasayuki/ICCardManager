using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// アプリケーション起動時の基本的な UI 表示を検証するテスト。
    /// </summary>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class AppLaunchTests
    {
        [Fact]
        public void アプリ起動後にメインウィンドウが表示される()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            page.Window.Should().NotBeNull();
            page.Name.Should().StartWith(TestConstants.MainWindowName);
        }

        [Fact]
        public void 起動直後に使い方ガイドが表示される()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            page.UsageGuideElement.Should().NotBeNull(
                "初回起動時は使い方ガイドが表示されるべき");
        }

        [Fact]
        public void ツールバーに全ボタンが存在する()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            page.ReportButton.Should().NotBeNull("帳票ボタン");
            page.StaffManageButton.Should().NotBeNull("職員管理ボタン");
            page.CardManageButton.Should().NotBeNull("交通系ICカード管理ボタン");
            page.DataExportImportButton.Should().NotBeNull("データ入出力ボタン");
            page.SettingsButton.Should().NotBeNull("設定ボタン");
            page.SystemManageButton.Should().NotBeNull("システム管理ボタン");
            page.HelpButton.Should().NotBeNull("ヘルプボタン");
            page.ExitButton.Should().NotBeNull("終了ボタン");
        }

        [Fact]
        public void ステータスバーにバージョンが表示される()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var versionElement = page.AppVersionElement;
            versionElement.Should().NotBeNull("ステータスバーにバージョン表示があるべき");
        }

        [Fact]
        public void ステータスバーにカードリーダー状態が表示される()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var readerStatus = page.CardReaderStatusElement;
            readerStatus.Should().NotBeNull("ステータスバーにカードリーダー接続状態があるべき");
        }

        [Fact]
        public void カード一覧サイドバーが表示される()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            page.CardListElement.Should().NotBeNull("カード一覧サイドバーが表示されるべき");
        }
    }
}
