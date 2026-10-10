using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static System.FormattableString;

namespace Insomnia.Windows.Processes;

/// <summary>
/// Per-process power throttling (EcoQoS) through
/// <c>Get/SetProcessInformation(ProcessPowerThrottling)</c>. The setting
/// lives only as long as the process, so nothing persistent is touched.
/// Identity is checked on the handle the call goes through.
/// </summary>
public sealed class ThrottleControl : IThrottleControl
{
    public unsafe (uint ControlMask, uint StateMask)? Get(int pid, long creationTime)
    {
        // Limited query access is enough on current builds; older ones want
        // full query access, so try that before giving up.
        foreach (var access in new[] { ProcessNative.ProcessQueryLimitedInformation, ProcessNative.ProcessQueryInformation })
        {
            if (ProcessHandles.OpenVerified(pid, creationTime, access, "throttle read", out var handle) != OpenOutcome.Opened)
            {
                return null;
            }
            using (handle)
            {
                var state = new ProcessNative.ProcessPowerThrottlingState { Version = ProcessNative.ProcessPowerThrottlingCurrentVersion };
                if (ProcessNative.GetProcessInformation(handle!, ProcessNative.ProcessPowerThrottlingClass, &state, (uint)sizeof(ProcessNative.ProcessPowerThrottlingState)))
                {
                    return (state.ControlMask, state.StateMask);
                }
                var error = Marshal.GetLastPInvokeError();
                if (error != ProcessNative.ErrorAccessDenied)
                {
                    // ERROR_INVALID_PARAMETER here means this build cannot report the state.
                    Log.Warn(Invariant($"throttle: reading power throttling of pid {pid} failed with error {error}"));
                    return null;
                }
            }
        }
        Log.Warn(Invariant($"throttle: reading power throttling of pid {pid} was denied"));
        return null;
    }

    public unsafe bool Set(int pid, long creationTime, uint controlMask, uint stateMask)
    {
        const uint access = ProcessNative.ProcessSetInformation | ProcessNative.ProcessQueryLimitedInformation;
        if (ProcessHandles.OpenVerified(pid, creationTime, access, "throttle write", out var handle) != OpenOutcome.Opened)
        {
            return false;
        }
        using (handle)
        {
            var state = new ProcessNative.ProcessPowerThrottlingState
            {
                Version = ProcessNative.ProcessPowerThrottlingCurrentVersion,
                ControlMask = controlMask,
                StateMask = stateMask,
            };
            if (ProcessNative.SetProcessInformation(handle!, ProcessNative.ProcessPowerThrottlingClass, &state, (uint)sizeof(ProcessNative.ProcessPowerThrottlingState)))
            {
                return true;
            }
            Log.Warn(Invariant($"throttle: setting power throttling of pid {pid} to control 0x{controlMask:X}, state 0x{stateMask:X} failed with error {Marshal.GetLastPInvokeError()}"));
            return false;
        }
    }
}
