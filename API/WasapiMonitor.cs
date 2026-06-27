using NAudio.CoreAudioApi;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading;

namespace TEAMS2HA.API
{
    /// <summary>
    /// Detects Teams mute state by polling WASAPI capture sessions every 250 ms.
    /// Teams either sets the Windows-level mute flag or releases its capture session when muting
    /// (which is what drives the hardware mute LED on the microphone).
    /// We check both signals so either mechanism is caught.
    /// </summary>
    public sealed class WasapiMonitor : IDisposable
    {
        private static readonly Lazy<WasapiMonitor> _instance = new(() => new WasapiMonitor());
        public static WasapiMonitor Instance => _instance.Value;

        private Timer? _pollTimer;
        private bool _lastMuteState;
        private bool _disposed;

        /// <summary>true = muted, false = unmuted.</summary>
        public event EventHandler<bool>? MuteStateChanged;

        private WasapiMonitor() { }

        public void Start(int intervalMs = 250)
        {
            _pollTimer = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(intervalMs));
            Log.Information("WasapiMonitor: polling WASAPI capture sessions every {ms}ms", intervalMs);
        }

        public void Stop() => _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        private void Poll(object? state)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                bool teamsSessionFound = false;
                bool teamsHwMuted = false;

                foreach (var device in devices)
                {
                    try
                    {
                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            if (!IsTeamsProcess(session.GetProcessID)) continue;

                            teamsSessionFound = true;
                            bool hwMute = session.SimpleAudioVolume.Mute;
                            string stateStr = session.State.ToString();
                            Log.Debug("WasapiMonitor: Teams session state={state}, hwMute={hwMute}, device={device}",
                                stateStr, hwMute, device.FriendlyName);

                            if (hwMute)
                                teamsHwMuted = true;
                        }
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }

                // Muted if Teams has no session at all, or if it set the Windows mute flag.
                bool muted = !teamsSessionFound || teamsHwMuted;

                Log.Debug("WasapiMonitor: sessionFound={found}, hwMuted={hw} → muted={muted}",
                    teamsSessionFound, teamsHwMuted, muted);

                SetMuteState(muted);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WasapiMonitor: poll error");
            }
        }

        private void SetMuteState(bool muted)
        {
            if (muted == _lastMuteState) return;
            _lastMuteState = muted;
            Log.Information("WasapiMonitor: mute → {muted}", muted);
            MuteStateChanged?.Invoke(this, muted);
        }

        private static bool IsTeamsProcess(uint pid)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName.Equals("ms-teams", StringComparison.OrdinalIgnoreCase)
                    || proc.ProcessName.Equals("MSTeams", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pollTimer?.Dispose();
        }
    }
}
