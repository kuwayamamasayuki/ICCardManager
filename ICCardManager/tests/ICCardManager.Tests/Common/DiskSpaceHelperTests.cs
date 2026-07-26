using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// DiskSpaceHelper の単体テスト（Issue #1689）
/// </summary>
/// <remarks>
/// 共有フォルダモードではバックアップ保存先が UNC パスになり得るため、
/// DriveInfo ではなく Win32 の GetDiskFreeSpaceEx を P/Invoke している。
/// ここでは「取得できるケース」「取得できず null になるケース」「単位整形」を固定する。
/// </remarks>
public class DiskSpaceHelperTests
{
    #region TryGetAvailableFreeSpace

    [Fact]
    public void TryGetAvailableFreeSpace_WithExistingFolder_ReturnsPositiveValue()
    {
        // 一時フォルダは必ず存在し、通常は空き容量が 0 より大きい
        var result = DiskSpaceHelper.TryGetAvailableFreeSpace(Path.GetTempPath());

        result.Should().NotBeNull("存在するフォルダでは空き容量を取得できるべき");
        result.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TryGetAvailableFreeSpace_WithTrailingSeparator_ReturnsSameValueAsWithout()
    {
        var withSeparator = Path.GetTempPath();                            // 末尾に "\" が付く
        var withoutSeparator = withSeparator.TrimEnd(Path.DirectorySeparatorChar);

        var a = DiskSpaceHelper.TryGetAvailableFreeSpace(withSeparator);
        var b = DiskSpaceHelper.TryGetAvailableFreeSpace(withoutSeparator);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
        // 同一ボリュームなので同程度の値になる（他プロセスの書き込みで厳密一致しない可能性があるため許容幅を持たせる）
        Math.Abs(b!.Value - a!.Value).Should().BeLessThan(100L * 1024 * 1024);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetAvailableFreeSpace_WithEmptyPath_ReturnsNull(string path)
    {
        DiskSpaceHelper.TryGetAvailableFreeSpace(path).Should().BeNull();
    }

    [Fact]
    public void TryGetAvailableFreeSpace_WithNonExistentFolder_ReturnsNull()
    {
        // GetDiskFreeSpaceEx は存在しないディレクトリに対して失敗する。
        // 呼び出し側が「不明」として扱えるよう、例外ではなく null を返すことを固定する。
        var nonExistent = Path.Combine(Path.GetTempPath(), $"NotExists_{Guid.NewGuid():N}");

        DiskSpaceHelper.TryGetAvailableFreeSpace(nonExistent).Should().BeNull();
    }

    #endregion

    #region FormatBytes

    [Fact]
    public void FormatBytes_WithNull_ReturnsUnknownLabel()
    {
        // 空き容量が取れなかった場合、画面には「不明」と出す（0 と誤読させない）
        DiskSpaceHelper.FormatBytes(null).Should().Be("不明");
    }

    [Theory]
    [InlineData(0L, "0 バイト")]
    [InlineData(1023L, "1,023 バイト")]
    [InlineData(1024L * 1024, "1.0 MB")]
    [InlineData(1024L * 1024 * 512, "512.0 MB")]
    [InlineData(1024L * 1024 * 1024, "1.0 GB")]
    [InlineData(1024L * 1024 * 1024 * 12 + 1024L * 1024 * 300, "12.3 GB")]
    public void FormatBytes_FormatsWithAppropriateUnit(long bytes, string expected)
    {
        DiskSpaceHelper.FormatBytes(bytes).Should().Be(expected);
    }

    #endregion
}
