using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;

namespace Insomnia.Windows.SystemServices;

/// <summary>
/// The recovery agent: a per-user Scheduled Task that runs
/// <c>Insomnia.exe --recover</c> at logon and every minute. It is managed
/// through <c>schtasks.exe</c> with a task XML, which a standard user may do
/// for a task that runs as themselves with least privilege.
/// </summary>
/// <remarks>
/// <c>schtasks</c> writes redirected output in the console's OEM code page
/// (whatever its XML declaration says), so output is decoded from bytes and
/// a path with characters that code page lacks is compared as it would
/// survive that trip. "Not found" is recognised in English and in the
/// system's own wording for the not-found errors, which <c>schtasks</c> uses
/// on localized Windows.
/// </remarks>
public sealed class RecoveryAgentTask : IRecoveryAgent
{
    public const string RecoverArgument = "--recover";

    /// <summary>
    /// Task names are machine-wide, so the user's SID is part of the name:
    /// two accounts on one PC each get their own task, and neither can
    /// overwrite the other's.
    /// </summary>
    public static string TaskNameFor(string userSid) => $@"\Insomnia\Recovery-{userSid}";

    /// <summary>The task name for the current user.</summary>
    public static string CurrentUserTaskName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return TaskNameFor(identity.User?.Value ?? identity.Name.Replace('\\', '-'));
    }

    private readonly string _taskName = CurrentUserTaskName();

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _executable;
    private readonly string _arguments = RecoverArgument;

    /// <param name="executablePath">The executable the task must run; normally the running app's own path.</param>
    public RecoveryAgentTask(string executablePath)
    {
        _executable = Path.GetFullPath(executablePath);
    }

    public RecoveryAgentState Check() => Query().State;

    public (RecoveryAgentState State, string? Error) Arm()
    {
        var (state, _) = Query();
        if (state == RecoveryAgentState.Armed)
        {
            return (state, null);
        }
        Log.Info($"Recovery agent: task reads {state}; registering it for {_executable}");
        var createError = Create();
        var (after, detail) = Query();
        if (after == RecoveryAgentState.Armed)
        {
            Log.Info("Recovery agent: armed");
            return (after, null);
        }
        var error = createError ?? detail ?? $"The recovery task still reads {after} after registering it.";
        Log.Error($"Recovery agent: not armed ({after}): {error}");
        return (after, error);
    }

    public bool Remove()
    {
        var result = Run("/Delete", "/TN", _taskName, "/F");
        if (result is null)
        {
            return false;
        }
        if (result.ExitCode == 0 || (!result.TimedOut && IsNotFound(result)))
        {
            Log.Info("Recovery agent: removed");
            return true;
        }
        Log.Error($"Recovery agent: removing the task failed: {Describe(result)}");
        return false;
    }

    private (RecoveryAgentState State, string? Detail) Query()
    {
        var result = Run("/Query", "/TN", _taskName, "/XML", "ONE");
        if (result is null)
        {
            return (RecoveryAgentState.Unknown, "schtasks.exe could not be run.");
        }
        if (result.TimedOut)
        {
            return (RecoveryAgentState.Unknown, Describe(result));
        }
        if (result.ExitCode != 0)
        {
            if (IsNotFound(result))
            {
                return (RecoveryAgentState.Missing, null);
            }
            Log.Warn($"Recovery agent: query failed: {Describe(result)}");
            return (RecoveryAgentState.Unknown, Describe(result));
        }
        return Parse(result.StandardOutput);
    }

    private (RecoveryAgentState State, string? Detail) Parse(string output)
    {
        var start = output.IndexOf('<', StringComparison.Ordinal);
        if (start < 0)
        {
            Log.Warn("Recovery agent: query printed no XML");
            return (RecoveryAgentState.Unknown, "The recovery task could not be read.");
        }
        XElement? task;
        try
        {
            task = XDocument.Parse(output[start..]).Root;
        }
        catch (XmlException ex)
        {
            Log.Warn($"Recovery agent: task XML did not parse: {ex.Message}");
            return (RecoveryAgentState.Unknown, "The recovery task could not be read.");
        }
        if (task is null)
        {
            return (RecoveryAgentState.Unknown, "The recovery task could not be read.");
        }

        // Settings/Enabled defaults to true when absent.
        var enabled = task.Element(Ns + "Settings")?.Element(Ns + "Enabled")?.Value.Trim();
        if (enabled is not null && !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) && enabled != "1")
        {
            return (RecoveryAgentState.Disabled, "The recovery task is disabled.");
        }

        var actions = task.Element(Ns + "Actions")?.Elements().ToList() ?? [];
        var exec = actions.Count == 1 && actions[0].Name == Ns + "Exec" ? actions[0] : null;
        var command = exec?.Element(Ns + "Command")?.Value;
        var arguments = exec?.Element(Ns + "Arguments")?.Value.Trim() ?? string.Empty;
        if (exec is null || !IsThisExecutable(command) || arguments != _arguments)
        {
            Log.Warn($"Recovery agent: task runs {command ?? "(no single program)"} {arguments}, not {_executable} {_arguments}");
            return (RecoveryAgentState.Mismatched, "The recovery task runs a different program.");
        }
        if (WhyItWouldNotRun(task) is { } reason)
        {
            Log.Warn($"Recovery agent: {reason}");
            return (RecoveryAgentState.Mismatched, $"The recovery task would not run: {reason}");
        }
        return (RecoveryAgentState.Armed, null);
    }

    /// <summary>
    /// A task that points at the right program can still never run: its
    /// triggers disabled or edited, another account's principal, or battery
    /// conditions that stop it exactly when it matters. Any of those reads as
    /// Mismatched, so Arm registers the task again.
    /// </summary>
    private static string? WhyItWouldNotRun(XElement task)
    {
        static bool Flag(XElement? parent, string name, bool missing)
        {
            var value = parent?.Element(Ns + name)?.Value.Trim();
            return value is null ? missing : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        var triggers = task.Element(Ns + "Triggers");
        var logon = triggers?.Elements(Ns + "LogonTrigger").FirstOrDefault(t =>
            Flag(t, "Enabled", missing: true)
            && IsCurrentUser(t.Element(Ns + "UserId")?.Value, missingMeansAny: true)
            && IsActiveNow(t)
            && t.Element(Ns + "Delay") is null);
        if (logon is null)
        {
            return "its sign-in trigger is missing, disabled, delayed, not yet active, or for another account";
        }
        var repeating = triggers?.Elements(Ns + "TimeTrigger").FirstOrDefault(t =>
            Flag(t, "Enabled", missing: true)
            && t.Element(Ns + "Repetition")?.Element(Ns + "Interval")?.Value.Trim() == "PT1M"
            && t.Element(Ns + "Repetition")?.Element(Ns + "Duration") is null
            && t.Element(Ns + "RandomDelay") is null
            && IsActiveNow(t));
        if (repeating is null)
        {
            return "its every-minute trigger is missing, disabled, delayed, not yet active, or changed";
        }
        var principal = task.Element(Ns + "Principals")?.Elements(Ns + "Principal").FirstOrDefault();
        var logonType = principal?.Element(Ns + "LogonType")?.Value.Trim();
        if (principal is null || !IsCurrentUser(principal.Element(Ns + "UserId")?.Value, missingMeansAny: false)
            || (logonType is not null && logonType != "InteractiveToken"))
        {
            return "it runs as another account or logon type";
        }
        var settings = task.Element(Ns + "Settings");
        if (Flag(settings, "DisallowStartIfOnBatteries", missing: true) || Flag(settings, "StopIfGoingOnBatteries", missing: true))
        {
            return "it does not run on battery";
        }
        if (Flag(settings, "RunOnlyIfIdle", missing: false) || Flag(settings, "RunOnlyIfNetworkAvailable", missing: false))
        {
            return "it runs only when idle or online";
        }
        return null;
    }

    /// <summary>
    /// A trigger counts only once it has started and if it never ends: a
    /// start moved into the future means no run until then, and any end
    /// could fall inside a session it is meant to protect. Boundaries without
    /// a zone are local time, as Task Scheduler writes them; one that cannot
    /// be parsed does not count.
    /// </summary>
    private static bool IsActiveNow(XElement trigger)
    {
        if (trigger.Element(Ns + "EndBoundary") is not null)
        {
            return false;
        }
        var start = trigger.Element(Ns + "StartBoundary")?.Value.Trim();
        return start is null || (TryParseBoundary(start, out var startAt) && startAt <= DateTimeOffset.Now.AddMinutes(2));
    }

    private static bool TryParseBoundary(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value);

    /// <summary>Task XML names a user by SID or by DOMAIN\name; either may come back from a query.</summary>
    private static bool IsCurrentUser(string? userId, bool missingMeansAny)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return missingMeansAny;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var value = userId.Trim();
        return string.Equals(value, identity.User?.Value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, identity.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, identity.Name.Split('\\').Last(), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsThisExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        var path = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return string.Equals(path, _executable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, SystemNative.ThroughOem(_executable), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Registers or overwrites the task. Returns an error message, or null when schtasks accepted it.</summary>
    private string? Create()
    {
        var file = Path.Combine(Path.GetTempPath(), $"insomnia-recovery-{Guid.NewGuid():N}.xml");
        try
        {
            using (var writer = new StreamWriter(file, append: false, new UnicodeEncoding(bigEndian: false, byteOrderMark: true)))
            {
                BuildTaskXml().Save(writer);
            }
            var result = Run("/Create", "/TN", _taskName, "/XML", file, "/F");
            if (result is null)
            {
                return "schtasks.exe could not be run.";
            }
            if (result.TimedOut || result.ExitCode != 0)
            {
                Log.Error($"Recovery agent: registering the task failed: {Describe(result)}");
                return Describe(result);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Recovery agent: writing the task XML failed", ex);
            return $"Could not write the task definition: {ex.Message}";
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Recovery agent: could not delete {file}: {ex.Message}");
            }
        }
    }

    /// <remarks>
    /// The user is named by SID rather than DOMAIN\name: it survives account
    /// renames and Microsoft-account display names. The time trigger repeats
    /// every minute with no duration, which means indefinitely. Element order
    /// follows what Task Scheduler itself exports.
    /// </remarks>
    private XDocument BuildTaskXml()
    {
        string user;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            user = identity.User?.Value ?? identity.Name;
        }
        var start = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        return new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(
                Ns + "Task",
                new XAttribute("version", "1.2"),
                new XElement(
                    Ns + "RegistrationInfo",
                    new XElement(Ns + "Author", "Insomnia"),
                    new XElement(Ns + "Description", "Restores what Insomnia changed (lid action, power mode, suspended apps, audio) if a session ended without cleaning up."),
                    new XElement(Ns + "URI", _taskName)),
                new XElement(
                    Ns + "Triggers",
                    new XElement(
                        Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", user)),
                    new XElement(
                        Ns + "TimeTrigger",
                        new XElement(
                            Ns + "Repetition",
                            new XElement(Ns + "Interval", "PT1M"),
                            new XElement(Ns + "StopAtDurationEnd", "false")),
                        new XElement(Ns + "StartBoundary", start),
                        new XElement(Ns + "Enabled", "true"))),
                new XElement(
                    Ns + "Principals",
                    new XElement(
                        Ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", user),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(
                    Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(
                        Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", "false"),
                        new XElement(Ns + "RestartOnIdle", "false")),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "true"),
                    new XElement(Ns + "RunOnlyIfIdle", "false"),
                    new XElement(Ns + "WakeToRun", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT2M"),
                    new XElement(Ns + "Priority", "7")),
                new XElement(
                    Ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(
                        Ns + "Exec",
                        new XElement(Ns + "Command", "\"" + _executable + "\""),
                        new XElement(Ns + "Arguments", _arguments)))));
    }

    /// <summary>
    /// Runs schtasks.exe from System32 (never from PATH) with no window and no
    /// shell, closes its input so a prompt cannot hang it, and kills it after
    /// <see cref="Timeout"/>. Returns null when it could not be started.
    /// </summary>
    private static CommandResult? Run(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                Log.Error("Recovery agent: schtasks.exe did not start");
                return null;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("Recovery agent: starting schtasks.exe failed", ex);
            return null;
        }

        process.StandardInput.Close();
        var output = ReadAllAsync(process.StandardOutput.BaseStream);
        var error = ReadAllAsync(process.StandardError.BaseStream);
        var exited = process.WaitForExit(Timeout);
        if (!exited)
        {
            Log.Error($"Recovery agent: schtasks {arguments[0]} did not finish within {Timeout.TotalSeconds:0} s; killing it");
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Log.Warn($"Recovery agent: killing schtasks.exe failed: {ex.Message}");
            }
        }
        // The pipes close when the process (and anything holding them) exits.
        try
        {
            Task.WaitAll([output, error], TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            Log.Warn($"Recovery agent: reading schtasks output failed: {ex.InnerException?.Message}");
        }
        var standardOutput = output.IsCompletedSuccessfully ? Decode(output.Result) : string.Empty;
        var standardError = error.IsCompletedSuccessfully ? Decode(error.Result) : string.Empty;
        return exited
            ? new CommandResult(process.ExitCode, standardOutput, standardError, TimedOut: false)
            : new CommandResult(-1, standardOutput, standardError, TimedOut: true);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] != 0 && bytes[1] == 0)
        {
            // UTF-16LE without a byte order mark.
            return Encoding.Unicode.GetString(bytes);
        }
        return SystemNative.FromOem(bytes);
    }

    private static bool IsNotFound(CommandResult result)
    {
        var text = result.StandardError + "\n" + result.StandardOutput;
        if (text.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        foreach (var code in (int[])[2, 3])
        {
            var message = new Win32Exception(code).Message.Trim().TrimEnd('.', '。').Trim();
            if (message.Length > 0 && text.Contains(message, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string Describe(CommandResult result)
    {
        if (result.TimedOut)
        {
            return $"schtasks.exe did not finish within {Timeout.TotalSeconds:0} seconds.";
        }
        var message = (result.StandardError.Trim().Length > 0 ? result.StandardError : result.StandardOutput).Trim();
        if (message.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            message = message["ERROR:".Length..].Trim();
        }
        return message.Length > 0 ? message : $"schtasks.exe exited with code {result.ExitCode}.";
    }
}
