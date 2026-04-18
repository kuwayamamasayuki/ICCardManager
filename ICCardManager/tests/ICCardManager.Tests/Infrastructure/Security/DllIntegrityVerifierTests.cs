using System;
using System.IO;
using System.Text;
using FluentAssertions;
using ICCardManager.Infrastructure.Security;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Security;

/// <summary>
/// Issue #1266: <see cref="DllIntegrityVerifier"/> の単体テスト。
/// </summary>
public class DllIntegrityVerifierTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly DllIntegrityVerifier _verifier;

    public DllIntegrityVerifierTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"DllIntegrityVerifierTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _verifier = new DllIntegrityVerifier();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { /* cleanup failure ignored */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 既知のバイト列 "hello" の SHA-256 ハッシュが計算できること。
    /// 期待値: https://emn178.github.io/online-tools/sha256.html で検証可能な値
    /// </summary>
    [Fact]
    public void ComputeSha256_KnownBytes_ReturnsExpectedHash()
    {
        // Arrange: "hello" の SHA-256 は既知の値
        var filePath = Path.Combine(_testDirectory, "hello.txt");
        File.WriteAllBytes(filePath, Encoding.ASCII.GetBytes("hello"));

        // Act
        var hash = DllIntegrityVerifier.ComputeSha256(filePath);

        // Assert
        hash.Should().Be("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
    }

    /// <summary>
    /// 空ファイルの SHA-256 は既知の値（e3b0c44...）になること。
    /// </summary>
    [Fact]
    public void ComputeSha256_EmptyFile_ReturnsWellKnownEmptyHash()
    {
        var filePath = Path.Combine(_testDirectory, "empty.bin");
        File.WriteAllBytes(filePath, Array.Empty<byte>());

        var hash = DllIntegrityVerifier.ComputeSha256(filePath);

        hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    /// <summary>
    /// ハッシュが期待値と一致する場合、Verified と報告されること。
    /// </summary>
    [Fact]
    public void Verify_MatchingHash_ReturnsVerified()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "match.bin");
        File.WriteAllBytes(filePath, Encoding.ASCII.GetBytes("integrity"));
        var expected = DllIntegrityVerifier.ComputeSha256(filePath);

        // Act
        var report = _verifier.Verify(filePath, expected);

        // Assert
        report.Result.Should().Be(VerificationResult.Verified);
        report.IsVerified.Should().BeTrue();
        report.ActualSha256.Should().Be(expected);
        report.ErrorMessage.Should().BeNull();
    }

    /// <summary>
    /// ハッシュが期待値と不一致の場合、HashMismatch と報告されること。
    /// </summary>
    [Fact]
    public void Verify_MismatchedHash_ReturnsHashMismatch()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "mismatch.bin");
        File.WriteAllBytes(filePath, Encoding.ASCII.GetBytes("original"));
        var wrongHash = new string('0', 64); // 64桁の 0 → 絶対に一致しない

        // Act
        var report = _verifier.Verify(filePath, wrongHash);

        // Assert
        report.Result.Should().Be(VerificationResult.HashMismatch);
        report.IsVerified.Should().BeFalse();
        report.ExpectedSha256.Should().Be(wrongHash);
        report.ActualSha256.Should().NotBeNullOrEmpty();
        report.ActualSha256.Should().NotBe(wrongHash);
        report.ErrorMessage.Should().Contain("一致しません");
    }

    /// <summary>
    /// ファイルが存在しない場合、FileNotFound と報告されること。
    /// </summary>
    [Fact]
    public void Verify_MissingFile_ReturnsFileNotFound()
    {
        var missingPath = Path.Combine(_testDirectory, "does_not_exist.bin");
        var expected = new string('a', 64);

        var report = _verifier.Verify(missingPath, expected);

        report.Result.Should().Be(VerificationResult.FileNotFound);
        report.IsVerified.Should().BeFalse();
        report.ActualSha256.Should().BeNull();
        report.ErrorMessage.Should().Contain("見つかりません");
    }

    /// <summary>
    /// ハッシュ比較は大文字小文字を区別しないこと。
    /// </summary>
    [Fact]
    public void Verify_UppercaseExpected_MatchesLowercaseComputed()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "case.bin");
        File.WriteAllBytes(filePath, Encoding.ASCII.GetBytes("case test"));
        var expected = DllIntegrityVerifier.ComputeSha256(filePath).ToUpperInvariant();

        // Act
        var report = _verifier.Verify(filePath, expected);

        // Assert
        report.IsVerified.Should().BeTrue();
    }

    /// <summary>
    /// 期待ハッシュ文字列の区切り文字（-, :, スペース）・前後空白が除去されて比較されること。
    /// </summary>
    [Theory]
    [InlineData("f4:9c:3a:f3")]      // ':' 区切り
    [InlineData("f4-9c-3a-f3")]      // '-' 区切り
    [InlineData("f4 9c 3a f3")]      // 空白区切り
    [InlineData("  f49c3af3  ")]     // 前後空白
    public void NormalizeHash_StripsDelimitersAndWhitespace(string input)
    {
        var normalized = DllIntegrityVerifier.NormalizeHash(input);
        normalized.Should().Be("f49c3af3");
    }

    /// <summary>
    /// 空またはnullパスでは ArgumentException が投げられること。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_EmptyFilePath_ThrowsArgumentException(string path)
    {
        var act = () => _verifier.Verify(path!, "abc");
        act.Should().Throw<ArgumentException>().WithMessage("*ファイルパス*");
    }

    /// <summary>
    /// 空またはnullの期待ハッシュでは ArgumentException が投げられること。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_EmptyExpectedHash_ThrowsArgumentException(string expected)
    {
        // 実ファイルの存在可否に関わらず、引数バリデーションが先行する
        var filePath = Path.Combine(_testDirectory, "any.bin");
        var act = () => _verifier.Verify(filePath, expected!);
        act.Should().Throw<ArgumentException>().WithMessage("*ハッシュ*");
    }

    /// <summary>
    /// BytesToHex は 16進小文字で 2桁ずつ連結すること。
    /// </summary>
    [Fact]
    public void BytesToHex_ProducesLowercaseHex()
    {
        var bytes = new byte[] { 0x00, 0x0F, 0x10, 0xA0, 0xFF };
        var hex = DllIntegrityVerifier.BytesToHex(bytes);
        hex.Should().Be("000f10a0ff");
    }

    /// <summary>
    /// FilePath プロパティは検証対象ファイルのパスを保持すること（エラーログ用）。
    /// </summary>
    [Fact]
    public void Verify_Report_ContainsFilePath()
    {
        var filePath = Path.Combine(_testDirectory, "logged.bin");
        File.WriteAllBytes(filePath, new byte[] { 1, 2, 3 });

        var report = _verifier.Verify(filePath, "wronghash");

        report.FilePath.Should().Be(filePath);
    }
}
