using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// FolderWriteAccessProbe の単体テスト（Issue #1690）
/// </summary>
/// <remarks>
/// Directory.Exists だけでは「見えるが書けない」読み取り専用共有を検出できないため、
/// 実際に一時ファイルを作って書き込み可否を確かめる方式を採っている。
/// 本テストでは実フォルダに対して動作させ、
/// 「診断がゴミファイルを残さない」ことも併せて固定する。
/// </remarks>
public class FolderWriteAccessProbeTests : IDisposable
{
    private readonly string _tempFolder;

    public FolderWriteAccessProbeTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "iccard_probe_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempFolder))
                Directory.Delete(_tempFolder, recursive: true);
        }
        catch (IOException)
        {
            // テスト後始末の失敗はテスト結果に影響させない
        }
    }

    [Fact]
    public void Probe_WithWritableFolder_ReturnsWritable()
    {
        FolderWriteAccessProbe.Probe(_tempFolder).Should().Be(FolderWriteAccess.Writable);
    }

    [Fact]
    public void Probe_WithWritableFolder_LeavesNoProbeFileBehind()
    {
        FolderWriteAccessProbe.Probe(_tempFolder);

        // FileOptions.DeleteOnClose により OS が削除するため、痕跡が残ってはならない
        Directory.GetFiles(_tempFolder)
            .Select(Path.GetFileName)
            .Should().NotContain(name => name.StartsWith(FolderWriteAccessProbe.ProbeFileNamePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_CalledTwice_SucceedsBothTimes()
    {
        // 一時ファイル名が固定だと 2 回目が「既に存在する」で失敗する。
        // GUID による一意化が効いていることを固定する。
        FolderWriteAccessProbe.Probe(_tempFolder).Should().Be(FolderWriteAccess.Writable);
        FolderWriteAccessProbe.Probe(_tempFolder).Should().Be(FolderWriteAccess.Writable);
    }

    [Fact]
    public void Probe_WithNonExistentFolder_ReturnsFolderNotFound()
    {
        var missing = Path.Combine(_tempFolder, "not_created_" + Guid.NewGuid().ToString("N"));

        FolderWriteAccessProbe.Probe(missing).Should().Be(FolderWriteAccess.FolderNotFound);
    }

    [Fact]
    public void Probe_WithUncPathToNonExistentServer_ReturnsFolderNotFound()
    {
        // 共有モードでネットワークが切断された状況を模す。
        // DriveInfo と異なり例外にならず、判定値として返ることを確認する。
        var unc = @"\\iccard-nonexistent-host-" + Guid.NewGuid().ToString("N") + @"\share";

        FolderWriteAccessProbe.Probe(unc).Should().Be(FolderWriteAccess.FolderNotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Probe_WithEmptyPath_ReturnsPathNotSpecified(string path)
    {
        FolderWriteAccessProbe.Probe(path).Should().Be(FolderWriteAccess.PathNotSpecified);
    }

    [Fact]
    public void Probe_WithInvalidPathCharacters_ReturnsFailureWithoutThrowing()
    {
        // 設定ファイルを手編集して壊れたパスが入るケース。例外を外へ漏らさないことが要件
        var invalid = "C:\\invalid\0path";

        var act = () => FolderWriteAccessProbe.Probe(invalid);

        act.Should().NotThrow();
        FolderWriteAccessProbe.Probe(invalid).Should().NotBe(FolderWriteAccess.Writable);
    }
}
