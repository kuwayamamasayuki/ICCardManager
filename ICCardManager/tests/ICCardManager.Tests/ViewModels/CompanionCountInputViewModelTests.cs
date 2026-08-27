using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// <see cref="CompanionCountInputViewModel"/> の単体テスト（Issue #1906）
/// </summary>
public class CompanionCountInputViewModelTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock = new();
    private readonly CompanionCountInputViewModel _vm;

    public CompanionCountInputViewModelTests()
    {
        _ledgerRepoMock
            .Setup(r => r.UpdateCompanionCountAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        _vm = new CompanionCountInputViewModel(_ledgerRepoMock.Object);
    }

    private static Ledger Usage(int id, string summary = "鉄道（博多～天神）", int expense = 260, string staffName = "博多 花子")
        => new Ledger { Id = id, Date = new DateTime(2026, 8, 20), Summary = summary, Expense = expense, StaffName = staffName };

    [Fact]
    public void Initialize_利用行だけを対象にすること()
    {
        var ledgers = new List<Ledger>
        {
            Usage(1),
            new Ledger { Id = 2, Summary = "役務費によりチャージ", Income = 3000, Expense = 0 },
            new Ledger { Id = 3, Summary = "ポイント還元", Income = 10, Expense = 0 },
            new Ledger { Id = 4, Summary = "（貸出中）", IsLentRecord = true, Expense = 0 },
            new Ledger { Id = 0, Summary = "鉄道（未保存）", Expense = 100 },
        };

        _vm.Initialize(ledgers);

        _vm.Items.Should().ContainSingle().Which.Ledger.Id.Should().Be(1);
        _vm.Items[0].CompanionCountText.Should().Be("0", "既定は同行者なし");
        _vm.Items[0].DisplayStaffNamePreview.Should().Be("博多 花子");
    }

    [Fact]
    public void Initialize_対象なし_案内を出すこと()
    {
        _vm.Initialize(new List<Ledger>());
        _vm.Items.Should().BeEmpty();
        _vm.StatusMessage.Should().Contain("ありません");
    }

    [Fact]
    public async Task SaveAsync_全行0_書き込まずに閉じること()
    {
        _vm.Initialize(new[] { Usage(1), Usage(2) });

        await _vm.SaveAsync();

        _vm.IsSaved.Should().BeTrue();
        _ledgerRepoMock.Verify(r => r.UpdateCompanionCountAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never,
            "返却時に 0 で INSERT 済みのため 0 の行は書き込まない");
    }

    [Fact]
    public async Task SaveAsync_入力した行だけ更新すること()
    {
        _vm.Initialize(new[] { Usage(1), Usage(2) });
        _vm.Items[1].CompanionCountText = "2";

        await _vm.SaveAsync();

        _vm.IsSaved.Should().BeTrue();
        _ledgerRepoMock.Verify(r => r.UpdateCompanionCountAsync(2, 2), Times.Once);
        _ledgerRepoMock.Verify(r => r.UpdateCompanionCountAsync(1, It.IsAny<int>()), Times.Never);
        _vm.Items[1].Ledger.CompanionCount.Should().Be(2, "in-memory の Ledger にも反映して履歴表示と揃える");
        _vm.Items[1].DisplayStaffNamePreview.Should().Be("博多 花子 外2名");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("100")]
    public async Task SaveAsync_不正な入力_保存せず3要素の案内を出すこと(string text)
    {
        _vm.Initialize(new[] { Usage(1) });
        _vm.Items[0].CompanionCountText = text;

        await _vm.SaveAsync();

        _vm.IsSaved.Should().BeFalse();
        _ledgerRepoMock.Verify(r => r.UpdateCompanionCountAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _vm.StatusMessage.Should().Contain(text).And.Contain("0～99").And.EndWith("入力してください。");
    }

    [Fact]
    public async Task SaveAsync_空欄は0として扱うこと()
    {
        _vm.Initialize(new[] { Usage(1) });
        _vm.Items[0].CompanionCountText = "";

        await _vm.SaveAsync();

        _vm.IsSaved.Should().BeTrue();
        _ledgerRepoMock.Verify(r => r.UpdateCompanionCountAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SaveAsync_影響行数0_競合として案内し閉じないこと()
    {
        _ledgerRepoMock.Setup(r => r.UpdateCompanionCountAsync(1, 1)).ReturnsAsync(false);
        _vm.Initialize(new[] { Usage(1) });
        _vm.Items[0].CompanionCountText = "1";

        await _vm.SaveAsync();

        _vm.IsSaved.Should().BeFalse();
        _vm.StatusMessage.Should().Contain("削除された可能性")
            .And.Contain("返却は記録済み")
            .And.Contain("行編集");
    }

    [Fact]
    public async Task SaveAsync_例外_再タッチを案内せず行編集での入力を案内すること()
    {
        _ledgerRepoMock.Setup(r => r.UpdateCompanionCountAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _vm.Initialize(new[] { Usage(1) });
        _vm.Items[0].CompanionCountText = "1";

        await _vm.SaveAsync();

        _vm.IsSaved.Should().BeFalse();
        _vm.IsBusy.Should().BeFalse();
        _vm.StatusMessage.Should().NotContain("database is locked", "生の ex.Message を UI へ出さない（#1614）")
            .And.Contain("返却は記録済み")
            .And.Contain("再タッチせず")
            .And.Contain("行編集");
    }

    [Fact]
    public void Skip_書き込まずに閉じること()
    {
        _vm.Initialize(new[] { Usage(1) });
        _vm.Items[0].CompanionCountText = "3";

        _vm.Skip();

        _vm.IsSaved.Should().BeTrue();
        _ledgerRepoMock.Verify(r => r.UpdateCompanionCountAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void SelectTargetLedgers_nullを空として扱うこと()
    {
        CompanionCountInputViewModel.SelectTargetLedgers(null).Should().BeEmpty();
    }
}
