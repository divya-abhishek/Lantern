namespace Lantern.Media.Mirroring;

/// <summary>Maps WASAPI device-frame positions to the session sample timeline.
/// Callback arrival time must never trim or pad a continuous sequence of PCM samples.</summary>
public sealed class CaptureAudioClock(int sampleRate, long originQpc100ns)
{
    private bool _anchored;
    private long _deviceOrigin, _sessionOrigin, _lastDevicePosition;

    public long Position(long devicePosition, long packetQpc100ns, bool discontinuity = false)
    {
        if (!_anchored || discontinuity || devicePosition < _lastDevicePosition)
        {
            _deviceOrigin = devicePosition;
            _sessionOrigin = (long)Math.Round((packetQpc100ns - originQpc100ns) * (double)sampleRate / 10_000_000);
            _anchored = true;
        }
        _lastDevicePosition = devicePosition;
        return _sessionOrigin + devicePosition - _deviceOrigin;
    }
}
