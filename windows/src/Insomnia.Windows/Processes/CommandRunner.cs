using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;

namespace Insomnia.Windows.Processes;

/// <summary>
/// Runs an external program with an argument list (never a shell string, so
/// nothing is reinterpreted by <c>cmd.exe</c>), no window, and UTF-8 output.
/// A timeout or cancellation kills the whole process tree, since tools such
/// as <c>wsl.exe</c> and <c>docker.exe</c> do their work in child processes.
/// </summary>
public sealed class CommandRunner : ICommandRunner
{
    /// <summary>How long to wait for output pipes to close after the process exited or was killed.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be zero or more, or infinite.");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return new CommandResult(-1, string.Empty, "cancelled before start", TimedOut: false);
        }

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new CommandResult(-1, string.Empty, $"{fileName} did not start", TimedOut: false);
            }
        }
        catch (Win32Exception ex)
        {
            Log.Warn($"command: could not start {fileName}: {ex.Message}");
            return new CommandResult(-1, string.Empty, ex.Message, TimedOut: false);
        }

        // No input is ever sent; closing stdin keeps a tool that reads it from waiting forever.
        process.StandardInput.Close();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readers = Task.WhenAll(
            DrainAsync(process.StandardOutput, stdout),
            DrainAsync(process.StandardError, stderr));
        // A reader abandoned after the grace period may still fault later; observe it.
        _ = readers.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process, fileName);
            await WaitQuietly(readers).ConfigureAwait(false);
            var cancelled = cancellationToken.IsCancellationRequested;
            Log.Warn(cancelled ? $"command: {fileName} cancelled" : $"command: {fileName} timed out after {timeout.TotalSeconds:0.#} s");
            return new CommandResult(-1, Snapshot(stdout), Snapshot(stderr), TimedOut: !cancelled);
        }

        // A grandchild that inherited the pipes can keep them open after the
        // process itself exited; take what arrived and close the rest down.
        if (!await WaitQuietly(readers).ConfigureAwait(false))
        {
            KillTree(process, fileName);
        }
        return new CommandResult(process.ExitCode, Snapshot(stdout), Snapshot(stderr), TimedOut: false);
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
            lock (sink)
            {
                sink.Append(buffer, 0, read);
            }
        }
    }

    /// <summary>True when the readers finished within the grace period.</summary>
    private static async Task<bool> WaitQuietly(Task readers)
    {
        try
        {
            await readers.WaitAsync(DrainGrace).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            // The pipe broke when the tree was killed; what was read is kept.
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static string Snapshot(StringBuilder sink)
    {
        lock (sink)
        {
            return sink.ToString();
        }
    }

    private static void KillTree(Process process, string fileName)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception ex)
        {
            Log.Warn($"command: could not kill {fileName}: {ex.Message}");
        }
        catch (AggregateException ex)
        {
            Log.Warn($"command: could not kill every process under {fileName}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
