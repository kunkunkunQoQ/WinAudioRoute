namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="AudioOperationResult"/> 与异常族的契约测试。
/// </summary>
public class AudioOperationResultTests
{
    [Fact]
    public void AllSucceeded_ReportsSuccess()
    {
        AudioOperationResult result = AudioOperationResult.AllSucceeded(3);

        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.True(result.IsSuccess);
        Assert.False(result.IsPartialSuccess);
        Assert.False(result.IsTotalFailure);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Partial_ReportsPartialSuccess()
    {
        AudioOperationResult result = AudioOperationResult.Partial(
            2,
            [new AudioOperationFailure("session-a", unchecked((int)0x80004005u), "failed")]);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.False(result.IsSuccess);
        Assert.True(result.IsPartialSuccess);
        Assert.False(result.IsTotalFailure);
    }

    [Fact]
    public void AllFailed_ReportsTotalFailure()
    {
        AudioOperationResult result = AudioOperationResult.AllFailed(
        [
            new AudioOperationFailure("role-1", unchecked((int)0x80070005u), "denied"),
            new AudioOperationFailure("role-2", unchecked((int)0x80070005u), "denied"),
            new AudioOperationFailure("role-3", unchecked((int)0x80004001u), "not implemented"),
        ]);

        Assert.Equal(3, result.Total);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(3, result.Failed);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsPartialSuccess);
        Assert.True(result.IsTotalFailure);
        Assert.Equal(unchecked((int)0x80070005u), result.DominantHResult);
    }

    [Fact]
    public void EmptyResult_IsNeitherSuccessNorFailure()
    {
        AudioOperationResult result = AudioOperationResult.AllSucceeded(0);

        Assert.Equal(0, result.Total);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsPartialSuccess);
        Assert.False(result.IsTotalFailure);
        Assert.Equal(0, result.DominantHResult);
    }

    [Fact]
    public void DominantHResult_IsZeroWhenNoFailures()
    {
        Assert.Equal(0, AudioOperationResult.AllSucceeded(5).DominantHResult);
    }

    [Fact]
    public void ToString_IsDiagnostic()
    {
        Assert.Equal("Total=3, Succeeded=3, Failed=0", AudioOperationResult.AllSucceeded(3).ToString());
    }

    [Fact]
    public void Partial_NullFailures_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AudioOperationResult.Partial(1, null!));
    }

    [Fact]
    public void AllFailed_NullFailures_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AudioOperationResult.AllFailed(null!));
    }
}

/// <summary>
/// 异常族的契约测试：HRESULT 保留、消息为英文中性、多候选信息可读。
/// </summary>
public class AudioExceptionTests
{
    [Fact]
    public void WinAudioException_PreservesHResult_OnBothViews()
    {
        const int hresult = unchecked((int)0x88890004u); // AUDCLNT_E_DEVICE_INVALIDATED

        var ex = new WinAudioException("device invalidated", hresult);

        Assert.Equal(hresult, ex.HResult);
        Assert.Equal(hresult, ((Exception)ex).HResult);
        Assert.Equal("0x88890004", ex.HResultHex);
    }

    [Fact]
    public void WinAudioException_DefaultHResult_IsTheClrDefault()
    {
        var ex = new WinAudioException("boom");

        // 未显式传入 HRESULT 时，基类 Exception 的默认值为 COR_E_EXCEPTION (0x80131500)
        Assert.Equal(unchecked((int)0x80131500u), ex.HResult);
        Assert.Equal(unchecked((int)0x80131500u), ((Exception)ex).HResult);
        Assert.Equal("0x80131500", ex.HResultHex);
    }

    [Fact]
    public void AudioDeviceNotFoundException_CarriesQueryAndFlow()
    {
        var ex = new AudioDeviceNotFoundException("no match")
        {
            Query = "Speakers",
            Flow = AudioDataFlow.Render,
        };

        Assert.Equal("Speakers", ex.Query);
        Assert.Equal(AudioDataFlow.Render, ex.Flow);
        Assert.IsAssignableFrom<WinAudioException>(ex);
    }

    [Fact]
    public void AmbiguousAudioDeviceException_CarriesCandidates()
    {
        AudioDevice[] candidates =
        [
            new AudioDevice { Id = "a", FriendlyName = "Speakers", Flow = AudioDataFlow.Render },
            new AudioDevice { Id = "b", FriendlyName = "Speakers", Flow = AudioDataFlow.Render },
        ];

        var ex = new AmbiguousAudioDeviceException("ambiguous", candidates)
        {
            Query = "Speakers",
            Flow = AudioDataFlow.Render,
        };

        Assert.Equal(2, ex.Candidates.Count);
        Assert.Equal("a", ex.Candidates[0].Id);
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousAudioDeviceException_NullCandidates_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AmbiguousAudioDeviceException("ambiguous", null!));
    }

    [Fact]
    public void AmbiguousAudioSessionException_CarriesCandidatePids()
    {
        var ex = new AmbiguousAudioSessionException("ambiguous", [100, 200, 300])
        {
            Query = "chrome.exe",
            CandidateSessionCount = 5,
        };

        Assert.Equal([100, 200, 300], ex.CandidateProcessIds);
        Assert.Equal(5, ex.CandidateSessionCount);
        Assert.Equal("chrome.exe", ex.Query);
        Assert.Contains("3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioSessionNotFoundException_CarriesQuery()
    {
        var ex = new AudioSessionNotFoundException("no sessions") { Query = "4242" };

        Assert.Equal("4242", ex.Query);
    }

    [Fact]
    public void AudioOperationFailedException_CarriesResult()
    {
        AudioOperationResult result = AudioOperationResult.AllFailed(
            [new AudioOperationFailure("t", unchecked((int)0x80004005u), "failed")]);

        var ex = new AudioOperationFailedException("failed", unchecked((int)0x80004005u), result);

        Assert.Equal(unchecked((int)0x80004005u), ex.HResult);
        Assert.NotNull(ex.OperationResult);
        Assert.True(ex.OperationResult!.IsTotalFailure);
    }

    [Fact]
    public void AudioRoutingNotSupportedException_PreservesHResult()
    {
        var ex = new AudioRoutingNotSupportedException("not supported", unchecked((int)0x80004001u));

        Assert.Equal(unchecked((int)0x80004001u), ex.HResult);
    }

    [Theory]
    [InlineData(typeof(WinAudioException))]
    [InlineData(typeof(AudioDeviceNotFoundException))]
    [InlineData(typeof(AmbiguousAudioDeviceException))]
    [InlineData(typeof(AudioSessionNotFoundException))]
    [InlineData(typeof(AmbiguousAudioSessionException))]
    [InlineData(typeof(AudioOperationFailedException))]
    [InlineData(typeof(AudioRoutingNotSupportedException))]
    public void AllExceptions_DeriveFromWinAudioException(Type exceptionType)
    {
        Assert.True(typeof(WinAudioException).IsAssignableFrom(exceptionType));
    }
}
