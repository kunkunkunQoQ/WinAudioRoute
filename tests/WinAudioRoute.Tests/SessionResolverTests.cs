using WinAudioRoute.Internal;

namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="SessionResolver"/> 的纯逻辑测试（不触碰 COM）。
/// </summary>
public class SessionResolverTests
{
    private static AudioSession Session(
        int pid,
        string? processName,
        string instance = "inst",
        AudioDataFlow flow = AudioDataFlow.Render) => new()
    {
        ProcessId = pid,
        ProcessName = processName,
        DisplayName = null,
        SessionIdentifier = "sid",
        SessionInstanceIdentifier = instance,
        State = AudioSessionState.Active,
        Flow = flow,
        DeviceId = "{0.0.0.0}.{dev}",
        Volume = 0.5f,
        IsMuted = false,
    };

    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("CHROME", "CHROME")]           // 保留大小写：规范化不做大小写转换
    [InlineData("CHROME.EXE", "CHROME")]
    [InlineData("Chrome.Exe", "Chrome")]
    [InlineData("  chrome.exe  ", "chrome")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeProcessName_StripsExeAndTrims(string? input, string expected)
    {
        Assert.Equal(expected, SessionResolver.NormalizeProcessName(input));
    }

    [Theory]
    [InlineData("chrome", "chrome", true)]
    [InlineData("chrome", "chrome.exe", true)]
    [InlineData("chrome.exe", "chrome", true)]
    [InlineData("CHROME.EXE", "chrome", true)]
    [InlineData("chrome", "firefox", false)]
    [InlineData(null, "chrome", false)]
    [InlineData("chrome", "", false)]
    [InlineData("chromium", "chrome", false)]
    public void ProcessNameMatches_NormalizesBothSides(string? candidate, string target, bool expected)
    {
        Assert.Equal(expected, SessionResolver.ProcessNameMatches(candidate, target));
    }

    [Fact]
    public void ResolveByProcessId_FindsAllSessionsOfThatProcess()
    {
        var sessions = new List<AudioSession>
        {
            Session(100, "chrome", "i1"),
            Session(100, "chrome", "i2"),
            Session(200, "firefox", "i3"),
        };

        SessionResolutionResult result = SessionResolver.ResolveByProcessId(sessions, 100);

        Assert.True(result.IsUnique);
        Assert.Equal([100], result.ProcessIds);
        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal("100", result.Query);
    }

    [Fact]
    public void ResolveByProcessId_NoSessions_ReturnsNotFound()
    {
        SessionResolutionResult result = SessionResolver.ResolveByProcessId([Session(100, "chrome")], 999);

        Assert.True(result.IsNotFound);
        Assert.Equal("999", result.Query);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolveByProcessId_NonPositivePid_Throws(int pid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SessionResolver.ResolveByProcessId([Session(100, "chrome")], pid));
    }

    [Fact]
    public void ResolveByProcessName_MatchesIgnoringCaseAndExeExtension()
    {
        var sessions = new List<AudioSession> { Session(100, "chrome", "i1") };

        foreach (string query in (string[])["chrome", "chrome.exe", "CHROME.EXE", "Chrome"])
        {
            SessionResolutionResult result = SessionResolver.ResolveByProcessName(sessions, query);

            Assert.True(result.IsUnique, $"query '{query}' 应唯一命中");
            Assert.Equal([100], result.ProcessIds);
        }
    }

    [Fact]
    public void ResolveByProcessName_MultipleSessionsSamePid_IsStillUnique()
    {
        var sessions = new List<AudioSession>
        {
            Session(100, "chrome", "i1"),
            Session(100, "chrome", "i2"),
        };

        SessionResolutionResult result = SessionResolver.ResolveByProcessName(sessions, "chrome");

        Assert.True(result.IsUnique);
        Assert.Single(result.ProcessIds);
        Assert.Equal(2, result.Sessions.Count);
    }

    [Fact]
    public void ResolveByProcessName_MultipleDistinctPids_ReturnsAmbiguous()
    {
        var sessions = new List<AudioSession>
        {
            Session(100, "chrome", "i1"),
            Session(200, "chrome", "i2"),
            Session(300, "chrome", "i3"),
        };

        SessionResolutionResult result = SessionResolver.ResolveByProcessName(sessions, "chrome.exe");

        Assert.True(result.IsAmbiguous);
        Assert.Equal([100, 200, 300], result.ProcessIds);
        Assert.Equal(3, result.Sessions.Count);
        Assert.Equal("chrome.exe", result.Query);
    }

    [Fact]
    public void ResolveByProcessName_AmbiguousPids_AreSortedAscending()
    {
        var sessions = new List<AudioSession>
        {
            Session(900, "chrome", "i1"),
            Session(100, "chrome", "i2"),
            Session(500, "chrome", "i3"),
        };

        SessionResolutionResult result = SessionResolver.ResolveByProcessName(sessions, "chrome");

        Assert.Equal([100, 500, 900], result.ProcessIds);
    }

    [Fact]
    public void ResolveByProcessName_NoMatch_ReturnsNotFound()
    {
        SessionResolutionResult result =
            SessionResolver.ResolveByProcessName([Session(100, "chrome")], "firefox");

        Assert.True(result.IsNotFound);
        Assert.Equal("firefox", result.Query);
    }

    [Fact]
    public void ResolveByProcessName_SessionsWithNullProcessName_AreIgnored()
    {
        var sessions = new List<AudioSession>
        {
            Session(100, null, "i1"),
            Session(200, "chrome", "i2"),
        };

        SessionResolutionResult result = SessionResolver.ResolveByProcessName(sessions, "chrome");

        Assert.True(result.IsUnique);
        Assert.Equal([200], result.ProcessIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".exe")]
    public void ResolveByProcessName_EmptyQuery_ReturnsNotFound(string query)
    {
        SessionResolutionResult result =
            SessionResolver.ResolveByProcessName([Session(100, "chrome")], query);

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public void ResolveByProcessName_EmptySessionCollection_ReturnsNotFound()
    {
        SessionResolutionResult result = SessionResolver.ResolveByProcessName([], "chrome");

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public void ResolveByProcessName_NullCollection_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SessionResolver.ResolveByProcessName(null!, "chrome"));
    }
}
