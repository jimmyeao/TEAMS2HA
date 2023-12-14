using Serilog;
using Serilog.Sinks.File;
using Serilog.Sinks.SystemConsole;
using System;
using System.IO;
using System.Text;

namespace TEAMS2HA
{
    public static class LoggingConfig
    {
        #region Public Fields

        public static string? logFileFullPath;

        #endregion Public Fields

        #region Public Methods

        public static void Configure()
        {
            var filePathHook = new CaptureFilePathHook();
            string appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folderPath = Path.Combine(appDataFolder, "Teams2HA");
            var logFilePath = Path.Combine(folderPath, "Teams2ha_Log.log");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day, hooks: filePathHook)
                .CreateLogger();
            Log.Information("Logger Created");
            logFileFullPath = filePathHook.Path;
        }

        #endregion Public Methods
    }


    internal class CaptureFilePathHook : FileLifecycleHooks
    {
        #region Public Properties

        public string? Path { get; private set; }

        #endregion Public Properties

        #region Public Methods

        public override Stream OnFileOpened(string path, Stream underlyingStream, Encoding encoding)
        {
            Path = path;
            return base.OnFileOpened(path, underlyingStream, encoding);
        }

        #endregion Public Methods
    }
}
