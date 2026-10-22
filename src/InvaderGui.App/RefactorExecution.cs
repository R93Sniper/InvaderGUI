using System.Collections.Immutable;
using System.Windows.Media;
using InvaderGui.Core;

namespace InvaderGui.App;

public partial class RefactorPreview
{
    private ShellViewModel? executionShell;
    private Action<Guid?>? showExecutionOutput;
    private readonly Dictionary<Guid, string> executionErrors = new();
    public bool IsExecuting { get; private set; }
    public string ExecutionStatus { get; private set; } = "";
    public TaskRun? CurrentRun { get; private set; }
    public Task<bool> ExecutionCompletion { get; private set; } = Task.FromResult(true);
    public bool StopRequested { get; private set; }
    private bool cancelExecutionRequested;
    private TaskCompletionSource? queueAdvanceDecision;
    private int queueAdvanceHolds;
    public bool CanExecute => !IsExecuting && !planFileBusy && CanReviewQueue && editingCommandId is null && pendingCommands.Count > 0 &&
        executionShell?.Scan?.For(Operations.All.Single(o => o.Id == "refactor")).Available == true && executionShell.Coordinator.Owner("refactor") is null;
    public bool HasSelectedCommand => SelectedCommandId is not null;

    public void ConfigureExecution(ShellViewModel shell, Action<Guid?> showOutput)
    { executionShell = shell; showExecutionOutput = showOutput; }

    public Task<bool> ExecuteQueueAsync(bool all, bool dryRun = false)
    {
        if (IsExecuting) return Task.FromResult(false);
        ExecutionCompletion = ExecuteQueueCoreAsync(all, dryRun);
        return ExecutionCompletion;
    }

    public bool RequestStop()
    {
        if (!IsExecuting) return false;
        StopRequested = true;
        ExecutionStatus = CurrentRun?.View.Active == true ? "Stopping after the current command finishes…" : "Stopping before the next command…";
        QueueBindingStatus.Text = ExecutionStatus;
        return true;
    }

    public bool CancelExecution()
    {
        if (!IsExecuting) return false;
        StopRequested = cancelExecutionRequested = true;
        CurrentRun?.Cancel();
        ExecutionStatus = "Canceling execution and refreshing tags…";
        QueueBindingStatus.Text = ExecutionStatus;
        return true;
    }

    public Task<bool> StopForCloseAsync(bool finishCurrent)
    {
        if (finishCurrent) RequestStop(); else CancelExecution();
        return ExecutionCompletion;
    }

    public IDisposable DeferQueueAdvance()
    {
        queueAdvanceHolds++;
        queueAdvanceDecision ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        return new QueueAdvanceHold(this);
    }

    private sealed class QueueAdvanceHold(RefactorPreview owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (--owner.queueAdvanceHolds != 0) return;
            var decision = owner.queueAdvanceDecision; owner.queueAdvanceDecision = null;
            decision?.TrySetResult();
        }
    }

    private async Task<bool> ExecuteQueueCoreAsync(bool all, bool dryRun)
    {
        if (!CanExecute) return ExecutionFailure("Finish the current edit, select the queue's tag folders, and configure Invader before running.");
        var shell = executionShell!;
        var selectedId = SelectedCommandId;
        if (!all && selectedId is null) return ExecutionFailure("Select a command first.");
        if (!shell.Coordinator.TryReserveSlot("refactor", out var lease)) return ExecutionFailure("The Refactor tab is already running.");
        using var reservation = lease!;
        IsExecuting = true;
        StopRequested = cancelExecutionRequested = false;
        ExecutionStatus = "Refreshing tags before execution…";
        var profile = browsingProfile!.Copy();
        profile.WorkingDirectory = boundWorkingDirectory!;
        profile.Paths.TagRoots = boundRoots!.Select(root => new TagRoot { Id = root.Id, Name = root.Name, Path = root.Path }).ToList();
        var roots = boundRoots!.ToArray();
        var scan = shell.Scan!;
        UpdateExecutionControls();
        try
        {
            RefreshHierarchy(); await HierarchyRefresh;
            while (pendingCommands.Count > 0)
            {
                if (queueAdvanceDecision is { } decision) await decision.Task;
                if (StopRequested)
                { ExecutionStatus = "Stopped. No further command was launched. Remaining commands are pending."; return !cancelExecutionRequested; }
                if (Snapshot is null || !PlanIsActive()) return ExecutionFailure("Tags could not be refreshed. No further command was launched.");
                var command = all ? pendingCommands[0] : pendingCommands.Single(entry => entry.Id == selectedId);
                if (all && PlannedReplay?.IsValid != true)
                    return ExecutionFailure("Queue conflicts must be resolved before Run All. Review the flagged commands.");
                var check = RefactorPlanner.Replay(Snapshot, [command]);
                if (!check.IsValid)
                    return ExecutionFailure("The selected command cannot run against Current. Run prerequisite commands first, or repair its conflicts.\n" + string.Join("\n", check.Entries.SelectMany(entry => entry.Errors)));
                var arguments = RefactorPlanner.Arguments(command, roots).ToList();
                if (dryRun) arguments.Add("--dry-run");
                var launch = TaskLaunchSnapshot.Create(Operations.All.Single(o => o.Id == "refactor"), scan, profile, arguments,
                    (dryRun ? "Dry run: " : "") + command.Summary, shell.LogsDirectory) with
                { OperationName = dryRun ? "Refactor Tags · Dry Run" : "Refactor Tags", EffectiveOptions = new Dictionary<string, string>
                    { ["commandId"] = command.Id.ToString("D"), ["dryRun"] = dryRun.ToString() }.ToImmutableDictionary() };
                ExecutionStatus = dryRun ? "Checking selected command…" : "Running: " + command.Summary;
                CommandQueue.SelectedItem = CommandQueue.Items.Cast<QueueItem>().First(item => item.Command.Id == command.Id);
                CommandQueue.ScrollIntoView(CommandQueue.SelectedItem);
                if (queueAdvanceDecision is { } launchDecision) await launchDecision.Task;
                if (StopRequested)
                { ExecutionStatus = "Stopped before launching the next command. Remaining commands are pending."; return !cancelExecutionRequested; }
                if (!shell.Coordinator.TryStart(launch, out var run, reservation)) return ExecutionFailure("The Refactor execution slot is unavailable.");
                CurrentRun = run;
                showExecutionOutput?.Invoke(run!.Snapshot.RunId);
                await run!.Completion;
                var result = run.View;
                var success = result.Status == RunStatus.Completed && result.ExitCode == 0 && result.Diagnostic is null;
                RefreshHierarchy(); await HierarchyRefresh;
                string Files(TagHierarchySnapshot snapshot) => string.Join("\n", RefactorPlanner.Flatten(snapshot.Nodes).Where(node => !node.IsFolder)
                    .Select(node => node.LogicalPath + "|" + string.Join("|", node.Definitions.Select(definition => definition.PhysicalPath)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)).ToUpperInvariant();
                var verified = Snapshot is not null && (dryRun || command.Mode == "no-move" || Files(Snapshot) == Files(check.Planned));
                success &= verified;
                if (success && !dryRun)
                {
                    rememberedBinding = CapturePlan().ProfileBinding;
                    pendingCommands.RemoveAll(entry => entry.Id == command.Id);
                    executionErrors.Remove(command.Id);
                    if (pendingCommands.Count == 0)
                    {
                        // Completed work is retained in history. There is no pending work to protect on close.
                        savedPlanSignature = PendingPlanSignature();
                        saveNeedsRetry = false;
                    }
                    MarkPlanChanged();
                }
                else if (!success)
                    executionErrors[command.Id] = $"Last attempt: {result.Status.ToDisplayText()} · exit {result.ExitCode?.ToString() ?? "unknown"}. " +
                        (result.Diagnostic ?? (!verified ? "Disk state could not be verified against the planned result. " : "") + "Inspect Output Log. The command remains pending; partial changes may exist.");
                else executionErrors.Remove(command.Id);
                UpdatePlanViews();
                if (!success) return ExecutionFailure("Execution stopped. The command remains pending. Review Output Log and the refreshed tags before retrying.");
                if (Snapshot is null) return ExecutionFailure("The command completed, but tags could not be refreshed. Review disk state before continuing.");
                if (dryRun)
                {
                    ExecutionStatus = "Dry run completed. Command remains pending. It checks Current only; dependent commands require their prerequisites to run first.";
                    return true;
                }
                if (!all || pendingCommands.Count == 0)
                { ExecutionStatus = all ? "Queue completed." : "Command completed. Remaining commands have been refreshed."; return true; }
                if (StopRequested)
                { ExecutionStatus = $"Stopped after the current command. {pendingCommands.Count} command(s) remain pending."; return !cancelExecutionRequested; }
            }
            return true;
        }
        catch (Exception ex)
        {
            // A UI callback must never release a slot while its process still runs.
            if (CurrentRun?.View.Active == true) { CurrentRun.Cancel(); await CurrentRun.Completion; }
            RefreshHierarchy(); await HierarchyRefresh;
            return ExecutionFailure("Execution stopped: " + ex.Message + " Review Output Log and disk state before retrying.");
        }
        finally
        {
            CurrentRun = null; IsExecuting = false;
            UpdatePlanViews(); UpdateExecutionControls();
            EditorValidation.Foreground = Brushes.LightGray;
            EditorValidation.Text = ExecutionStatus;
        }
    }

    private bool ExecutionFailure(string message) { ExecutionStatus = message; EditorValidation.Text = message; EditorValidation.Foreground = Brushes.Salmon; return false; }

    private void UpdateExecutionControls()
    {
        // Keep the list readable and scrollable; mutation controls and handlers enforce the execution lock.
        CommandQueue.IsEnabled = true;
        TagFolderSelector.IsEnabled = !IsExecuting && browsingProfile?.Paths.TagRoots.Count > 0;
        ReferenceOptions.IsEnabled = !IsExecuting;
        UpdateQueueControls();
        if (IsExecuting) OperationEditor.IsEnabled = false;
    }
}
