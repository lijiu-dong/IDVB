using System.Collections.Concurrent;

namespace IDVBuff.Features.Maps;

/// <summary>Owns WGC creation and native shutdown on one MTA thread.</summary>
internal static class CaptureStreamWorker
{
    private static readonly Lazy<Worker> Owner = new(() => new Worker());

    internal static bool HasThreadAccess => Owner.IsValueCreated
        && Environment.CurrentManagedThreadId == Owner.Value.ThreadId;

    internal static Task RunAsync(Action action) => Owner.Value.Enqueue(action);

    private sealed class Worker
    {
        private readonly BlockingCollection<WorkItem> _work = new();
        private readonly Thread _thread;

        internal Worker()
        {
            _thread = new Thread(() =>
            {
                foreach (var item in _work.GetConsumingEnumerable()) item.Execute();
            }) { IsBackground = true, Name = "IDVB WGC resources" };
            _thread.SetApartmentState(ApartmentState.MTA);
            // A long-lived capture owner must not inherit the first scan's
            // AsyncLocal deadline, frame or diagnostic request context.
            if (ExecutionContext.IsFlowSuppressed()) _thread.Start();
            else
            {
                using var flow = ExecutionContext.SuppressFlow();
                _thread.Start();
            }
        }

        internal int ThreadId => _thread.ManagedThreadId;

        internal Task Enqueue(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(new WorkItem(action, completion));
            return completion.Task;
        }
    }

    private sealed class WorkItem(Action action, TaskCompletionSource completion)
    {
        private Action? _action = action;
        private TaskCompletionSource? _completion = completion;

        internal void Execute()
        {
            // The consuming iterator/idle worker stack can retain its last item.
            // Keep that item empty after execution so a completed retirement
            // cannot keep the closed WGC stream and its WinRT wrappers alive.
            var callback = Interlocked.Exchange(ref _action, null)!;
            var completed = Interlocked.Exchange(ref _completion, null)!;
            try { callback(); completed.TrySetResult(); }
            catch (Exception exception) { completed.TrySetException(exception); }
        }
    }
}
