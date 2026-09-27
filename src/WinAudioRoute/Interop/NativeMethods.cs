using System.Runtime.InteropServices;

namespace WinAudioRoute.Interop;

// =====================================================================
// 原生 P/Invoke 与互操作布局断言
//
// 提取来源：SonicRoute.Core/Interop/ComInterop.cs（MIT License,
// Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）
//
// 提取原则：P/Invoke 声明（DLL 名、签名、CharSet）全部原样保留，未做"现代化重写"
// （例如未改用 LibraryImport 源生成），以免改动已在真实系统上验证过的互操作路径。
// =====================================================================

/// <summary>
/// 本库使用的原生 API 声明。
/// </summary>
internal static class NativeMethods
{
    /// <summary><c>PROPVARIANT</c> 在受支持平台（x64 / ARM64）上的预期字节数。</summary>
    internal const int ExpectedPropVariantSize = 24;

    /// <summary>释放 <c>PROPVARIANT</c> 内的原生内存（ole32.dll）。</summary>
    /// <param name="pvar">要清理的 <c>PROPVARIANT</c>。</param>
    /// <returns>HRESULT。</returns>
    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    /// <summary>释放 COM 任务分配器分配的内存（ole32.dll）。</summary>
    /// <param name="pv">要释放的指针。</param>
    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    // 注意：.NET 8 的 DllImport 不支持 [MarshalAs(UnmanagedType.HString)] string，
    // 因此 HSTRING 一律手动创建（WindowsCreateString）后以 IntPtr 传入。
    /// <summary>取得 WinRT 可激活类的激活工厂（combase.dll）。</summary>
    /// <param name="activatableClassId">HSTRING 形式的可激活类 ID。</param>
    /// <param name="iid">请求的接口 IID。</param>
    /// <param name="factory">接收工厂指针。</param>
    /// <returns>HRESULT。</returns>
    [DllImport("combase.dll")]
    public static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        [Out] out IntPtr factory);

    /// <summary>创建 HSTRING（combase.dll）。</summary>
    /// <param name="src">源字符串。</param>
    /// <param name="length">字符数。</param>
    /// <param name="hstring">接收 HSTRING。</param>
    /// <returns>HRESULT。</returns>
    [DllImport("combase.dll")]
    public static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string src,
        [In] uint length,
        [Out] out IntPtr hstring);

    /// <summary>释放 HSTRING（combase.dll）。</summary>
    /// <param name="hstring">要释放的 HSTRING。</param>
    /// <returns>HRESULT。</returns>
    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>取得 HSTRING 的原始缓冲区（combase.dll）。</summary>
    /// <param name="hstring">HSTRING。</param>
    /// <param name="length">接收字符数。</param>
    /// <returns>原始缓冲区指针；调用期间保持有效。</returns>
    [DllImport("combase.dll")]
    public static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    /// <summary>
    /// 断言原生互操作结构在本进程中的实际布局与声明一致。
    /// <para>
    /// 这是"fail fast"而非"尽力运行"：<see cref="PROPVARIANT"/> 的 24 字节假设一旦不成立，
    /// 后续 <c>PropVariantClear</c> 会破坏内存，因此宁可在此处立即失败。
    /// </para>
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">布局与预期不符时抛出。</exception>
    internal static void AssertInteropLayout()
    {
        int actual = Marshal.SizeOf<PROPVARIANT>();
        if (actual != ExpectedPropVariantSize)
        {
            throw new PlatformNotSupportedException(
                $"互操作布局校验失败：PROPVARIANT 期望 {ExpectedPropVariantSize} 字节，实际 {actual} 字节" +
                $"（进程架构 {RuntimeInformation.ProcessArchitecture}，指针宽度 {IntPtr.Size * 8} 位）。" +
                "本库仅支持 x64 / ARM64。");
        }

        int keySize = Marshal.SizeOf<PROPERTYKEY>();
        // PROPERTYKEY = GUID(16) + DWORD(4)，Pack = 4 → 20 字节（所有架构一致）
        if (keySize != 20)
        {
            throw new PlatformNotSupportedException(
                $"互操作布局校验失败：PROPERTYKEY 期望 20 字节，实际 {keySize} 字节。");
        }
    }
}
