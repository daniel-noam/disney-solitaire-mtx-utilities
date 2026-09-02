using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Tools.Editor.EditorUtilities.EasyUpload
{
    /// <summary>
    /// A running deploy, readable from the main thread while worker threads write to it.
    ///
    /// Everything the window draws goes through here rather than through callbacks, because IMGUI
    /// can only be touched from the main thread and a repaint may happen at any moment.
    /// </summary>
    public class UploadJob
    {
        private readonly object gate = new object();
        private readonly List<string> errors = new List<string>();
        private string status = "";

        public int TotalFiles;
        public long TotalBytes;

        private int doneFiles;
        private long doneBytes;

        public int DoneFiles => Volatile.Read(ref doneFiles);
        public long DoneBytes => Interlocked.Read(ref doneBytes);

        public volatile bool Running;
        public volatile bool Cancelled;
        public DateTime StartedUtc;
        public DateTime FinishedUtc;

        public string Status
        {
            get { lock (gate) return status; }
            set { lock (gate) status = value; }
        }

        public List<string> Errors
        {
            get { lock (gate) return new List<string>(errors); }
        }

        public int ErrorCount { get { lock (gate) return errors.Count; } }

        public float Fraction =>
            TotalBytes > 0 ? Math.Min(1f, (float)(DoneBytes / (double)TotalBytes))
            : (TotalFiles > 0 ? Math.Min(1f, DoneFiles / (float)TotalFiles) : 0f);

        internal void AddBytes(long delta) => Interlocked.Add(ref doneBytes, delta);
        internal void FileDone() => Interlocked.Increment(ref doneFiles);
        internal void AddError(string message) { lock (gate) errors.Add(message); }
    }

    /// <summary>Runs a reviewed plan against S3 on a pool of background threads.</summary>
    public class Uploader
    {
        /// <summary>
        /// Ceiling on upload threads regardless of bucket count. These are OS threads blocked on
        /// sockets, not async tasks, so the pool has to stay something a machine can carry.
        /// </summary>
        private const int MaxWorkers = 64;

        private class WorkItem
        {
            public string Bucket;
            public string Key;
            public LocalFile File;
        }

        private CancellationTokenSource cancellation;

        public UploadJob Job { get; private set; }

        /// <summary>
        /// Start uploading everything ticked in <paramref name="plan"/>. Returns immediately; poll
        /// <see cref="Job"/> from the main thread.
        /// </summary>
        public UploadJob Start(S3Client client, SyncPlan plan, int concurrency)
        {
            // Per bucket, round-robin rather than bucket after bucket. Each bucket is a different
            // host with its own connection pool, so interleaving means the first workers spread
            // across all of them instead of queueing behind one.
            var perBucket = new List<List<WorkItem>>();
            long totalBytes = 0;
            var totalFiles = 0;

            foreach (var bucketPlan in plan.Buckets)
            {
                if (!string.IsNullOrEmpty(bucketPlan.Error)) continue;

                var items = new List<WorkItem>();
                foreach (var entry in bucketPlan.Entries)
                {
                    if (!entry.Selected || !entry.CanUpload) continue;
                    items.Add(new WorkItem
                    {
                        Bucket = bucketPlan.Bucket,
                        Key = bucketPlan.Prefix + entry.File.Relative,
                        File = entry.File,
                    });
                    totalBytes += entry.File.Size;
                    totalFiles++;
                }

                if (items.Count > 0) perBucket.Add(items);
            }

            var queue = new ConcurrentQueue<WorkItem>();
            for (var depth = 0; ; depth++)
            {
                var placed = false;
                foreach (var items in perBucket)
                {
                    if (depth >= items.Count) continue;
                    queue.Enqueue(items[depth]);
                    placed = true;
                }
                if (!placed) break;
            }

            Job = new UploadJob
            {
                TotalFiles = totalFiles,
                TotalBytes = totalBytes,
                Running = true,
                StartedUtc = DateTime.UtcNow,
                Status = totalFiles == 0 ? "Nothing to upload." : "Starting…",
            };

            if (totalFiles == 0)
            {
                Job.Running = false;
                Job.FinishedUtc = DateTime.UtcNow;
                return Job;
            }

            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;

            // `concurrency` is per bucket, matching the desktop app: three buckets at 24 means 72
            // uploads in flight, not 24 shared between them. Capped so a wide deploy cannot spawn an
            // unreasonable number of threads, and kept under the connection limit set in S3Client.
            var wanted = concurrency * Math.Max(1, perBucket.Count);
            var workers = Math.Max(1, Math.Min(Math.Min(wanted, MaxWorkers), totalFiles));
            var remaining = workers;

            for (var i = 0; i < workers; i++)
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        Drain(client, queue, Job, token);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref remaining) == 0)
                        {
                            Job.FinishedUtc = DateTime.UtcNow;
                            Job.Status = Job.Cancelled ? "Stopped."
                                : Job.ErrorCount > 0 ? "Finished with " + Job.ErrorCount + " error(s)."
                                : "Done.";
                            Job.Running = false;
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "EasyUpload worker " + i,
                };
                thread.Start();
            }

            return Job;
        }

        public void Cancel()
        {
            if (Job != null) Job.Cancelled = true;
            cancellation?.Cancel();
        }

        private static void Drain(S3Client client, ConcurrentQueue<WorkItem> queue, UploadJob job, CancellationToken token)
        {
            while (!token.IsCancellationRequested && queue.TryDequeue(out var item))
            {
                job.Status = item.Bucket + " · " + item.File.Relative;

                // Byte progress is reported per file as a running total, so it has to be turned back
                // into deltas before it can be added to a shared counter.
                long lastSeen = 0;
                try
                {
                    client.PutObject(item.Bucket, item.Key, item.File.Absolute, sent =>
                    {
                        job.AddBytes(sent - lastSeen);
                        lastSeen = sent;
                    }, token);

                    job.FileDone();
                }
                catch (OperationCanceledException)
                {
                    // Whatever went out for this file does not count as progress.
                    job.AddBytes(-lastSeen);
                    return;
                }
                catch (Exception e)
                {
                    job.AddBytes(item.File.Size - lastSeen);   // keep the bar honest about what is left
                    job.FileDone();
                    job.AddError(item.Bucket + "/" + item.Key + " — " + e.Message);
                }
            }
        }
    }
}
