using Microsoft.Win32;
using Serilog;
using System;
using System.Threading;

namespace TEAMS2HA.API
{
    /// <summary>
    /// Detects whether Teams is accessing the microphone via the Windows Privacy Consent Store.
    /// LastUsedTimeStop == 0  →  mic session open  →  Teams is in an active call (and if new Teams
    ///                           releases the mic device on mute, this also indicates unmuted).
    /// LastUsedTimeStop != 0  →  mic released      →  not in call, or muted if Teams releases on mute.
    /// Use alongside WasapiMonitor.MuteStateChanged which reads the session's actual mute flag.
    /// </summary>
    public sealed class MicrophoneMonitor : IDisposable
    {
        private static readonly Lazy<MicrophoneMonitor> _instance = new(() => new MicrophoneMonitor());
        public static MicrophoneMonitor Instance => _instance.Value;

        private const string NewTeamsMicKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\MSTeams_8wekyb3d8bbwe";

        private Timer? _pollTimer;
        private bool _lastMicActive;
        private bool _disposed;

        /// <summary>
        /// true  = Teams is actively capturing the microphone.
        /// false = Teams has released the microphone.
        /// </summary>
        public event EventHandler<bool>? MicActiveChanged;

        private MicrophoneMonitor() { }

        public void Start(int intervalMs = 2000)
        {
            _pollTimer = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(intervalMs));
            Log.Information("MicrophoneMonitor: Started, polling every {interval}ms", intervalMs);
        }

        public void Stop() => _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        private void Poll(object? state)
        {
            try
            {
                bool active = IsMicActive();
                if (active != _lastMicActive)
                {
                    _lastMicActive = active;
                    Log.Debug("MicrophoneMonitor: Teams mic active={active}", active);
                    MicActiveChanged?.Invoke(this, active);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MicrophoneMonitor: Poll error");
            }
        }

        private static bool IsMicActive()
        {
            using var key = Registry.CurrentUser.OpenSubKey(NewTeamsMicKey);
            if (key == null) return false;

            var stopTime = key.GetValue("LastUsedTimeStop");

            if (stopTime == null)
                return key.GetValue("LastUsedTimeStart") != null;

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
