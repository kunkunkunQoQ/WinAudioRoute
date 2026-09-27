using System.Runtime.InteropServices;
using WinAudioRoute.Interop;

namespace WinAudioRoute.Tests;

/// <summary>
/// 互操作布局契约测试。
/// <para>
/// 这些断言保护的是"架构假设"本身：<c>PROPVARIANT</c> 的 24 字节布局在 x64/ARM64 成立、
/// 在 x86 不成立。一旦布局漂移，后续 <c>PropVariantClear</c> 会读越界，
/// 因此必须在测试中显式钉住，而不是依赖运行期偶然正确。
/// </para>
/// </summary>
public class InteropLayoutTests
{
    [Fact]
    public void PropVariant_SizeIs24Bytes()
    {
        Assert.Equal(24, Marshal.SizeOf<PROPVARIANT>());
    }

    [Fact]
    public void PropVariant_MatchesDeclaredExpectation()
    {
        Assert.Equal(NativeMethods.ExpectedPropVariantSize, Marshal.SizeOf<PROPVARIANT>());
    }

    [Fact]
    public void PropVariant_FieldOffsets_AreAsDeclared()
    {
        // vt 在偏移 0；pwszVal 在偏移 8（联合体偏移 0，因 8 字节对齐而落在结构体偏移 8）
        Assert.Equal(0, (int)Marshal.OffsetOf<PROPVARIANT>(nameof(PROPVARIANT.vt)));
        Assert.Equal(8, (int)Marshal.OffsetOf<PROPVARIANT>(nameof(PROPVARIANT.pwszVal)));
    }

    [Fact]
    public void PropertyKey_SizeIs20Bytes()
    {
        // GUID(16) + DWORD(4)，Pack = 4 → 20 字节（架构无关）
        Assert.Equal(20, Marshal.SizeOf<PROPERTYKEY>());
    }

    [Fact]
    public void AssertInteropLayout_DoesNotThrow_OnSupportedProcess()
    {
        // 测试工程锁定 x64；本机为 64 位进程时布局必须通过
        Assert.True(Environment.Is64BitProcess, "测试主机必须是 64 位进程");
        NativeMethods.AssertInteropLayout();
    }

    [Fact]
    public void FriendlyNamePropertyKey_MatchesWindowsSdk()
    {
        // PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, PID 14
        PROPERTYKEY key = PropertyKeys.PKEY_Device_FriendlyName;
        Assert.Equal(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), key.fmtid);
        Assert.Equal(14u, key.pid);
    }

    [Fact]
    public void ComConstants_MatchWindowsSdk()
    {
        Assert.Equal(0x1, ComConstants.CLSCTX_INPROC_SERVER);
        Assert.Equal(0x17, ComConstants.CLSCTX_ALL);
        Assert.Equal(0x0, ComConstants.STGM_READ);
        Assert.Equal(31, ComConstants.VT_LPWSTR);
    }
}
