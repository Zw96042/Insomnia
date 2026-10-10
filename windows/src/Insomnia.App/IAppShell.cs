using Insomnia.App.Interop;

namespace Insomnia.App;

/// <summary>What the tray icon, the flyout and the menu ask of the app. Implemented by <see cref="App"/>; UI thread only.</summary>
internal interface IAppShell
{
    /// <summary>Left click on the tray icon.</summary>
    void ToggleFlyout();

    /// <summary>Open the flyout (second launch, a notification click, a blocked quit).</summary>
    void ShowFlyout();

    /// <summary>
    /// Open the tray menu at <paramref name="at"/> (physical pixels), flipping
    /// to stay in the work area. <paramref name="fromFlyout"/> is true for the
    /// flyout's own "more" button, which must not take activation from it.
    /// </summary>
    void ShowMenu(NativePoint at, bool fromFlyout = false);

    void ShowSettings();

    void OpenLogs();

    /// <summary>Ask for confirmation, then end the session (the menu has no hold-to-end).</summary>
    Task ConfirmAndEndSessionAsync();

    /// <summary>End any session and quit, unless the controller says quitting would abandon something.</summary>
    Task QuitAsync();

    /// <summary>Open the flyout with a message in its message bar (a failed menu action).</summary>
    void ShowMessage(string text, bool critical);
}
