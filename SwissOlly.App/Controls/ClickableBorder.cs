using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using SwissOlly.App.ViewModels;

namespace SwissOlly.App.Controls;

/// <summary>
/// A lightweight wrapper around Border that shows a hand cursor on hover.
/// Used for legend rows and other clickable non-button elements.
/// Invokable rows are also a Tab stop: Enter or Space raises <see cref="Invoked"/>, a ring marks
/// keyboard focus, and assistive tech sees a button.
/// </summary>
internal sealed class ClickableBorder : UserControl
{
    private readonly Border _content;
    private readonly bool _invokable;
    private Brush? _restBorderBrush;

    public ClickableBorder(Border content, bool invokable = false)
    {
        _content = content;
        _invokable = invokable;
        Content = content;
        // No custom cursor: list rows keep the arrow, as Windows list items do, and the hover
        // fill marks them as clickable. Assigning a hand cursor from Loaded crashed the app
        // while the process list refreshed (see PointerCursors).

        if (!invokable)
        {
            return;
        }

        IsTabStop = true;
        UseSystemFocusVisuals = false;
        // Reserve the ring's width up front, taken out of the padding, so focusing a row never
        // shifts its contents and rows keep lining up with headers that have no ring.
        if (_content.BorderThickness == default)
        {
            var ring = UiMetrics.Hairline;
            var padding = _content.Padding;
            _content.BorderThickness = new Thickness(ring);
            _content.BorderBrush = BrushFactory.CreateBrush("#00000000");
            _content.Padding = new Thickness(
                Math.Max(0, padding.Left - ring),
                Math.Max(0, padding.Top - ring),
                Math.Max(0, padding.Right - ring),
                Math.Max(0, padding.Bottom - ring));
        }

        KeyDown += OnKeyDown;
        GotFocus += (_, _) =>
        {
            if (FocusState == FocusState.Keyboard)
            {
                _restBorderBrush = _content.BorderBrush;
                _content.BorderBrush = ThemeVisuals.ResolveBrush("AccentStrongBrush", "#B6D9FF");
            }
        };
        LostFocus += (_, _) =>
        {
            if (_restBorderBrush is not null)
            {
                _content.BorderBrush = _restBorderBrush;
                _restBorderBrush = null;
            }
        };
    }

    /// <summary>Enter, Space, or an assistive-technology invoke.</summary>
    public event EventHandler? Invoked;

    internal void RaiseInvoked() => Invoked?.Invoke(this, EventArgs.Empty);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            RaiseInvoked();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        _invokable ? new InvokableRowAutomationPeer(this) : base.OnCreateAutomationPeer();

    private sealed class InvokableRowAutomationPeer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        public InvokableRowAutomationPeer(ClickableBorder owner)
            : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(ClickableBorder);

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

        public void Invoke() => ((ClickableBorder)Owner).RaiseInvoked();
    }
}
