namespace SolutionDeployer.Mcp;

public enum RunState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// A deploy or restore started over MCP. The host feeds it output and per-target results as the run goes;
/// callers poll (or wait on) it. Thread-safe: the host reports from build threads.
/// </summary>
public sealed class DeploymentRun
{
    public const int MaxLogLines = 4000;

    private readonly object _gate = new();
    private readonly Queue<string> _log = new();
    private readonly List<RunTarget> _targets;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _cancel;
    private int _droppedLines;

    public DeploymentRun(string kind, IEnumerable<(string Id, string Name)> targets)
    {
        Kind = kind;
        _targets = targets.Select(t => new RunTarget(t.Id, t.Name)).ToList();
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>"deploy" or "restore".</summary>
    public string Kind { get; }

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedUtc { get; private set; }

    public RunState State { get; private set; } = RunState.Running;

    public string? Message { get; private set; }

    /// <summary>Completes when the run finishes, however it ends.</summary>
    public Task Completion => _completion.Task;

    public void AppendLog(string line)
    {
        lock (_gate)
        {
            _log.Enqueue(line);
            if (_log.Count > MaxLogLines)
            {
                _log.Dequeue();
                _droppedLines++;
            }
        }
    }

    public void SetTargetStatus(string targetId, string status, string? result = null)
    {
        lock (_gate)
        {
            var index = _targets.FindIndex(t => t.Id == targetId);
            if (index >= 0)
                _targets[index] = _targets[index] with { Status = status, Result = result };
        }
    }

    /// <summary>Records how the run ended. Only the first call counts.</summary>
    public void Complete(RunState state, string? message)
    {
        lock (_gate)
        {
            if (State != RunState.Running)
                return;
            State = state;
            Message = message;
            FinishedUtc = DateTimeOffset.UtcNow;
            _cancel = null;
        }

        _completion.TrySetResult();
    }

    /// <summary>What <see cref="RequestCancel"/> does, set by the host once the run has started.</summary>
    public void OnCancel(Action cancel)
    {
        lock (_gate)
            _cancel = cancel;
    }

    /// <summary>Asks the host to stop the run. False when it already finished.</summary>
    public bool RequestCancel()
    {
        Action? cancel;
        lock (_gate)
        {
            if (State != RunState.Running)
                return false;
            cancel = _cancel;
        }

        cancel?.Invoke();
        return true;
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the run to finish.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || Completion.IsCompleted)
            return;

        try
        {
            await Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still running: the caller reports progress so far.
        }
    }

    public RunSnapshot Snapshot(int logLines)
    {
        lock (_gate)
        {
            var take = Math.Clamp(logLines, 0, _log.Count);
            var omitted = _droppedLines + _log.Count - take;
            return new RunSnapshot(
                Id,
                Kind,
                State.ToString().ToLowerInvariant(),
                Message,
                StartedUtc,
                FinishedUtc,
                (int)((FinishedUtc ?? DateTimeOffset.UtcNow) - StartedUtc).TotalSeconds,
                _targets.ToList(),
                omitted,
                _log.Skip(_log.Count - take).ToList());
        }
    }
}

public sealed record RunTarget(string Id, string Name, string Status = "pending", string? Result = null);

public sealed record RunSnapshot(
    string RunId,
    string Kind,
    string State,
    string? Message,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    int ElapsedSeconds,
    IReadOnlyList<RunTarget> Targets,
    int EarlierLogLinesOmitted,
    IReadOnlyList<string> Log);

/// <summary>The recent MCP runs, so callers can look one up by id. Keeps the newest <see cref="Capacity"/>.</summary>
public sealed class RunRegistry
{
    public const int Capacity = 20;

    private readonly object _gate = new();
    private readonly List<DeploymentRun> _runs = [];

    public void Add(DeploymentRun run)
    {
        lock (_gate)
        {
            _runs.Insert(0, run);
            if (_runs.Count > Capacity)
                _runs.RemoveRange(Capacity, _runs.Count - Capacity);
        }
    }

    /// <summary>The run with this id, or the newest one when <paramref name="runId"/> is empty.</summary>
    public DeploymentRun? Find(string? runId)
    {
        lock (_gate)
        {
            return string.IsNullOrWhiteSpace(runId)
                ? _runs.FirstOrDefault()
                : _runs.FirstOrDefault(r => string.Equals(r.Id, runId.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }
}
