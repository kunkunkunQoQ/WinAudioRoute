using WinAudioRoute.Events;
using WinAudioRoute.Interop;
using WinAudioRoute.Internal;

namespace WinAudioRoute.Tests;

/// <summary>
/// 会话事件回调 → 托管事件参数的映射与生命周期测试。
/// </summary>
public class SessionEventClientTests
{
    private static (SessionEventClient Client, List<AudioSessionChangedEventArgs> Events) Create()
    {
        var events = new List<AudioSessionChangedEventArgs>();
        var client = new SessionEventClient(4242, "sid", "iid", events.Add);
        return (client, events);
    }

    [Fact]
    public void OnStateChanged_MapsState()
    {
        (SessionEventClient client, List<AudioSessionChangedEventArgs> events) = Create();
        Guid context = Guid.Empty;

        client.OnStateChanged(InteropSessionState.Active);

        AudioSessionChangedEventArgs args = Assert.Single(events);
        Assert.Equal(AudioSessionChangeKind.StateChanged, args.Kind);
        Assert.Equal(AudioSessionState.Active, args.State);
        Assert.Equal(4242, args.ProcessId);
        Assert.Equal("iid", args.SessionInstanceIdentifier);
        Assert.Equal("sid", args.SessionIdentifier);
    }

    [Fact]
    public void OnSessionDisconnected_MapsReason()
    {
        (SessionEventClient client, List<AudioSessionChangedEventArgs> events) = Create();

        client.OnSessionDisconnected(InteropSessionDisconnectReason.DisconnectReasonDeviceRemoval);

        AudioSessionChangedEventArgs args = Assert.Single(events);
        Assert.Equal(AudioSessionChangeKind.Disconnected, args.Kind);
        Assert.Equal(AudioSessionDisconnectReason.DeviceRemoval, args.DisconnectReason);
    }

    [Fact]
    public void OnSimpleVolumeChanged_MapsVolumeAndMute()
    {
        (SessionEventClient client, List<AudioSessionChangedEventArgs> events) = Create();
        Guid context = Guid.Empty;

        client.OnSimpleVolumeChanged(0.35f, true, ref context);

        AudioSessionChangedEventArgs args = Assert.Single(events);
        Assert.Equal(AudioSessionChangeKind.VolumeChanged, args.Kind);
        Assert.Equal(0.35f, args.Volume);
        Assert.True(args.IsMuted);
    }

    [Fact]
    public void DisplayNameAndIconChanges_AreMapped()
    {
        (SessionEventClient client, List<AudioSessionChangedEventArgs> events) = Create();
        Guid context = Guid.Empty;

        client.OnDisplayNameChanged("new name", ref context);
        client.OnIconPathChanged("new icon", ref context);
        Guid grouping = Guid.Empty;
        client.OnGroupingParamChanged(ref grouping, ref context);

        Assert.Equal(
            [AudioSessionChangeKind.DisplayNameChanged, AudioSessionChangeKind.IconPathChanged, AudioSessionChangeKind.GroupingParamChanged],
            events.Select(e => e.Kind));
    }

    [Fact]
    public void AllCallbacks_ReturnSZero()
    {
        (SessionEventClient client, _) = Create();
        Guid context = Guid.Empty;
        Guid grouping = Guid.Empty;

        Assert.Equal(0, client.OnDisplayNameChanged("n", ref context));
        Assert.Equal(0, client.OnIconPathChanged("i", ref context));
        Assert.Equal(0, client.OnSimpleVolumeChanged(0.5f, false, ref context));
        Assert.Equal(0, client.OnGroupingParamChanged(ref grouping, ref context));
        Assert.Equal(0, client.OnStateChanged(InteropSessionState.Active));
        Assert.Equal(0, client.OnSessionDisconnected(InteropSessionDisconnectReason.DisconnectReasonSessionLogoff));
    }

    [Fact]
    public void MarkDisposed_StopsAllFurtherEvents()
    {
        (SessionEventClient client, List<AudioSessionChangedEventArgs> events) = Create();
        Guid context = Guid.Empty;

        client.OnStateChanged(InteropSessionState.Active);
        Assert.Single(events);

        client.MarkDisposed();

        client.OnStateChanged(InteropSessionState.Inactive);
        client.OnSimpleVolumeChanged(0.1f, true, ref context);
        client.OnSessionDisconnected(InteropSessionDisconnectReason.DisconnectReasonSessionLogoff);

        Assert.Single(events);
        Assert.True(client.IsDisposed);
    }

    [Fact]
    public void SubscriberThrows_DoesNotEscapeCallback()
    {
        var client = new SessionEventClient(
            1, "sid", "iid",
            _ => throw new InvalidOperationException("subscriber failure"));
        Guid context = Guid.Empty;

        Assert.Equal(0, client.OnStateChanged(InteropSessionState.Active));
        Assert.Equal(0, client.OnSimpleVolumeChanged(0.5f, false, ref context));
        Assert.Equal(0, client.OnSessionDisconnected(InteropSessionDisconnectReason.DisconnectReasonSessionLogoff));
    }

    [Fact]
    public void NullCallback_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SessionEventClient(1, "sid", "iid", null!));
    }

    [Fact]
    public void NullIdentifiers_BecomeEmptyStrings()
    {
        var events = new List<AudioSessionChangedEventArgs>();
        var client = new SessionEventClient(1, null!, null!, events.Add);

        client.OnStateChanged(InteropSessionState.Active);

        AudioSessionChangedEventArgs args = Assert.Single(events);
        Assert.Equal(string.Empty, args.SessionIdentifier);
        Assert.Equal(string.Empty, args.SessionInstanceIdentifier);
    }
}

/// <summary>
/// 事件参数的契约测试。
/// </summary>
public class AudioChangedEventArgsTests
{
    [Fact]
    public void DeviceArgs_DefaultOptionalFieldsAreNull()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = CreateDevice();

        client.OnDeviceAdded("dev");

        AudioDeviceChangedEventArgs args = Assert.Single(events);
        Assert.Null(args.Flow);
        Assert.Null(args.Role);
        Assert.Null(args.State);
    }

    [Fact]
    public void DeviceArgs_ToString_IsDiagnostic()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = CreateDevice();

        client.OnDefaultDeviceChanged(EDataFlow.eRender, ERole.eCommunications, "dev");

        string text = Assert.Single(events).ToString();
        Assert.Contains("DefaultChanged", text, StringComparison.Ordinal);
        Assert.Contains("Communications", text, StringComparison.Ordinal);
        Assert.Contains("dev", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionArgs_ToString_IsDiagnostic()
    {
        var events = new List<AudioSessionChangedEventArgs>();
        var client = new SessionEventClient(7, "sid", "iid", events.Add);

        client.OnSessionDisconnected(InteropSessionDisconnectReason.DisconnectReasonServerShutdown);

        string text = Assert.Single(events).ToString();
        Assert.Contains("Disconnected", text, StringComparison.Ordinal);
        Assert.Contains("iid", text, StringComparison.Ordinal);
        Assert.Contains("7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EventKindEnums_AreDistinct()
    {
        Assert.NotEqual(typeof(AudioDeviceChangeKind), typeof(AudioSessionChangeKind));

        Assert.Contains("Added", Enum.GetNames<AudioDeviceChangeKind>());
        Assert.Contains("Removed", Enum.GetNames<AudioDeviceChangeKind>());
        Assert.Contains("StateChanged", Enum.GetNames<AudioDeviceChangeKind>());
        Assert.Contains("DefaultChanged", Enum.GetNames<AudioDeviceChangeKind>());
        Assert.Contains("PropertyChanged", Enum.GetNames<AudioDeviceChangeKind>());

        Assert.Contains("Created", Enum.GetNames<AudioSessionChangeKind>());
        Assert.Contains("Disconnected", Enum.GetNames<AudioSessionChangeKind>());
    }

    private static (DeviceNotificationClient Client, List<AudioDeviceChangedEventArgs> Events, List<int> Failures) CreateDevice()
    {
        var events = new List<AudioDeviceChangedEventArgs>();
        var failures = new List<int>();
        return (new DeviceNotificationClient(events.Add, failures.Add), events, failures);
    }
}
