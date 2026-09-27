using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinAudioRoute.Cli;

/// <summary>
/// JSON 输出构建工具。
/// <para>
/// <b>为什么用 <see cref="JsonNode"/> 而不是反射序列化</b>：输出结构需要跨 PowerShell / Python /
/// Node.js / AutoHotkey 消费，因此<b>字段名与形状必须显式、稳定、不随重构漂移</b>。
/// 手写节点可以让每个字段的命名与是否可为 null 都成为显式决策。
/// </para>
/// <para>所有 JSON 一律写入 <b>stdout</b>；诊断与错误一律写入 <b>stderr</b>。</para>
/// </summary>
internal static class Json
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 不转义非 ASCII，便于中文设备名可读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>创建输出信封：包含命令名、时间戳与"是否使用了未公开 API"的披露。</summary>
    /// <param name="command">命令名。</param>
    /// <param name="usesUndocumentedApi">该命令是否依赖未公开的 Windows API。</param>
    /// <returns>信封对象。</returns>
    public static JsonObject Envelope(string command, bool usesUndocumentedApi = false) => new()
    {
        ["tool"] = "winaudio",
        ["version"] = Program.Version,
        ["command"] = command,
        ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        ["usesUndocumentedApi"] = usesUndocumentedApi,
    };

    /// <summary>把节点序列化为 JSON 文本（缩进、不转义 ASCII）。</summary>
    /// <param name="node">JSON 节点。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(JsonNode node) => node.ToJsonString(Options);

    /// <summary>创建 JSON 数组，并跳过 null 元素。</summary>
    /// <param name="items">元素序列。</param>
    /// <returns>数组节点。</returns>
    public static JsonArray ArrayOf(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (JsonNode? item in items)
        {
            if (item is not null)
            {
                array.Add(item);
            }
        }

        return array;
    }

    /// <summary>把字符串写成 JSON 值；null 写成 JSON null。</summary>
    /// <param name="value">字符串。</param>
    /// <returns>JSON 值。</returns>
    public static JsonNode? StringOrNull(string? value) =>
        value is null ? null : JsonValue.Create(value);
}

/// <summary>
/// 设备与会话的 JSON 投影。
/// </summary>
internal static class JsonProjection
{
    /// <summary>设备 → JSON 对象。</summary>
    /// <param name="device">设备。</param>
    /// <returns>JSON 对象。</returns>
    public static JsonObject Device(AudioDevice device) => new()
    {
        ["id"] = device.Id,
        ["name"] = device.FriendlyName,
        ["flow"] = Flow(device.Flow),
        ["state"] = device.State.ToString(),
        ["isActive"] = device.IsActive,
        ["isDefaultConsole"] = device.IsDefaultConsole,
        ["isDefaultMultimedia"] = device.IsDefaultMultimedia,
        ["isDefaultCommunications"] = device.IsDefaultCommunications,
    };

    /// <summary>会话 → JSON 对象。</summary>
    /// <param name="session">会话。</param>
    /// <returns>JSON 对象。</returns>
    public static JsonObject Session(AudioSession session) => new()
    {
        ["processId"] = session.ProcessId,
        ["processName"] = Json.StringOrNull(session.ProcessName),
        ["displayName"] = Json.StringOrNull(session.DisplayName),
        ["sessionIdentifier"] = session.SessionIdentifier,
        ["sessionInstanceIdentifier"] = session.SessionInstanceIdentifier,
        ["state"] = session.State.ToString(),
        ["flow"] = Flow(session.Flow),
        ["deviceId"] = session.DeviceId,
        ["volume"] = session.Volume is null ? null : JsonValue.Create(session.Volume.Value),
        ["isMuted"] = session.IsMuted is null ? null : JsonValue.Create(session.IsMuted.Value),
        ["isSystemSoundsSession"] = session.IsSystemSoundsSession,
    };

    /// <summary>操作结果 → JSON 对象。</summary>
    /// <param name="result">操作结果。</param>
    /// <returns>JSON 对象。</returns>
    public static JsonObject Result(AudioOperationResult result)
    {
        var failures = new JsonArray();
        foreach (AudioOperationFailure failure in result.Failures)
        {
            failures.Add(new JsonObject
            {
                ["target"] = failure.Target,
                ["hresult"] = $"0x{failure.HResult:X8}",
                ["message"] = failure.Message,
            });
        }

        return new JsonObject
        {
            ["total"] = result.Total,
            ["succeeded"] = result.Succeeded,
            ["failed"] = result.Failed,
            ["isSuccess"] = result.IsSuccess,
            ["isPartialSuccess"] = result.IsPartialSuccess,
            ["failures"] = failures,
        };
    }

    /// <summary>数据流方向 → 稳定的字符串标识。</summary>
    /// <param name="flow">方向。</param>
    /// <returns><c>render</c> 或 <c>capture</c>。</returns>
    public static string Flow(AudioDataFlow flow) => flow switch
    {
        AudioDataFlow.Render => "render",
        AudioDataFlow.Capture => "capture",
        _ => "all",
    };

    /// <summary>角色 → 稳定的字符串标识。</summary>
    /// <param name="role">角色。</param>
    /// <returns>小写的角色名。</returns>
    public static string Role(AudioRole role) => role.ToString().ToLowerInvariant();
}

/// <summary>
/// 面向终端的文本格式化（表格与行）。
/// </summary>
internal static class TextOutput
{
    /// <summary>输出一行到 stdout。</summary>
    /// <param name="line">行内容。</param>
    public static void Line(string line = "") => Console.Out.WriteLine(line);

    /// <summary>输出可空值，null 显示为 <c>-</c>。</summary>
    /// <param name="value">值。</param>
    /// <returns>显示文本。</returns>
    public static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    /// <summary>输出音量标量的紧凑表示。</summary>
    /// <param name="value">标量。</param>
    /// <returns>显示文本。</returns>
    public static string Volume(float? value) =>
        value is null ? "-" : $"{(int)Math.Round(value.Value * 100f)}%";

    /// <summary>用固定宽度打印一行的列。</summary>
    /// <param name="columns">列内容。</param>
    public static void Columns(params string[] columns) =>
        Console.Out.WriteLine(string.Join("  ", columns));
}
