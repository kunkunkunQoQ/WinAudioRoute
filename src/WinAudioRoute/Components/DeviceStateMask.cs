namespace WinAudioRoute.Internal;

/// <summary>
/// 设备状态掩码的校验与归一化。
/// <para>
/// <b>为什么单独成类</b>：状态校验必须发生在<b>服务层</b>，而不是只在具体 COM 实现里。
/// 否则换一个 <see cref="IDeviceEnumerator"/> 实现（例如测试替身）就会绕过校验，
/// 使"契约"变成实现细节。全部设备查询路径都先经过这里。
/// </para>
/// </summary>
internal static class DeviceStateMask
{
    /// <summary>
    /// 全部被定义的状态位，等于 Windows 官方常量
    /// <c>DEVICE_STATEMASK_ALL = 0x0000000F</c>（mmdeviceapi.h）。
    /// </summary>
    internal const AudioDeviceState AllValidStates =
        AudioDeviceState.Active | AudioDeviceState.Disabled | AudioDeviceState.NotPresent | AudioDeviceState.Unplugged;

    /// <summary>
    /// 校验并归一化状态掩码。
    /// <para>
    /// 归一化只处理<b>非法输入</b>：Windows 的合法掩码就是 0x0F 的任意子集，
    /// 因此任何"包含全部四位"的输入（例如调用方误写的 <c>unchecked((int)0xFFFFFFFF)</c>）
    /// 都被归一化为 0x0F；包含未定义位（如 0x10）的输入被拒绝。
    /// 这是防御性校验，不代表 Windows 的 ALL 常量是 0xFFFFFFFF。
    /// </para>
    /// </summary>
    /// <param name="states">调用方传入的状态掩码。</param>
    /// <returns>可直接传给原生 API 的归一化掩码。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 掩码为 0（未选择任何状态），或包含未定义的状态位。
    /// </exception>
    internal static AudioDeviceState Normalize(AudioDeviceState states)
    {
        if (states == AudioDeviceState.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(states), states, "At least one device state must be requested.");
        }

        // 先归一化"包含全部有效位"的输入（例如 unchecked((int)0xFFFFFFFF)），
        // 再校验剩余位——否则 -1 会因为高位而误判为"含未定义标志"。
        if ((states & AllValidStates) == AllValidStates)
        {
            return AllValidStates;
        }

        if ((states & ~AllValidStates) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(states), states, "The device state mask contains flags that are not defined.");
        }

        return states;
    }
}
