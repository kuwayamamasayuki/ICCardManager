using System;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1691: 帳票出力状況の表示要素を決める <see cref="ReportExportStatusPresenter"/> の単体テスト。
/// </summary>
public class ReportExportStatusPresenterTests
{
    /// <summary>
    /// CLAUDE.md「色・アイコン・テキストで状態を伝達（色のみに依存しない）」原則の担保。
    /// すべての状態でアイコン・テキスト・読み上げ文が空にならないこと。
    /// </summary>
    [Theory]
    [InlineData(ReportExportState.Exported)]
    [InlineData(ReportExportState.NotExported)]
    [InlineData(ReportExportState.Unknown)]
    public void Resolve_ShouldProvideIconTextAndAccessibilityForEveryState(ReportExportState state)
    {
        // Act
        var presentation = ReportExportStatusPresenter.Resolve(state);

        // Assert
        presentation.State.Should().Be(state);
        presentation.Icon.Should().NotBeNullOrWhiteSpace();
        presentation.ShortText.Should().NotBeNullOrWhiteSpace();
        presentation.AccessibilityText.Should().NotBeNullOrWhiteSpace();
        presentation.BrushKey.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// 状態ごとにアイコンとテキストが区別できること
    /// （同じアイコンだとアイコンが情報を担っていないことになる）
    /// </summary>
    [Fact]
    public void Resolve_ShouldUseDistinctIconAndTextPerState()
    {
        var states = Enum.GetValues(typeof(ReportExportState))
            .Cast<ReportExportState>()
            .Select(s => ReportExportStatusPresenter.Resolve(s))
            .ToList();

        states.Select(p => p.Icon).Should().OnlyHaveUniqueItems();
        states.Select(p => p.ShortText).Should().OnlyHaveUniqueItems();
        states.Select(p => p.BrushKey).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// 色はリソースキー名で返し、色値リテラルを返さないこと（Issue #1392 / #1461）
    /// </summary>
    [Theory]
    [InlineData(ReportExportState.Exported)]
    [InlineData(ReportExportState.NotExported)]
    [InlineData(ReportExportState.Unknown)]
    public void Resolve_ShouldReturnResourceKeyNotColorLiteral(ReportExportState state)
    {
        var presentation = ReportExportStatusPresenter.Resolve(state);

        presentation.BrushKey.Should().NotStartWith("#");
        presentation.BrushKey.Should().EndWith("Brush");
    }

    [Fact]
    public void Resolve_WithLastWriteTime_ShouldIncludeTimestamp()
    {
        // Arrange
        var lastWrite = new DateTime(2026, 7, 28, 14, 2, 0);

        // Act
        var presentation = ReportExportStatusPresenter.Resolve(ReportExportState.Exported, lastWrite);

        // Assert
        presentation.ShortText.Should().Contain("出力済み");
        presentation.ShortText.Should().Contain("2026/07/28 14:02");
        presentation.AccessibilityText.Should().Contain("2026/07/28 14:02");
    }

    [Fact]
    public void Resolve_ExportedWithoutTimestamp_ShouldStillReadNaturally()
    {
        // Act
        var presentation = ReportExportStatusPresenter.Resolve(ReportExportState.Exported, null);

        // Assert
        presentation.ShortText.Should().Be("出力済み");
        presentation.ShortText.Should().NotContain("（）");
    }

    /// <summary>
    /// 判定不能の説明は「なぜ」と「どうすれば」を含むこと（.claude/rules/error-messages.md）
    /// </summary>
    [Fact]
    public void Resolve_Unknown_ShouldExplainCauseAndAction()
    {
        // Act
        var presentation = ReportExportStatusPresenter.Resolve(ReportExportState.Unknown);

        // Assert
        presentation.AccessibilityText.Length.Should().BeGreaterOrEqualTo(20);
        presentation.AccessibilityText.Should().Contain("出力先フォルダ");
        presentation.AccessibilityText.Should().EndWith("してください");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FormatWarningMarker_WithoutWarnings_ShouldBeEmpty(int warningCount)
    {
        ReportExportStatusPresenter.FormatWarningMarker(warningCount).Should().BeEmpty();
        ReportExportStatusPresenter.FormatWarningAccessibilityText(warningCount).Should().BeEmpty();
    }

    [Fact]
    public void FormatWarningMarker_WithWarnings_ShouldShowIconAndCount()
    {
        // Act
        var marker = ReportExportStatusPresenter.FormatWarningMarker(3);

        // Assert
        marker.Should().Contain(ReportExportStatusPresenter.WarningIcon);
        marker.Should().Contain("警告3件");
    }

    /// <summary>
    /// 警告の読み上げ文は件数と次の操作を示すこと
    /// </summary>
    [Fact]
    public void FormatWarningAccessibilityText_ShouldGuideToPreflightDialog()
    {
        // Act
        var text = ReportExportStatusPresenter.FormatWarningAccessibilityText(2);

        // Assert
        text.Should().Contain("2件");
        text.Should().Contain("事前チェック");
        text.Should().EndWith("してください");
    }
}
