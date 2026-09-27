using WinAudioRoute.Internal;

namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="DeviceResolver"/> 的纯逻辑测试（不触碰 COM）。
/// </summary>
public class DeviceResolverTests
{
    private static AudioDevice Device(string id, string name, AudioDataFlow flow = AudioDataFlow.Render) => new()
    {
        Id = id,
        FriendlyName = name,
        Flow = flow,
        State = AudioDeviceState.Active,
    };

    private static readonly AudioDevice Speakers =
        Device("{0.0.0.00000000}.{aaaa}", "Speakers (Realtek)");

    private static readonly AudioDevice Headset =
        Device("{0.0.0.00000000}.{bbbb}", "Headset Earphone (SteelSeries Sonar)");

    private static readonly AudioDevice Microphone =
        Device("{0.0.1.00000000}.{cccc}", "Microphone (USB Audio)", AudioDataFlow.Capture);

    private static IReadOnlyList<AudioDevice> All =>
        [Speakers, Headset, Microphone];

    [Fact]
    public void Resolve_ById_ExactMatch()
    {
        DeviceResolutionResult result = DeviceResolver.Resolve(All, Speakers.Id);

        Assert.True(result.IsUnique);
        Assert.Equal(Speakers.Id, result.Device!.Id);
    }

    [Fact]
    public void Resolve_ById_IsCaseSensitive_ButStillMatchesViaOtherRules()
    {
        // ID 精确匹配使用序数比较；大小写不同的 ID 不构成 ID 命中，
        // 但会退化为名称匹配并（通常）无命中，因此必须显式报"未找到"。
        DeviceResolutionResult result = DeviceResolver.Resolve(All, Speakers.Id.ToUpperInvariant());

        Assert.True(result.IsNotFound, "大小写不同的 ID 不应被当作 ID 精确命中");
    }

    [Fact]
    public void Resolve_ByName_ExactMatch()
    {
        DeviceResolutionResult result = DeviceResolver.Resolve(All, "Speakers (Realtek)");

        Assert.True(result.IsUnique);
        Assert.Equal(Speakers.Id, result.Device!.Id);
    }

    [Fact]
    public void Resolve_ByName_ExactMatch_IsCaseInsensitive()
    {
        DeviceResolutionResult result = DeviceResolver.Resolve(All, "speakers (REALTEK)");

        Assert.True(result.IsUnique);
        Assert.Equal(Speakers.Id, result.Device!.Id);
    }

    [Fact]
    public void Resolve_ByName_UniqueSubstring()
    {
        DeviceResolutionResult result = DeviceResolver.Resolve(All, "SteelSeries");

        Assert.True(result.IsUnique);
        Assert.Equal(Headset.Id, result.Device!.Id);
    }

    [Fact]
    public void Resolve_ByName_Substring_IsCaseInsensitive()
    {
        DeviceResolutionResult result = DeviceResolver.Resolve(All, "usb audio");

        Assert.True(result.IsUnique);
        Assert.Equal(Microphone.Id, result.Device!.Id);
    }

    [Fact]
    public void Resolve_ById_TakesPrecedenceOverName()
    {
        // 查询文本同时是某个设备的 ID 与另一个设备的名称 → ID 规则优先
        var tricky = new List<AudioDevice>
        {
            Device("{0.0.0.0}.{1111}", "Alpha"),
            Device("{0.0.0.0}.{2222}", "{0.0.0.0}.{1111}"),
        };

        DeviceResolutionResult result = DeviceResolver.Resolve(tricky, "{0.0.0.0}.{1111}");

        Assert.True(result.IsUnique);
        Assert.Equal("Alpha", result.Device!.FriendlyName);
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNotFound()
    {
        DeviceResolutionResult result = DeviceResolver.Resolve(All, "Nonexistent Device 9000");

        Assert.True(result.IsNotFound);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Resolve_SubstringMatchesMultiple_ReturnsAmbiguous()
    {
        var devices = new List<AudioDevice>
        {
            Device("{0.0.0.0}.{1}", "Speakers (Realtek)"),
            Device("{0.0.0.0}.{2}", "Speakers (USB)"),
        };

        DeviceResolutionResult result = DeviceResolver.Resolve(devices, "Speakers");

        Assert.True(result.IsAmbiguous);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Resolve_ExactNameMatchesMultiple_ReturnsAmbiguous()
    {
        // 两个设备同名（完全一致）→ 精确名称规则已产生多个候选，不得回退到"取第一个"
        var devices = new List<AudioDevice>
        {
            Device("{0.0.0.0}.{1}", "Speakers"),
            Device("{0.0.0.0}.{2}", "Speakers"),
        };

        DeviceResolutionResult result = DeviceResolver.Resolve(devices, "Speakers");

        Assert.True(result.IsAmbiguous);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Resolve_ExactNameMatch_Wins_OverSubstringOfOthers()
    {
        var devices = new List<AudioDevice>
        {
            Device("{0.0.0.0}.{1}", "Speakers"),
            Device("{0.0.0.0}.{2}", "Speakers (Realtek)"),
        };

        DeviceResolutionResult result = DeviceResolver.Resolve(devices, "Speakers");

        Assert.True(result.IsUnique);
        Assert.Equal("Speakers", result.Device!.FriendlyName);
    }

    [Fact]
    public void Resolve_EmptyDeviceCollection_ReturnsNotFound()
    {
        DeviceResolutionResult result = DeviceResolver.Resolve([], "anything");

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public void Resolve_DeviceWithEmptyFriendlyName_IsNotMatchedByName()
    {
        var devices = new List<AudioDevice>
        {
            Device("{0.0.0.0}.{1}", string.Empty),
            Device("{0.0.0.0}.{2}", "Real Speakers"),
        };

        // 空名称设备不应因为空串是任意串的子串而被命中
        DeviceResolutionResult result = DeviceResolver.Resolve(devices, "Speakers");

        Assert.True(result.IsUnique);
        Assert.Equal("{0.0.0.0}.{2}", result.Device!.Id);
    }

    [Fact]
    public void Resolve_NullDevices_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DeviceResolver.Resolve(null!, "x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyQuery_Throws(string query)
    {
        Assert.Throws<ArgumentException>(() => DeviceResolver.Resolve(All, query));
    }
}
