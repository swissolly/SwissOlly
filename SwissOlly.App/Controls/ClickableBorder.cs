namespace SwissOlly.App.Controls;

/// <summary>
/// A lightweight wrapper around Border that shows a hand cursor on hover.
/// Used for legend rows and other clickable non-button elements.
/// </summary>
internal sealed class ClickableBorder : UserControl
{
    public ClickableBorder(Border content)
    {
        Content = content;
        Loaded += (_, _) =>
        {
            try { ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand); }
            catch { /* cursor not supported in all hosts */ }
        };
    }
}
