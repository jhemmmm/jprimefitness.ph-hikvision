using HikVision.Models;

namespace HikVision.Services;

public class AttendanceCallbackProbeService
{
    private readonly object _lock = new();
    private long _callbackCount;
    private AttendanceCallbackSnapshot? _lastCallback;

    public AttendanceCallbackSnapshot Record(AttendanceCallbackSnapshot snapshot)
    {
        snapshot.CallbackCount = Interlocked.Increment(ref _callbackCount);

        lock (_lock)
        {
            _lastCallback = snapshot;
        }

        return snapshot;
    }

    public AttendanceCallbackSnapshot? GetLastCallback()
    {
        lock (_lock)
        {
            return _lastCallback;
        }
    }
}
