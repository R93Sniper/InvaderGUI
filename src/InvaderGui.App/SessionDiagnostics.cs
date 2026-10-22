using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InvaderGui.App;

internal static class SessionDiagnostics
{
    private static readonly object gate = new();
    private static readonly Stopwatch elapsed = Stopwatch.StartNew();
    private static string? logPath;
    private static readonly HashSet<string> sampledExceptions = new();
    private static long exceptionCount;
    private static double nextHeartbeat;
    [ThreadStatic] private static bool writingDiagnostics;

    public static void Start(string path)
    {
        logPath = path;
        Write($"Session started; PID={Environment.ProcessId}; {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; process={RuntimeInformation.ProcessArchitecture}");
    }

    public static void FirstChance(Exception exception)
    {
        if (logPath is null || writingDiagnostics) return;
        writingDiagnostics = true;
        try
        {
            Interlocked.Increment(ref exceptionCount);
            lock (gate)
            {
                if (sampledExceptions.Count >= 16) return;
                var key = $"{exception.GetType().FullName}: {exception.Message}";
                if (sampledExceptions.Add(key))
                    Write("First-chance exception sample (may be handled normally)", exception);
            }
        }
        catch (Exception)
        {
            // First-chance diagnostics must not interfere with exception dispatch.
        }
        finally { writingDiagnostics = false; }
    }

    public static bool BeginRefresh(string operation)
    {
        if (logPath is null || elapsed.Elapsed.TotalSeconds < nextHeartbeat) return false;
        nextHeartbeat = elapsed.Elapsed.TotalSeconds + 5;
        Write($"Idle refresh starting; operation={operation}; first-chance events={Interlocked.Read(ref exceptionCount)}; managed memory={GC.GetTotalMemory(false)} bytes");
        return true;
    }

    public static void Write(string message, Exception? exception = null)
    {
        if (logPath is null) return;
        var wasWriting = writingDiagnostics;
        writingDiagnostics = true;
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath,
                    $"{DateTimeOffset.UtcNow:O} +{elapsed.Elapsed.TotalSeconds:F3}s [thread {Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}" +
                    (exception is null ? "" : exception + Environment.NewLine));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Diagnostic output must never cause an application failure.
            Debug.WriteLine($"Could not write diagnostics: {ex.Message}");
        }
        finally { writingDiagnostics = wasWriting; }
    }
}
