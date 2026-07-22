using System;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1687: <see cref="AppVersionInfo"/> のバージョン取得・正規化パースを検証する。
/// AssemblyVersion（4要素）と latest_version.txt（3要素想定）の比較で
/// "2.10.0.0" &gt; "2.10.0" と誤判定されないことが正規化の目的。
/// </summary>
public class AppVersionInfoTests
{
    [Fact]
    public void Current_3要素に正規化されていること()
    {
        var version = AppVersionInfo.Current;

        version.Revision.Should().Be(-1, "Revision を落とした3要素で返す（4要素比較の罠を防ぐ）");
        version.Build.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void CurrentString_MajorMinorBuild形式であること()
    {
        AppVersionInfo.CurrentString.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }

    [Theory]
    [InlineData("2.11.0", 2, 11, 0)]
    [InlineData("v2.11.0", 2, 11, 0)]
    [InlineData("V2.11.0", 2, 11, 0)]
    [InlineData("  2.11.0  ", 2, 11, 0)]
    [InlineData("2.11", 2, 11, 0)]        // 2要素はBuild=0とみなす
    [InlineData("2.11.0.7", 2, 11, 0)]    // 4要素はRevisionを無視して3要素に正規化
    public void TryParseNormalized_有効なバージョン文字列をパースできること(
        string input, int major, int minor, int build)
    {
        var success = AppVersionInfo.TryParseNormalized(input, out var version);

        success.Should().BeTrue();
        version.Should().Be(new Version(major, minor, build));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("2")]           // 1要素はVersionとして不正
    [InlineData("2.x.0")]
    public void TryParseNormalized_不正な文字列はfalseを返すこと(string input)
    {
        var success = AppVersionInfo.TryParseNormalized(input, out var version);

        success.Should().BeFalse();
        version.Should().BeNull();
    }
}
