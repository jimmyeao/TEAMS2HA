using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Serilog;

namespace TEAMS2HA.Utils
{
    public class TeamsLogWatcher : IDisposable
    {
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\Logs");

        // New Teams (MSTeams_*.log) patterns
        private static readonly Regex PresenceActionRegex = new Regex(
            @"UserPresenceAction:\s*\{cloud_context:\s*https://teams\.microsoft\.com,\s*availability:\s*(\w+)\}",
            RegexOptions.Compiled);

        private static readonly Regex GlobalStateUnreadRegex = new Regex(
            @"unread notification count:\s*(\d+)",
            RegexOptions.Compiled);

        private static readonly Regex MeetingCountRegex = new Regex(
            @"OnMeetingDetailsUpdateMeeting details updated for store:.*with\s+(\d+)\s+meetings",
            RegexOptions.Compiled);

        // Real-time call state events — fire immediately on join/leave/mute
        private static readonly Regex MuteStateRegex = new Regex(
            @"CallInfo: NotifyCallMuteStateChanged.*muteState:\s*(true|false)",
            RegexOptions.Compiled);

        private static readonly Regex CallActiveRegex = new Regex(
            @"CallInfo: NotifyCallActive\b",
            RegexOptions.Compiled);

        private static readonly Regex CallEndedRegex = new Regex(
            @"CallInfo: NotifyCallEnded\b",
            RegexOptions.Compiled);

        // Screen sharing (broad patterns until a real log sample confirms exact text)
        private static readonly Regex SharingStartedRegex = new Regex(
            @"(?:ContentSharing.*start|StartSharing|sharing.*started|ScreenShare.*start)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SharingStoppedRegex = new Regex(
            @"(?:ContentSharing.*stop|StopSharing|sharing.*stopped|ScreenShare.*stop)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Recording (broad patterns until a real log sample confirms exact text)
        private static readonly Regex RecordingStartedRegex = new Regex(
            @"(?:recording.*started|RecordingState.*true|StartRecording)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RecordingStoppedRegex = new Regex(
            @"(?:recording.*stopped|RecordingState.*false|StopRecording)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Classic Teams fallback pattern
        private static readonly Regex ClassicStatusRegex = new Regex(
            @"tags=Main\) SetTaskbarIconOverlay.*status (.+?)\s*$",
            RegexOptions.Compiled);

        private readonly Timer _pollTimer;
        private FileSystemWatcher? _fileWatcher;
        private readonly object _readLock = new();
        private FileStream? _fileStream;
        private StreamReader? _reader;
        private string? _currentLogFile;
        private string _lastStatus = string.Empty;
        private bool _lastMeetingState;
        private bool _lastMuteState;
        private bool _lastSharingState;
        private bool _lastRecordingState;
        private bool _lastUnreadState;
        private bool _disposed;

        // Fires with Teams availability string ("Available", "Away", "Busy", etc.)
        public event EventHandler<string>? StatusChanged;

        // Fires with true when in a meeting, false when not
        public event EventHandler<bool>? MeetingStateChanged;

        // Fires with true when muted, false when unmuted
        public event EventHandler<bool>? MuteStateChanged;

        // Fires with true when screen sharing, false when stopped
        public event EventHandler<bool>? SharingStateChanged;

        // Fires with true when recording is on, false when off
        public event EventHandler<bool>? RecordingStateChanged;

        // Fires with true when there are unread messages
        public event EventHandler<bool>? UnreadMessagesChanged;

        public string CurrentStatus => _lastStatus;

        // True while the app has seen a CallActive event with no subsequent CallEnded.
        public bool IsInCall => _lastMeetingState;

        public TeamsLogWatcher()
        {
            _pollTimer = new Timer(PollLogFile, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            if (!Directory.Exists(LogDirectory))
            {
                Log.Warning("Teams log directory not found: {path}", LogDirectory);
                return;
            }

            OpenLatestLogFile();

            // Start poll timer unconditionally — this is the reliable baseline.
            _pollTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));

            // FileSystemWatcher provides near-instant detection on top of the poll timer.
            // UWP sandbox paths can deny ReadDirectoryChangesW, so wrap in try-catch.
            try
            {
                _fileWatcher = new FileSystemWatcher(LogDirectory, "MSTeams_*.log")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _fileWatcher.Changed += (s, e) =>
                {
                    if (string.Equals(e.FullPath, _currentLogFile, StringComparison.OrdinalIgnoreCase))
                        ReadNewLines();
                };
                _fileWatcher.Created += (s, e) => PollLogFile(null);
                Log.Information("TeamsLogWatcher: FileSystemWatcher enabled for instant detection");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "TeamsLogWatcher: FileSystemWatcher failed — poll-only mode active");
                _fileWatcher = null;
            }

            Log.Information("TeamsLogWatcher started, monitoring: {path}", LogDirectory);
        }

        public void Stop()
        {
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            CloseCurrentFile();
            Log.Information("TeamsLogWatcher stopped.");
        }

        private void OpenLatestLogFile()
        {
            try
            {
                var latestFile = GetLatestLogFile();
                if (latestFile == null)
                {
                    Log.Debug("No Teams log files found.");
                    return;
                }

                if (latestFile == _currentLogFile && _fileStream != null)
                    return;

                CloseCurrentFile();
                ReadInitialState(latestFile);

                _fileStream = new FileStream(latestFile, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                _reader = new StreamReader(_fileStream);
                _fileStream.Seek(0, SeekOrigin.End);
                _reader.DiscardBufferedData();

                _currentLogFile = latestFile;
                Log.Information("TeamsLogWatcher opened log file: {file}", Path.GetFileName(latestFile));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error opening Teams log file");
            }
        }

        private void ReadInitialState(string filePath)
        {
            try
            {
                string? lastStatus = null;
                bool lastInMeeting = false;
                bool lastMuted = false;
                bool lastSharing = false;
                bool lastRecording = false;
                int totalUnread = 0;

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Presence action
                    var presenceMatch = PresenceActionRegex.Match(line);
                    if (presenceMatch.Success)
                    {
                        lastStatus = presenceMatch.Groups[1].Value;
                        continue;
                    }

                    // Meeting count (fires every ~30 min during calls)
                    var meetingMatch = MeetingCountRegex.Match(line);
                    if (meetingMatch.Success)
                    {
                        lastInMeeting = int.Parse(meetingMatch.Groups[1].Value) > 0;
                        continue;
                    }

                    // Real-time call active / ended
                    if (CallActiveRegex.IsMatch(line))
                    {
                        lastInMeeting = true;
                        continue;
                    }
                    if (CallEndedRegex.IsMatch(line))
                    {
                        lastInMeeting = false;
                        lastMuted = false;
                        lastSharing = false;
                        lastRecording = false;
                        continue;
                    }

                    // Mute state
                    var muteMatch = MuteStateRegex.Match(line);
                    if (muteMatch.Success)
                    {
                        lastMuted = muteMatch.Groups[1].Value == "true";
                        continue;
                    }

                    // Sharing
                    if (SharingStartedRegex.IsMatch(line)) { lastSharing = true; continue; }
                    if (SharingStoppedRegex.IsMatch(line)) { lastSharing = false; continue; }

                    // Recording
                    if (RecordingStartedRegex.IsMatch(line)) { lastRecording = true; continue; }
                    if (RecordingStoppedRegex.IsMatch(line)) { lastRecording = false; continue; }

                    // Unread count — sum all accounts in the line
                    if (line.Contains("UserDataGlobalState"))
                    {
                        totalUnread = 0;
                        foreach (Match m in GlobalStateUnreadRegex.Matches(line))
                            totalUnread += int.Parse(m.Groups[1].Value);
                        continue;
                    }

                    // Classic Teams fallback
                    var classicMatch = ClassicStatusRegex.Match(line);
                    if (classicMatch.Success)
                        lastStatus = classicMatch.Groups[1].Value;
                }

                if (lastStatus != null && lastStatus != _lastStatus)
                {
                    _lastStatus = lastStatus;
                    Log.Information("TeamsLogWatcher initial status: {status}", lastStatus);
                    StatusChanged?.Invoke(this, lastStatus);
                }

                if (lastInMeeting != _lastMeetingState)
                {
                    _lastMeetingState = lastInMeeting;
                    MeetingStateChanged?.Invoke(this, lastInMeeting);
                }

                if (lastMuted != _lastMuteState)
                {
                    _lastMuteState = lastMuted;
                    MuteStateChanged?.Invoke(this, lastMuted);
                }

                if (lastSharing != _lastSharingState)
                {
                    _lastSharingState = lastSharing;
                    SharingStateChanged?.Invoke(this, lastSharing);
                }

                if (lastRecording != _lastRecordingState)
                {
                    _lastRecordingState = lastRecording;
                    RecordingStateChanged?.Invoke(this, lastRecording);
                }

                bool hasUnread = totalUnread > 0;
                if (hasUnread != _lastUnreadState)
                {
                    _lastUnreadState = hasUnread;
                    UnreadMessagesChanged?.Invoke(this, hasUnread);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error reading initial state from Teams log file");
            }
        }

        private void PollLogFile(object? state)
        {
            try
            {
                var latestFile = GetLatestLogFile();
                if (latestFile != null && !string.Equals(latestFile, _currentLogFile, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Teams log file rotated to: {file}", Path.GetFileName(latestFile));

                    // Do NOT call ReadInitialState here. The in-memory state (_lastMuteState etc.)
                    // is already correct. Calling ReadInitialState on a fresh log file would reset
                    // mute to false (no events yet) even if the user is currently muted.
                    // Just switch the file pointer to the end of the new file and read forward.
                    lock (_readLock)
                    {
                        CloseCurrentFile();
                        _fileStream = new FileStream(latestFile, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        _reader = new StreamReader(_fileStream);
                        _fileStream.Seek(0, SeekOrigin.End);
                        _reader.DiscardBufferedData();
                        _currentLogFile = latestFile;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in log file rotation check");
            }

            ReadNewLines();
        }

        private void ReadNewLines()
        {
            lock (_readLock)
            {
                if (_reader == null) return;
                try
                {
                    string? line;
                    while ((line = _reader.ReadLine()) != null)
                        ProcessLine(line);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error reading Teams log file");
                }
            }
        }

        private void ProcessLine(string line)
        {
            // Presence change
            var presenceMatch = PresenceActionRegex.Match(line);
            if (presenceMatch.Success)
            {
                var status = presenceMatch.Groups[1].Value;
                if (status != _lastStatus)
                {
                    _lastStatus = status;
                    Log.Information("Teams availability changed to: {status}", status);
                    StatusChanged?.Invoke(this, status);
                }
                return;
            }

            // Call joined
            if (CallActiveRegex.IsMatch(line))
            {
                if (!_lastMeetingState)
                {
                    _lastMeetingState = true;
                    Log.Information("Teams call active (joined meeting)");
                    MeetingStateChanged?.Invoke(this, true);
                }
                return;
            }

            // Call ended — reset mute/sharing/recording too
            if (CallEndedRegex.IsMatch(line))
            {
                bool wasMeeting = _lastMeetingState;
                bool wasMuted = _lastMuteState;
                bool wasSharing = _lastSharingState;
                bool wasRecording = _lastRecordingState;

                _lastMeetingState = false;
                _lastMuteState = false;
                _lastSharingState = false;
                _lastRecordingState = false;

                if (wasMeeting)
                {
                    Log.Information("Teams call ended (left meeting)");
                    MeetingStateChanged?.Invoke(this, false);
                }
                if (wasMuted) MuteStateChanged?.Invoke(this, false);
                if (wasSharing) SharingStateChanged?.Invoke(this, false);
                if (wasRecording) RecordingStateChanged?.Invoke(this, false);
                return;
            }

            // Mute state
            var muteMatch = MuteStateRegex.Match(line);
            if (muteMatch.Success)
            {
                bool muted = muteMatch.Groups[1].Value == "true";
                if (muted != _lastMuteState)
                {
                    _lastMuteState = muted;
                    Log.Information("Teams mute changed to: {muted}", muted);
                    MuteStateChanged?.Invoke(this, muted);
                }
                return;
            }

            // Screen sharing start
            if (SharingStartedRegex.IsMatch(line))
            {
                if (!_lastSharingState)
                {
                    _lastSharingState = true;
                    Log.Information("Teams screen sharing started");
                    SharingStateChanged?.Invoke(this, true);
                }
                return;
            }

            // Screen sharing stop
            if (SharingStoppedRegex.IsMatch(line))
            {
                if (_lastSharingState)
                {
                    _lastSharingState = false;
                    Log.Information("Teams screen sharing stopped");
                    SharingStateChanged?.Invoke(this, false);
                }
                return;
            }

            // Recording started
            if (RecordingStartedRegex.IsMatch(line))
            {
                if (!_lastRecordingState)
                {
                    _lastRecordingState = true;
                    Log.Information("Teams recording started");
                    RecordingStateChanged?.Invoke(this, true);
                }
                return;
            }

            // Recording stopped
            if (RecordingStoppedRegex.IsMatch(line))
            {
                if (_lastRecordingState)
                {
                    _lastRecordingState = false;
                    Log.Information("Teams recording stopped");
                    RecordingStateChanged?.Invoke(this, false);
                }
                return;
            }

            // Meeting count (fires every ~30 min — supplements CallActive/CallEnded)
            var meetingMatch = MeetingCountRegex.Match(line);
            if (meetingMatch.Success)
            {
                bool inMeeting = int.Parse(meetingMatch.Groups[1].Value) > 0;
                if (inMeeting != _lastMeetingState)
                {
                    _lastMeetingState = inMeeting;
                    Log.Information("Teams meeting state changed (count): inMeeting={inMeeting}", inMeeting);
                    MeetingStateChanged?.Invoke(this, inMeeting);
                }
                return;
            }

            // Unread messages (periodic broadcast, sum all accounts)
            if (line.Contains("UserDataGlobalState"))
            {
                int total = 0;
                foreach (Match m in GlobalStateUnreadRegex.Matches(line))
                    total += int.Parse(m.Groups[1].Value);

                bool hasUnread = total > 0;
                if (hasUnread != _lastUnreadState)
                {
                    _lastUnreadState = hasUnread;
                    Log.Information("Teams unread messages changed: hasUnread={hasUnread}", hasUnread);
                    UnreadMessagesChanged?.Invoke(this, hasUnread);
                }
                return;
            }

            // Classic Teams fallback
            var classicMatch = ClassicStatusRegex.Match(line);
            if (classicMatch.Success)
            {
                var status = classicMatch.Groups[1].Value;
                if (status != _lastStatus)
                {
                    _lastStatus = status;
                    Log.Information("Teams status changed to: {status}", status);
                    StatusChanged?.Invoke(this, status);
                }
            }
        }

        private string? GetLatestLogFile()
        {
            try
            {
                var files = Directory.GetFiles(LogDirectory, "MSTeams_*.log");
                if (files.Length == 0) return null;
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                return files[files.Length - 1];
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error finding latest Teams log file");
                return null;
            }
        }

        private void CloseCurrentFile()
        {
            _reader?.Dispose();
            _fileStream?.Dispose();
            _reader = null;
            _fileStream = null;
            _currentLogFile = null;
        }

        /// <summary>
        /// Fires all current known state events unconditionally. Call this when the WebSocket
        /// drops so MQTT immediately reflects what the log last reported.
        /// </summary>
        public void PublishCurrentState()
        {
            if (!string.IsNullOrEmpty(_lastStatus))
                StatusChanged?.Invoke(this, _lastStatus);
            MeetingStateChanged?.Invoke(this, _lastMeetingState);
            MuteStateChanged?.Invoke(this, _lastMuteState);
            SharingStateChanged?.Invoke(this, _lastSharingState);
            RecordingStateChanged?.Invoke(this, _lastRecordingState);
            UnreadMessagesChanged?.Invoke(this, _lastUnreadState);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _pollTimer.Dispose();
                _fileWatcher?.Dispose();
                CloseCurrentFile();
            }
        }
    }
}
