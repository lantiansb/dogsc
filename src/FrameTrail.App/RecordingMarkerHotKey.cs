using System.Runtime.InteropServices;

namespace FrameTrail.App;

internal sealed class RecordingMarkerHotKey : IDisposable
{
    internal const int Id = 17;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyM = 0x4D;
    private const int ErrorHotKeyAlreadyRegistered = 1409;

    private readonly Func<IntPtr, int, uint, uint, bool> registerHotKey;
    private readonly Func<IntPtr, int, bool> unregisterHotKey;
    private readonly Func<int> getLastError;
    private IntPtr registeredWindow;
    private bool registered;
    private bool disposed;

    internal RecordingMarkerHotKey()
        : this(RegisterNative, UnregisterNative, Marshal.GetLastWin32Error)
    {
    }

    internal RecordingMarkerHotKey(
        Func<IntPtr, int, uint, uint, bool> registerHotKey,
        Func<IntPtr, int, bool> unregisterHotKey,
        Func<int> getLastError)
    {
        this.registerHotKey = registerHotKey ?? throw new ArgumentNullException(nameof(registerHotKey));
        this.unregisterHotKey = unregisterHotKey ?? throw new ArgumentNullException(nameof(unregisterHotKey));
        this.getLastError = getLastError ?? throw new ArgumentNullException(nameof(getLastError));
    }

    internal string? ShortcutText { get; private set; }
    internal bool IsFallback { get; private set; }
    internal int RegistrationError { get; private set; }

    internal bool Register(IntPtr hwnd)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (hwnd == IntPtr.Zero) throw new ArgumentException("A window handle is required.", nameof(hwnd));
        if (registered && registeredWindow == hwnd) return true;
        if (registered) ReleaseRegistration();

        ShortcutText = null;
        IsFallback = false;
        RegistrationError = 0;

        if (TryRegister(hwnd, ModControl | ModAlt | ModNoRepeat, out var error))
        {
            registered = true;
            registeredWindow = hwnd;
            ShortcutText = "Ctrl+Alt+M";
            return true;
        }

        if (error != ErrorHotKeyAlreadyRegistered)
        {
            RegistrationError = error;
            return false;
        }

        if (TryRegister(hwnd, ModControl | ModAlt | ModShift | ModNoRepeat, out error))
        {
            registered = true;
            registeredWindow = hwnd;
            IsFallback = true;
            ShortcutText = "Ctrl+Alt+Shift+M";
            return true;
        }

        RegistrationError = error;
        return false;
    }

    /// <summary>
    /// Releases the system-wide key while keeping the object reusable. Mac RecordingMarkerHotKey registers the key
    /// only while a recording is running, so other applications can use Ctrl+Alt+M the rest of the time.
    /// </summary>
    internal void Unregister()
    {
        if (disposed || !registered) return;
        ReleaseRegistration();
    }

    internal bool IsRegistered => registered;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (registered) ReleaseRegistration();
    }

    private bool TryRegister(IntPtr hwnd, uint modifiers, out int error)
    {
        if (registerHotKey(hwnd, Id, modifiers, VirtualKeyM))
        {
            error = 0;
            return true;
        }

        error = getLastError();
        return false;
    }

    private void ReleaseRegistration()
    {
        var hwnd = registeredWindow;
        registered = false;
        registeredWindow = IntPtr.Zero;
        ShortcutText = null;
        IsFallback = false;
        unregisterHotKey(hwnd, Id);
    }

    private static bool RegisterNative(IntPtr hwnd, int id, uint modifiers, uint key) =>
        NativeRegisterHotKey(hwnd, id, modifiers, key);

    private static bool UnregisterNative(IntPtr hwnd, int id) => NativeUnregisterHotKey(hwnd, id);

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    private static extern bool NativeRegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
    private static extern bool NativeUnregisterHotKey(IntPtr hwnd, int id);
}
