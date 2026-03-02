using System.IO;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// AppFixture のパス解決ロジックのユニットテスト。
    /// GUI 不要のため Category=UI を付けず、CI でも実行される。
    /// </summary>
    public class AppFixturePathResolutionTests
    {
        [Theory]
        [InlineData("Debug")]
        [InlineData("Release")]
        public void ResolveExePathCandidates_テストDLLの構成と同じ構成が優先される(string config)
        {
            // Arrange: テスト DLL のパスを模擬
            var testAssemblyDir = Path.Combine(
                "C:", "repo", "tests", "ICCardManager.UITests", "bin", config, "net48");

            // Act
            var (primaryPath, _) = AppFixture.ResolveExePathCandidates(testAssemblyDir);

            // Assert: primary パスにテスト構成と同じ構成名が含まれること
            primaryPath.Should().Contain(
                Path.Combine("bin", config, "net48", "ICCardManager.exe"));
        }

        [Fact]
        public void ResolveExePathCandidates_Debug構成のフォールバックはRelease()
        {
            var testAssemblyDir = Path.Combine(
                "C:", "repo", "tests", "ICCardManager.UITests", "bin", "Debug", "net48");

            var (_, fallbackPath) = AppFixture.ResolveExePathCandidates(testAssemblyDir);

            fallbackPath.Should().Contain(
                Path.Combine("bin", "Release", "net48", "ICCardManager.exe"));
        }

        [Fact]
        public void ResolveExePathCandidates_Release構成のフォールバックはDebug()
        {
            var testAssemblyDir = Path.Combine(
                "C:", "repo", "tests", "ICCardManager.UITests", "bin", "Release", "net48");

            var (_, fallbackPath) = AppFixture.ResolveExePathCandidates(testAssemblyDir);

            fallbackPath.Should().Contain(
                Path.Combine("bin", "Debug", "net48", "ICCardManager.exe"));
        }

        [Theory]
        [InlineData("Debug")]
        [InlineData("Release")]
        public void ResolveExePathCandidates_プロジェクトルートからのsrc相対パスが正しい(string config)
        {
            var testAssemblyDir = Path.Combine(
                "C:", "repo", "tests", "ICCardManager.UITests", "bin", config, "net48");

            var (primaryPath, _) = AppFixture.ResolveExePathCandidates(testAssemblyDir);

            // プロジェクトルート/src/ICCardManager/bin/{config}/net48/ICCardManager.exe
            primaryPath.Should().Contain(Path.Combine("src", "ICCardManager", "bin"));
            primaryPath.Should().EndWith("ICCardManager.exe");
        }
    }
}
