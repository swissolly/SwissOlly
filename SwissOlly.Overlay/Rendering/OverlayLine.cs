using System.Drawing;

namespace SwissOlly.Overlay.Rendering;

/// <summary>
/// One HUD row, split into label and value so the renderer can align values into a column.
/// Rendering them as a single pre-joined string left the values ragged, because "GPU Temp:" is
/// far wider than "FPS:".
/// </summary>
/// <param name="Label">Metric name, without a trailing colon.</param>
/// <param name="Value">Formatted value, already padded to a stable width by the caller.</param>
/// <param name="Color">
/// Value colour. Carries threshold state (normal / warning / critical) so the HUD is glanceable;
/// the label is always drawn in the user's configured colour.
/// </param>
internal readonly record struct OverlayLine(string Label, string Value, Color Color);
