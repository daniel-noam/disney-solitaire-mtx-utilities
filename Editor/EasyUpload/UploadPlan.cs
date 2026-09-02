using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Utilities.Editor.EasyUpload
{
    /// <summary>Why a file is, or is not, going to be sent.</summary>
    public enum UploadReason
    {
        New,        // the bucket does not have this key
        Size,       // it has it, at a different size
        Newer,      // same size, but the local copy is newer
        Forced,     // identical, ticked anyway
        UpToDate,   // nothing to do
        TooLarge,   // over the single-PUT ceiling
        Junk,       // matches a drop pattern in Settings
    }

    /// <summary>One file under the build folder.</summary>
    public class LocalFile
    {
        public string Absolute;
        /// <summary>Forward-slash path relative to the build folder; becomes the object key.</summary>
        public string Relative;
        public long Size;
        /// <summary>Seconds since the epoch, or 0 when the OS would not say.</summary>
        public long Mtime;
        /// <summary>Matches a drop pattern: walked, listed, never uploaded.</summary>
        public bool Junk;
    }

    /// <summary>One file's verdict against one bucket.</summary>
    public class PlanEntry
    {
        public LocalFile File;
        public UploadReason Reason;
        public bool Selected;
        public long RemoteSize = -1;

        public bool CanUpload => Reason != UploadReason.TooLarge && Reason != UploadReason.Junk;
    }

    /// <summary>What one bucket needs.</summary>
    public class BucketPlan
    {
        public string Bucket;
        public string Region;
        public string Prefix;
        public List<PlanEntry> Entries = new List<PlanEntry>();
        /// <summary>Set when the bucket could not be inspected; the others still deploy.</summary>
        public string Error;

        public int SelectedCount
        {
            get
            {
                var n = 0;
                foreach (var e in Entries) if (e.Selected && e.CanUpload) n++;
                return n;
            }
        }

        public long SelectedBytes
        {
            get
            {
                long n = 0;
                foreach (var e in Entries) if (e.Selected && e.CanUpload) n += e.File.Size;
                return n;
            }
        }
    }

    /// <summary>The whole review: one folder against every selected bucket.</summary>
    public class SyncPlan
    {
        public string Root;
        public List<LocalFile> Files = new List<LocalFile>();
        public List<BucketPlan> Buckets = new List<BucketPlan>();
        public bool Truncated;

        public int TotalSelected
        {
            get { var n = 0; foreach (var b in Buckets) n += b.SelectedCount; return n; }
        }

        public long TotalBytes
        {
            get { long n = 0; foreach (var b in Buckets) n += b.SelectedBytes; return n; }
        }
    }

    /// <summary>Walking the build folder and deciding what each bucket is missing.</summary>
    public static class UploadPlanner
    {
        /// <summary>A single PutObject caps out at 5 GiB; past that needs a multipart upload, which
        /// this version does not implement. Better to say so than to fail mid-transfer.</summary>
        public const long MaxSinglePut = 5L * 1024 * 1024 * 1024;

        /// <summary>Past this the review list stops being something a person reads, and IMGUI stops
        /// being something that draws it.</summary>
        public const int MaxPlanEntries = 20000;

        /// <summary>
        /// Above this many local files, enumerating the prefix costs fewer round trips than asking
        /// about each file one by one. See <see cref="S3Client.HeadProbe"/> for why the two scale in
        /// opposite directions.
        ///
        /// Conservative on purpose, so neither case can be much worse than the other: at the
        /// threshold a probe is a few waves against the two pages that are the minimum for a prefix
        /// which did not fit in one — a fraction of a second either way.
        /// </summary>
        public const int HeadProbeMax = 250;

        /// <summary>
        /// True when a file name matches one of the configured drop patterns. Matching is on the
        /// name alone, so a pattern applies at any depth, and is case-insensitive because the
        /// filesystems this runs on are.
        /// </summary>
        public static bool IsDropped(string fileName, IList<string> patterns)
        {
            if (patterns == null) return false;
            for (var i = 0; i < patterns.Count; i++)
                if (Glob(fileName, patterns[i])) return true;
            return false;
        }

        /// <summary>
        /// `*` and `?` wildcard matching. Hand-rolled rather than via Regex: this runs once per file
        /// in the build folder, and building a Regex per pattern per file would cost more than the
        /// walk it is part of.
        /// </summary>
        public static bool Glob(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            int t = 0, p = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length &&
                    (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
                {
                    t++;
                    p++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = t;
                }
                else if (star >= 0)
                {
                    // Backtrack: let the last '*' swallow one more character and try again.
                    p = star + 1;
                    t = ++mark;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        /// <summary>Every file under the folder, junk included and flagged.</summary>
        public static List<LocalFile> Walk(string root, IList<string> dropPatterns, CancellationToken cancel)
        {
            var files = new List<LocalFile>();
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

            // DirectoryInfo rather than a path enumeration plus `new FileInfo(path)`: the directory
            // scan already carries size and timestamp, so this reads them instead of paying a
            // second stat call per file. On a build folder of thousands, that is most of the walk.
            foreach (var info in new DirectoryInfo(rootFull).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancel.ThrowIfCancellationRequested();

                var relative = info.FullName.Substring(rootFull.Length)
                    .TrimStart(Path.DirectorySeparatorChar, '/');

                files.Add(new LocalFile
                {
                    Absolute = info.FullName,
                    // S3 keys always use forward slashes, including when built on Windows.
                    Relative = relative.Replace('\\', '/'),
                    Size = info.Length,
                    Mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
                    Junk = IsDropped(info.Name, dropPatterns),
                });
            }

            files.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
            return files;
        }

        /// <summary>
        /// The comparison rule, shared by every bucket.
        ///
        /// This is `aws s3 sync`'s default rule: upload when the destination lacks the key, when the
        /// size differs, or when the local copy is newer. No checksums — the CLI does not compare
        /// them either, and hashing a whole build to decide what to send would dominate the deploy.
        /// </summary>
        public static UploadReason Decide(LocalFile local, RemoteObject remote, bool force)
        {
            if (local.Junk) return UploadReason.Junk;
            if (local.Size > MaxSinglePut) return UploadReason.TooLarge;

            if (force) return remote == null ? UploadReason.New : UploadReason.Forced;
            if (remote == null) return UploadReason.New;
            if (remote.Size != local.Size) return UploadReason.Size;

            // An unknown local timestamp with a matching size is good enough to skip, which is how
            // the CLI treats an unreadable mtime too.
            if (local.Mtime > 0 && remote.Mtime > 0 && local.Mtime > remote.Mtime) return UploadReason.Newer;
            return UploadReason.UpToDate;
        }

        public static string KeyPrefix(string version)
        {
            var v = (version ?? "").Trim().Trim('/');
            return v.Length == 0 ? "" : v + "/";
        }

        /// <summary>
        /// Inspect every bucket and produce the review. A bucket that cannot be listed records its
        /// error and the rest of the plan carries on — one unreachable bucket should not cost you
        /// the whole review.
        /// </summary>
        public static SyncPlan Build(
            S3Client client,
            string root,
            IList<string> buckets,
            string version,
            bool force,
            IList<string> dropPatterns,
            CancellationToken cancel,
            Action<string> onProgress)
        {
            var plan = new SyncPlan { Root = root };

            onProgress?.Invoke("Reading the build folder…");
            plan.Files = Walk(root, dropPatterns, cancel);
            plan.Truncated = plan.Files.Count > MaxPlanEntries;

            var prefix = KeyPrefix(version);

            // Every bucket is inspected at once. Listing is almost entirely waiting on S3, so doing
            // them one after another made a three-bucket review take three times as long as it
            // needed to — the single biggest reason this felt slower than it should.
            var results = new BucketPlan[buckets.Count];
            var threads = new List<Thread>(buckets.Count);
            var finished = 0;

            onProgress?.Invoke("Inspecting " + buckets.Count +
                               (buckets.Count == 1 ? " bucket…" : " buckets…"));

            for (var i = 0; i < buckets.Count; i++)
            {
                var index = i;
                var bucket = buckets[i];

                var thread = new Thread(() =>
                {
                    var bucketPlan = new BucketPlan { Bucket = bucket, Prefix = prefix };
                    try
                    {
                        bucketPlan.Region = client.RegionFor(bucket);
                        Compare(bucketPlan, plan.Files, Inspect(client, bucket, prefix, plan.Files, cancel), force);
                    }
                    catch (OperationCanceledException)
                    {
                        bucketPlan.Error = "Stopped.";
                    }
                    catch (Exception e)
                    {
                        // One unreachable bucket should not cost the whole review, so the error is
                        // recorded against that bucket and the rest still report.
                        bucketPlan.Error = e.Message;
                    }

                    results[index] = bucketPlan;
                    var done = Interlocked.Increment(ref finished);
                    onProgress?.Invoke("Inspected " + done + " of " + buckets.Count + "…");
                })
                {
                    IsBackground = true,
                    Name = "EasyUpload plan " + bucket,
                };

                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads) thread.Join();
            cancel.ThrowIfCancellationRequested();

            foreach (var bucketPlan in results)
                if (bucketPlan != null) plan.Buckets.Add(bucketPlan);

            return plan;
        }

        /// <summary>
        /// What the bucket already has, by whichever route costs fewer round trips.
        ///
        /// A small build asks about its own keys directly; a large one enumerates the prefix. The
        /// probe falls back to enumerating on its own if the credentials cannot read objects.
        /// </summary>
        private static Dictionary<string, RemoteObject> Inspect(S3Client client, string bucket,
            string prefix, List<LocalFile> files, CancellationToken cancel)
        {
            var wanted = new List<string>();
            foreach (var file in files)
                if (!file.Junk) wanted.Add(file.Relative);

            if (wanted.Count > 0 && wanted.Count <= HeadProbeMax)
            {
                var probed = client.HeadProbe(bucket, prefix, wanted, cancel);
                if (probed != null) return probed;
            }

            return client.ListObjects(bucket, prefix, cancel);
        }

        /// <summary>One bucket's verdict on every local file.</summary>
        private static void Compare(BucketPlan bucketPlan, List<LocalFile> files,
            Dictionary<string, RemoteObject> remote, bool force)
        {
            var shown = 0;
            foreach (var file in files)
            {
                if (shown++ >= MaxPlanEntries) break;

                remote.TryGetValue(file.Relative, out var existing);
                var reason = Decide(file, existing, force);

                bucketPlan.Entries.Add(new PlanEntry
                {
                    File = file,
                    Reason = reason,
                    // Files the bucket needs start ticked; files it already has start unticked, so
                    // pressing Upload does the expected thing without reading the list.
                    Selected = reason == UploadReason.New || reason == UploadReason.Size ||
                               reason == UploadReason.Newer || reason == UploadReason.Forced,
                    RemoteSize = existing?.Size ?? -1,
                });
            }
        }

        public static string Describe(UploadReason reason)
        {
            switch (reason)
            {
                case UploadReason.New: return "New";
                case UploadReason.Size: return "Size differs";
                case UploadReason.Newer: return "Newer locally";
                case UploadReason.Forced: return "Re-send";
                case UploadReason.UpToDate: return "Up to date";
                case UploadReason.TooLarge: return "Over 5 GB";
                case UploadReason.Junk: return "Dropped";
                default: return reason.ToString();
            }
        }

        public static string Explain(UploadReason reason)
        {
            switch (reason)
            {
                case UploadReason.New: return "The bucket does not have this file yet.";
                case UploadReason.Size: return "The bucket has this file at a different size.";
                case UploadReason.Newer: return "Same size, but your local copy was modified more recently.";
                case UploadReason.Forced: return "Identical to what is in the bucket. Ticked, so it will be sent again.";
                case UploadReason.UpToDate: return "Same size and no newer than the bucket's copy. Tick it to send it anyway.";
                case UploadReason.TooLarge: return "Larger than 5 GB. That needs a multipart upload, which this version does not do.";
                case UploadReason.Junk: return "Matches a drop pattern in Settings, so it is never uploaded. Listed so the file count still matches the AWS console.";
                default: return "";
            }
        }

        public static string HumanBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0
                ? bytes + " B"
                : value.ToString(value >= 100 ? "0" : "0.0") + " " + units[unit];
        }
    }
}
