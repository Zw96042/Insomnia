using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Insomnia.App.Animation;

namespace Insomnia.App.Controls;

/// <summary>
/// The live countdown. Each character sits in its own slot and digit slots
/// share one width, so the text never reflows as it ticks; a changed digit
/// rolls up into place on a quick spring (a crossfade with animation effects
/// off). The countdown shape is fixed for a session, so slots are rebuilt
/// only when the shape itself changes (for example "1d 0:00:00" to "23:59:59").
/// </summary>
public sealed class CountdownText : StackPanel
{
    private readonly List<CharacterSlot> _slots = [];
    private string _text = "";

    public CountdownText()
    {
        Orientation = Orientation.Horizontal;
        SnapsToDevicePixels = true;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Off);
    }

    public string Text => _text;

    /// <summary>Show <paramref name="text"/>, rolling the digits that changed when <paramref name="animate"/> is true.</summary>
    public void SetText(string text, bool animate)
    {
        if (text == _text)
        {
            return;
        }
        if (!SameShape(text, _text))
        {
            Rebuild(text);
        }
        else
        {
            var reduced = Motion.ReduceMotion;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != _text[i])
                {
                    _slots[i].Show(text[i], animate, reduced);
                }
            }
        }
        _text = text;
        AutomationProperties.SetName(this, text);
    }

    private static bool SameShape(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Length; i++)
        {
            if (char.IsAsciiDigit(a[i]) != char.IsAsciiDigit(b[i]) || (!char.IsAsciiDigit(a[i]) && a[i] != b[i]))
            {
                return false;
            }
        }
        return true;
    }

    private void Rebuild(string text)
    {
        Children.Clear();
        _slots.Clear();
        var digitWidth = DigitWidth();
        foreach (var c in text)
        {
            var slot = new CharacterSlot(c, char.IsAsciiDigit(c) ? digitWidth : double.NaN);
            _slots.Add(slot);
            Children.Add(slot);
        }
    }

    /// <summary>Widest digit in the current font, so every digit slot is the same width even without tabular figures.</summary>
    private double DigitWidth()
    {
        var typeface = new Typeface(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            TextElement.GetFontWeight(this),
            TextElement.GetFontStretch(this));
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var widest = 0.0;
        for (var d = 0; d <= 9; d++)
        {
            var formatted = new FormattedText(
                d.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                TextElement.GetFontSize(this),
                Brushes.Black,
                pixelsPerDip);
            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
        }
        return Math.Ceiling(widest);
    }

    /// <summary>One character: the current glyph and, during a roll, the outgoing one.</summary>
    private sealed class CharacterSlot : Grid
    {
        private readonly TextBlock _current;
        private readonly TextBlock _outgoing;
        private readonly TranslateTransform _currentShift = new();
        private readonly TranslateTransform _outgoingShift = new();

        public CharacterSlot(char c, double width)
        {
            ClipToBounds = true;
            if (!double.IsNaN(width))
            {
                Width = width;
            }
            _outgoing = MakeText();
            _outgoing.RenderTransform = _outgoingShift;
            _outgoing.Opacity = 0;
            _current = MakeText();
            _current.RenderTransform = _currentShift;
            _current.Text = c.ToString();
            Children.Add(_outgoing);
            Children.Add(_current);
        }

        public void Show(char c, bool animate, bool reduced)
        {
            _outgoing.Text = _current.Text;
            _current.Text = c.ToString();
            if (!animate)
            {
                Motion.Jump(_outgoing, OpacityProperty, 0);
                Motion.Jump(_current, OpacityProperty, 1);
                Motion.Jump(_currentShift, TranslateTransform.YProperty, 0);
                return;
            }
            if (reduced)
            {
                Motion.Jump(_outgoing, OpacityProperty, 1);
                Motion.Jump(_current, OpacityProperty, 0);
                Motion.Jump(_outgoingShift, TranslateTransform.YProperty, 0);
                Motion.Jump(_currentShift, TranslateTransform.YProperty, 0);
                Motion.EaseTo(_outgoing, OpacityProperty, 0, Motion.ReducedDuration);
                Motion.EaseTo(_current, OpacityProperty, 1, Motion.ReducedDuration);
                return;
            }
            // Counting down: the new digit comes from above, the old one drops away.
            var travel = Math.Max(ActualHeight, 1) * 0.45;
            Motion.Jump(_outgoing, OpacityProperty, 1);
            Motion.Jump(_outgoingShift, TranslateTransform.YProperty, 0);
            Motion.Jump(_current, OpacityProperty, 0);
            Motion.Jump(_currentShift, TranslateTransform.YProperty, -travel);
            Motion.SpringTo(_outgoingShift, TranslateTransform.YProperty, travel, Motion.Tick);
            Motion.SpringTo(_outgoing, OpacityProperty, 0, Motion.Tick);
            Motion.SpringTo(_currentShift, TranslateTransform.YProperty, 0, Motion.Tick);
            Motion.SpringTo(_current, OpacityProperty, 1, Motion.Tick);
        }

        private static TextBlock MakeText() => new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
    }
}
