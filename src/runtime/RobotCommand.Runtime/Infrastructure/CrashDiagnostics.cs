using System.Text;
using Microsoft.Extensions.Logging;

namespace RobotCommand.Infrastructure;

public static class CrashDiagnostics
{
    private static readonly object FileGate = new();
    private static int _installed;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Psycraft",
        "Robot Command",
        "Logs");

    public static string CurrentLogPath => Path.Combine(
        LogDirectory,
        $"robot-command-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        Write("INFO", "Diagnostics", "Crash diagnostics installed.");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteException("FATAL", "AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteException("ERROR", "TaskScheduler", args.Exception);
            args.SetObserved();
        };
    }

    public static void WriteException(string level, string source, Exception? exception)
        => Write(level, source, exception?.ToString() ?? "Unknown exception.");

    public static void Write(string level, string source, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var text = $"{DateTimeOffset.Now:O} [{level}] {source}: {message}{Environment.NewLine}";
            lock (FileGate)
            {
                File.AppendAllText(CurrentLogPath, text, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never take down the application.
        }
    }
}

public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string categoryName) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            CrashDiagnostics.Write(logLevel.ToString().ToUpperInvariant(), categoryName, message);
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }
}
