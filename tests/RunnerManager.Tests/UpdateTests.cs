namespace RunnerManager.Tests;

public class UpdateCheckTests
{
    private const string Release = """
        {
          "tag_name": "v2.4.0",
          "html_url": "https://github.com/Sandiozo/runner-manager/releases/tag/v2.4.0",
          "assets": [
            { "name": "RunnerManagerSetup.exe", "browser_download_url": "https://github.com/Sandiozo/runner-manager/releases/download/v2.4.0/RunnerManagerSetup.exe", "size": 10 },
            { "name": "RunnerManager.exe", "browser_download_url": "https://github.com/Sandiozo/runner-manager/releases/download/v2.4.0/RunnerManager.exe",
              "size": 1234, "digest": "sha256:ABCDEF" }
          ]
        }
        """;

    [Fact]
    public void ParseRelease_ReadsTheExeAsset()
    {
        var check = AppUpdater.ParseRelease(Release);
        Assert.NotNull(check.Release);
        Assert.Equal("v2.4.0", check.Release!.Tag);
        Assert.Equal("2.4.0", check.Release.Version);
        Assert.Equal("ABCDEF", check.Release.Sha256);
        Assert.Equal(1234, check.Release.Size);
        Assert.EndsWith("/v2.4.0/RunnerManager.exe", check.Release.DownloadUrl);
    }

    [Fact]
    public void ParseRelease_WithoutTheExe_IsAFailureWithAReason()
    {
        var check = AppUpdater.ParseRelease("""{ "tag_name": "v2.4.0", "assets": [] }""");
        Assert.Null(check.Release);
        Assert.Contains("v2.4.0", check.Problem);
        Assert.False(check.UpdateAvailable);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "message": "Not Found" }""")]
    [InlineData("""{ "tag_name": "v2.4.0", "assets": 5 }""")]
    public void ParseRelease_UnreadableAnswer_IsAFailure(string json)
    {
        var check = AppUpdater.ParseRelease(json);
        Assert.Null(check.Release);
        Assert.False(string.IsNullOrEmpty(check.Problem));
    }

    [Fact]
    public void DescribeFailure_RateLimit_SaysWhenToTryAgain()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddMinutes(20).ToUnixTimeSeconds().ToString();
        var text = AppUpdater.DescribeFailure(403, "0", reset, null, now);
        Assert.Contains("limits", text);
        Assert.Contains(now.AddMinutes(20).ToLocalTime().ToString("HH:mm"), text);
        Assert.Contains("limits", AppUpdater.DescribeFailure(429, null, null, TimeSpan.FromSeconds(60), now));
    }

    [Theory]
    [InlineData(404, "No Runner Manager release")]
    [InlineData(500, "HTTP 500")]
    [InlineData(403, "HTTP 403")]
    public void DescribeFailure_OtherErrors(int status, string expected)
        => Assert.Contains(expected, AppUpdater.DescribeFailure(status, "42", null, null, DateTimeOffset.UtcNow));

    [Fact]
    public void DownloadUrl_OnlyUsesThisRepositorysDownloads()
    {
        var release = new ReleaseInfo { Tag = "v2.4.0", DownloadUrl = "https://example.com/RunnerManager.exe" };
        Assert.Equal("https://github.com/Sandiozo/runner-manager/releases/download/v2.4.0/RunnerManager.exe", AppUpdater.DownloadUrlFor(release));
        var own = new ReleaseInfo { Tag = "v2.4.0", DownloadUrl = "https://github.com/Sandiozo/runner-manager/releases/download/v2.4.0/RunnerManager.exe" };
        Assert.Equal(own.DownloadUrl, AppUpdater.DownloadUrlFor(own));
    }
}
