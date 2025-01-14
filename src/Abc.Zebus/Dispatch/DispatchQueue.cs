﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Abc.Zebus.Dispatch.Pipes;
using Abc.Zebus.Util;
using Abc.Zebus.Util.Collections;
using Microsoft.Extensions.Logging;

namespace Abc.Zebus.Dispatch
{
    public class DispatchQueue : IDisposable
    {
        [ThreadStatic]
        private static string? _currentDispatchQueueName;

        private static readonly ILogger _logger = ZebusLogManager.GetLogger(typeof(DispatchQueue));

        private readonly IPipeManager _pipeManager;
        private readonly int _batchSize;
        private FlushableBlockingCollection<Entry> _queue = new FlushableBlockingCollection<Entry>();
        private Thread? _thread;
        private volatile bool _isRunning;
        private int _asyncInvocationsCount;
        private int _hasCompletedAsyncInvocationsSinceLastWait;

        public string Name { get; }

        private bool IsCurrentDispatchQueue => _currentDispatchQueueName == Name;

        internal SynchronizationContext SynchronizationContext { get; }

        public DispatchQueue(IPipeManager pipeManager, int batchSize, string name)
        {
            _pipeManager = pipeManager;
            _batchSize = batchSize;
            Name = name;

            SynchronizationContext = new DispatchQueueSynchronizationContext(this);
        }

        public bool IsRunning => _isRunning;

        public int QueueLength => _queue.Count;

        public void Dispose()
        {
            Stop();
        }

        public void Enqueue(MessageDispatch dispatch, IMessageHandlerInvoker invoker)
        {
            _queue.Add(new Entry(dispatch, invoker));
        }

        private void Enqueue(Action action)
        {
            _queue.Add(new Entry(action));
        }

        public void Start()
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = $"{Name}.DispatchThread",
            };

            _thread.Start();
            _logger.LogInformation($"{Name} started");
        }

        public void Stop()
        {
            if (!_isRunning)
                return;

            _isRunning = false;

            while (WaitUntilAllMessagesAreProcessed())
                Thread.Sleep(1);

            _queue.CompleteAdding();

            _thread?.Join();
            _thread = null;

            _queue = new FlushableBlockingCollection<Entry>();
            _logger.LogInformation($"{Name} stopped");
        }

        private void ThreadProc()
        {
            _currentDispatchQueueName = Name;
            try
            {
                _logger.LogInformation($"{Name} processing started");
                var batch = new Batch(_batchSize);

                foreach (var entries in _queue.GetConsumingEnumerable(_batchSize))
                {
                    ProcessEntries(entries, batch);
                }

                _logger.LogInformation($"{Name} processing stopped");
            }
            finally
            {
                _currentDispatchQueueName = null;
            }
        }

        private void ProcessEntries(List<Entry> entries, Batch batch)
        {
            foreach (var entry in entries)
            {
                if (!batch.CanAdd(entry))
                    RunBatch(batch);

                if (entry.Action != null)
                    RunAction(entry.Action);
                else
                    batch.Add(entry);
            }

            RunBatch(batch);
        }

        private void RunAction(Action action)
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(SynchronizationContext);
                action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running action");
            }
            finally
            {
                LocalDispatch.Reset();
            }
        }

        private void RunSingle(MessageDispatch dispatch, IMessageHandlerInvoker invoker)
        {
            if (!_isRunning)
                return;

            var batch = new Batch(1);
            batch.Add(new Entry(dispatch, invoker));

            RunBatch(batch);
        }

        private void RunBatch(Batch batch)
        {
            if (batch.IsEmpty)
                return;

            try
            {
                if (!_isRunning)
                    return;

                switch (batch.FirstEntry.Invoker!.Mode)
                {
                    case MessageHandlerInvokerMode.Synchronous:
                    {
                        SynchronizationContext.SetSynchronizationContext(null);
                        var invocation = _pipeManager.BuildPipeInvocation(batch.FirstEntry.Invoker, batch.Messages, batch.FirstEntry.Dispatch!.Context);
                        invocation.Run();
                        batch.SetHandled(null);
                        break;
                    }

                    case MessageHandlerInvokerMode.Asynchronous:
                    {
                        SynchronizationContext.SetSynchronizationContext(SynchronizationContext);
                        var asyncBatch = batch.Clone();
                        var invocation = _pipeManager.BuildPipeInvocation(asyncBatch.FirstEntry.Invoker!, asyncBatch.Messages, asyncBatch.FirstEntry.Dispatch!.Context);
                        Interlocked.Increment(ref _asyncInvocationsCount);
                        invocation.RunAsync().ContinueWith(task => OnAsyncBatchCompleted(task, asyncBatch), TaskContinuationOptions.ExecuteSynchronously);
                        break;
                    }

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running batch");
                batch.SetHandled(ex);
            }
            finally
            {
                batch.Clear();
                LocalDispatch.Reset();
            }
        }

        private void OnAsyncBatchCompleted(Task task, Batch asyncBatch)
        {
            try
            {
                var exception = task.IsFaulted
                    ? task.Exception?.InnerException ?? new Exception("Task failed")
                    : task.IsCanceled
                        ? task.Exception?.InnerException ?? new TaskCanceledException()
                        : null;

                if (exception != null)
                    _logger.LogError(exception, "Error running async batch");

                asyncBatch.SetHandled(exception);
            }
            finally
            {
                _hasCompletedAsyncInvocationsSinceLastWait = 1;
                Interlocked.Decrement(ref _asyncInvocationsCount);
            }
        }

        public virtual int Purge()
        {
            var flushedEntries = _queue.Flush();
            return flushedEntries.Count;
        }

        /// <summary>
        /// Waits until the dispatch queue is empty and no messages are currently being processed
        /// </summary>
        /// <returns>
        /// true if the dispatch queue has processed messages since the last call to this function
        /// </returns>
        public bool WaitUntilAllMessagesAreProcessed()
        {
            if (_thread is null)
                return false;

            bool continueWait, hasChanged = false;

            do
            {
                continueWait = false;

                while (Volatile.Read(ref _asyncInvocationsCount) > 0)
                {
                    continueWait = true;
                    Thread.Sleep(1);
                }

                if (Interlocked.Exchange(ref _hasCompletedAsyncInvocationsSinceLastWait, 0) != 0)
                    continueWait = true;

                if (_queue.WaitUntilIsEmpty())
                    continueWait = true;

                if (continueWait)
                    hasChanged = true;
            } while (continueWait);

            return hasChanged;
        }

        public void RunOrEnqueue(MessageDispatch dispatch, IMessageHandlerInvoker invoker)
        {
            if (dispatch.ShouldRunSynchronously || IsCurrentDispatchQueue)
            {
                RunSingle(dispatch, invoker);
            }
            else
            {
                dispatch.BeforeEnqueue();
                Enqueue(dispatch, invoker);
            }
        }

        // for unit tests
        internal static string? GetCurrentDispatchQueueName()
        {
            return _currentDispatchQueueName;
        }

        // for unit tests
        internal static IDisposable SetCurrentDispatchQueueName(string queueName)
        {
            _currentDispatchQueueName = queueName;

            return new DisposableAction(() => _currentDispatchQueueName = null);
        }

        private readonly struct Entry
        {
            public Entry(MessageDispatch dispatch, IMessageHandlerInvoker invoker)
            {
                Dispatch = dispatch;
                Invoker = invoker;
                Action = null;
            }

            public Entry(Action action)
            {
                Dispatch = null;
                Invoker = null;
                Action = action;
            }

            public readonly MessageDispatch? Dispatch;
            public readonly IMessageHandlerInvoker? Invoker;
            public readonly Action? Action;
        }

        private class Batch
        {
            public readonly List<Entry> Entries;
            public readonly List<IMessage> Messages;

            public Batch(int capacity)
            {
                Entries = new List<Entry>(capacity);
                Messages = new List<IMessage>(capacity);
            }

            public Entry FirstEntry => Entries[0];
            public bool IsEmpty => Entries.Count == 0;

            public void Add(Entry entry)
            {
                Entries.Add(entry);
                Messages.Add(entry.Dispatch!.Message);
            }

            public void SetHandled(Exception? error)
            {
                foreach (var entry in Entries)
                {
                    try
                    {
                        entry.Dispatch!.SetHandled(entry.Invoker!, error);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Unable to run dispatch continuation, MessageType: {entry.Invoker!.MessageType.Name}, HandlerType: {entry.Invoker!.MessageHandlerType.Name}, HandlerError: {error}");
                    }
                }
            }

            public void Clear()
            {
                Entries.Clear();
                Messages.Clear();
            }

            public Batch Clone()
            {
                var clone = new Batch(Entries.Count);
                clone.Entries.AddRange(Entries);
                clone.Messages.AddRange(Messages);
                return clone;
            }

            public bool CanAdd(Entry entry)
            {
                if (entry.Action != null)
                    return false;

                if (IsEmpty)
                    return true;

                return entry.Invoker!.CanMergeWith(FirstEntry.Invoker!);
            }
        }

        private class DispatchQueueSynchronizationContext : SynchronizationContext
        {
            private readonly DispatchQueue _dispatchQueue;

            public DispatchQueueSynchronizationContext(DispatchQueue dispatchQueue)
            {
                _dispatchQueue = dispatchQueue;
            }

            public override void Post(SendOrPostCallback d, object? state)
            {
                _dispatchQueue.Enqueue(() => d(state));
            }
        }
    }
}
