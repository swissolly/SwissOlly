namespace Vaktr.Overlay.Fps;

public enum FpsAccuracy
{
    /// <summary>Counts the tracked process's own present calls, not frames confirmed displayed by the monitor.</summary>
    Exact,

    /// <summary>
    /// Derived from compositor cadence, which tracks the display refresh rate rather than the
    /// game. Correct only while the game is v-synced to the refresh rate; over-reports otherwise.
    /// </summary>
    Approximate,
}

public interface IFpsSource : IDisposable
{
    void StartTracking(uint pid);
    void StopTracking();

    event Action<double> FpsUpdated;
    event Action<string> ErrorOccurred;

    FpsAccuracy Accuracy { get; }
}
