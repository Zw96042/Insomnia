using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Insomnia.App.Animation;
using Insomnia.App.Controls;
using Insomnia.App.Formatting;
using Insomnia.App.Interop;
using Insomnia.App.Theme;
using Insomnia.Core.Engine;
using Insomnia.Core.Logging;
using Insomnia.Core.Logic;
using Insomnia.Core.Model;

namespace Insomnia.App;

/// <summary>
/// The tray flyout: start a session, watch it, add time, hold to end, and
/// act on warnings. Renders <see cref="SessionStatus"/> snapshots pushed by
/// <see cref="App"/> on the UI thread and calls the controller without ever
/// blocking on it.
/// </summary>
internal sealed partial class FlyoutWindow
{
    private enum View
    {
        /// <summary>The start pills.</summary>
        Entry,
        /// <summary>The countdown, extend row and hold-to-end.</summary>
        Countdown,
    }

    /// <summary>A tray click this soon after a deactivation hide is the same click, not a reopen.</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

    private readonly ISessionController _controller;
    private readonly IAppShell _shell;
    private readonly DispatcherTimer _tick;
    private readonly TranslateTransform _bodyShift = new();
    private readonly ScaleTransform _countdownScale = new(1, 1);
    private readonly TranslateTransform _idleShift = new();
    private readonly TranslateTransform _activeShift = new();
    private readonly Dictionary<string, WarningRow> _warningRows = new(StringComparer.Ordinal);
    private readonly Border _warningDivider = new() { Height = 1, Margin = new Thickness(0, 0, 0, 6) };

    private SessionStatus _status = SessionStatus.Initial;
    private View _view = View.Entry;
    private TimeSpan? _pendingDuration;
    private DateTimeOffset _pendingStartedAt;
    private bool _busy;
    private bool _closing;
    private DateTime _hiddenAt = DateTime.MinValue;
    private TaskbarEdge _edge = TaskbarEdge.Bottom;
    private NativeRect _anchor;
    private int _viewToken;
    private List<double> _presets = [];
    private double _defaultPreset = double.NaN;
    private DurationField _lastStartField = DurationField.Hours;
    private DateTimeOffset? _renderedEndsAt;
    private bool _allowClose;

    public FlyoutWindow(ISessionController controller, IAppShell shell)
    {
        _controller = controller;
        _shell = shell;
        InitializeComponent();

        // Borderless but still a real DWM frame: shadow, rounded corners and
        // the acrylic backdrop on Windows 11; a 1 px glass edge keeps the
        // shadow on Windows 10.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = NativeMethods.SupportsSystemBackdrop ? new Thickness(-1) : new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None,
        });
        SourceInitialized += (_, _) => WindowEffects.MakeToolWindow(this);
        ThemeManager.Current.Attach(this, Backdrop.Transient, managesFrame: false);

        Body.RenderTransform = _bodyShift;
        Countdown.RenderTransform = _countdownScale;
        IdleBody.RenderTransform = _idleShift;
        ActiveBody.RenderTransform = _activeShift;
        ExtendEntry.IsCompact = true;
        _warningDivider.SetResourceReference(Border.BackgroundProperty, "Brush.Divider");

        _tick = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _tick.Tick += OnTick;

        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += OnPreviewKeyDown;
        SizeChanged += (_, _) => Reanchor();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Reanchor);

        StartEntry.InputChanged += (_, _) =>
        {
            HideMessage();
            UpdateSubline();
        };
        StartEntry.GotKeyboardFocus += (_, _) => _lastStartField = StartEntry.FocusedField ?? _lastStartField;
        StartEntry.Submitted += (_, _) => _ = StartAsync();
        StartButton.Click += (_, _) => _ = StartAsync();
        ExtendEntry.InputChanged += (_, _) => HideMessage();
        ExtendEntry.Submitted += (_, _) => _ = ExtendAsync();
        ExtendButton.Click += (_, _) => _ = ExtendAsync();
        HoldToEnd.Held += (_, _) => _ = EndAsync();
        SettingsButton.Click += (_, _) =>
        {
            Dismiss();
            _shell.ShowSettings();
        };
        MoreButton.Click += (_, _) =>
        {
            var corner = MoreButton.PointToScreen(new Point(MoreButton.ActualWidth, 0));
            _shell.ShowMenu(new NativePoint((int)corner.X, (int)corner.Y), fromFlyout: true);
        };

        ApplyView(View.Entry, animate: false, force: true);
        UpdateSubline();
    }

    /// <summary>Visible and not on its way out.</summary>
    public bool IsOpen => IsVisible && !_closing;

    /// <summary>True right after a deactivation hid the flyout, so the tray click that caused it does not reopen it.</summary>
    public bool WasJustDismissed => DateTime.UtcNow - _hiddenAt < ReopenGuard;

    /// <summary>Show the flyout against <paramref name="anchor"/> (the tray icon, physical pixels), or bring it forward.</summary>
    public void Open(NativeRect anchor)
    {
        _anchor = anchor;
        if (IsOpen)
        {
            Activate();
            return;
        }
        _closing = false;
        RefreshPresets();
        ApplyView(DesiredView(), animate: false, force: true);
        UpdateSubline();
        UpdateWarnings(animate: false);
        UpdateControls();

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Root.Measure(new Size(Width, double.PositiveInfinity));
        var placement = FlyoutPlacement.Compute(anchor, Width, Root.DesiredSize.Height);
        _edge = placement.Edge;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, placement.X, placement.Y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        Motion.Jump(Root, OpacityProperty, 0);
        Show();
        Activate();
        NativeMethods.SetForegroundWindow(hwnd);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Reanchor);
        AnimateIn();
        UpdateTimer();
    }

    /// <summary>
    /// Hide the flyout (deactivation, Esc, a menu command). It leaves at once,
    /// as Windows' own flyouts do: the acrylic frame cannot move with its
    /// content, so fading the content out would leave an empty panel behind.
    /// Esc's exit motion is the pill fold, which runs before this.
    /// </summary>
    public void Dismiss()
    {
        if (!IsVisible || _closing)
        {
            return;
        }
        _closing = true;
        _hiddenAt = DateTime.UtcNow;
        _tick.Stop();
        HoldToEnd.Reset();
        Hide();
        _closing = false;
        HideMessage();
    }

    /// <summary>Render a new status snapshot.</summary>
    public void ApplyStatus(SessionStatus status)
    {
        _status = status;
        var animate = IsOpen;
        ApplyView(DesiredView(), animate);
        if (_view == View.Countdown && status.Session?.EndsAt != _renderedEndsAt)
        {
            // An extension (or a fresh session) moves the deadline: roll to it now.
            RenderCountdown(animate);
        }
        UpdateSubline();
        UpdateWarnings(animate);
        UpdateControls();
        UpdateTimer();
    }

    /// <summary>Show a message in the flyout's message bar (a failed action, a blocked quit).</summary>
    public void ShowMessage(string text, bool critical)
    {
        ErrorBar.SetResourceReference(StyleProperty, critical ? "InfoBar.Critical" : "InfoBar.Caution");
        ErrorGlyph.Text = critical ? Glyphs.Error : Glyphs.Warning;
        ErrorGlyph.SetResourceReference(TextBlock.ForegroundProperty, critical ? "Brush.Critical" : "Brush.Caution");
        ErrorText.Text = text;
        AutomationProperties.SetName(ErrorBar, text);
        // Announce it: errors appear without focus moving.
        (UIElementAutomationPeer.FromElement(ErrorText) ?? UIElementAutomationPeer.CreatePeerForElement(ErrorText))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        var wasHidden = ErrorBar.Visibility != Visibility.Visible;
        ErrorBar.Visibility = Visibility.Visible;
        if (wasHidden && IsOpen)
        {
            Motion.Jump(ErrorBar, OpacityProperty, 0);
            Motion.To(ErrorBar, OpacityProperty, 1, Motion.Base, Motion.ReduceMotion);
        }
        else
        {
            Motion.Jump(ErrorBar, OpacityProperty, 1);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 on the flyout hides it; the app owns its lifetime.
        if (!_allowClose)
        {
            e.Cancel = true;
            Dismiss();
        }
        base.OnClosing(e);
    }

    /// <summary>Close for real at shutdown.</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        _tick.Stop();
        Close();
    }

    #region Views

    private View DesiredView() =>
        _pendingDuration is not null || _status.Phase != SessionPhase.Idle ? View.Countdown : View.Entry;

    private void ApplyView(View target, bool animate, bool force = false)
    {
        if (target == _view && !force)
        {
            return;
        }
        var changed = target != _view;
        _view = target;
        var token = ++_viewToken;
        animate &= changed;

        if (target == View.Countdown)
        {
            Eye.SetOpen(true, animate);
            HoldHint.Visibility = Visibility.Visible;
            if (!animate)
            {
                StartEntry.Fold(animate: false);
                ShowCountdownStrip(animate: false);
                SwapBodies(toActive: true, animate: false);
                return;
            }
            StartEntry.Fold(animate: true, () =>
            {
                if (token == _viewToken)
                {
                    ShowCountdownStrip(animate: true);
                }
            });
            SwapBodies(toActive: true, animate: true);
            Keyboard.Focus(Root);
        }
        else
        {
            Eye.SetOpen(false, animate);
            HoldHint.Visibility = Visibility.Hidden;
            HideCountdownStrip(animate, () =>
            {
                if (token != _viewToken)
                {
                    return;
                }
                StartEntry.Appear(animate);
                if (IsActive)
                {
                    StartEntry.FocusField(_lastStartField);
                }
            });
            SwapBodies(toActive: false, animate);
        }
    }

    private void ShowCountdownStrip(bool animate)
    {
        RenderCountdown(animate: false);
        CountdownStrip.Visibility = Visibility.Visible;
        if (!animate)
        {
            Motion.Jump(_countdownScale, ScaleTransform.ScaleXProperty, 1);
            Motion.Jump(_countdownScale, ScaleTransform.ScaleYProperty, 1);
            Motion.Jump(Countdown, OpacityProperty, 1);
            Motion.Jump(HoldToEnd, OpacityProperty, 1);
            return;
        }
        var reduced = Motion.ReduceMotion;
        if (reduced)
        {
            Motion.Jump(_countdownScale, ScaleTransform.ScaleXProperty, 1);
            Motion.Jump(_countdownScale, ScaleTransform.ScaleYProperty, 1);
        }
        else
        {
            // The countdown grows out of the eye's side.
            Motion.Jump(_countdownScale, ScaleTransform.ScaleXProperty, Motion.CountdownScale);
            Motion.Jump(_countdownScale, ScaleTransform.ScaleYProperty, Motion.CountdownScale);
            Motion.SpringTo(_countdownScale, ScaleTransform.ScaleXProperty, 1, Motion.Base);
            Motion.SpringTo(_countdownScale, ScaleTransform.ScaleYProperty, 1, Motion.Base);
        }
        Motion.Jump(Countdown, OpacityProperty, 0);
        Motion.To(Countdown, OpacityProperty, 1, Motion.Base, reduced);
        Motion.Jump(HoldToEnd, OpacityProperty, 0);
        Motion.To(HoldToEnd, OpacityProperty, 1, Motion.Base, reduced, TimeSpan.FromMilliseconds(80));
    }

    private void HideCountdownStrip(bool animate, Action then)
    {
        HoldToEnd.Reset();
        if (!animate || CountdownStrip.Visibility != Visibility.Visible)
        {
            CountdownStrip.Visibility = Visibility.Hidden;
            then();
            return;
        }
        var reduced = Motion.ReduceMotion;
        var duration = reduced ? Motion.ReducedDuration : TimeSpan.FromMilliseconds(180);
        Motion.EaseTo(Countdown, OpacityProperty, 0, duration);
        Motion.EaseTo(HoldToEnd, OpacityProperty, 0, duration);
        if (!reduced)
        {
            Motion.EaseTo(_countdownScale, ScaleTransform.ScaleXProperty, 0.85, duration);
            Motion.EaseTo(_countdownScale, ScaleTransform.ScaleYProperty, 0.85, duration);
        }
        var token = _viewToken;
        After(duration, () =>
        {
            if (token == _viewToken)
            {
                CountdownStrip.Visibility = Visibility.Hidden;
                then();
            }
        });
    }

    private void SwapBodies(bool toActive, bool animate)
    {
        var show = toActive ? ActiveBody : IdleBody;
        var hide = toActive ? IdleBody : ActiveBody;
        var showShift = toActive ? _activeShift : _idleShift;
        show.Visibility = Visibility.Visible;
        show.IsHitTestVisible = true;
        hide.IsHitTestVisible = false;
        if (!animate)
        {
            hide.Visibility = Visibility.Hidden;
            Motion.Jump(show, OpacityProperty, 1);
            Motion.Jump(showShift, TranslateTransform.YProperty, 0);
            if (toActive)
            {
                ExtendEntry.Appear(animate: false);
            }
            return;
        }
        var reduced = Motion.ReduceMotion;
        Motion.EaseTo(hide, OpacityProperty, 0, reduced ? Motion.ReducedDuration : TimeSpan.FromMilliseconds(120));
        Motion.Jump(show, OpacityProperty, 0);
        var delay = reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(120);
        if (!reduced)
        {
            Motion.Jump(showShift, TranslateTransform.YProperty, 8);
            Motion.SpringTo(showShift, TranslateTransform.YProperty, 0, Motion.Base, delay);
        }
        Motion.To(show, OpacityProperty, 1, Motion.Base, reduced, delay);
        if (toActive)
        {
            ExtendEntry.Appear(animate: true);
        }
        var token = _viewToken;
        After(TimeSpan.FromMilliseconds(reduced ? 160 : 130), () =>
        {
            if (token == _viewToken)
            {
                hide.Visibility = Visibility.Hidden;
            }
        });
    }

    private void AnimateIn()
    {
        // The window (acrylic, shadow, corners) appears in place; only the
        // content moves, and only within the body's own padding, so no edge
        // of empty backdrop ever shows.
        var reduced = Motion.ReduceMotion;
        if (reduced)
        {
            Motion.Jump(_bodyShift, TranslateTransform.XProperty, 0);
            Motion.Jump(_bodyShift, TranslateTransform.YProperty, 0);
            Motion.EaseTo(Root, OpacityProperty, 1, Motion.ReducedDuration);
        }
        else
        {
            var (dx, dy) = TowardTaskbar(8);
            Motion.Jump(_bodyShift, TranslateTransform.XProperty, dx);
            Motion.Jump(_bodyShift, TranslateTransform.YProperty, dy);
            Motion.SpringTo(_bodyShift, TranslateTransform.XProperty, 0, Motion.Base);
            Motion.SpringTo(_bodyShift, TranslateTransform.YProperty, 0, Motion.Base);
            Motion.EaseOutTo(Root, OpacityProperty, 1, TimeSpan.FromMilliseconds(120));
        }
        if (_view == View.Entry)
        {
            StartEntry.Appear(animate: true);
            StartEntry.FocusField(_lastStartField);
        }
        else
        {
            Keyboard.Focus(Root);
        }
    }

    private (double X, double Y) TowardTaskbar(double distance) => _edge switch
    {
        TaskbarEdge.Top => (0, -distance),
        TaskbarEdge.Left => (-distance, 0),
        TaskbarEdge.Right => (distance, 0),
        _ => (0, distance),
    };

    /// <summary>Keep the flyout against the taskbar after its size or DPI changes.</summary>
    private void Reanchor()
    {
        if (!IsVisible)
        {
            return;
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }
        var placement = FlyoutPlacement.Compute(_anchor, ActualWidth, ActualHeight);
        _edge = placement.Edge;
        if (placement.X != rect.Left || placement.Y != rect.Top)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, placement.X, placement.Y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    #endregion

    #region Countdown

    private (DateTimeOffset EndsAt, CountdownShape Shape)? CountdownTarget()
    {
        if (_status.Phase != SessionPhase.Idle && _status.Session is { } session)
        {
            return (session.EndsAt, session.CountdownShape);
        }
        if (_pendingDuration is { } duration)
        {
            return (_pendingStartedAt + duration, CountdownShapes.ForInitialDuration(duration));
        }
        return null;
    }

    private void RenderCountdown(bool animate)
    {
        if (CountdownTarget() is not { } target)
        {
            // A start this flyout did not begin (launch reconcile): no figure yet.
            Countdown.SetText("", animate: false);
            _renderedEndsAt = null;
            return;
        }
        _renderedEndsAt = _status.Session?.EndsAt;
        var remaining = target.EndsAt - DateTimeOffset.UtcNow;
        Countdown.SetText(SessionMath.FormatCountdown(remaining, target.Shape), animate);
    }

    /// <summary>
    /// Tick at 1 Hz on the second boundaries of the remaining time, only while
    /// the countdown is on screen: the flyout is open and the lid is open.
    /// </summary>
    private void UpdateTimer()
    {
        var run = IsOpen && _view == View.Countdown && !_status.LidClosed && CountdownTarget() is not null;
        if (!run)
        {
            _tick.Stop();
            return;
        }
        if (!_tick.IsEnabled)
        {
            RenderCountdown(animate: false);
            ScheduleTick();
        }
    }

    private void ScheduleTick()
    {
        if (CountdownTarget() is not { } target)
        {
            _tick.Stop();
            return;
        }
        var remaining = target.EndsAt - DateTimeOffset.UtcNow;
        var fraction = remaining > TimeSpan.Zero ? TimeSpan.FromTicks(remaining.Ticks % TimeSpan.TicksPerSecond) : TimeSpan.FromSeconds(1);
        if (fraction <= TimeSpan.Zero)
        {
            fraction = TimeSpan.FromSeconds(1);
        }
        // Land just after the boundary so the rounded-up display has changed.
        _tick.Interval = fraction + TimeSpan.FromMilliseconds(15);
        _tick.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _tick.Stop();
        RenderCountdown(animate: true);
        if (IsOpen && _view == View.Countdown && !_status.LidClosed)
        {
            ScheduleTick();
        }
    }

    #endregion

    #region Actions

    private async Task StartAsync()
    {
        if (_busy || _view != View.Entry)
        {
            return;
        }
        var input = StartEntry.Input;
        TimeSpan duration;
        if (input.IsEmpty)
        {
            duration = TimeSpan.FromSeconds(_controller.Config.DefaultPreset);
        }
        else if (input.Total is { } total)
        {
            duration = total;
        }
        else
        {
            StartEntry.Reject();
            ShowMessage("Enter at least 1 minute.", critical: true);
            return;
        }

        _busy = true;
        HideMessage();
        _pendingDuration = duration;
        _pendingStartedAt = DateTimeOffset.UtcNow;
        ApplyView(View.Countdown, animate: IsOpen);
        UpdateSubline();
        UpdateControls();
        UpdateTimer();
        try
        {
            var result = await _controller.StartAsync(duration);
            _pendingDuration = null;
            switch (result.Outcome)
            {
                case StartOutcome.Started:
                case StartOutcome.AlreadyActive:
                    StartEntry.Clear();
                    break;
                default:
                    var message = result.Message is { Length: > 0 } m ? m
                        : result.Outcome == StartOutcome.Refused ? "Insomnia couldn't start the session." : "The session couldn't start, and everything was put back.";
                    Log.Warn($"start {result.Outcome}: {message}");
                    ShowMessage(message, critical: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            _pendingDuration = null;
            Log.Error("start failed", ex);
            ShowMessage($"Couldn't start the session: {ex.Message}", critical: true);
        }
        finally
        {
            _busy = false;
            ApplyStatus(_controller.Status);
        }
    }

    private async Task ExtendAsync()
    {
        if (_busy || _status.Phase != SessionPhase.Active)
        {
            return;
        }
        if (ExtendEntry.Input.Total is not { } extra)
        {
            ExtendEntry.Reject();
            ShowMessage("Enter at least 1 minute to add.", critical: true);
            return;
        }
        _busy = true;
        HideMessage();
        UpdateControls();
        var animate = IsOpen;
        var folded = false;
        bool? extended = null;
        void Settle()
        {
            if (!folded || extended is null)
            {
                return;
            }
            if (extended == true)
            {
                ExtendEntry.Clear();
            }
            ExtendEntry.Appear(animate);
            if (IsActive)
            {
                ExtendEntry.FocusField(DurationField.Hours);
            }
        }
        ExtendEntry.Fold(animate, () =>
        {
            folded = true;
            Settle();
        });
        try
        {
            extended = await _controller.ExtendAsync(extra);
            if (extended == false)
            {
                ShowMessage("Couldn't add time. The session may have just ended.", critical: true);
            }
        }
        catch (Exception ex)
        {
            extended = false;
            Log.Error("extend failed", ex);
            ShowMessage($"Couldn't add time: {ex.Message}", critical: true);
        }
        finally
        {
            _busy = false;
            // Re-enable the pills before Settle may focus one.
            UpdateControls();
            Settle();
            ApplyStatus(_controller.Status);
        }
    }

    private async Task EndAsync()
    {
        if (_busy || _status.Phase != SessionPhase.Active)
        {
            return;
        }
        _busy = true;
        HideMessage();
        UpdateControls();
        try
        {
            var outcome = await _controller.EndAsync(EndReason.User);
            if (outcome == EndOutcome.Incomplete)
            {
                ShowMessage("The session ended, but some changes couldn't be put back yet. Insomnia keeps retrying; the warnings below say what is left.", critical: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("end failed", ex);
            ShowMessage($"Couldn't end the session: {ex.Message}", critical: true);
        }
        finally
        {
            _busy = false;
            ApplyStatus(_controller.Status);
        }
    }

    private async Task PerformAsync(MenuAction action, Button button)
    {
        if (action is MenuAction.OpenLogs)
        {
            // A UI action: the engine has no folder window to open.
            _shell.OpenLogs();
            return;
        }
        button.IsEnabled = false;
        try
        {
            await _controller.PerformAsync(action);
        }
        catch (Exception ex)
        {
            Log.Error($"action \"{action.Label}\" failed", ex);
            ShowMessage($"{action.Label} failed: {ex.Message}", critical: true);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    #endregion

    #region Content

    private void UpdateControls()
    {
        var active = _status.Phase == SessionPhase.Active && !_busy;
        HoldToEnd.IsEnabled = active;
        ExtendButton.IsEnabled = active;
        ExtendEntry.IsEnabled = active;
        StartButton.IsEnabled = !_busy;
    }

    private void UpdateSubline()
    {
        var now = DateTimeOffset.UtcNow;
        var attention = false;
        string text;
        if (_view == View.Countdown)
        {
            Subline.Margin = new Thickness(0, 0, 76, 0);
            text = _status.Phase switch
            {
                SessionPhase.Ending => "Ending the session…",
                SessionPhase.Active when _status.Session is { } session => StatusText.EndsPhrase(session.EndsAt, now),
                _ => "Starting…",
            };
        }
        else
        {
            Subline.Margin = new Thickness(0);
            var input = StartEntry.Input;
            if (_status.StartBlockedReason is { Length: > 0 } reason)
            {
                text = reason;
                attention = true;
            }
            else if (input.IsEmpty)
            {
                text = $"Type a duration, or press Enter for {SessionMath.FormatPreset(_controller.Config.DefaultPreset)}";
            }
            else if (input.Total is { } total)
            {
                text = "Stays awake " + UntilPhrase(now + total, now);
            }
            else
            {
                text = "Enter at least 1 minute";
            }
        }
        Subline.Text = text;
        Subline.SetResourceReference(TextBlock.ForegroundProperty, attention ? "Brush.Caution" : "Brush.TextSecondary");
        Subline.ToolTip = text.Length > 40 ? text : null;
    }

    /// <summary>"until 6:40 PM", "until tomorrow at 6:40 PM", "until Tuesday at 6:40 PM".</summary>
    private static string UntilPhrase(DateTimeOffset endsAt, DateTimeOffset now)
    {
        var phrase = StatusText.AtPhrase(endsAt, now);
        return phrase.StartsWith("at ", StringComparison.Ordinal) ? "until " + phrase[3..] : "until " + phrase;
    }

    private void RefreshPresets()
    {
        var config = _controller.Config;
        if (config.Presets.SequenceEqual(_presets) && config.DefaultPreset.Equals(_defaultPreset))
        {
            return;
        }
        _presets = [.. config.Presets];
        _defaultPreset = config.DefaultPreset;
        Presets.Children.Clear();
        foreach (var seconds in _presets)
        {
            var label = SessionMath.FormatPreset(seconds);
            var chip = new Button
            {
                Content = label,
                Margin = new Thickness(3),
                ToolTip = seconds.Equals(_defaultPreset) ? "Default. Enter starts this when the fields are empty." : null,
            };
            chip.SetResourceReference(StyleProperty, "Button.Chip");
            if (seconds.Equals(_defaultPreset))
            {
                chip.FontWeight = FontWeights.SemiBold;
            }
            AutomationProperties.SetName(chip, $"Fill {label}");
            chip.Click += (_, _) =>
            {
                HideMessage();
                StartEntry.SetInput(DurationInput.FromSeconds(seconds));
                StartEntry.FocusField(_lastStartField);
            };
            Presets.Children.Add(chip);
        }
    }

    private void HideMessage()
    {
        ErrorBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateWarnings(bool animate)
    {
        var warnings = _status.Warnings;
        var ids = warnings.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _warningRows.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _warningRows.Remove(gone);
        }
        if (warnings.Count == 0)
        {
            WarningsPanel.Children.Clear();
            WarningsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        WarningsPanel.Visibility = Visibility.Visible;
        var reduced = Motion.ReduceMotion;
        var added = new List<WarningRow>();
        var desired = new List<UIElement> { _warningDivider };
        foreach (var warning in warnings)
        {
            if (!_warningRows.TryGetValue(warning.Id, out var row))
            {
                row = new WarningRow(this);
                _warningRows[warning.Id] = row;
                added.Add(row);
            }
            row.Update(warning);
            desired.Add(row.Element);
        }
        // Touch the panel only when rows came, went or moved, so a focused
        // action link keeps focus while unrelated status changes arrive.
        if (!WarningsPanel.Children.Cast<UIElement>().SequenceEqual(desired))
        {
            WarningsPanel.Children.Clear();
            foreach (var element in desired)
            {
                WarningsPanel.Children.Add(element);
            }
        }
        foreach (var row in added)
        {
            if (animate)
            {
                Motion.Jump(row.Element, OpacityProperty, 0);
                Motion.To(row.Element, OpacityProperty, 1, Motion.Base, reduced);
                if (!reduced)
                {
                    Motion.Jump(row.Shift, TranslateTransform.YProperty, -6);
                    Motion.SpringTo(row.Shift, TranslateTransform.YProperty, 0, Motion.Base);
                }
            }
        }
    }

    /// <summary>One warning: severity glyph, text, and its action as a link.</summary>
    private sealed class WarningRow
    {
        private readonly FlyoutWindow _owner;
        private readonly TextBlock _glyph;
        private readonly TextBlock _text;
        private readonly Button _action;
        private MenuWarning? _warning;

        public WarningRow(FlyoutWindow owner)
        {
            _owner = owner;
            _glyph = new TextBlock { Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top, FontSize = 16 };
            _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
            _text = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
            _text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
            _action = new Button { Margin = new Thickness(-6, 2, 0, 0), FontSize = 13 };
            _action.SetResourceReference(StyleProperty, "Button.Link");
            _action.Click += (_, _) =>
            {
                if (_warning?.Action is { } action)
                {
                    _ = _owner.PerformAsync(action, _action);
                }
            };
            var column = new StackPanel();
            column.Children.Add(_text);
            column.Children.Add(_action);
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6), RenderTransform = Shift };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(column, 1);
            grid.Children.Add(_glyph);
            grid.Children.Add(column);
            Element = grid;
        }

        public FrameworkElement Element { get; }

        public TranslateTransform Shift { get; } = new();

        public void Update(MenuWarning warning)
        {
            _warning = warning;
            _glyph.Text = warning.Severity switch
            {
                WarningSeverity.Error => Glyphs.Error,
                WarningSeverity.Warning => Glyphs.Warning,
                _ => Glyphs.Info,
            };
            _glyph.SetResourceReference(TextBlock.ForegroundProperty, warning.Severity switch
            {
                WarningSeverity.Error => "Brush.Critical",
                WarningSeverity.Warning => "Brush.Caution",
                _ => "Brush.AccentText",
            });
            _text.Text = warning.Text;
            AutomationProperties.SetName(Element, warning.Text);
            if (warning.Action is { } action)
            {
                _action.Content = action.Label;
                _action.Visibility = Visibility.Visible;
            }
            else
            {
                _action.Visibility = Visibility.Collapsed;
            }
        }
    }

    #endregion

    #region Input

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
        e.Handled = true;
        if (_view == View.Entry && StartEntry.Visibility == Visibility.Visible && !_closing)
        {
            // Esc folds the pills into the eye, then the flyout leaves.
            StartEntry.Fold(animate: true, () =>
            {
                StartEntry.Clear();
                Dismiss();
            });
            return;
        }
        Dismiss();
    }

    #endregion

    private void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
