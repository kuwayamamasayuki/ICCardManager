using System;
using System.Threading.Tasks;
using System.Windows;
using FluentAssertions;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// INavigationServiceのモック統合テスト（Issue #853）
/// ViewModelでINavigationServiceをモックして使用するパターンの検証
/// </summary>
public class NavigationServiceIntegrationTests
{
    /// <summary>
    /// ShowDialogが呼ばれたことをVerifyで検証できること
    /// </summary>
    [Fact]
    public void ShowDialog_ShouldBeVerifiable()
    {
        // Arrange
        var mock = new Mock<INavigationService>();

        // Act
        mock.Object.ShowDialog<Window>();

        // Assert
        mock.Verify(n => n.ShowDialog<Window>(It.IsAny<Action<Window>>()), Times.Once);
    }

    /// <summary>
    /// ShowDialogAsyncが呼ばれたことをVerifyで検証できること
    /// </summary>
    [Fact]
    public async Task ShowDialogAsync_ShouldBeVerifiable()
    {
        // Arrange
        var mock = new Mock<INavigationService>();
        mock.Setup(n => n.ShowDialogAsync<Window>(It.IsAny<Func<Window, Task>>()))
            .ReturnsAsync((bool?)null);

        // Act
        await mock.Object.ShowDialogAsync<Window>();

        // Assert
        mock.Verify(n => n.ShowDialogAsync<Window>(It.IsAny<Func<Window, Task>>()), Times.Once);
    }

    /// <summary>
    /// ShowDialogがfalseを返した場合の処理が正しく動作すること
    /// </summary>
    [Fact]
    public void ShowDialog_WhenReturnsFalse_ShouldIndicateCancel()
    {
        // Arrange
        var mock = new Mock<INavigationService>();
        mock.Setup(n => n.ShowDialog<Window>(It.IsAny<Action<Window>>()))
            .Returns(false);

        // Act
        var result = mock.Object.ShowDialog<Window>();

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// ShowDialogAsyncがtrueを返した場合の処理が正しく動作すること
    /// </summary>
    [Fact]
    public async Task ShowDialogAsync_WhenReturnsTrue_ShouldIndicateSuccess()
    {
        // Arrange
        var mock = new Mock<INavigationService>();
        mock.Setup(n => n.ShowDialogAsync<Window>(It.IsAny<Func<Window, Task>>()))
            .ReturnsAsync(true);

        // Act
        var result = await mock.Object.ShowDialogAsync<Window>();

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// configureコールバックなしでShowDialogが呼べること
    /// </summary>
    [Fact]
    public void ShowDialog_WithoutConfigure_ShouldWork()
    {
        // Arrange
        var mock = new Mock<INavigationService>();
        mock.Setup(n => n.ShowDialog<Window>(null))
            .Returns(true);

        // Act
        var result = mock.Object.ShowDialog<Window>();

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// INavigationServiceとIDialogServiceの両方のメソッドが1つのモックで使えること
    /// </summary>
    [Fact]
    public void Mock_ShouldSupportBothInterfaceMethods()
    {
        // Arrange
        var mock = new Mock<INavigationService>();
        mock.Setup(n => n.ShowConfirmation("確認メッセージ", "タイトル")).Returns(true);
        mock.Setup(n => n.ShowDialog<Window>(It.IsAny<Action<Window>>())).Returns(true);

        // Act
        var confirmResult = mock.Object.ShowConfirmation("確認メッセージ", "タイトル");
        var dialogResult = mock.Object.ShowDialog<Window>();

        // Assert
        confirmResult.Should().BeTrue("IDialogServiceのメソッドが動作すること");
        dialogResult.Should().BeTrue("INavigationServiceのメソッドが動作すること");
    }

    /// <summary>
    /// ShowDialogの呼び出し回数が正しく記録されること
    /// </summary>
    [Fact]
    public void ShowDialog_ShouldTrackCallCount()
    {
        // Arrange
        var mock = new Mock<INavigationService>();

        // Act
        mock.Object.ShowDialog<Window>();
        mock.Object.ShowDialog<Window>();
        mock.Object.ShowDialog<Window>();

        // Assert
        mock.Verify(n => n.ShowDialog<Window>(It.IsAny<Action<Window>>()), Times.Exactly(3));
    }
}
