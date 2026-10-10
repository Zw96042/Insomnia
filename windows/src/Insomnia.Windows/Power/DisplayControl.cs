using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static Insomnia.Windows.Power.PowerNative;

namespace Insomnia.Windows.Power;

/// <summary>
/// Best-effort display power through <c>SC_MONITORPOWER</c>.
/// </summary>
/// <remarks>
/// The request goes to an invisible top-level window Insomnia owns, on its
/// own thread, where <c>DefWindowProc</c> carries it out. That avoids the
/// usual <c>HWND_BROADCAST</c>, which makes every top-level window on the
/// desktop process the command and can stall on a hung one; posting also
/// means the engine never waits. The window is a hidden popup rather than
/// message-only because <c>SC_MONITORPOWER</c> sent to message-only windows
/// is widely reported to be ignored. If the window cannot be created, a broadcast with
/// <c>SMTO_ABORTIFHUNG</c> and a 2 s timeout is the fallback. Waking adds a
/// one-shot display-required execution state and a zero-distance mouse move,
/// because recent builds ignore <c>SC_MONITORPOWER</c> on (-1) and wake the
/// panel only on input.
/// </remarks>
public sealed class DisplayControl : IDisplayControl, IDisposable
{
    private const nint MonitorOff = 2;
    private const nint MonitorOn = -1;
    private const uint BroadcastTimeoutMilliseconds = 2000;

    private readonly object _gate = new();
    private WindowThread? _window;
    private bool _windowFailed;
    private bool _disposed;

    public void RequestDisplaysOff()
    {
        try
        {
            SendMonitorPower(MonitorOff);
        }
        catch (Exception ex)
        {
            Log.Warn($"Display: turning displays off failed: {ex.Message}");
        }
    }

    public void WakeDisplays()
    {
        try
        {
            SendMonitorPower(MonitorOn);
            if (SetThreadExecutionState(ES_DISPLAY_REQUIRED) == 0)
            {
                Log.Warn("Display: SetThreadExecutionState(display required) failed");
            }
            NudgeInput();
        }
        catch (Exception ex)
        {
            Log.Warn($"Display: waking displays failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _window?.Dispose();
            _window = null;
        }
    }

    private void SendMonitorPower(nint state)
    {
        var window = Window();
        if (window is not null && window.Post(WM_SYSCOMMAND, SC_MONITORPOWER, state))
        {
            return;
        }
        if (window is not null)
        {
            Log.Warn($"Display: posting to the display window failed (error {Marshal.GetLastPInvokeError()}); broadcasting");
        }
        if (SendMessageTimeoutW(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, state, SMTO_ABORTIFHUNG, BroadcastTimeoutMilliseconds, out _) == 0)
        {
            Log.Warn($"Display: SC_MONITORPOWER broadcast failed (error {Marshal.GetLastPInvokeError()})");
        }
    }

    private WindowThread? Window()
    {
        lock (_gate)
        {
            if (_disposed || _windowFailed)
            {
                return null;
            }
            if (_window is null)
            {
                var window = new WindowThread("Display", messageOnly: false, (_, _, _, _) => null);
                if (!window.Start())
                {
                    window.Dispose();
                    _windowFailed = true;
                    return null;
                }
                _window = window;
            }
            return _window;
        }
    }

    private static unsafe void NudgeInput()
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE },
        };
        if (SendInput(1, &input, sizeof(INPUT)) != 1)
        {
            // Fails when another desktop (lock screen, UAC) has input; the
            // execution state above still keeps the panel from idling off.
            Log.Info($"Display: input nudge was not delivered (error {Marshal.GetLastPInvokeError()})");
        }
    }
}
