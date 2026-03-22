using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// ServiceResult / ServiceResult&lt;T&gt; の単体テスト
/// </summary>
public class ServiceResultTests
{
    #region ServiceResult（型パラメータなし）

    [Fact]
    public void Ok_ShouldReturnSuccessResult()
    {
        var result = ServiceResult.Ok();

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Fail_ShouldReturnFailureResult()
    {
        var result = ServiceResult.Fail("エラーが発生しました");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("エラーが発生しました");
    }

    [Fact]
    public void Fail_WithNullMessage_ShouldReturnFailureWithNullMessage()
    {
        var result = ServiceResult.Fail(null);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Fail_WithEmptyMessage_ShouldReturnFailureWithEmptyMessage()
    {
        var result = ServiceResult.Fail("");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public void ImplicitBoolConversion_Success_ShouldBeTrue()
    {
        ServiceResult result = ServiceResult.Ok();

        bool boolValue = result;

        boolValue.Should().BeTrue();
    }

    [Fact]
    public void ImplicitBoolConversion_Failure_ShouldBeFalse()
    {
        ServiceResult result = ServiceResult.Fail("エラー");

        bool boolValue = result;

        boolValue.Should().BeFalse();
    }

    [Fact]
    public void ImplicitBoolConversion_Null_ShouldBeFalse()
    {
        ServiceResult result = null;

        bool boolValue = result;

        boolValue.Should().BeFalse();
    }

    [Fact]
    public void ImplicitBoolConversion_CanBeUsedInIfStatement()
    {
        var result = ServiceResult.Ok();
        var enteredSuccessBranch = false;

        if (result)
        {
            enteredSuccessBranch = true;
        }

        enteredSuccessBranch.Should().BeTrue("成功結果はif文でtrueとして評価されるべき");
    }

    #endregion

    #region ServiceResult<T>（ジェネリック版）

    [Fact]
    public void GenericOk_ShouldReturnSuccessWithData()
    {
        var result = ServiceResult<int>.Ok(42);

        result.Success.Should().BeTrue();
        result.Data.Should().Be(42);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void GenericFail_ShouldReturnFailureWithoutData()
    {
        var result = ServiceResult<int>.Fail("計算エラー");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("計算エラー");
        result.Data.Should().Be(default(int));
    }

    [Fact]
    public void GenericOk_WithComplexType_ShouldReturnData()
    {
        var data = new[] { "A", "B", "C" };
        var result = ServiceResult<string[]>.Ok(data);

        result.Success.Should().BeTrue();
        result.Data.Should().BeEquivalentTo(new[] { "A", "B", "C" });
    }

    [Fact]
    public void GenericOk_WithNullData_ShouldReturnSuccessWithNullData()
    {
        var result = ServiceResult<string>.Ok(null);

        result.Success.Should().BeTrue();
        result.Data.Should().BeNull();
    }

    [Fact]
    public void GenericResult_ShouldInheritImplicitBoolConversion()
    {
        ServiceResult<string> success = ServiceResult<string>.Ok("データ");
        ServiceResult<string> failure = ServiceResult<string>.Fail("エラー");

        ((bool)success).Should().BeTrue();
        ((bool)failure).Should().BeFalse();
    }

    #endregion
}
