using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Runs delegates on a single, long-lived, dedicated STA thread. (P0.1)
    ///
    /// SolidWorks COM objects must be called from an STA apartment. OWIN / Web API request
    /// threads are MTA, and while COM marshaling lets lightweight calls (e.g. FeatureExtrusion3)
    /// limp across the apartment boundary, heavier or more apartment-sensitive calls fail off-STA:
    /// InsertSheetMetalBaseFlange2 silently returned null and add_edge_feature (edge fillet)
    /// deadlocked (ContextSwitchDeadlock). Routing ALL COM-touching work through one STA thread —
    /// including the GetActiveObject attach — makes the RCW live on the STA, fixing both.
    ///
    /// A single worker thread also serializes COM access, which suits the single-writer,
    /// process-global state model the OperationGuard already assumes.
    ///
    /// Busy gate: a job only enters the queue once the worker is free. When SolidWorks hangs inside
    /// a COM call (e.g. ModelDoc2.Save3 never returning), every later request used to queue behind it
    /// unseen, block its HTTP caller until the client timed out, and then still RUN whenever
    /// SolidWorks recovered — a stale, unattended model change. Now a request waits at most its
    /// start timeout for the gate and otherwise fails with <see cref="StaBusyException"/> naming the
    /// call that holds the worker, and nothing is left queued.
    /// </summary>
    public sealed class StaExecutor
    {
        public static readonly StaExecutor Instance = new StaExecutor();

        /// <summary>Default wait for the worker to become free before a request fails as busy.</summary>
        public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(20);

        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _infoLock = new object();
        private string _currentLabel;
        private DateTime _currentStartUtc;

        private StaExecutor()
        {
            var thread = new Thread(Worker)
            {
                IsBackground = true,
                Name = "SolidWorksStaThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Worker()
        {
            // Each job sets its own completion event and captures its own exception, so a throwing
            // job never tears down the worker loop.
            foreach (var work in _queue.GetConsumingEnumerable())
                work();
        }

        /// <summary>The call running on the STA thread (null when idle) and since when (UTC).</summary>
        public bool TryGetCurrent(out string label, out DateTime startUtc)
        {
            lock (_infoLock)
            {
                label = _currentLabel;
                startUtc = _currentStartUtc;
                return label != null;
            }
        }

        /// <summary>Marshal <paramref name="func"/> onto the STA thread and block until it returns.</summary>
        public T Run<T>(Func<T> func) => Run(func, "unnamed call", DefaultStartTimeout);

        /// <summary>
        /// Marshal <paramref name="func"/> onto the STA thread and block until it returns, waiting at
        /// most <paramref name="startTimeout"/> for the worker to be free. Throws
        /// <see cref="StaBusyException"/> (nothing queued, nothing run) when it is still busy.
        /// </summary>
        public T Run<T>(Func<T> func, string label, TimeSpan startTimeout)
        {
            if (!_gate.Wait(startTimeout))
            {
                string busyLabel;
                DateTime busySince;
                TryGetCurrent(out busyLabel, out busySince);
                throw new StaBusyException(busyLabel, busySince);
            }

            T result = default(T);
            Exception captured = null;
            try
            {
                lock (_infoLock)
                {
                    _currentLabel = label;
                    _currentStartUtc = DateTime.UtcNow;
                }
                using (var done = new ManualResetEventSlim(false))
                {
                    _queue.Add(() =>
                    {
                        try { result = func(); }
                        catch (Exception ex) { captured = ex; }
                        finally { done.Set(); }
                    });
                    done.Wait();
                }
            }
            finally
            {
                lock (_infoLock) _currentLabel = null;
                _gate.Release();
            }
            if (captured != null)
                throw new InvalidOperationException("STA execution failed: " + captured.Message, captured);
            return result;
        }
    }

    /// <summary>The STA worker is still running another call (SolidWorks busy or hung).</summary>
    public sealed class StaBusyException : Exception
    {
        public string BusyLabel { get; }
        public DateTime BusySinceUtc { get; }

        public StaBusyException(string busyLabel, DateTime busySinceUtc)
            : base(BuildMessage(busyLabel, busySinceUtc))
        {
            BusyLabel = busyLabel;
            BusySinceUtc = busySinceUtc;
        }

        private static string BuildMessage(string busyLabel, DateTime busySinceUtc)
        {
            if (busyLabel == null)
                return "The SolidWorks worker thread is busy.";
            var seconds = (int)(DateTime.UtcNow - busySinceUtc).TotalSeconds;
            return $"SolidWorks is still running '{busyLabel}' (started " +
                   $"{busySinceUtc.ToLocalTime():HH:mm:ss}, {seconds} s ago).";
        }
    }
}
