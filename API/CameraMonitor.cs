using Microsoft.Win32;
using Serilog;
using System;
using System.Threading;

namespace TEAMS2HA.API
{
    /// <summary>
    /// Detects whether Teams has the camera open by reading the Windows Privacy Consent Store
    /// registry key — the same source that drives the camera privacy LED/indicator.
    /// LastUsedTimeStop == 0  →  session still open  →  video is ON
    /// LastUsedTimeStop != 0  →  session closed       →  video is OFF
    /// </summary>
    public sealed class CameraMonitor : IDisposable
    {
        private static readonly Lazy<CameraMonitor> _instance = new(() => new CameraMonitor());
        public static CameraMonitor Instance => _instance.Value;

        private const string NewTeamsWebcamKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\MSTeams_8wekyb3d8bbwe";

        private Timer? _pollTimer;
        private bool _lastCameraActive;
        private bool _disposed;

        /// <summary>Fires when Teams camera state changes: true = video ON, false = video OFF.</summary>
        public event EventHandler<bool>? CameraActiveChanged;

        private CameraMonitor() { }

        public void Start(int intervalMs = 500)
        {
            _pollTimer = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(intervalMs));
            Log.Information("CameraMonitor: Started, polling every {interval}ms", intervalMs);
        }

        public void Stop() => _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        private void Poll(object? state)
        {
            try
            {
                bool active = IsCameraActive();
                if (active != _lastCameraActive)
                {
                    _lastCameraActive = active;
                    Log.Debug("CameraMonitor: Teams camera active={active}", active);
                    CameraActiveChanged?.Invoke(this, active);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "CameraMonitor: Poll error");
            }
        }

        private static bool IsCameraActive()
        {
            using var key = Registry.CurrentUser.OpenSubKey(NewTeamsWebcamKey);
            if (key == null) return false;

            var stopTime = key.GetValue("LastUsedTimeStop");

            // null = key absent (camera was never used or Teams just started using it)
            // 0    = camera session is still open → video ON
            // > 0  = FILETIME when camera was released → video OFF
            if (stopTime == null)
            {
                // If start exists but stop doesn't, assume in use
                return key.GetValue("LastUsedTimeStart") != null;
            }

            return stopTime is long t && t == 0L;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pollTimer?.Dispose();
        }
    }
}
