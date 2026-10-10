namespace Insomnia.Core.Store;

/// <summary>
/// The lock every transaction holds: the app's start, end and lid events,
/// reconcile, and the recovery agent. It is an exclusive handle on
/// <c>recovery.lock</c>, which the OS releases when the process dies, so a
/// crash never leaves it held. Failing to take it never permits an
/// unprotected journal write or side effect: the caller gives up and retries.
/// </summary>
public sealed class RecoveryLock
{
    private readonly string _path;

    public RecoveryLock(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Try to take the lock, polling until <paramref name="wait"/> passes.
    /// Returns null when another process (or another transaction in this one)
    /// holds it.
    /// </summary>
    public IDisposable? TryAcquire(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return null;
                }
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
