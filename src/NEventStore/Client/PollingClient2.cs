﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NEventStore.Logging;
using NEventStore.Persistence;

namespace NEventStore.Client
{
    /// <summary>
    /// This is the new polling client that does not depends on RX.
    /// </summary>
    public class PollingClient2 : IDisposable
    {
        public enum HandlingResult
        {
            MoveToNext = 0,
            Retry = 1,
            Stop = 2,
        }

        private readonly ILog _logger;

        private readonly Func<ICommit, HandlingResult> _commitCallback;

        private readonly IPersistStreams _persistStreams;

        private readonly Int32 _waitInterval;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="persistStreams"></param>
        /// <param name="callback"></param>
        /// <param name="waitInterval">Interval in Milliseconds to wait when the provider
        /// return no more commit and the next request</param>
        public PollingClient2(IPersistStreams persistStreams, Func<ICommit, HandlingResult> callback, Int32 waitInterval = 100)
        {
            if (persistStreams == null)
                throw new ArgumentNullException("PersistStreams cannot be null", "persistStreams");
            if (callback == null)
                throw new ArgumentNullException("Cannot use polling client without callback", "callback");

            _logger = LogFactory.BuildLogger(GetType());
            _waitInterval = waitInterval;
           
            _commitCallback = callback;
            _persistStreams = persistStreams;
        }

        private Thread _pollingThread;

        private Func<IEnumerable<ICommit>> _pollingFunc;

        private Int64 _checkpointToken;

        public void StartFrom(Int64 checkpointToken = 0)
        {
            if (_pollingThread != null)
                throw new PollingClientException("Polling client already started");
            _checkpointToken = checkpointToken;
            ConfigurePollingFunction();
            _pollingThread = new Thread(InnerPollingLoop);
            _pollingThread.Start();
        }

        public void ConfigurePollingFunction(string bucketId = null)
        {
            if (_pollingThread != null)
                throw new PollingClientException("Cannot configure when polling client already started polling");
            if (bucketId == null)
                _pollingFunc = () => _persistStreams.GetFrom(_checkpointToken);
            else
                _pollingFunc = () => _persistStreams.GetFrom(bucketId, _checkpointToken);
        }

        public void StartFromBucket(string bucketId, Int64 checkpointToken = 0)
        {
            if (_pollingThread != null)
                throw new PollingClientException("Polling client already started");
            _checkpointToken = checkpointToken;
            ConfigurePollingFunction(bucketId);
            _pollingThread = new Thread(InnerPollingLoop);
            _pollingThread.Start();
        }

        public void Stop()
        {
            _stopRequest = true;
        }

        public Task PollNow()
        {
            //if (_pollingThread == null)
            //    throw new ArgumentException("You cannot call PollNow on a poller that is not started");
            return Task<Boolean>.Factory.StartNew(InnerPoll);
        }

        private int _isPolling = 0;

        private Boolean _stopRequest = false;

        private void InnerPollingLoop(object obj)
        {
            while (_stopRequest == false)
            {
                if (InnerPoll()) return;
                Thread.Sleep(_waitInterval);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns>Returns true if we need to stop the outer cycle.</returns>
        private bool InnerPoll()
        {
            if (Interlocked.CompareExchange(ref _isPolling, 1, 0) == 0)
            {
                try
                {
                    var commits = _pollingFunc();

                    foreach (var commit in commits)
                    {
                        if (_stopRequest)
                        {
                            return true;
                        }
                        var result = _commitCallback(commit);
                        if (result == HandlingResult.Retry)
                        {
                            break;
                        }
                        else if (result == HandlingResult.Stop)
                        {
                            Stop();
                            return true;
                        }
                        _checkpointToken = commit.CheckpointToken;
                    }
                }
                catch (Exception ex)
                {
                    // These exceptions are expected to be transient
                    _logger.Error(String.Format("Error during polling client {0}", ex.ToString()));
                } 
                Interlocked.Exchange(ref _isPolling, 0);
            }
            return false;
        }

        private Boolean _isDisposed;

        public void Dispose()
        {
            Dispose(true);
        }

        public void Dispose(Boolean isDisposing)
        {
            if (_isDisposed) return;
            if (isDisposing)
            {
                Stop();
            }
            _isDisposed = true;
        }
    }
}
