using System;
using System.Collections.Generic;

namespace CanEmAllCoop.Enemies;

/// <summary>
/// First-in first-out list of deferred work for the client: a burst of enemy spawns (one truck delivery is up to several
/// hundred) is created over a few frames instead of in one, and every later message (release, damage, death, return,
/// clean-up) queues up behind it so the order the host produced them in is kept.
/// A job gets a "budget is used up" probe, does at least one unit of work and returns true when it is finished; a job
/// that returns false stays at the head and is called again on the next pump.
/// </summary>
public sealed class WorkQueue
{
    public delegate bool Job(Func<bool> outOfBudget);

    readonly Queue<Job> _jobs = new();

    public int Count => _jobs.Count;
    public bool HasWork => _jobs.Count > 0;

    public void Enqueue(Job job) { if (job != null) _jobs.Enqueue(job); }

    public void Clear() => _jobs.Clear();

    /// <summary>
    /// Runs jobs in order until the queue is empty or <paramref name="outOfBudget"/> turns true. Always gives the head job one
    /// call, so a budget that is already used up cannot stall the queue. Returns the number of job calls made.
    /// </summary>
    public int Pump(Func<bool> outOfBudget)
    {
        int calls = 0;
        while (_jobs.Count > 0)
        {
            var head = _jobs.Peek();
            bool done = head(outOfBudget);
            calls++;
            if (done) _jobs.Dequeue();
            else break;                       // the head wants more time (or is waiting for something): keep the order
            if (outOfBudget()) break;
        }
        return calls;
    }

    /// <summary>
    /// Job that walks <paramref name="count"/> items: at least one per call, then more while the budget allows.
    /// <paramref name="step"/> returns false to say "cannot do this item yet" (the job pauses on it).
    /// </summary>
    public static Job ForEachItem(int count, Func<int, bool> step)
    {
        int next = 0;
        return outOfBudget =>
        {
            do
            {
                if (next >= count) return true;
                if (!step(next)) return false;
                next++;
            } while (next < count && !outOfBudget());
            return next >= count;
        };
    }
}
