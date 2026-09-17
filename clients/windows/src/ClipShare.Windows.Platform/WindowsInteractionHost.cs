namespace ClipShare.Windows.Platform;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsInteractionHost : IDisposable
{
    private const uint ClipboardUpdateMessage = 0x031D;
    private const uint HotKeyMessage = 0x0312;
    private const uint SessionChangeMessage = 0x02B1;
    private const nuint SessionLock = 0x7;
    private const uint TrayMessage = 0x8001;
    private const uint LeftButtonUp = 0x0202;
    private const uint LeftButtonDoubleClick = 0x0203;
    private const int HotKeyId = 0xC203;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierShift = 0x0004;
    private const uint NoRepeat = 0x4000;
    private const uint VirtualKeyV = 0x56;
    private const uint NotifyAdd = 0x00000000;
    private const uint NotifyDelete = 0x00000002;
    private const uint NotifyIconMessage = 0x00000001;
    private const uint NotifyIcon = 0x00000002;
    private const uint NotifyTip = 0x00000004;
    private const uint NotifyVersion = 0x00000004;
    private const uint NotifySetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint SessionNotifyThisSession = 0;
    private const nuint SubclassId = 0xC203;
    private readonly SubclassProcedure procedure;
    private IntPtr windowHandle;
    private bool clipboardEnabled;
    private bool hotKeyRegistered;
    private bool trayAdded;
    private bool started;
    private bool disposed;

    public WindowsInteractionHost() => procedure = WindowProcedure;

    public event EventHandler? ClipboardChanged;

    public event EventHandler? HotKeyPressed;

    public event EventHandler? TrayActivated;

    public event EventHandler? SessionLocked;

    public void Start(IntPtr windowHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started || windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows interaction host has an invalid start state.");
        }

        this.windowHandle = windowHandle;
        if (!SetWindowSubclass(windowHandle, procedure, SubclassId, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the ClipShare window message adapter.");
        }

        started = true;
        try
        {
            hotKeyRegistered = RegisterHotKey(
                windowHandle,
                HotKeyId,
                ModifierControl | ModifierShift | NoRepeat,
                VirtualKeyV);
            if (!WTSRegisterSessionNotification(windowHandle, SessionNotifyThisSession))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not subscribe to Windows session events.");
            }

            AddTrayIcon();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool HotKeyAvailable => hotKeyRegistered;

    public void SetClipboardMonitoring(bool enabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
        {
            throw new InvalidOperationException("Windows interaction host is not started.");
        }

        if (enabled == clipboardEnabled)
        {
            return;
        }

        bool success = enabled
            ? AddClipboardFormatListener(windowHandle)
            : RemoveClipboardFormatListener(windowHandle);
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not update clipboard monitoring.");
        }

        clipboardEnabled = enabled;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (started)
        {
            if (clipboardEnabled)
            {
                _ = RemoveClipboardFormatListener(windowHandle);
                clipboardEnabled = false;
            }

            if (hotKeyRegistered)
            {
                _ = UnregisterHotKey(windowHandle, HotKeyId);
                hotKeyRegistered = false;
            }

            _ = WTSUnRegisterSessionNotification(windowHandle);
            RemoveTrayIcon();
            _ = RemoveWindowSubclass(windowHandle, procedure, SubclassId);
            started = false;
        }
    }

    private IntPtr WindowProcedure(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclass,
        nuint reference)
    {
        _ = subclass;
        _ = reference;
        if (message == ClipboardUpdateMessage && clipboardEnabled)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (message == HotKeyMessage && wParam == HotKeyId)
        {
            HotKeyPressed?.Invoke(this, EventArgs.Empty);
        }
        else if (message == SessionChangeMessage && (nuint)wParam == SessionLock)
        {
            SessionLocked?.Invoke(this, EventArgs.Empty);
        }
        else if (message == TrayMessage && ((uint)(nuint)lParam is LeftButtonUp or LeftButtonDoubleClick))
        {
            TrayActivated?.Invoke(this, EventArgs.Empty);
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void AddTrayIcon()
    {
        NotifyIconData data = CreateNotifyIconData();
        if (!ShellNotifyIcon(NotifyAdd, ref data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the ClipShare tray icon.");
        }

        trayAdded = true;
        data.VersionOrTimeout = NotifyIconVersion4;
        _ = ShellNotifyIcon(NotifySetVersion, ref data);
    }

    private void RemoveTrayIcon()
    {
        if (!trayAdded)
        {
            return;
        }

        NotifyIconData data = CreateNotifyIconData();
        _ = ShellNotifyIcon(NotifyDelete, ref data);
        trayAdded = false;
    }

    private NotifyIconData CreateNotifyIconData()
    {
        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = windowHandle,
            Id = 1,
            Flags = NotifyIconMessage | NotifyIcon | NotifyTip,
            CallbackMessage = TrayMessage,
            Icon = LoadIcon(IntPtr.Zero, new IntPtr(0x7F00)),
        };
        data.SetTip("ClipShare 加密内容库");
        return data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProcedure(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclass,
        nuint reference);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;

        public fixed char Tip[128];

        public uint State;
        public uint StateMask;

        public fixed char Info[256];

        public uint VersionOrTimeout;

        public fixed char InfoTitle[64];

        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;

        public void SetTip(string value)
        {
            fixed (char* destination = Tip)
            {
                int length = Math.Min(value.Length, 127);
                value.AsSpan(0, length).CopyTo(new Span<char>(destination, 128));
                destination[length] = '\0';
            }
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(
        IntPtr window,
        SubclassProcedure procedure,
        nuint subclass,
        nuint reference);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(
        IntPtr window,
        SubclassProcedure procedure,
        nuint subclass);

    [LibraryImport("comctl32.dll")]
    private static partial IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(IntPtr window, uint flags);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(IntPtr window);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);
}
