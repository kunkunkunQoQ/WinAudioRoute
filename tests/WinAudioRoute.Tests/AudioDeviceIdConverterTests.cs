using WinAudioRoute.Routing;

namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="AudioDeviceIdConverter"/> 的纯逻辑测试（不触碰 COM）。
/// <para>
/// 这是最适合纯测试覆盖的一块：短 ID 与完整设备接口路径之间的转换一旦出错，
/// 按应用路由会静默失效（SonicRoute 已实测过这个坑）。
/// </para>
/// </summary>
public class AudioDeviceIdConverterTests
{
    private const string ShortRender = "{0.0.0.00000000}.{11111111-1111-4111-8111-111111111111}";
    private const string ShortCapture = "{0.0.1.00000000}.{22222222-2222-4222-8222-222222222222}";

    private const string FullRender =
        @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{11111111-1111-4111-8111-111111111111}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private const string FullCapture =
        @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{22222222-2222-4222-8222-222222222222}#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    [Fact]
    public void Constants_MatchWindowsSdk()
    {
        Assert.Equal(@"\\?\SWD#MMDEVAPI#", AudioDeviceIdConverter.MmdevapiToken);
        Assert.Equal("#{e6327cad-dcec-4949-ae8a-991e976a79d2}", AudioDeviceIdConverter.RenderInterfaceSuffix);
        Assert.Equal("#{2eef81be-33fa-4800-9670-1cd474972c3f}", AudioDeviceIdConverter.CaptureInterfaceSuffix);
    }

    // ------------------------------------------------------------------
    // short -> full
    // ------------------------------------------------------------------

    [Fact]
    public void ToFullDeviceId_ShortRenderId_ProducesRenderInterfacePath()
    {
        Assert.Equal(FullRender, AudioDeviceIdConverter.ToFullDeviceId(ShortRender, AudioDataFlow.Render));
    }

    [Fact]
    public void ToFullDeviceId_ShortCaptureId_ProducesCaptureInterfacePath()
    {
        Assert.Equal(FullCapture, AudioDeviceIdConverter.ToFullDeviceId(ShortCapture, AudioDataFlow.Capture));
    }

    [Fact]
    public void ToFullDeviceId_AlreadyFull_IsUnchanged()
    {
        Assert.Equal(FullRender, AudioDeviceIdConverter.ToFullDeviceId(FullRender, AudioDataFlow.Render));

        // 即使传入的方向与之不符，也不重复包装（保持幂等）
        Assert.Equal(FullRender, AudioDeviceIdConverter.ToFullDeviceId(FullRender, AudioDataFlow.Capture));
    }

    [Theory]
    [InlineData(AudioDataFlow.All)]
    public void ToFullDeviceId_NonConcreteFlow_Throws(AudioDataFlow flow)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AudioDeviceIdConverter.ToFullDeviceId(ShortRender, flow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ToFullDeviceId_Empty_Throws(string? deviceId)
    {
        // null 会走 ArgumentNullException（ArgumentException 的派生类），空串/空白走 ArgumentException
        Assert.ThrowsAny<ArgumentException>(
            () => AudioDeviceIdConverter.ToFullDeviceId(deviceId!, AudioDataFlow.Render));
    }

    // ------------------------------------------------------------------
    // full -> short
    // ------------------------------------------------------------------

    [Fact]
    public void ToShortDeviceId_FullRenderPath_ReturnsShortId()
    {
        Assert.Equal(ShortRender, AudioDeviceIdConverter.ToShortDeviceId(FullRender));
    }

    [Fact]
    public void ToShortDeviceId_FullCapturePath_ReturnsShortId()
    {
        Assert.Equal(ShortCapture, AudioDeviceIdConverter.ToShortDeviceId(FullCapture));
    }

    [Fact]
    public void ToShortDeviceId_AlreadyShort_IsUnchanged()
    {
        Assert.Equal(ShortRender, AudioDeviceIdConverter.ToShortDeviceId(ShortRender));
        Assert.Equal(ShortCapture, AudioDeviceIdConverter.ToShortDeviceId(ShortCapture));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ToShortDeviceId_Empty_ReturnsEmpty(string? deviceId)
    {
        Assert.Equal(string.Empty, AudioDeviceIdConverter.ToShortDeviceId(deviceId));
    }

    [Fact]
    public void ToShortDeviceId_MalformedInput_IsReturnedAsIs()
    {
        // 既没有前缀也没有已知后缀：不做猜测，原样返回
        const string malformed = "not-a-device-id";
        Assert.Equal(malformed, AudioDeviceIdConverter.ToShortDeviceId(malformed));
    }

    [Fact]
    public void ToShortDeviceId_PrefixOnly_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AudioDeviceIdConverter.ToShortDeviceId(AudioDeviceIdConverter.MmdevapiToken));
    }

    // ------------------------------------------------------------------
    // 大小写
    // ------------------------------------------------------------------

    [Fact]
    public void ToFullDeviceId_PrefixCaseInsensitive_IsRecognisedAsFull()
    {
        string upperPrefix = FullRender.ToUpperInvariant();
        Assert.True(AudioDeviceIdConverter.IsFullDeviceId(upperPrefix));
        Assert.Equal(upperPrefix, AudioDeviceIdConverter.ToFullDeviceId(upperPrefix, AudioDataFlow.Render));
    }

    [Fact]
    public void ToShortDeviceId_SuffixCaseInsensitive_IsStripped()
    {
        string upperSuffix = FullRender.ToUpperInvariant();
        Assert.Equal(ShortRender.ToUpperInvariant(), AudioDeviceIdConverter.ToShortDeviceId(upperSuffix));
    }

    [Fact]
    public void IsFullDeviceId_Cases()
    {
        Assert.True(AudioDeviceIdConverter.IsFullDeviceId(FullRender));
        Assert.True(AudioDeviceIdConverter.IsFullDeviceId(FullCapture));

        Assert.False(AudioDeviceIdConverter.IsFullDeviceId(ShortRender));
        Assert.False(AudioDeviceIdConverter.IsFullDeviceId(null));
        Assert.False(AudioDeviceIdConverter.IsFullDeviceId(string.Empty));
    }

    // ------------------------------------------------------------------
    // 往返
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(AudioDataFlow.Render)]
    [InlineData(AudioDataFlow.Capture)]
    public void RoundTrip_ShortThenFullThenShort(AudioDataFlow flow)
    {
        string shortId = flow == AudioDataFlow.Render ? ShortRender : ShortCapture;

        string full = AudioDeviceIdConverter.ToFullDeviceId(shortId, flow);
        string back = AudioDeviceIdConverter.ToShortDeviceId(full);

        Assert.Equal(shortId, back);
        Assert.Equal(flow, AudioDeviceIdConverter.TryGetFlow(full));
    }

    [Theory]
    [InlineData(AudioDataFlow.Render)]
    [InlineData(AudioDataFlow.Capture)]
    public void RoundTrip_IsIdempotent(AudioDataFlow flow)
    {
        string shortId = flow == AudioDataFlow.Render ? ShortRender : ShortCapture;

        string full1 = AudioDeviceIdConverter.ToFullDeviceId(shortId, flow);
        string full2 = AudioDeviceIdConverter.ToFullDeviceId(full1, flow);

        Assert.Equal(full1, full2);
    }

    // ------------------------------------------------------------------
    // 方向判定
    // ------------------------------------------------------------------

    [Fact]
    public void TryGetFlow_FullPaths()
    {
        Assert.Equal(AudioDataFlow.Render, AudioDeviceIdConverter.TryGetFlow(FullRender));
        Assert.Equal(AudioDataFlow.Capture, AudioDeviceIdConverter.TryGetFlow(FullCapture));
    }

    [Fact]
    public void TryGetFlow_ShortIds_AreUnknown()
    {
        Assert.Null(AudioDeviceIdConverter.TryGetFlow(ShortRender));
        Assert.Null(AudioDeviceIdConverter.TryGetFlow(ShortCapture));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetFlow_Empty_ReturnsNull(string? deviceId)
    {
        Assert.Null(AudioDeviceIdConverter.TryGetFlow(deviceId));
    }

    [Fact]
    public void IsFlowCompatible_ShortId_IsAlwaysCompatible()
    {
        // 短 ID 不含方向信息，因此不做限制（方向由调用方显式指定）
        Assert.True(AudioDeviceIdConverter.IsFlowCompatible(ShortRender, AudioDataFlow.Capture));
        Assert.True(AudioDeviceIdConverter.IsFlowCompatible(ShortRender, AudioDataFlow.Render));
    }

    [Fact]
    public void IsFlowCompatible_FullPath_MustMatch()
    {
        Assert.True(AudioDeviceIdConverter.IsFlowCompatible(FullRender, AudioDataFlow.Render));
        Assert.False(AudioDeviceIdConverter.IsFlowCompatible(FullRender, AudioDataFlow.Capture));

        Assert.True(AudioDeviceIdConverter.IsFlowCompatible(FullCapture, AudioDataFlow.Capture));
        Assert.False(AudioDeviceIdConverter.IsFlowCompatible(FullCapture, AudioDataFlow.Render));
    }

    [Fact]
    public void IsFlowCompatible_Empty_IsCompatible()
    {
        Assert.True(AudioDeviceIdConverter.IsFlowCompatible(null, AudioDataFlow.Render));
        Assert.True(AudioDeviceIdConverter.IsFlowCompatible(string.Empty, AudioDataFlow.Capture));
    }
}
