namespace Vaktr.Overlay.Fps;

public interface IFpsSource : IDisposable
{
    void StartTracking(uint pid);
    void StopTracking();
    event Action<double> FpsUpdated;
}
