using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static Insomnia.Windows.Power.PowerNative;

namespace Insomnia.Windows.Power;

/// <summary>
/// Holds off idle sleep with two mechanisms that both die with the process:
/// the thread execution state, which is per thread and so must be set and
/// cleared on one long-lived thread, and a named system-required power
/// request, which is what <c>powercfg /requests</c> shows. Either one alone
/// blocks idle sleep, so the hold counts as acquired when at least one took.
/// </summary>
public sealed class KeepAwake : IKeepAwake, IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly BlockingCollection<Command> _commands = new();
    private Thread? _thread;
    private bool _disposed;
    private volatile bool _held;

    // Only touched on the owner thread.
    private SafePowerRequestHandle? _request;
    private bool _executionStateSet;

    public bool IsHeld => _held;

    public bool Acquire(string reason) => Send(new Command(reason));

    public void Release() => Send(new Command(null));

    /// <summary>Releases the hold and ends the owner thread.</summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (_thread is not null)
            {
                Send(new Command(null));
            }
            _disposed = true;
            _commands.CompleteAdding();
            thread = _thread;
        }
        if (thread is not null && !thread.Join(CallTimeout))
        {
            Log.Warn("Keep-awake: owner thread did not stop");
        }
    }

    private bool Send(Command command)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                Log.Warn("Keep-awake: used after dispose");
                return false;
            }
            if (_thread is null)
            {
                if (command.Reason is null)
                {
                    // Nothing was ever acquired, so there is nothing to clear.
                    return true;
                }
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "Insomnia keep-awake",
                };
                _thread.Start();
            }
            _commands.Add(command);
            if (!command.Done.Task.Wait(CallTimeout))
            {
                Log.Error("Keep-awake: owner thread did not answer");
                return false;
            }
            return command.Done.Task.Result;
        }
    }

    private void Run()
    {
        foreach (var command in _commands.GetConsumingEnumerable())
        {
            bool result;
            try
            {
                result = command.Reason is null ? DoRelease() : DoAcquire(command.Reason);
            }
            catch (Exception ex)
            {
                Log.Error("Keep-awake: call failed", ex);
                result = false;
            }
            _held = _request is not null || _executionStateSet;
            command.Done.TrySetResult(result);
        }
    }

    private bool DoAcquire(string reason)
    {
        // Set the new request before dropping the old one so the hold never lapses.
        var request = CreateRequest(reason);
        if (request is not null)
        {
            var previous = _request;
            _request = request;
            if (previous is not null)
            {
                ClearAndClose(previous);
            }
        }
        else if (_request is not null)
        {
            Log.Warn("Keep-awake: keeping the previous power request; its reason is stale");
        }

        if (SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) == 0)
        {
            Log.Warn("Keep-awake: SetThreadExecutionState failed");
        }
        else
        {
            _executionStateSet = true;
        }

        var held = _request is not null || _executionStateSet;
        if (!held)
        {
            Log.Error("Keep-awake: neither the power request nor the execution state could be set");
        }
        return held;
    }

    private bool DoRelease()
    {
        var ok = true;
        if (_request is not null)
        {
            ClearAndClose(_request);
            _request = null;
        }
        if (_executionStateSet)
        {
            if (SetThreadExecutionState(ES_CONTINUOUS) == 0)
            {
                Log.Warn("Keep-awake: clearing the execution state failed");
                ok = false;
            }
            else
            {
                _executionStateSet = false;
            }
        }
        return ok;
    }

    private static unsafe SafePowerRequestHandle? CreateRequest(string reason)
    {
        SafePowerRequestHandle request;
        fixed (char* text = reason)
        {
            var context = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = text,
            };
            request = PowerCreateRequest(&context);
        }
        if (request.IsInvalid)
        {
            Log.Warn($"Keep-awake: PowerCreateRequest failed (error {Marshal.GetLastPInvokeError()})");
            request.Dispose();
            return null;
        }
        if (!PowerSetRequest(request, PowerRequestSystemRequired))
        {
            Log.Warn($"Keep-awake: PowerSetRequest failed (error {Marshal.GetLastPInvokeError()})");
            request.Dispose();
            return null;
        }
        return request;
    }

    /// <summary>Closing the handle drops the request even when clearing it failed.</summary>
    private static void ClearAndClose(SafePowerRequestHandle request)
    {
        if (!PowerClearRequest(request, PowerRequestSystemRequired))
        {
            Log.Warn($"Keep-awake: PowerClearRequest failed (error {Marshal.GetLastPInvokeError()}); closing the handle instead");
        }
        request.Dispose();
    }

    /// <summary>A null reason means release.</summary>
    private sealed class Command(string? reason)
    {
        public string? Reason { get; } = reason;

        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
