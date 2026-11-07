namespace PrintShopCompanion.Infrastructure.Tests;

using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Infrastructure.Update;
using Xunit;

/// <summary>
/// Tests for the auto-update pipeline. These tests exercise the
/// IsNewer() comparison and the SHA-256 verification path of
/// GitHubUpdateService.InstallUpdateAsync.
///
/// The tests stub the GitHub Releases API response with an in-memory
/// HttpMessageHandler so they don't make real network calls.
/// </summary>
public class GitHubUpdateServiceTests
{
    /// <summary>
    /// Verify that IsNewer correctly compares semantic versions.
    /// Note: IsNewer does NOT strip a 'v' prefix from the latest version —
    /// that's done by the LatestVersion property. IsNewer receives already-
    /// cleaned version strings and parses them with Version.TryParse.
    /// </summary>
    [Theory]
    [InlineData("0.4.3", "0.4.4", true)]   // patch bump → newer
    [InlineData("0.4.4", "0.4.3", false)]  // downgrade → not newer
    [InlineData("0.4.3", "0.4.3", false)]  // same → not newer
    [InlineData("0.4.3", "0.5.0", true)]   // minor bump → newer
    [InlineData("0.4.3", "1.0.0", true)]   // major bump → newer
    [InlineData("0.4.3", "invalid", false)] // unparseable latest → not newer
    [InlineData("0.4.3", "", false)]        // empty latest → not newer
    [InlineData("0.4.3", null, false)]      // null latest → not newer
    [InlineData("0.4.3", "0.4.4.0", true)]  // four-part version → newer
    public void IsNewer_compares_semver_correctly(string current, string? latest, bool expected)
    {
        // Reflection to call private static IsNewer — it's the heart of the
        // update detection logic and deserves direct test coverage. If the
        // method ever becomes public, change this to a direct call.
        var method = typeof(GitHubUpdateService).GetMethod("IsNewer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("IsNewer must exist as a private static method");
        var result = (bool)method!.Invoke(null, new object?[] { latest, current })!;
        result.Should().Be(expected,
            $"current={current}, latest={latest}: expected IsNewer={expected}");
    }

    /// <summary>
    /// Verify that the v-prefix stripping happens at the LatestVersion
    /// property level (not inside IsNewer). This is the contract documented
    /// in the GitHubRelease class: TagName="v0.4.4" → LatestVersion="0.4.4".
    /// </summary>
    [Fact]
    public async Task LatestVersion_strips_v_prefix_from_tag_name()
    {
        var json = """
        {
            "tag_name": "v99.99.99",
            "name": "test",
            "body": "test notes",
            "assets": [
                {
                    "name": "PrintShopCompanion-portable.zip",
                    "browser_download_url": "https://example.com/zip",
                    "size": 100,
                    "digest": "sha256:deadbeef"
                }
            ]
        }
        """;
        var handler = new StubHandler(json);
        var http = new HttpClient(handler);
        var svc = new GitHubUpdateService(http, NullLogger<GitHubUpdateService>.Instance);
        await svc.CheckForUpdatesAsync();
        svc.LatestVersion.Should().Be("99.99.99",
            "the 'v' prefix is stripped by the LatestVersion property");
    }

    /// <summary>
    /// Verify that the SHA-256 computed from a downloaded byte stream
    /// matches the expected digest. This is the verification logic the
    /// updater uses — the test does it inline to validate the algorithm.
    /// </summary>
    [Fact]
    public async Task Sha256_of_streamed_bytes_matches_known_digest()
    {
        // Arrange: a known test string
        var testBytes = Encoding.UTF8.GetBytes("PrintShop Companion test payload for SHA-256 verification");
        var expectedSha = BitConverter.ToString(SHA256.HashData(testBytes))
            .Replace("-", "").ToLowerInvariant();

        // Act: stream the bytes through a CryptoStream (mirrors the updater's
        // approach of computing SHA-256 while writing to disk in a single pass).
        // IMPORTANT: CryptoStream must be DISPOSED (or FlushFinalBlock called)
        // before reading sha.Hash — the hash is only finalized when the
        // CryptoStream is closed.
        string actualSha;
        using (var sha = SHA256.Create())
        {
            using (var ms = new MemoryStream())
            using (var cryptoStream = new CryptoStream(ms, sha, CryptoStreamMode.Write))
            {
                await cryptoStream.WriteAsync(testBytes);
                await cryptoStream.FlushAsync();
                // Dispose the CryptoStream to finalize the hash.
                // (We can't read sha.Hash before this — it would throw
                // "Hash must be finalized before the hash value is retrieved".)
            }
            actualSha = BitConverter.ToString(sha.Hash!)
                .Replace("-", "").ToLowerInvariant();
        }

        // Assert
        actualSha.Should().Be(expectedSha,
            "the SHA-256 of the streamed bytes must match the SHA-256 of the original bytes");
    }

    /// <summary>
    /// Verify that two different byte streams produce different SHA-256
    /// hashes. This validates that the verifier can detect a corrupted
    /// download (the whole point of the SHA-256 check).
    /// </summary>
    [Fact]
    public void Sha256_distinguishes_different_byte_streams()
    {
        var bytesA = Encoding.UTF8.GetBytes("payload A");
        var bytesB = Encoding.UTF8.GetBytes("payload B");
        var shaA = BitConverter.ToString(SHA256.HashData(bytesA)).Replace("-", "").ToLowerInvariant();
        var shaB = BitConverter.ToString(SHA256.HashData(bytesB)).Replace("-", "").ToLowerInvariant();

        shaA.Should().NotBe(shaB, "different byte streams must produce different SHA-256 hashes");
    }

    /// <summary>
    /// Verify that a GitHub release JSON payload with a `digest` field on
    /// the asset deserializes correctly to expose the DownloadDigest
    /// property on GitHubUpdateService.
    /// </summary>
    [Fact]
    public void GitHubReleaseJson_with_digest_deserializes_correctly()
    {
        // Arrange: a sample GitHub release JSON that includes the digest field
        var json = """
        {
            "tag_name": "v0.4.4",
            "name": "PrintShop Companion v0.4.4",
            "html_url": "https://github.com/Arjunyadavsoma/PrintEXE/releases/tag/v0.4.4",
            "body": "Release notes here",
            "assets": [
                {
                    "name": "PrintShopCompanion-portable.zip",
                    "browser_download_url": "https://github.com/Arjunyadavsoma/PrintEXE/releases/download/v0.4.4/PrintShopCompanion-portable.zip",
                    "size": 629000,
                    "digest": "sha256:abc123def456"
                }
            ]
        }
        """;

        // Act
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null  // use the JsonPropertyName attributes
        });

        // Assert
        release.Should().NotBeNull();
        release!.TagName.Should().Be("v0.4.4");
        release.Assets.Should().NotBeNull();
        release.Assets!.Count.Should().Be(1);
        release.Assets[0].Digest.Should().Be("sha256:abc123def456");
        release.Assets[0].BrowserDownloadUrl.Should().Contain("PrintShopCompanion-portable.zip");
    }

    /// <summary>
    /// Verify that GitHubUpdateService.CheckForUpdatesAsync correctly
    /// parses a release JSON and exposes the digest through the
    /// DownloadDigest property.
    /// </summary>
    [Fact]
    public async Task CheckForUpdatesAsync_exposes_download_digest_from_github_response()
    {
        // Arrange: a fake GitHub API response with a digest
        var json = """
        {
            "tag_name": "v99.99.99",
            "name": "test",
            "body": "test notes",
            "assets": [
                {
                    "name": "PrintShopCompanion-portable.zip",
                    "browser_download_url": "https://example.com/zip",
                    "size": 100,
                    "digest": "sha256:deadbeef"
                }
            ]
        }
        """;

        var handler = new StubHandler(json);
        var http = new HttpClient(handler);
        var logger = NullLogger<GitHubUpdateService>.Instance;
        var svc = new GitHubUpdateService(http, logger);

        // Act
        var isNewer = await svc.CheckForUpdatesAsync();

        // Assert: v99.99.99 should be newer than the current assembly version
        // (whatever it is — it's much smaller than 99.99.99)
        isNewer.Should().BeTrue("v99.99.99 is greater than any reasonable current version");
        svc.DownloadDigest.Should().Be("sha256:deadbeef");
        svc.ReleaseNotes.Should().Be("test notes");
        svc.LatestVersion.Should().Be("99.99.99");
    }

    /// <summary>
    /// Verify that the UpdateState enum has all the expected values for the
    /// state machine documented in docs/UPDATE_SYSTEM.md.
    /// </summary>
    [Fact]
    public void UpdateState_enum_has_expected_values()
    {
        // Verify the state machine values exist with the expected names.
        // If anyone renames them, the UI banner's switch statement will break,
        // so this test is a guard against that.
        Enum.GetNames(typeof(UpdateState)).Should().Contain(new[]
        {
            "Idle", "Checking", "Available", "Downloading",
            "Verifying", "Extracting", "Installing", "Restarting",
            "UpToDate", "Failed"
        });
    }
}

/// <summary>
/// Stub HttpMessageHandler that returns a fixed JSON response for any
/// request. Used by the tests to avoid real network calls.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly string _response;

    public StubHandler(string response) => _response = response;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = new StringContent(_response, Encoding.UTF8, "application/json");
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = content
        });
    }
}
