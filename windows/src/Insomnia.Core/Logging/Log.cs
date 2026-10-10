using System.Globalization;
using System.Text;

namespace Insomnia.Core.Logging;

/// <summary>
/// Append-only text logs capped at 1 MiB, with one older copy kept as
/// <c>.1</c>. A log the user replaced with a symbolic link is never rotated:
/// the file it points to is theirs to manage. Logging never throws.
/// </summary>
public sealed class RotatingLog
{
    public const long Cap = 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _path;

    public RotatingLog(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                RotateIfNeeded();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.LinkTarget is not null || info.Length < Cap)
        {
            return;
        }
        var older = _path + ".1";
        if (File.Exists(older) && new FileInfo(older).LinkTarget is not null)
        {
            return;
        }
        File.Move(_path, older, overwrite: true);
    }
}

/// <summary>
/// Process-wide log. Until <see cref="Configure"/> runs, lines go only to
/// the debug sink, so code that logs before startup finishes never fails.
/// </summary>
public static class Log
{
    private static RotatingLog? _file;

    /// <summary>Extra sink for tests and the debugger.</summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>A short tag for this process, for example <c>app</c> or <c>recover</c>.</summary>
    public static string Tag { get; private set; } = "app";

    public static void Configure(string path, string tag)
    {
        _file = new RotatingLog(path);
        Tag = tag;
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}: {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fffzzz} [{Tag}:{Environment.ProcessId}] {level} {message}");
        _file?.Write(line);
        Sink?.Invoke(line);
        System.Diagnostics.Debug.WriteLine(line);
    }
}
