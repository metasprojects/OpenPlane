using System.Collections.Concurrent;

namespace OpenPlane.App.Services;

/// <summary>One line in a job's progress list.</summary>
public sealed class JobStep
{
    public string Text { get; set; } = "";
    /// <summary>running, done, skipped or failed.</summary>
    public string State { get; set; } = "running";
    public string? Detail { get; set; }
}

/// <summary>A long task (installing the mod) the page follows by polling.</summary>
public sealed class Job
{
    public string Id { get; } = Guid.NewGuid().ToString("N").Substring(0, 12);
    public string Kind { get; init; } = "";
    /// <summary>running, done or failed.</summary>
    public string State { get; set; } = "running";
    public List<JobStep> Steps { get; } = new();
    /// <summary>0..1 for the current step when known (downloads).</summary>
    public double? Progress { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    /// <summary>Things the player should do next (for example the Steam launch option on Linux).</summary>
    public List<string> NextSteps { get; } = new();
    /// <summary>True when the job finished but something still needs the player (shown as a warning).</summary>
    public bool Partial { get; set; }

    private readonly object _lock = new();

    public JobStep Step(string text)
    {
        lock (_lock)
        {
            foreach (var s in Steps.Where(s => s.State == "running")) s.State = "done";
            var step = new JobStep { Text = text };
            Steps.Add(step);
            Progress = null;
            return step;
        }
    }

    public object Snapshot()
    {
        lock (_lock)
            return new
            {
                id = Id, kind = Kind, state = State, progress = Progress, message = Message, error = Error,
                steps = Steps.Select(s => new { text = s.Text, state = s.State, detail = s.Detail }).ToList(),
                next_steps = NextSteps.ToList(),
                partial = Partial,
            };
    }

    public void Finish(string message)
    {
        lock (_lock)
        {
            foreach (var s in Steps.Where(s => s.State == "running")) s.State = "done";
            Message = message; State = "done"; Progress = null;
        }
    }

    public void Fail(string error)
    {
        lock (_lock)
        {
            foreach (var s in Steps.Where(s => s.State == "running")) s.State = "failed";
            Error = error; State = "failed"; Progress = null;
        }
    }
}

public sealed class JobManager
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    /// <summary>Starts work in the background; a failure is recorded on the job with its plain message.</summary>
    public Job Start(string kind, Func<Job, Task> work)
    {
        var job = new Job { Kind = kind };
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try { await work(job); if (job.State == "running") job.Finish("Done."); }
            catch (UserFacingException e) { job.Fail(e.Message); }
            catch (Exception e) { job.Fail("Something went wrong: " + e.Message); }
        });
        return job;
    }

    public Job? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public bool AnyRunning(string kind) => _jobs.Values.Any(j => j.Kind == kind && j.State == "running");

    private readonly object _startLock = new();

    /// <summary>Starts the work unless a job of the same kind is still running (checked and started in one step, so two quick clicks can't both start).</summary>
    public Job? TryStart(string kind, Func<Job, Task> work)
    {
        lock (_startLock)
        {
            if (AnyRunning(kind)) return null;
            return Start(kind, work);
        }
    }
}

/// <summary>An error whose message is written for the player (what happened and how to fix it).</summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException(string message, int status = 400) : base(message) { Status = status; }
    public int Status { get; }
}
