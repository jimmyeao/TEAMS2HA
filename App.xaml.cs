using System;
using System.Threading;
using System.Windows;
using Serilog; // Ensure Serilog is configured properly in the project

namespace TEAMS2HA
{
    public partial class App : Application
    {
        private static Mutex mutex = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "TEAMS2HA";
            bool createdNew;

            mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // App is already running! Exiting the application
                MessageBox.Show("An instance of TEAMS2HA is already running.", "Instance already running", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            // Configure logging here if not already configured
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File("logs\\TEAMS2HA_.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            // Handle unhandled exceptions
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error($"Unhandled exception caught on dispatcher thread: {e.Exception}");
            // Optionally, prevent application exit
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log.Error($"Unhandled exception caught on current domain: {e.ExceptionObject}");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.CloseAndFlush(); // Ensure all logs are flushed properly
            mutex?.ReleaseMutex(); // Release the mutex when application is exiting
            base.OnExit(e);
        }
    }
}
