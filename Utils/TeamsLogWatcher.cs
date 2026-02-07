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

        private static readonly Regex StatusRegex = new Regex(
            @"tags=Main\) SetTaskbarIconOverlay.*status (.+?)\s*$",
            RegexOptions.Compiled);

        private readonly Timer _pollTimer;
        private FileStream? _fileStream;
        private StreamReader? _reader;
        private string? _currentLogFile;
        private string _lastStatus = string.Empty;
        private bool _disposed;

        public event EventHandler<string>? StatusChanged;

        public string CurrentStatus => _lastStatus;

        public TeamsLogWatcher()
        {
            // Poll every 2 seconds for new log content
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
            _pollTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
            Log.Information("TeamsLogWatcher started, monitoring: {path}", LogDirectory);
        }

        public void Stop()
        {
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
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

                // Scan the existing file for the most recent status before tailing
                ReadInitialStatus(latestFile);

                _fileStream = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _reader = new StreamReader(_fileStream);

                // Seek to end — from here on we only process new lines
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

        private void ReadInitialStatus(string filePath)
        {
            try
            {
                string? lastFoundStatus = null;

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var match = StatusRegex.Match(line);
                    if (match.Success)
                    {
                        lastFoundStatus = match.Groups[1].Value;
                    }
                }

                if (lastFoundStatus != null && lastFoundStatus != _lastStatus)
                {
                    _lastStatus = lastFoundStatus;
                    Log.Information("TeamsLogWatcher initial status from log: {status}", lastFoundStatus);
                    StatusChanged?.Invoke(this, lastFoundStatus);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error reading initial status from Teams log file");
            }
        }

        private void PollLogFile(object? state)
        {
            try
            {
                // Check if Teams has rotated to a new log file
                var latestFile = GetLatestLogFile();
                if (latestFile != null && latestFile != _currentLogFile)
                {
                    Log.Information("Teams log file rotated to: {file}", Path.GetFileName(latestFile));
                    CloseCurrentFile();

                    _fileStream = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    _reader = new StreamReader(_fileStream);
                    _currentLogFile = latestFile;
                    // Read from the beginning for new files so we don't miss initial status
                }

                if (_reader == null)
                    return;

                string? line;
                while ((line = _reader.ReadLine()) != null)
                {
                    var match = StatusRegex.Match(line);
                    if (match.Success)
                    {
                        var status = match.Groups[1].Value;
                        if (status != _lastStatus)
                        {
                            _lastStatus = status;
                            Log.Information("Teams status changed to: {status}", status);
                            StatusChanged?.Invoke(this, status);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error polling Teams log file");
            }
        }

        private string? GetLatestLogFile()
        {
            try
            {
                var files = Directory.GetFiles(LogDirectory, "MSTeams_*.log");
                if (files.Length == 0)
                    return null;

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

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _pollTimer.Dispose();
                CloseCurrentFile();
            }
        }
    }
}
