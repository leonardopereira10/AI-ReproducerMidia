using System.Globalization;
using System.Text;

namespace CATRA.Core.Library;

/// <summary>
/// Dependency-free, thread-safe file logger used for crash diagnostics.
/// WPF apps run as <c>WinExe</c>, so <c>Console</c>/<c>stderr</c> output is
/// invisible to a user who launches the EXE normally; everything important is
/// mirrored to a rolling log file under <c>%LOCALAPPDATA%/CATRA/logs/</c> so a
/// render crash (e.g. <c>MediaContext.RenderMessageHandlerCore</c>) can be
/// inspected after the fact.
/// </summary>
public static class DiagnosticsLogger
{
    private static readonly object Gate = new();
    private static string? _logFilePath;

    /// <summary>Absolute path of the active log file (created lazily).</summary>
    public static string LogFilePath
    {
        get
        {
            lock (Gate)
            {
                return _logFilePath ??= ResolveLogFilePath();
            }
        }
    }

    /// <summary>Writes an informational line.</summary>
    public static void Info(string message) => Write("INFO", message, null);

    /// <summary>Writes a warning line.</summary>
    public static void Warn(string message) => Write("WARN", message, null);

    /// <summary>Writes an error line with the full exception chain.</summary>
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>Writes a fatal line with the full exception chain.</summary>
    public static void Fatal(string message, Exception? exception = null) => Write("FATAL", message, exception);

    /// <summary>
    /// Formats <paramref name="exception"/> including every inner and aggregate
    /// exception plus stack traces, so a wrapped WPF/MIL/DXGI failure is never
    /// truncated to its outer message.
    /// </summary>
    public static string FormatException(Exception? exception)
    {
        if (exception is null)
        {
            return "(no exception)";
        }

        var builder = new StringBuilder();
        FormatExceptionRecursive(exception, builder, depth: 0);
        return builder.ToString();
    }

    private static void Write(string level, string message, Exception? exception)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string thread = Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture);

        var line = new StringBuilder();
        line.Append('[').Append(timestamp).Append("] [").Append(level)
            .Append("] [tid=").Append(thread).Append("] ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(FormatException(exception));
        }

        string text = line.ToString();

        lock (Gate)
        {
            // Always mirror to the debug trace so an attached debugger sees it too.
            System.Diagnostics.Trace.WriteLine(text);

            try
            {
                string path = LogFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, text + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    private static void FormatExceptionRecursive(Exception exception, StringBuilder builder, int depth)
    {
        string indent = new(' ', depth * 2);

        builder.Append(indent).Append("--> ").Append(exception.GetType().FullName)
            .Append(": ").AppendLine(exception.Message);

        if (!string.IsNullOrEmpty(exception.HelpLink))
        {
            builder.Append(indent).Append("    HelpLink: ").AppendLine(exception.HelpLink);
        }

        // COM/DXGI failures carry the HRESULT in HResult; surface it explicitly
        // because WPF render exceptions are often thin wrappers over a COM error.
        builder.Append(indent)
            .Append("    HResult: 0x")
            .AppendLine(exception.HResult.ToString("X8", CultureInfo.InvariantCulture));

        if (exception.StackTrace is not null)
        {
            builder.Append(indent).AppendLine("    StackTrace:").AppendLine(Prefix(exception.StackTrace, indent + "      "));
        }

        if (exception is AggregateException aggregate)
        {
            for (int i = 0; i < aggregate.InnerExceptions.Count; i++)
            {
                builder.Append(indent).Append("    [inner #").Append(i).AppendLine("]");
                FormatExceptionRecursive(aggregate.InnerExceptions[i], builder, depth + 1);
            }
        }
        else if (exception.InnerException is not null)
        {
            builder.Append(indent).AppendLine("    [inner]");
            FormatExceptionRecursive(exception.InnerException, builder, depth + 1);
        }
    }

    private static string Prefix(string text, string prefix)
    {
        var builder = new StringBuilder();
        foreach (string line in text.Split('\n'))
        {
            builder.Append(prefix).AppendLine(line.TrimEnd('\r'));
        }

        return builder.ToString().TrimEnd();
    }

    private static string ResolveLogFilePath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string logDir = Path.Combine(baseDir, "CATRA", "logs");
        string fileName = $"catra-{DateTime.Now:yyyyMMdd}.log";
        return Path.Combine(logDir, fileName);
    }
}
