using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace InvaderGui.Core;

public sealed record ResolvedTagRoot(string Id, string Name, string Path);
public sealed record TaskLaunchStep(string OperationId, string Name, string ExecutablePath, ImmutableArray<string> Arguments);
public sealed record TaskStepResult(int Index, string Name, RunStatus Status, int? ExitCode, string? Diagnostic);
public sealed record TaskLaunchSnapshot(
    Guid RunId, string PageId, string SlotId, string OperationName,
    string ExecutablePath, Guid DiscoveryScanId, DateTimeOffset DiscoveryScanUtc,
    Guid? ProfileId, string ProfileName, string WorkingDirectory,
    string? DataRoot, ImmutableArray<ResolvedTagRoot> TagRoots, string? MapsRoot, string? ResourceMapsRoot,
    ImmutableDictionary<string, string> EffectiveOptions, ImmutableArray<string> Arguments,
    string Summary, string LogFilePath, bool Administrative)
{
    public ImmutableArray<TaskLaunchStep> Steps { get; init; } = [];
    public static TaskLaunchSnapshot Create(OperationDefinition operation, InstallationScan scan,
        ProjectProfile? profile, IEnumerable<string> arguments, string summary, string logsDirectory,
        bool administrative = false, IEnumerable<KeyValuePair<string, string>>? options = null)
    {
        if (!administrative && profile is null) throw new ArgumentException("Select an editing kit profile.");
        if (!administrative && profile!.Validate().Count != 0) throw new ArgumentException(string.Join("\n", profile.Validate()));
        var observation = scan.For(operation);
        if (!observation.Available) throw new FileNotFoundException($"{operation.Executable}: {observation.Reason}");
        var runId = Guid.NewGuid();
        var working = profile?.WorkingDirectory ?? scan.Directory;
        string Resolve(string path) => Path.GetFullPath(path, working);
        return new(runId, operation.Id, operation.SlotId, operation.Name, observation.FullPath, scan.Id, scan.ScannedUtc,
            profile?.Id, profile?.Name ?? "Installation", working,
            profile is null ? null : Resolve(profile.Paths.DataRoot),
            profile?.Paths.TagRoots.Select(r => new ResolvedTagRoot(r.Id, r.Name, Resolve(r.Path))).ToImmutableArray() ?? [],
            profile is null ? null : Resolve(profile.Paths.MapsRoot),
            profile?.Paths.ResourceMapsRoot is { } resource ? Resolve(resource) : null,
            options?.ToImmutableDictionary() ?? ImmutableDictionary<string, string>.Empty,
            arguments.ToImmutableArray(), summary, Path.Combine(logsDirectory, $"{runId:D}.output.jsonl"), administrative);
    }
}

public enum RunStatus { Starting, Running, Completed, NonzeroExit, StartFailed, Canceled }
public static class RunStatusDisplay
{
    public static string ToDisplayText(this RunStatus status) => status == RunStatus.NonzeroExit ? "Error" : status.ToString();
}
public sealed record OutputChunk(DateTimeOffset TimestampUtc, string Stream, string Text);
public sealed record RunView(TaskLaunchSnapshot Snapshot, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc,
    RunStatus Status, int? ExitCode, bool CancellationRequested, string? Diagnostic, string? LogError,
    TimeSpan Duration, string Output)
{
    public ImmutableArray<TaskStepResult> StepResults { get; init; } = [];
    public bool Active => Status is RunStatus.Starting or RunStatus.Running;
    public override string ToString() => $"{StartedUtc.LocalDateTime:MM/dd HH:mm:ss}  {Snapshot.OperationName}{(Snapshot.Administrative ? " (Help/Info)" : "")} · {Snapshot.ProfileName} · {Status.ToDisplayText()}";
}

public sealed class TaskRun
{
    private readonly object gate = new();
    private readonly OutputFormatter output = new();
    private readonly List<OutputChunk> chunks = new();
    private readonly List<TaskStepResult> stepResults = new();
    private RunStatus status = RunStatus.Starting;
    private DateTimeOffset? finished;
    private int? exitCode;
    private bool cancellationRequested;
    private string? diagnostic;
    private string? logError;
    private StreamWriter? writer;
    internal CancellationTokenSource Cancellation { get; } = new();
    public TaskLaunchSnapshot Snapshot { get; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public Task Completion { get; internal set; } = Task.CompletedTask;
    public RunView View { get { lock (gate) return new(Snapshot, StartedUtc, finished, status, exitCode,
        cancellationRequested, diagnostic, logError, (finished ?? DateTimeOffset.UtcNow) - StartedUtc, output.ToString()) { StepResults = stepResults.ToImmutableArray() }; } }
    public IReadOnlyList<OutputChunk> Output { get { lock (gate) return chunks.ToArray(); } }

    internal TaskRun(TaskLaunchSnapshot snapshot) => Snapshot = snapshot;

    public bool Cancel()
    {
        lock (gate)
        {
            if (status is not (RunStatus.Starting or RunStatus.Running)) return false;
            cancellationRequested = true;
        }
        Cancellation.Cancel();
        return true;
    }

    internal void OpenLog()
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Snapshot.LogFilePath)!);
                writer = new StreamWriter(new FileStream(Snapshot.LogFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { logError = ex.Message; }
        }
    }

    internal void Append(string stream, string text)
    {
        lock (gate)
        {
            var chunk = new OutputChunk(DateTimeOffset.UtcNow, stream, text);
            chunks.Add(chunk);
            output.Append(stream, text);
            try { writer?.WriteLine(JsonSerializer.Serialize(chunk)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            { logError = ex.Message; writer?.Dispose(); writer = null; }
        }
    }

    internal void SetRunning() { lock (gate) status = RunStatus.Running; }
    internal void RecordStep(TaskStepResult result) { lock (gate) stepResults.Add(result); }
    internal void AddDiagnostic(string message) { lock (gate) diagnostic = message; }
    internal void Finish(RunStatus result, int? code, string? message)
    {
        lock (gate)
        {
            status = cancellationRequested && result != RunStatus.StartFailed ? RunStatus.Canceled : result;
            exitCode = code; finished = DateTimeOffset.UtcNow;
            if (message is not null) diagnostic = message;
            try
            {
                writer?.Dispose(); writer = null;
                var metadataPath = Snapshot.LogFilePath + ".run.json";
                File.WriteAllText(metadataPath, JsonSerializer.Serialize(View with { Output = "" }, JsonFiles.Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { logError = ex.Message; }
        }
    }
}

public sealed class TaskCoordinator
{
    private readonly object gate = new();
    private readonly Dictionary<string, TaskRun> slots = new();
    private readonly Dictionary<string, SlotLease> leases = new();
    private readonly List<TaskRun> runs = new();
    public IReadOnlyList<TaskRun> Runs { get { lock (gate) return runs.ToArray(); } }
    public TaskRun? Owner(string slotId) { lock (gate) return slots.GetValueOrDefault(slotId); }

    public bool ForgetCompleted(Guid runId)
    {
        lock (gate)
        {
            var run = runs.FirstOrDefault(run => run.Snapshot.RunId == runId);
            return run is not null && !run.View.Active && run.Completion.IsCompleted && runs.Remove(run);
        }
    }

    public sealed class SlotLease : IDisposable
    {
        private readonly TaskCoordinator coordinator;
        internal string SlotId { get; }
        internal SlotLease(TaskCoordinator coordinator, string slotId) { this.coordinator = coordinator; SlotId = slotId; }
        public void Dispose()
        {
            lock (coordinator.gate)
            {
                if (coordinator.leases.GetValueOrDefault(SlotId) != this) return;
                if (coordinator.slots.GetValueOrDefault(SlotId)?.View.Active == true)
                    throw new InvalidOperationException("Wait for the running process before releasing its slot.");
                coordinator.leases.Remove(SlotId); coordinator.slots.Remove(SlotId);
            }
        }
    }

    public bool TryReserveSlot(string slotId, out SlotLease? lease)
    {
        lock (gate)
        {
            if (slots.ContainsKey(slotId) || leases.ContainsKey(slotId)) { lease = null; return false; }
            lease = new(this, slotId); leases.Add(slotId, lease); return true;
        }
    }

    public bool TryStart(TaskLaunchSnapshot snapshot, out TaskRun? run, SlotLease? lease = null)
    {
        lock (gate)
        {
            if (lease is not null && (lease.SlotId != snapshot.SlotId || leases.GetValueOrDefault(snapshot.SlotId) != lease)) { run = null; return false; }
            if (!snapshot.Administrative && (leases.ContainsKey(snapshot.SlotId) && leases[snapshot.SlotId] != lease ||
                slots.TryGetValue(snapshot.SlotId, out var occupied) && (lease is null || occupied.View.Active))) { run = null; return false; }
            run = new(snapshot);
            if (!snapshot.Administrative) slots[snapshot.SlotId] = run;
            runs.Add(run);
            var started = run;
            run.Completion = Task.Run(() => ExecuteAsync(started));
            return true;
        }
    }

    private async Task ExecuteAsync(TaskRun run)
    {
        run.OpenLog();
        try
        {
            var workflow = !run.Snapshot.Steps.IsDefaultOrEmpty;
            var steps = workflow ? run.Snapshot.Steps : [new TaskLaunchStep(run.Snapshot.PageId, run.Snapshot.OperationName, run.Snapshot.ExecutablePath, run.Snapshot.Arguments)];
            for (var index = 0; index < steps.Length; index++)
            {
                if (run.Cancellation.IsCancellationRequested) { run.Finish(RunStatus.Canceled, null, "Canceled before the next step. Completed changes are not undone."); return; }
                var step = steps[index];
                if (workflow) run.Append("workflow", $"Step {index + 1}/{steps.Length}: {step.Name}\n");
                var outcome = await ExecuteStepAsync(run, step).ConfigureAwait(false);
                if (workflow)
                {
                    run.RecordStep(new(index + 1, step.Name, outcome.Status, outcome.ExitCode, outcome.Diagnostic));
                    run.Append("workflow", $"{step.Name}: {outcome.Status.ToDisplayText()}; exit {outcome.ExitCode?.ToString() ?? "—"}\n");
                }
                var failed = outcome.Status != RunStatus.Completed || outcome.Diagnostic?.StartsWith("Invader reported tag errors.", StringComparison.Ordinal) == true;
                if (failed || index == steps.Length - 1)
                {
                    var message = outcome.Diagnostic;
                    if (workflow && failed && index < steps.Length - 1) message = (message ?? $"Step '{step.Name}' did not complete successfully.") + "\nRemaining repair steps were not run. Completed changes are not undone.";
                    run.Finish(outcome.Status, outcome.ExitCode, message); return;
                }
            }
        }
        catch (Exception ex) { run.Finish(RunStatus.StartFailed, null, ex.Message); }
        finally
        {
            lock (gate)
            {
                if (!run.Snapshot.Administrative && !leases.ContainsKey(run.Snapshot.SlotId) && slots.TryGetValue(run.Snapshot.SlotId, out var owner) && ReferenceEquals(owner, run)) slots.Remove(run.Snapshot.SlotId);
            }
        }
    }

    private sealed record StepOutcome(RunStatus Status, int? ExitCode, string? Diagnostic);
    private async Task<StepOutcome> ExecuteStepAsync(TaskRun run, TaskLaunchStep step)
    {
        using var process = new Process();
        var started = false;
        var firstChunk = run.Output.Count;
        try
        {
            if (run.Cancellation.IsCancellationRequested) return new(RunStatus.Canceled, null, "Canceled before launch.");
            process.StartInfo = new ProcessStartInfo(step.ExecutablePath)
            {
                WorkingDirectory = run.Snapshot.WorkingDirectory,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in step.Arguments) process.StartInfo.ArgumentList.Add(argument);
            started = process.Start();
            if (!started) throw new InvalidOperationException("Windows did not start the executable.");
            run.SetRunning();
            using var registration = run.Cancellation.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                { run.AddDiagnostic($"Cancellation could not terminate the process tree: {ex.Message}. The task remains tracked until it exits."); }
            });
            var stdout = PumpAsync(process.StandardOutput, "stdout", run);
            var stderr = PumpAsync(process.StandardError, "stderr", run);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new(run.Cancellation.IsCancellationRequested ? RunStatus.Canceled : process.ExitCode == 0 ? RunStatus.Completed : RunStatus.NonzeroExit, process.ExitCode,
                TagHandlingDiagnostics.Summarize(step.OperationId, run.Output.Skip(firstChunk)));
        }
        catch (Exception ex)
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (Exception cleanup) { run.AddDiagnostic($"{ex.Message}\nProcess cleanup failed: {cleanup.Message}. Waiting for process exit before releasing the slot."); }
                // A failed termination request must not release a still-running operation's slot.
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            return new(started ? RunStatus.NonzeroExit : RunStatus.StartFailed, null, ex.Message);
        }
    }

    private static async Task PumpAsync(StreamReader reader, string stream, TaskRun run)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0) run.Append(stream, new string(buffer, 0, count));
    }
}

public static class RunHistory
{
    public static void DeleteFiles(RunView view, string logsDirectory)
    {
        if (view.Active) throw new InvalidOperationException("Running logs cannot be cleared.");
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(logsDirectory));
        var path = Path.GetFullPath(view.Snapshot.LogFilePath);
        if (!string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(path), $"{view.Snapshot.RunId:D}.output.jsonl", StringComparison.OrdinalIgnoreCase) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The saved log path is outside the configured log folder or has an unexpected filename.");
        File.Delete(path);
        File.Delete(path + ".run.json");
    }

    public static IReadOnlyList<RunView> Load(string directory, int retainCount, out string? warning)
    {
        warning = null;
        var result = new List<RunView>();
        if (!Directory.Exists(directory)) return result;
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.output.jsonl.run.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Take(retainCount))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<RunView>(File.ReadAllText(file.FullName), JsonFiles.Options);
                    if (record is not null && !record.Active) result.Add(record);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
                { warning = $"Some history files could not be loaded: {ex.Message}"; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = ex.Message; }
        return result;
    }

    public static string ReadOutput(RunView view)
    {
        var text = new OutputFormatter();
        foreach (var line in File.ReadLines(view.Snapshot.LogFilePath))
        {
            var chunk = JsonSerializer.Deserialize<OutputChunk>(line);
            if (chunk is not null) text.Append(chunk.Stream, chunk.Text);
        }
        return text.ToString();
    }
}

/// <summary>Phase 5 supplies the plan owner; the shell consults it before closing.</summary>
public interface IUnsavedPlanGuard
{
    bool HasUnsavedChanges { get; }
    Task<bool> SaveAsync();
    void Discard();
}
public sealed class NoUnsavedPlan : IUnsavedPlanGuard
{
    public bool HasUnsavedChanges => false;
    public Task<bool> SaveAsync() => Task.FromResult(true);
    public void Discard() { }
}

