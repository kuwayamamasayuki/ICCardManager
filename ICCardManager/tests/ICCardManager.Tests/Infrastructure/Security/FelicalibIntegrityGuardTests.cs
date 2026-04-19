using System;
using System.IO;
using System.Text;
using FluentAssertions;
using ICCardManager.Infrastructure.Security;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Security;

/// <summary>
/// Issue #1266: <see cref="FelicalibIntegrityGuard"/> の単体テスト。
/// </summary>
/// <remarks>
/// Guard は felicalib.dll の期待 SHA-256 をコード定数として埋め込む。
/// 本テストは任意ディレクトリ下の DLL 検証ロジックを
/// テスト用ハッシュで上書きして検証する。
/// </remarks>
public class FelicalibIntegrityGuardTests : IDisposable
{
    private readonly string _testDirectory;

    public FelicalibIntegrityGuardTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FelicalibIntegrityGuardTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 既定のインスタンスは <see cref="FelicalibIntegrityGuard.ExpectedSha256"/> を期待値として使用すること。
    /// </summary>
    [Fact]
    public void DefaultConstructor_UsesEmbeddedSha256Constant()
    {
        // Arrange: 期待値とは異なる内容の DLL を偽装配置
        var dllPath = Path.Combine(_testDirectory, FelicalibIntegrityGuard.FelicalibDllName);
        File.WriteAllBytes(dllPath, Encoding.ASCII.GetBytes("fake content"));

        var guard = new FelicalibIntegrityGuard();

        // Act
        var report = guard.VerifyAt(_testDirectory);

        // Assert: ExpectedSha256 定数と比較されている
        report.ExpectedSha256.Should().Be(FelicalibIntegrityGuard.ExpectedSha256);
        // 偽装内容なので Mismatch になる
        report.Result.Should().Be(VerificationResult.HashMismatch);
    }

    /// <summary>
    /// ExpectedSha256 定数は 64文字（SHA-256 16進）であること。
    /// </summary>
    [Fact]
    public void ExpectedSha256_IsValidSha256HexString()
    {
        FelicalibIntegrityGuard.ExpectedSha256.Should().HaveLength(64);
        FelicalibIntegrityGuard.ExpectedSha256.Should().MatchRegex(
            "^[0-9a-f]{64}$",
            "SHA-256 ハッシュは 16進小文字 64文字である必要がある");
    }

    /// <summary>
    /// FelicalibDllName はファイル名 "felicalib.dll" であること。
    /// </summary>
    [Fact]
    public void FelicalibDllName_IsExpectedFileName()
    {
        FelicalibIntegrityGuard.FelicalibDllName.Should().Be("felicalib.dll");
    }

    /// <summary>
    /// 正規 DLL（期待ハッシュと一致する内容）を配置した場合、Verified と報告されること。
    /// </summary>
    [Fact]
    public void VerifyAt_GenuineDll_ReturnsVerified()
    {
        // Arrange: 任意の内容を作り、そのハッシュを期待値としてテスト用 Guard を作成
        var dllPath = Path.Combine(_testDirectory, FelicalibIntegrityGuard.FelicalibDllName);
        File.WriteAllBytes(dllPath, Encoding.ASCII.GetBytes("genuine felicalib"));
        var genuineHash = DllIntegrityVerifier.ComputeSha256(dllPath);

        var guard = new FelicalibIntegrityGuard(new DllIntegrityVerifier(), genuineHash);

        // Act
        var report = guard.VerifyAt(_testDirectory);

        // Assert
        report.IsVerified.Should().BeTrue();
        report.Result.Should().Be(VerificationResult.Verified);
        report.ActualSha256.Should().Be(genuineHash);
    }

    /// <summary>
    /// 偽造 DLL（異なる内容）を配置した場合、HashMismatch と報告されること。
    /// </summary>
    [Fact]
    public void VerifyAt_TamperedDll_ReturnsHashMismatch()
    {
        // Arrange: 期待ハッシュを固定し、実ファイルには別内容を置く
        var dllPath = Path.Combine(_testDirectory, FelicalibIntegrityGuard.FelicalibDllName);
        File.WriteAllBytes(dllPath, Encoding.ASCII.GetBytes("malicious payload"));
        var expectedHash = DllIntegrityVerifier.ComputeSha256(
            WriteTempFile("genuine content"));

        var guard = new FelicalibIntegrityGuard(new DllIntegrityVerifier(), expectedHash);

        // Act
        var report = guard.VerifyAt(_testDirectory);

        // Assert
        report.Result.Should().Be(VerificationResult.HashMismatch);
        report.IsVerified.Should().BeFalse();
        report.ActualSha256.Should().NotBe(expectedHash);
    }

    /// <summary>
    /// DLL 未配置の場合、FileNotFound と報告されること。
    /// </summary>
    [Fact]
    public void VerifyAt_NoDll_ReturnsFileNotFound()
    {
        var guard = new FelicalibIntegrityGuard(new DllIntegrityVerifier(), new string('a', 64));

        var report = guard.VerifyAt(_testDirectory);

        report.Result.Should().Be(VerificationResult.FileNotFound);
        report.FilePath.Should().EndWith(FelicalibIntegrityGuard.FelicalibDllName);
    }

    /// <summary>
    /// コンストラクタで null verifier を渡すと ArgumentNullException が投げられること。
    /// </summary>
    [Fact]
    public void Constructor_NullVerifier_ThrowsArgumentNullException()
    {
        var act = () => new FelicalibIntegrityGuard(null!, "abcd");
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// コンストラクタで空の期待ハッシュを渡すと ArgumentException が投げられること。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_EmptyExpectedHash_ThrowsArgumentException(string expected)
    {
        var act = () => new FelicalibIntegrityGuard(new DllIntegrityVerifier(), expected!);
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// 空のディレクトリパスでは ArgumentException が投げられること。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VerifyAt_EmptyDirectory_ThrowsArgumentException(string directory)
    {
        var guard = new FelicalibIntegrityGuard(new DllIntegrityVerifier(), new string('a', 64));
        var act = () => guard.VerifyAt(directory!);
        act.Should().Throw<ArgumentException>();
    }

    private string WriteTempFile(string content)
    {
        var path = Path.Combine(_testDirectory, $"ref_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return path;
    }
}
