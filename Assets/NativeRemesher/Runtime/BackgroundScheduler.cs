// Runs a remesh without taking the machine with it.
//
// The window ran the solve with Task.Run, and every Parallel.For inside it then filled every core
// at normal priority: on ccnb at 100,000 quads the editor and the rest of the desktop stalled for
// the whole run. A task started on this scheduler runs on its own threads, one fewer than the
// cores and below normal priority, so the desktop keeps a core and wins every contest for the
// others.
//
// A Parallel.For given no options does NOT follow the scheduler it is called from: measured on
// .NET 8, 19,585 of 20,000 iterations ran on the shared pool at normal priority and 415 on one of
// these threads. So every Parallel call in the runtime passes Options, which names the current
// scheduler explicitly -- these threads inside a job, the pool anywhere else.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NativeRemesher
{
    public sealed class BackgroundScheduler : TaskScheduler, IDisposable
    {
        readonly BlockingCollection<Task> m_Queue = new BlockingCollection<Task>();
        readonly List<Thread> m_Threads = new List<Thread>();

        /// <summary>Threads: the cores less one, at least one.</summary>
        public BackgroundScheduler() : this(Math.Max(1, Environment.ProcessorCount - 1)) { }

        public BackgroundScheduler(int threads)
        {
            for (var i = 0; i < Math.Max(1, threads); i++)
            {
                var thread = new Thread(Work)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = "Native Remesher " + i,
                };
                m_Threads.Add(thread);
                thread.Start();
            }
        }

        public override int MaximumConcurrencyLevel => m_Threads.Count;

        /// <summary>
        /// Options for a Parallel call that keep it on the scheduler it runs under: this one inside
        /// a job started here, the default anywhere else.
        /// </summary>
        public static ParallelOptions Options => new ParallelOptions { TaskScheduler = TaskScheduler.Current };

        /// <summary>Runs <paramref name="work"/> on this scheduler's threads.</summary>
        public Task<T> Run<T>(Func<T> work, CancellationToken token) =>
            Task.Factory.StartNew(work, token, TaskCreationOptions.None, this);

        void Work()
        {
            try
            {
                foreach (var task in m_Queue.GetConsumingEnumerable()) TryExecuteTask(task);
            }
            catch (ObjectDisposedException) { }
        }

        protected override void QueueTask(Task task) => m_Queue.Add(task);

        // Inline only on our own threads, so nothing of the solve ever runs on a caller's thread.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
            m_Threads.Contains(Thread.CurrentThread) && TryExecuteTask(task);

        protected override IEnumerable<Task> GetScheduledTasks() => m_Queue.ToArray();

        public void Dispose() => m_Queue.CompleteAdding();
    }
}
