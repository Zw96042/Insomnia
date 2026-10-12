using System.Windows;
using Insomnia.App.Demo;
using Insomnia.App.Interop;
using Insomnia.App.Recovery;
using Insomnia.App.Services;
using Insomnia.Core.Engine;
using Insomnia.Core.Logging;
using Insomnia.Core.Store;

namespace Insomnia.App;

/// <summary>
/// Entry point. <c>--recover</c> runs the recovery agent and exits with no
/// UI; <c>--demo</c> runs the full UI against an in-memory controller that
/// changes nothing on the machine; otherwise this is the tray app, one per
/// user session.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {

        if (HasFlag(args, "--recover"))
        {
            return RecoveryEntry.Run(args);
        }
        if (HasFlag(args, "--arm-agent"))
        {
            return RecoveryEntry.ArmAgent();
        }
        if (HasFlag(args, "--remove-agent"))
        {
            return RecoveryEntry.RemoveAgent();
        }
        if (HasFlag(args, "--version"))
        {
            // A GUI-subsystem process has no console of its own; borrow the caller's.
            NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
            Console.WriteLine($"Insomnia {SettingsWindow.AppVersion()}");
            return 0;
        }

        var demo = HasFlag(args, "--demo");
        using var instance = SingleInstance.Acquire(demo);
        if (HasFlag(args, "--quit"))
        {
            // 0: not running, or it quit. 1: it refused (something is not restored yet).
            return instance.IsPrimary || instance.RequestPrimaryQuit(TimeSpan.FromSeconds(90)) ? 0 : 1;
        }
        if (!instance.IsPrimary)
        {
            instance.SignalPrimary();
            return 0;
        }

        if (!demo)
        {
            ConfigureLogging();
        }

        // NotifyIcon callbacks run inside Windows Forms' window procedure, so
        // their exceptions go to Application.ThreadException rather than WPF's
        // dispatcher handler. Log them instead of showing the WinForms crash dialog.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => Log.Error("unhandled tray exception", e.Exception);
        Log.Info($"starting Insomnia {SettingsWindow.AppVersion()}{(demo ? " in demo mode" : "")}");

        // The notifier exists before the controller so launch reconcile can
        // notify; it shows those notifications once the tray icon is up.
        var notifier = new TrayNotifier();
        ISessionController controller;
        ISettingsServices services;
        try
        {
            controller = demo ? new DemoController(notifier) : AppComposition.CreateController(notifier);
            services = demo ? new DemoSettingsServices() : AppComposition.CreateSettingsServices();
        }
        catch (Exception ex)
        {
            Log.Error("startup failed", ex);
            MessageBox.Show(
                $"Insomnia couldn't start.\n\n{ex.Message}\n\nThe log has details.",
                "Insomnia",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }

        var app = new App(controller, services, notifier, instance, demo);
        app.InitializeComponent();
        var code = app.Run();
        Log.Info($"exiting with code {code}");
        return code;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static void ConfigureLogging()
    {
        try
        {
            var paths = Paths.Default();
            paths.EnsureFolders();
            Log.Configure(paths.InsomniaLog, "app");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging never stops the app; lines go to the debug sink instead.
            Log.Warn($"log folder unavailable: {ex.Message}");
        }
    }
}
