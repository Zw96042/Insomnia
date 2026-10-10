using System.Globalization;
using System.Text;
using System.Text.Json;
using Insomnia.Core.Logging;
using Insomnia.Core.Model;

namespace Insomnia.Core.Store;

/// <summary>What reading <c>session.json</c> found.</summary>
public abstract record SessionRead
{
    private SessionRead() { }

    public sealed record Missing : SessionRead;

    public sealed record Valid(Session Session) : SessionRead;

    /// <summary>
    /// The file holds no session Insomnia can enforce: it does not decode,
    /// lacks a required key, or is not a regular file.
    /// </summary>
    public sealed record Unreadable(string Reason) : SessionRead;

    /// <summary>
    /// The file could not be opened or read (held by another program, an
    /// I/O error). It may hold a valid session, so nothing is concluded from
    /// it: callers retry rather than treat it as ended.
    /// </summary>
    public sealed record Inaccessible(string Reason) : SessionRead;
}

/// <summary>What reading <c>state.json</c> found. A missing file is an empty journal.</summary>
public abstract record StateRead
{
    private StateRead() { }

    public sealed record Valid(RuntimeState State) : StateRead;

    /// <summary>The journal exists but does not decode. Every transaction is refused while it stays so.</summary>
    public sealed record Unreadable(string Reason) : StateRead;
}

/// <summary>
/// Reads and writes the session, journal and config files. Every write is
/// atomic: a temporary file in the same folder is flushed to disk and then
/// moved over the target. Writes throw <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> on failure so callers can keep
/// the entry and retry; reads never throw.
/// </summary>
public sealed class JournalStore
{
    private readonly Paths _paths;

    public JournalStore(Paths paths)
    {
        _paths = paths;
    }

    public Paths Paths => _paths;

    // Session

    public SessionRead ReadSession()
    {
        var path = _paths.Session;
        // Existence checks answer false on access errors too, so absence is
        // decided only by a read that fails with "not found".
        try
        {
            if (Directory.Exists(path))
            {
                return new SessionRead.Unreadable("session.json is a folder, not a file");
            }
            if (new FileInfo(path).LinkTarget is not null)
            {
                return new SessionRead.Unreadable("session.json is a link, not a regular file");
            }
            var text = File.ReadAllText(path, Encoding.UTF8);
            return DecodeSession(text);
        }
        catch (Exception e) when (IsNotFound(e))
        {
            return new SessionRead.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SessionRead.Inaccessible($"session.json could not be read: {e.Message}");
        }
    }

    /// <summary>
    /// Decodes a session strictly: <c>startedAt</c> and <c>endsAt</c> must be
    /// dates in the store's format and <c>extensions</c> an array of numbers.
    /// </summary>
    public static SessionRead DecodeSession(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SessionRead.Unreadable("session.json is not a JSON object");
            }
            if (!root.TryGetProperty("startedAt", out var started) || started.ValueKind != JsonValueKind.String
                || Json.ParseDate(started.GetString()) is not { } startedAt)
            {
                return new SessionRead.Unreadable("session.json has no valid startedAt");
            }
            if (!root.TryGetProperty("endsAt", out var ends) || ends.ValueKind != JsonValueKind.String
                || Json.ParseDate(ends.GetString()) is not { } endsAt)
            {
                return new SessionRead.Unreadable("session.json has no valid endsAt");
            }
            if (!root.TryGetProperty("extensions", out var ext) || ext.ValueKind != JsonValueKind.Array)
            {
                return new SessionRead.Unreadable("session.json has no extensions array");
            }
            var extensions = new List<double>();
            foreach (var item in ext.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var seconds) || !double.IsFinite(seconds))
                {
                    return new SessionRead.Unreadable("session.json has a non-numeric extension");
                }
                extensions.Add(seconds);
            }
            return new SessionRead.Valid(new Session(startedAt, endsAt, extensions));
        }
        catch (JsonException e)
        {
            return new SessionRead.Unreadable($"session.json does not decode: {e.Message}");
        }
    }

    public void WriteSession(Session session)
    {
        var payload = new Dictionary<string, object>
        {
            ["startedAt"] = Json.FormatDate(session.StartedAt),
            ["endsAt"] = Json.FormatDate(session.EndsAt),
            ["extensions"] = session.Extensions,
        };
        AtomicWrite(_paths.Session, JsonSerializer.Serialize(payload, Json.Options));
    }

    /// <summary>Deletes session.json. A missing file is not an error; any other failure throws.</summary>
    public void DeleteSession() => DeleteIfPresent(_paths.Session);

    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // No data folder, so no file.
        }
    }

    private static bool IsNotFound(Exception e) => e is FileNotFoundException or DirectoryNotFoundException;

    /// <summary>
    /// Renames an unreadable session file to
    /// <c>session.json.unreadable-&lt;UTC stamp&gt;</c>, never overwriting an
    /// earlier copy, and returns the new path. It may have been a valid
    /// session, so it is kept as evidence rather than deleted.
    /// </summary>
    public string SetAsideSession(DateTimeOffset now)
    {
        var stamp = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var target = _paths.Session + ".unreadable-" + stamp;
        for (var i = 1; File.Exists(target) || Directory.Exists(target); i++)
        {
            target = _paths.Session + ".unreadable-" + stamp + "-" + i.ToString(CultureInfo.InvariantCulture);
        }
        if (Directory.Exists(_paths.Session))
        {
            Directory.Move(_paths.Session, target);
        }
        else
        {
            File.Move(_paths.Session, target, overwrite: false);
        }
        return target;
    }

    // Journal

    public StateRead ReadState() => ReadState(out _);

    /// <summary>
    /// Reads the journal and returns its raw text too (null when there is no
    /// file), so a caller can tell whether another process changed it since.
    /// </summary>
    public StateRead ReadState(out string? raw)
    {
        raw = null;
        var path = _paths.State;
        try
        {
            if (Directory.Exists(path))
            {
                return new StateRead.Unreadable("state.json is a folder, not a file");
            }
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception e) when (IsNotFound(e))
            {
                // Only a read that finds no file means an empty journal.
                return new StateRead.Valid(new RuntimeState());
            }
            raw = text;
            var state = JsonSerializer.Deserialize<RuntimeState>(text, Json.Options);
            if (state is null)
            {
                return new StateRead.Unreadable("state.json is null");
            }
            Sanitize(state);
            return new StateRead.Valid(state);
        }
        catch (JsonException e)
        {
            return new StateRead.Unreadable($"state.json does not decode: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new StateRead.Unreadable($"state.json could not be read: {e.Message}");
        }
    }

    /// <summary>
    /// Writes the journal, or deletes the file when the journal is empty.
    /// Returns the text now on disk (null when there is no file), as
    /// <see cref="ReadState(out string?)"/> would read it.
    /// </summary>
    public string? WriteState(RuntimeState state)
    {
        if (state.IsEmpty)
        {
            DeleteIfPresent(_paths.State);
            return null;
        }
        var text = JsonSerializer.Serialize(state, Json.Options);
        AtomicWrite(_paths.State, text);
        return text;
    }

    // A hand-edited journal can carry nulls where lists belong.
    private static void Sanitize(RuntimeState state)
    {
        state.LidPolicy ??= [];
        state.PowerMode ??= [];
        state.PowerModeRelinquished ??= [];
        state.FrozenProcesses ??= [];
        state.SavedAudioOutputs ??= [];
        state.ThrottleOverrides ??= [];
        state.FrozenProcesses.RemoveAll(p => p is null || p.Pid <= 0 || string.IsNullOrEmpty(p.Image));
        state.SavedAudioOutputs.RemoveAll(a => a is null || string.IsNullOrEmpty(a.DeviceId));
    }

    // Config

    /// <summary>
    /// Loads settings. A missing file gives the defaults; a file that does not
    /// decode gives the defaults too, and the caller is told so it can keep
    /// the broken file rather than overwrite it.
    /// </summary>
    public (Config Config, string? Problem) ReadConfig() => ReadConfig(out _);

    /// <param name="missing">True only when the read found no file, so writing the defaults cannot replace a file that exists.</param>
    public (Config Config, string? Problem) ReadConfig(out bool missing)
    {
        missing = false;
        try
        {
            string text;
            try
            {
                text = File.ReadAllText(_paths.Config, Encoding.UTF8);
            }
            catch (Exception e) when (IsNotFound(e))
            {
                missing = true;
                return (new Config(), null);
            }
            var config = JsonSerializer.Deserialize<Config>(text, Json.Options);
            return config is null ? (new Config(), "config.json is null") : (config, null);
        }
        catch (JsonException e)
        {
            return (new Config(), $"config.json does not decode: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new Config(), $"config.json could not be read: {e.Message}");
        }
    }

    public void WriteConfig(Config config) =>
        AtomicWrite(_paths.Config, JsonSerializer.Serialize(config, Json.Options));

    /// <summary>
    /// Writes through a temporary file in the same folder, flushed to disk,
    /// then moved over the target, so a crash leaves either the old file or
    /// the new one, never a torn one.
    /// </summary>
    public static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not remove temporary file {path}: {e.Message}");
        }
    }
}
