using Insomnia.Core.Engine;
using Insomnia.Core.Logging;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;
using Insomnia.Core.Store;
using Insomnia.Windows.Devices;
using Insomnia.Windows.Network;
using Insomnia.Windows.Power;
using Insomnia.Windows.Processes;
using Insomnia.Windows.SystemServices;

namespace Insomnia.App.Composition;

/// <summary>
/// The Windows implementations of every platform contract, built once per
/// process and disposed together. The tray app builds all of them; the
/// recovery agent passes <paramref name="powerEvents"/> and skips the ones
/// that need a running message loop.
/// </summary>
internal sealed class WindowsHost : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public WindowsHost(INotifier notifier, IPowerEvents? powerEvents = null)
    {
        ExecutablePath = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
        Paths = Paths.Default();
        Paths.EnsureFolders();
        Store = new JournalStore(Paths);
        Lock = new RecoveryLock(Paths.Lock);

        var catalog = new ProcessCatalog();
        var commands = new CommandRunner();
        Startup = new StartupRegistration(ExecutablePath);
        Credentials = new CredentialStore();
        Agent = new RecoveryAgentTask(ExecutablePath);
        Catalog = catalog;

        Services = new PlatformServices(
            powerEvents ?? Own(new PowerEventWindow()),
            Own(new KeepAwake()),
            new LidPolicy(),
            new PowerMode(),
            new PowerSource(),
            Own(new ThermalSensor()),
            catalog,
            new ProcessFreezer(),
            new ThrottleControl(),
            Own(new AudioControl()),
            Own(new DisplayControl()),
            new Docker(catalog, commands),
            notifier,
            Agent,
            Startup,
            Credentials,
            Own(new Wifi()),
            commands,
            new BrowserControl(catalog));
    }

    public string ExecutablePath { get; }

    public Paths Paths { get; }

    public JournalStore Store { get; }

    public RecoveryLock Lock { get; }

    public PlatformServices Services { get; }

    public IStartupRegistration Startup { get; }

    public ICredentialStore Credentials { get; }

    public RecoveryAgentTask Agent { get; }

    public IProcessCatalog Catalog { get; }

    private T Own<T>(T item)
        where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                _owned[i].Dispose();
            }
            catch (Exception e)
            {
                Log.Error($"disposing {_owned[i].GetType().Name} failed", e);
            }
        }
        _owned.Clear();
    }
}

/// <summary>
/// The engine as the tray app sees it, owning the platform services it runs
/// on so that both go away together after a successful quit.
/// </summary>
internal sealed class HostedEngine : ISessionController, IDisposable
{
    private readonly WindowsHost _host;
    private readonly SessionEngine _engine;

    public HostedEngine(WindowsHost host)
    {
        _host = host;
        _engine = new SessionEngine(host.Services, host.Store, host.Lock);
    }

    public SessionStatus Status => _engine.Status;

    public event EventHandler<SessionStatus>? StatusChanged
    {
        add => _engine.StatusChanged += value;
        remove => _engine.StatusChanged -= value;
    }

    public Config Config => _engine.Config;

    /// <summary>Launch reconcile, in the background; its outcome surfaces through <see cref="Status"/>.</summary>
    public Task InitializeAsync() => _engine.InitializeAsync();

    public Task<StartResult> StartAsync(TimeSpan duration) => _engine.StartAsync(duration);

    public Task<bool> ExtendAsync(TimeSpan extra) => _engine.ExtendAsync(extra);

    public Task<EndOutcome> EndAsync(EndReason reason) => _engine.EndAsync(reason);

    public Task<bool> PrepareToQuitAsync() => _engine.PrepareToQuitAsync();

    public Task<string?> SaveConfigAsync(Config config) => _engine.SaveConfigAsync(config);

    public Task PerformAsync(MenuAction action) => _engine.PerformAsync(action);

    public StatusDetails ReadDetails() => _engine.ReadDetails();

    public FreezePreview PreviewFreeze(Config config) => _engine.PreviewFreeze(config);

    public void Dispose()
    {
        _engine.Dispose();
        _host.Dispose();
    }
}
