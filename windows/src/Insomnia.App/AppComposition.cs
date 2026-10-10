using Insomnia.App.Composition;
using Insomnia.App.Services;
using Insomnia.Core.Engine;
using Insomnia.Core.Platform;
using Insomnia.Core.Store;

namespace Insomnia.App;

/// <summary>
/// Where the UI meets the real engine. Program.Main calls
/// <see cref="CreateController"/> and then <see cref="CreateSettingsServices"/>
/// on the UI thread, after the single-instance check and logging are set up;
/// <c>--demo</c> never calls into this class.
/// </summary>
internal static class AppComposition
{
    private static WindowsHost? _host;

    /// <summary>
    /// Builds the Windows services and the session engine, and starts launch
    /// reconcile in the background. Its outcome surfaces through
    /// <see cref="ISessionController.Status"/>.
    /// </summary>
    public static ISessionController CreateController(INotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        if (Paths.Default().IsOverride)
        {
            // Sessions change machine-wide settings (the lid action, the power
            // mode) that one journal must own, and the recovery task always
            // recovers the default folder. A session kept elsewhere could be
            // left unrecoverable, or undone by the other folder's recovery.
            throw new InvalidOperationException(
                "INSOMNIA_HOME is set. Sessions can run only with the default data folder; unset it, or run with --demo to try the interface.");
        }
        _host = new WindowsHost(notifier);
        var engine = new HostedEngine(_host);
        _ = engine.InitializeAsync();
        return engine;
    }

    public static ISettingsServices CreateSettingsServices() =>
        new WindowsSettingsServices(_host ?? throw new InvalidOperationException("CreateController runs first."));

    /// <summary>Public home of this Windows port, linked from Settings > About. Null hides the link until it is published.</summary>
    public static Uri? PortRepository => null;
}
