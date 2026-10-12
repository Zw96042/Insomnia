using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using static Insomnia.Windows.Power.PowerNative;

namespace Insomnia.Windows.Power;

/// <summary>
/// A hidden window on its own STA thread with its own message loop, so
/// Windows can deliver messages to Insomnia without depending on whichever
/// UI framework the app uses. Every callback runs on that thread.
/// </summary>
internal sealed unsafe class WindowThread : IDisposable
{
    /// <summary>Handles a message; returns null to fall through to <c>DefWindowProc</c>.</summary>
    public delegate nint? MessageHandler(nint hwnd, uint message, nuint wParam, nint lParam);

    /// <summary>Asks the window to destroy itself from its own thread, which DestroyWindow requires.</summary>
    private const uint WM_SHUTDOWN = WM_APP + 0x3FF;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    // The window procedure is a static function pointer; each window lives on
    // its own thread, so the thread tells it which instance it serves.
    [ThreadStatic]
    private static WindowThread? t_current;

    private readonly object _gate = new();
    private readonly string _name;
    private readonly bool _messageOnly;
    private readonly MessageHandler _handler;
    private readonly Action<nint>? _onCreated;
    private readonly Action<nint>? _onDestroying;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private nint _hwnd;
    private bool _disposed;

    /// <param name="name">Thread and window name, for diagnostics.</param>
    /// <param name="messageOnly">
    /// True for an <c>HWND_MESSAGE</c> window (receives sent and posted
    /// messages, never broadcasts); false for an invisible top-level popup.
    /// </param>
    /// <param name="handler">Message handler, called on the window thread.</param>
    /// <param name="onCreated">Called on the window thread once the window exists, before the loop runs.</param>
    /// <param name="onDestroying">Called on the window thread while the window is being destroyed.</param>
    public WindowThread(string name, bool messageOnly, MessageHandler handler, Action<nint>? onCreated = null, Action<nint>? onDestroying = null)
    {
        _name = name;
        _messageOnly = messageOnly;
        _handler = handler;
        _onCreated = onCreated;
        _onDestroying = onDestroying;
    }

    /// <summary>The window handle, or zero before creation, after destruction, or when creation failed.</summary>
    public nint Handle => Volatile.Read(ref _hwnd);

    public bool IsWindowThread => Thread.CurrentThread == _thread;

    /// <summary>Starts the thread and waits for the window. Returns false when the window could not be created.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is null)
            {
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "Insomnia " + _name,
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }
        if (!_ready.Wait(StartTimeout))
        {
            Log.Error($"{_name}: window thread did not start within {StartTimeout.TotalSeconds:0} s");
            return false;
        }
        return Handle != 0;
    }

    public bool Post(uint message, nuint wParam, nint lParam)
    {
        var hwnd = Handle;
        return hwnd != 0 && PostMessageW(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            thread = _thread;
        }
        if (thread is null)
        {
            _ready.Dispose();
            return;
        }
        if (!_ready.Wait(StartTimeout))
        {
            // Creation is stuck; the background thread dies with the process.
            Log.Error($"{_name}: window thread never became ready; abandoning it");
            return;
        }
        var hwnd = Handle;
        if (hwnd != 0 && !PostMessageW(hwnd, WM_SHUTDOWN, 0, 0))
        {
            Log.Warn($"{_name}: could not post shutdown (error {Marshal.GetLastPInvokeError()})");
        }
        if (thread == Thread.CurrentThread)
        {
            // Disposed from inside a callback: the loop ends once this returns.
            return;
        }
        if (!thread.Join(StopTimeout))
        {
            Log.Warn($"{_name}: window thread did not stop within {StopTimeout.TotalSeconds:0} s");
            return;
        }
        _ready.Dispose();
    }

    private void Run()
    {
        t_current = this;
        var className = $"Insomnia.{_name}.{Guid.NewGuid():N}";
        var instance = GetModuleHandleW(null);
        var classRegistered = false;
        try
        {
            ushort atom;
            fixed (char* classNamePtr = className)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProc,
                    hInstance = instance,
                    lpszClassName = classNamePtr,
                };
                atom = RegisterClassExW(&wc);
            }
            if (atom == 0)
            {
                Log.Error($"{_name}: RegisterClassEx failed (error {Marshal.GetLastPInvokeError()})");
                return;
            }
            classRegistered = true;

            var hwnd = _messageOnly
                ? CreateWindowExW(0, className, _name, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, instance, 0)
                : CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, className, _name, WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
            if (hwnd == 0)
            {
                Log.Error($"{_name}: CreateWindowEx failed (error {Marshal.GetLastPInvokeError()})");
                return;
            }
            Volatile.Write(ref _hwnd, hwnd);
            try
            {
                _onCreated?.Invoke(hwnd);
            }
            catch (Exception ex)
            {
                Log.Error($"{_name}: window setup failed", ex);
            }
            _ready.Set();

            MSG msg;
            int result;
            while ((result = GetMessageW(&msg, 0, 0, 0)) != 0)
            {
                if (result == -1)
                {
                    Log.Error($"{_name}: GetMessage failed (error {Marshal.GetLastPInvokeError()})");
                    break;
                }
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        finally
        {
            var hwnd = Handle;
            if (hwnd != 0)
            {
                // The loop ended without WM_DESTROY (GetMessage failed).
                DestroyWindow(hwnd);
            }
            if (classRegistered && !UnregisterClassW(className, instance))
            {
                Log.Warn($"{_name}: UnregisterClass failed (error {Marshal.GetLastPInvokeError()})");
            }
            t_current = null;
            _ready.Set();
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        var self = t_current;
        if (self is null)
        {
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
        // An exception must never unwind into user32.
        try
        {
            switch (message)
            {
                case WM_SHUTDOWN:
                    DestroyWindow(hwnd);
                    return 0;
                case WM_DESTROY:
                    try
                    {
                        self._onDestroying?.Invoke(hwnd);
                    }
                    finally
                    {
                        Volatile.Write(ref self._hwnd, 0);
                        PostQuitMessage(0);
                    }
                    return 0;
                default:
                    var handled = self._handler(hwnd, message, wParam, lParam);
                    if (handled.HasValue)
                    {
                        return handled.Value;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{self._name}: message 0x{message:X4} handler failed", ex);
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
