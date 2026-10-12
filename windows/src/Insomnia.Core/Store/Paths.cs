namespace Insomnia.Core.Store;

/// <summary>
/// Where Insomnia keeps its files. The data folder is
/// <c>%LOCALAPPDATA%\Insomnia</c>, or <c>INSOMNIA_HOME</c> when set (tests,
/// <c>--demo</c> and one-off <c>--recover</c> runs).
/// </summary>
public sealed class Paths
{
    public Paths(string root, bool isOverride = false)
    {
        Root = root;
        IsOverride = isOverride;
    }

    public string Root { get; }

    /// <summary>
    /// True when <c>INSOMNIA_HOME</c> chose the folder. That is for tests,
    /// <c>--demo</c> and one-off <c>--recover</c> runs: the tray app refuses
    /// to run sessions from another folder, since the recovery task always
    /// recovers the default one.
    /// </summary>
    public bool IsOverride { get; }

    public string Config => Path.Combine(Root, "config.json");

    public string Session => Path.Combine(Root, "session.json");

    public string State => Path.Combine(Root, "state.json");

    public string Lock => Path.Combine(Root, "recovery.lock");

    /// <summary>The recovery agent's last notification, so a lasting failure is announced once, not every minute.</summary>
    public string RecoveryNotice => Path.Combine(Root, "recovery-notice.txt");

    public string Logs => Path.Combine(Root, "Logs");

    public string InsomniaLog => Path.Combine(Logs, "insomnia.log");

    public string HandoffsLog => Path.Combine(Logs, "handoffs.log");

    public static Paths Default()
    {
        var overridden = Environment.GetEnvironmentVariable("INSOMNIA_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new Paths(Path.GetFullPath(overridden), isOverride: true);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new Paths(Path.Combine(local, "Insomnia"));
    }

    public void EnsureFolders()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}
