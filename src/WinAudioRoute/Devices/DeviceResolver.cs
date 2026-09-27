namespace WinAudioRoute.Internal;

/// <summary>
/// 设备解析（名称/ID → <see cref="AudioDevice"/>）的纯逻辑实现。
/// <para>
/// <b>解析顺序固定</b>：
/// <list type="number">
///   <item><description>设备 ID 精确匹配（区分大小写，因为 ID 是系统生成的稳定标识）。</description></item>
///   <item><description><c>FriendlyName</c> 大小写不敏感的精确匹配。</description></item>
///   <item><description><c>FriendlyName</c> 大小写不敏感的唯一子串匹配。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>歧义必须显式失败</b>：命中的候选多于一个时返回多个候选，由调用方抛出
/// <see cref="AmbiguousAudioDeviceException"/>；本类绝不"取第一个"。
/// </para>
/// <para>
/// 本类不触碰 COM，因此可以完整单测。
/// </para>
/// </summary>
internal static class DeviceResolver
{
    /// <summary>
    /// 在给定设备集合中解析查询文本。
    /// </summary>
    /// <param name="devices">候选设备集合。</param>
    /// <param name="query">设备 ID 或名称。</param>
    /// <returns>解析结果（唯一命中 / 无命中 / 多个候选）。</returns>
    public static DeviceResolutionResult Resolve(IEnumerable<AudioDevice> devices, string query)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        return Resolve(devices, query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在给定设备集合中解析查询文本，并指定名称匹配用的字符串比较方式。
    /// </summary>
    /// <param name="devices">候选设备集合。</param>
    /// <param name="query">设备 ID 或名称。</param>
    /// <param name="nameComparison">FriendlyName 比较方式（ID 匹配始终为序数比较）。</param>
    /// <returns>解析结果（唯一命中 / 无命中 / 多个候选）。</returns>
    public static DeviceResolutionResult Resolve(
        IEnumerable<AudioDevice> devices,
        string query,
        StringComparison nameComparison)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        List<AudioDevice> all = devices.ToList();
        if (all.Count == 0)
        {
            return DeviceResolutionResult.NotFound;
        }

        // 1) 设备 ID 精确匹配（序数比较）
        List<AudioDevice> byId = all
            .Where(d => string.Equals(d.Id, query, StringComparison.Ordinal))
            .ToList();

        if (byId.Count == 1)
        {
            return DeviceResolutionResult.Found(byId[0]);
        }

        if (byId.Count > 1)
        {
            return DeviceResolutionResult.Ambiguous(byId);
        }

        // 2) FriendlyName 精确匹配（默认大小写不敏感）
        List<AudioDevice> byExactName = all
            .Where(d => string.Equals(d.FriendlyName, query, nameComparison))
            .ToList();

        if (byExactName.Count == 1)
        {
            return DeviceResolutionResult.Found(byExactName[0]);
        }

        if (byExactName.Count > 1)
        {
            return DeviceResolutionResult.Ambiguous(byExactName);
        }

        // 3) FriendlyName 唯一子串匹配（默认大小写不敏感）
        List<AudioDevice> bySubstring = all
            .Where(d => !string.IsNullOrEmpty(d.FriendlyName)
                        && d.FriendlyName.Contains(query, nameComparison))
            .ToList();

        return bySubstring.Count switch
        {
            1 => DeviceResolutionResult.Found(bySubstring[0]),
            > 1 => DeviceResolutionResult.Ambiguous(bySubstring),
            _ => DeviceResolutionResult.NotFound,
        };
    }
}

/// <summary>
/// 设备解析的结果类型。
/// </summary>
internal readonly struct DeviceResolutionResult
{
    private DeviceResolutionResult(AudioDevice? device, IReadOnlyList<AudioDevice> candidates)
    {
        Device = device;
        Candidates = candidates;
    }

    /// <summary>唯一命中时的设备；否则为 <see langword="null"/>。</summary>
    public AudioDevice? Device { get; }

    /// <summary>命中多个时的候选列表（可能为空）。</summary>
    public IReadOnlyList<AudioDevice> Candidates { get; }

    /// <summary>是否唯一命中。</summary>
    public bool IsUnique => Device is not null;

    /// <summary>是否命中多个候选。</summary>
    public bool IsAmbiguous => Device is null && Candidates.Count > 0;

    /// <summary>是否完全没有命中。</summary>
    public bool IsNotFound => Device is null && Candidates.Count == 0;

    /// <summary>构造唯一命中结果。</summary>
    /// <param name="device">命中的设备。</param>
    /// <returns>结果。</returns>
    public static DeviceResolutionResult Found(AudioDevice device) => new(device, []);

    /// <summary>构造多候选结果。</summary>
    /// <param name="candidates">候选设备。</param>
    /// <returns>结果。</returns>
    public static DeviceResolutionResult Ambiguous(IReadOnlyList<AudioDevice> candidates) => new(null, candidates);

    /// <summary>无命中的结果。</summary>
    public static DeviceResolutionResult NotFound => new(null, []);
}
