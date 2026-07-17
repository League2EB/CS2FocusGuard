using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using CS2FocusGuard.Core;

namespace CS2FocusGuard.App;

internal interface IManagedAudioSessionController : IAudioSessionController, IDisposable
{
}

internal static class AudioControllerLoader
{
    internal static async Task<IManagedAudioSessionController> CreateAsync(
        Func<IManagedAudioSessionController> factory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var creation = Task.Run(factory, CancellationToken.None);

        try
        {
            return await creation.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            DisposeAfterCompletion(creation);
            throw new TimeoutException(
                $"Audio initialization did not complete within {timeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            DisposeAfterCompletion(creation);
            throw;
        }
    }

    private static void DisposeAfterCompletion(
        Task<IManagedAudioSessionController> creation) =>
        _ = creation.ContinueWith(
            task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    task.Result.Dispose();
                }
                else
                {
                    _ = task.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

internal sealed class WindowsAudioSessionController
    : IManagedAudioSessionController
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);
    private readonly MtaDispatcher _dispatcher = new();
    private readonly Dictionary<string, EndpointRegistration> _endpoints =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceNotificationClient _deviceNotification;
    private IMMDeviceEnumerator? _deviceEnumerator;
    private bool _disposed;

    internal WindowsAudioSessionController()
    {
        _deviceNotification = new DeviceNotificationClient(this);
        try
        {
            _dispatcher.Invoke(
                () =>
                {
                    _deviceEnumerator =
                        (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
                    Marshal.ThrowExceptionForHR(
                        _deviceEnumerator.RegisterEndpointNotificationCallback(
                            _deviceNotification));
                    RefreshEndpoints();
                });
        }
        catch
        {
            _dispatcher.Invoke(ReleaseResources);
            _dispatcher.Dispose();
            throw;
        }
    }

    public event EventHandler? SessionsChanged;

    public event EventHandler<AudioMuteChanged>? MuteChanged;

    public Task<IReadOnlyList<AudioSession>> GetSessionsAsync(
        CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync<IReadOnlyList<AudioSession>>(
            () =>
            {
                RefreshEndpoints();
                var sessions = new List<AudioSession>();
                foreach (var endpoint in _endpoints.Values)
                {
                    endpoint.RefreshSessions();
                    sessions.AddRange(endpoint.GetSessions());
                }

                return sessions;
            },
            cancellationToken);

    public Task SetMuteAsync(
        AudioSessionKey key,
        bool muted,
        Guid eventContext,
        CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(
            () =>
            {
                RefreshEndpoints();
                if (!_endpoints.TryGetValue(key.EndpointId, out var endpoint))
                {
                    throw new InvalidOperationException(
                        "The audio output device is no longer available.");
                }

                if (!endpoint.TrySetMute(key, muted, eventContext))
                {
                    endpoint.RefreshSessions();
                    if (!endpoint.TrySetMute(key, muted, eventContext))
                    {
                        throw new InvalidOperationException(
                            "The audio session is no longer available.");
                    }
                }
            },
            cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _dispatcher.Invoke(ReleaseResources, DisposeTimeout);
        }
        catch (TimeoutException)
        {
        }

        _dispatcher.Dispose();
    }

    private void ReleaseResources()
    {
        if (_deviceEnumerator is not null)
        {
            _ = _deviceEnumerator.UnregisterEndpointNotificationCallback(
                _deviceNotification);
        }

        foreach (var endpoint in _endpoints.Values)
        {
            endpoint.Dispose();
        }

        _endpoints.Clear();
        Com.Release(_deviceEnumerator);
        _deviceEnumerator = null;
    }

    private void RefreshEndpoints()
    {
        if (_deviceEnumerator is null)
        {
            return;
        }

        IMMDeviceCollection? collection = null;
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Marshal.ThrowExceptionForHR(
                _deviceEnumerator.EnumAudioEndpoints(
                    EDataFlow.Render,
                    DeviceState.Active,
                    out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));

            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    Marshal.ThrowExceptionForHR(collection.Item(index, out device));
                    var id = GetDeviceId(device);
                    discovered.Add(id);
                    if (!_endpoints.ContainsKey(id))
                    {
                        _endpoints[id] = new EndpointRegistration(
                            id,
                            device,
                            QueueSessionCreated,
                            OnMuteChanged);
                        device = null;
                    }
                }
                catch (COMException)
                {
                }
                finally
                {
                    Com.Release(device);
                }
            }
        }
        finally
        {
            Com.Release(collection);
        }

        foreach (var id in _endpoints.Keys.Where(id => !discovered.Contains(id)).ToArray())
        {
            _endpoints[id].Dispose();
            _endpoints.Remove(id);
        }
    }

    private static string GetDeviceId(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var pointer));
        try
        {
            return Marshal.PtrToStringUni(pointer)
                ?? throw new InvalidOperationException(
                    "Windows returned an empty audio device identifier.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private void QueueTopologyRefresh()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.Post(
            () =>
            {
                try
                {
                    RefreshEndpoints();
                    SessionsChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (COMException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            });
    }

    private void OnSessionCreated() =>
        SessionsChanged?.Invoke(this, EventArgs.Empty);

    private void QueueSessionCreated(
        string endpointId,
        IAudioSessionControl session)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.Post(
            () =>
            {
                try
                {
                    if (_endpoints.TryGetValue(endpointId, out var endpoint))
                    {
                        endpoint.RegisterSession(session);
                        OnSessionCreated();
                    }
                }
                catch (COMException)
                {
                }
                catch (InvalidCastException)
                {
                }
            });
    }

    private void OnMuteChanged(AudioMuteChanged change) =>
        MuteChanged?.Invoke(this, change);

    private sealed class EndpointRegistration : IDisposable
    {
        private readonly string _endpointId;
        private readonly IMMDevice _device;
        private readonly IAudioSessionManager2 _manager;
        private readonly SessionNotificationClient _sessionNotification;
        private readonly Action<string, IAudioSessionControl> _sessionCreated;
        private readonly Action<AudioMuteChanged> _muteChanged;
        private readonly Dictionary<string, SessionRegistration> _sessions =
            new(StringComparer.Ordinal);
        private bool _disposed;

        internal EndpointRegistration(
            string endpointId,
            IMMDevice device,
            Action<string, IAudioSessionControl> sessionCreated,
            Action<AudioMuteChanged> muteChanged)
        {
            _endpointId = endpointId;
            _device = device;
            _sessionCreated = sessionCreated;
            _muteChanged = muteChanged;
            _manager = ActivateSessionManager(device);
            _sessionNotification = new SessionNotificationClient(this);
            Marshal.ThrowExceptionForHR(
                _manager.RegisterSessionNotification(_sessionNotification));
            RefreshSessions();
        }

        internal void RefreshSessions()
        {
            if (_disposed)
            {
                return;
            }

            IAudioSessionEnumerator? enumerator = null;
            var discovered = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                Marshal.ThrowExceptionForHR(_manager.GetSessionEnumerator(out enumerator));
                Marshal.ThrowExceptionForHR(enumerator.GetCount(out var count));
                for (var index = 0; index < count; index++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(
                            enumerator.GetSession(index, out control));
                        var control2 = (IAudioSessionControl2)control;
                        var sessionId = GetSessionInstanceId(control2);
                        discovered.Add(sessionId);
                        if (!_sessions.ContainsKey(sessionId))
                        {
                            _sessions[sessionId] = new SessionRegistration(
                                _endpointId,
                                sessionId,
                                control,
                                _muteChanged);
                            control = null;
                        }
                    }
                    catch (COMException)
                    {
                    }
                    finally
                    {
                        Com.Release(control);
                    }
                }
            }
            finally
            {
                Com.Release(enumerator);
            }

            foreach (var id in _sessions.Keys.Where(id => !discovered.Contains(id)).ToArray())
            {
                _sessions[id].Dispose();
                _sessions.Remove(id);
            }
        }

        internal List<AudioSession> GetSessions()
        {
            var sessions = new List<AudioSession>();
            foreach (var session in _sessions.Values)
            {
                try
                {
                    sessions.Add(session.Read());
                }
                catch (COMException)
                {
                }
            }

            return sessions;
        }

        internal bool TrySetMute(
            AudioSessionKey key,
            bool muted,
            Guid eventContext)
        {
            if (!_sessions.TryGetValue(key.SessionId, out var session))
            {
                return false;
            }

            session.SetMute(muted, eventContext);
            return true;
        }

        internal void RegisterSession(IAudioSessionControl control)
        {
            var control2 = (IAudioSessionControl2)control;
            var sessionId = GetSessionInstanceId(control2);
            if (!_sessions.ContainsKey(sessionId))
            {
                _sessions[sessionId] = new SessionRegistration(
                    _endpointId,
                    sessionId,
                    control,
                    _muteChanged);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = _manager.UnregisterSessionNotification(_sessionNotification);
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
            Com.Release(_manager);
            Com.Release(_device);
        }

        private static IAudioSessionManager2 ActivateSessionManager(IMMDevice device)
        {
            var interfaceId = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(
                device.Activate(
                    ref interfaceId,
                    ClsCtx.All,
                    IntPtr.Zero,
                    out var instance));
            return (IAudioSessionManager2)instance;
        }

        private static string GetSessionInstanceId(IAudioSessionControl2 control)
        {
            Marshal.ThrowExceptionForHR(
                control.GetSessionInstanceIdentifier(out var pointer));
            try
            {
                return Marshal.PtrToStringUni(pointer)
                    ?? throw new InvalidOperationException(
                        "Windows returned an empty audio session identifier.");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        private sealed class SessionNotificationClient(EndpointRegistration owner)
            : IAudioSessionNotification
        {
            public int OnSessionCreated(IAudioSessionControl newSession)
            {
                owner._sessionCreated(owner._endpointId, newSession);
                return 0;
            }
        }
    }

    private sealed class SessionRegistration : IDisposable
    {
        private readonly AudioSessionKey _key;
        private readonly IAudioSessionControl _control;
        private readonly IAudioSessionControl2 _control2;
        private readonly ISimpleAudioVolume _volume;
        private readonly AudioSessionEventsClient _events;
        private readonly Action<AudioMuteChanged> _muteChanged;
        private readonly string _applicationId;
        private readonly string _displayName;
        private bool _disposed;

        internal SessionRegistration(
            string endpointId,
            string sessionId,
            IAudioSessionControl control,
            Action<AudioMuteChanged> muteChanged)
        {
            _key = new AudioSessionKey(endpointId, sessionId);
            _control = control;
            _control2 = (IAudioSessionControl2)control;
            _volume = (ISimpleAudioVolume)control;
            _muteChanged = muteChanged;
            _applicationId = GetApplicationId(_control2);
            _displayName = GetDisplayName(_control, _applicationId);
            _events = new AudioSessionEventsClient(this);
            Marshal.ThrowExceptionForHR(_control.RegisterAudioSessionNotification(_events));
        }

        internal AudioSession Read()
        {
            Marshal.ThrowExceptionForHR(_volume.GetMute(out var muted));
            return new AudioSession(
                _key,
                _applicationId,
                _displayName,
                muted);
        }

        internal void SetMute(bool muted, Guid eventContext) =>
            Marshal.ThrowExceptionForHR(
                _volume.SetMute(muted, ref eventContext));

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = _control.UnregisterAudioSessionNotification(_events);
            Com.Release(_control);
        }

        private static string GetApplicationId(IAudioSessionControl2 control)
        {
            var systemSoundsResult = control.IsSystemSoundsSession();
            Marshal.ThrowExceptionForHR(systemSoundsResult);
            if (systemSoundsResult == 0)
            {
                return AudioApplicationIdentity.SystemSounds;
            }

            Marshal.ThrowExceptionForHR(control.GetProcessId(out var processId));
            if (processId == 0)
            {
                return $"unknown:{GetSessionIdentifier(control)}";
            }

            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                return AudioApplicationIdentity.Normalize(process.ProcessName);
            }
            catch (ArgumentException)
            {
                return $"unknown:{processId}";
            }
            catch (InvalidOperationException)
            {
                return $"unknown:{processId}";
            }
        }

        private static string GetDisplayName(
            IAudioSessionControl control,
            string applicationId)
        {
            if (applicationId == AudioApplicationIdentity.SystemSounds)
            {
                return "Windows";
            }

            if (control.GetDisplayName(out var pointer) >= 0)
            {
                try
                {
                    var displayName = Marshal.PtrToStringUni(pointer);
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        return displayName;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }

            return applicationId;
        }

        private static string GetSessionIdentifier(IAudioSessionControl2 control)
        {
            Marshal.ThrowExceptionForHR(control.GetSessionIdentifier(out var pointer));
            try
            {
                return Marshal.PtrToStringUni(pointer) ?? "unknown";
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        private void NotifyMuteChanged(bool muted, Guid eventContext) =>
            _muteChanged(
                new AudioMuteChanged(
                    _key,
                    _applicationId,
                    muted,
                    eventContext));

        private sealed class AudioSessionEventsClient(SessionRegistration owner)
            : IAudioSessionEvents
        {
            public int OnDisplayNameChanged(string newDisplayName, ref Guid eventContext) => 0;

            public int OnIconPathChanged(string newIconPath, ref Guid eventContext) => 0;

            public int OnSimpleVolumeChanged(
                float newVolume,
                bool newMute,
                ref Guid eventContext)
            {
                owner.NotifyMuteChanged(newMute, eventContext);
                return 0;
            }

            public int OnChannelVolumeChanged(
                uint channelCount,
                IntPtr newChannelVolumeArray,
                uint changedChannel,
                ref Guid eventContext) => 0;

            public int OnGroupingParamChanged(
                ref Guid newGroupingParam,
                ref Guid eventContext) => 0;

            public int OnStateChanged(AudioSessionState newState) => 0;

            public int OnSessionDisconnected(AudioSessionDisconnectReason reason) => 0;
        }
    }

    private sealed class DeviceNotificationClient(WindowsAudioSessionController owner)
        : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            owner.QueueTopologyRefresh();
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            owner.QueueTopologyRefresh();
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            owner.QueueTopologyRefresh();
            return 0;
        }

        public int OnDefaultDeviceChanged(
            EDataFlow flow,
            ERole role,
            string defaultDeviceId)
        {
            owner.QueueTopologyRefresh();
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}

internal sealed class MtaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private bool _disposed;

    internal MtaDispatcher()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Windows audio controller"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    internal Task<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(operation());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            cancellationToken);
        return completion.Task;
    }

    internal async Task InvokeAsync(
        Action operation,
        CancellationToken cancellationToken)
    {
        await InvokeAsync(
            () =>
            {
                operation();
                return true;
            },
            cancellationToken);
    }

    internal void Invoke(Action operation) =>
        InvokeAsync(operation, CancellationToken.None).GetAwaiter().GetResult();

    internal void Invoke(Action operation, TimeSpan timeout) =>
        InvokeAsync(operation, CancellationToken.None)
            .WaitAsync(timeout)
            .GetAwaiter()
            .GetResult();

    internal void Post(Action operation)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _queue.Add(operation);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var operation in _queue.GetConsumingEnumerable())
        {
            operation();
        }
    }
}

internal static class Com
{
    internal static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.ReleaseComObject(instance);
        }
    }
}

internal enum EDataFlow
{
    Render,
    Capture,
    All
}

internal enum ERole
{
    Console,
    Multimedia,
    Communications
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x1
}

[Flags]
internal enum ClsCtx : uint
{
    InprocServer = 0x1,
    InprocHandler = 0x2,
    LocalServer = 0x4,
    RemoteServer = 0x10,
    All = InprocServer | InprocHandler | LocalServer | RemoteServer
}

internal enum AudioSessionState
{
    Inactive,
    Active,
    Expired
}

internal enum AudioSessionDisconnectReason
{
    DeviceRemoval,
    ServerShutdown,
    FormatChanged,
    SessionLogoff,
    SessionDisconnected,
    ExclusiveModeOverride
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    internal Guid FormatId;
    internal uint PropertyId;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject;

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(
        EDataFlow dataFlow,
        DeviceState stateMask,
        out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(
        EDataFlow dataFlow,
        ERole role,
        out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint deviceCount);

    [PreserveSig]
    int Item(uint deviceIndex, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(
        ref Guid interfaceId,
        ClsCtx classContext,
        IntPtr activationParameters,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(uint storageAccess, out IntPtr properties);

    [PreserveSig]
    int GetId(out IntPtr id);

    [PreserveSig]
    int GetState(out DeviceState state);
}

[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        DeviceState newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(
        EDataFlow flow,
        ERole role,
        [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        PropertyKey key);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(
        ref Guid audioSessionGuid,
        uint streamFlags,
        out IAudioSessionControl sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(
        ref Guid audioSessionGuid,
        uint streamFlags,
        out ISimpleAudioVolume audioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

    [PreserveSig]
    int RegisterSessionNotification(IAudioSessionNotification sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(IAudioSessionNotification sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification(
        [MarshalAs(UnmanagedType.LPWStr)] string sessionId,
        IntPtr duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    [PreserveSig]
    int GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IAudioSessionControl
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName(out IntPtr displayName);

    [PreserveSig]
    int SetDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string displayName,
        ref Guid eventContext);

    [PreserveSig]
    int GetIconPath(out IntPtr iconPath);

    [PreserveSig]
    int SetIconPath(
        [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
        ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IAudioSessionEvents client);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IAudioSessionEvents client);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName(out IntPtr displayName);

    [PreserveSig]
    int SetDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string displayName,
        ref Guid eventContext);

    [PreserveSig]
    int GetIconPath(out IntPtr iconPath);

    [PreserveSig]
    int SetIconPath(
        [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
        ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IAudioSessionEvents client);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IAudioSessionEvents client);

    [PreserveSig]
    int GetSessionIdentifier(out IntPtr sessionId);

    [PreserveSig]
    int GetSessionInstanceIdentifier(out IntPtr sessionInstanceId);

    [PreserveSig]
    int GetProcessId(out uint processId);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference(bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
}

[ComImport]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IAudioSessionNotification
{
    [PreserveSig]
    int OnSessionCreated(IAudioSessionControl newSession);
}

[ComImport]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SuppressMessage(
    "Interoperability",
    "SYSLIB1096",
    Justification = "Windows Core Audio requires classic COM marshalling.")]
internal interface IAudioSessionEvents
{
    [PreserveSig]
    int OnDisplayNameChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string newDisplayName,
        ref Guid eventContext);

    [PreserveSig]
    int OnIconPathChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string newIconPath,
        ref Guid eventContext);

    [PreserveSig]
    int OnSimpleVolumeChanged(
        float newVolume,
        [MarshalAs(UnmanagedType.Bool)] bool newMute,
        ref Guid eventContext);

    [PreserveSig]
    int OnChannelVolumeChanged(
        uint channelCount,
        IntPtr newChannelVolumeArray,
        uint changedChannel,
        ref Guid eventContext);

    [PreserveSig]
    int OnGroupingParamChanged(
        ref Guid newGroupingParam,
        ref Guid eventContext);

    [PreserveSig]
    int OnStateChanged(AudioSessionState newState);

    [PreserveSig]
    int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason);
}
