using System;
using System.Threading.Tasks;
using System.Windows;
using FluentAssertions;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// NavigationServiceの単体テスト（Issue #853）
/// </summary>
public class NavigationServiceTests
{
    /// <summary>
    /// INavigationServiceがIDialogServiceを継承していること
    /// </summary>
    [Fact]
    public void INavigationService_ShouldInheritFromIDialogService()
    {
        // Assert
        typeof(INavigationService).GetInterfaces().Should().Contain(typeof(IDialogService));
    }

    /// <summary>
    /// NavigationServiceがINavigationServiceを実装していること
    /// </summary>
    [Fact]
    public void NavigationService_ShouldImplementINavigationService()
    {
        // Assert
        typeof(NavigationService).Should().Implement<INavigationService>();
    }

    /// <summary>
    /// NavigationServiceがDialogServiceを継承していること
    /// </summary>
    [Fact]
    public void NavigationService_ShouldInheritFromDialogService()
    {
        // Assert
        typeof(NavigationService).Should().BeDerivedFrom<DialogService>();
    }

    /// <summary>
    /// INavigationServiceにShowDialogメソッドが定義されていること
    /// </summary>
    [Fact]
    public void INavigationService_ShouldHaveShowDialogMethod()
    {
        // Assert
        var method = typeof(INavigationService).GetMethod("ShowDialog");
        method.Should().NotBeNull("ShowDialog<TDialog> メソッドが定義されているべき");
        method!.IsGenericMethod.Should().BeTrue("ジェネリックメソッドであるべき");
    }

    /// <summary>
    /// INavigationServiceにShowDialogAsyncメソッドが定義されていること
    /// </summary>
    [Fact]
    public void INavigationService_ShouldHaveShowDialogAsyncMethod()
    {
        // Assert
        var method = typeof(INavigationService).GetMethod("ShowDialogAsync");
        method.Should().NotBeNull("ShowDialogAsync<TDialog> メソッドが定義されているべき");
        method!.IsGenericMethod.Should().BeTrue("ジェネリックメソッドであるべき");
        method.ReturnType.GetGenericTypeDefinition().Should().Be(typeof(Task<>),
            "Task<bool?> を返すべき");
    }

    /// <summary>
    /// INavigationServiceのモックが正常に作成できること
    /// </summary>
    [Fact]
    public void INavigationService_ShouldBeMockable()
    {
        // Arrange & Act
        var mock = new Mock<INavigationService>();

        // Assert - モックが作成できること
        mock.Object.Should().NotBeNull();
        mock.Object.Should().BeAssignableTo<IDialogService>();
        mock.Object.Should().BeAssignableTo<INavigationService>();
    }

    /// <summary>
    /// モックのShowDialogが期待通りの戻り値を返せること
    /// </summary>
    [Fact]
    public void MockedShowDialog_ShouldReturnConfiguredValue()
    {
        // Arrange
        var mock = new Mock<INavigationService>();
        mock.Setup(n => n.ShowDialog<Window>(It.IsAny<Action<Window>>()))
            .Returns(true);

        // Act
        var result = mock.Object.ShowDialog<Window>();

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// モックのShowDialogAsyncが期待通りの戻り値を返せること
    /// </summary>
    [Fact]
    public async Task MockedShowDialogAsync_ShouldReturnConfiguredValue()
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
    /// モックのShowDialogがnullを返せること（キャンセル等）
    /// </summary>
    [Fact]
    public void MockedShowDialog_ShouldReturnNull()
    {
        // Arrange
        var mock = new Mock<INavigationService>();
        mock.Setup(n => n.ShowDialog<Window>(It.IsAny<Action<Window>>()))
            .Returns((bool?)null);

        // Act
        var result = mock.Object.ShowDialog<Window>();

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// IDialogServiceとしてもモックメソッドが使えること（継承テスト）
    /// </summary>
    [Fact]
    public void MockedINavigationService_ShouldWorkAsIDialogService()
    {
        // Arrange
        var mock = new Mock<INavigationService>();
        mock.Setup(n => n.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        IDialogService dialogService = mock.Object;

        // Act
        var result = dialogService.ShowConfirmation("テスト", "確認");

        // Assert
        result.Should().BeTrue();
    }
}
