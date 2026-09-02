using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace Utilities.Editor.EasyUpload
{
    /// <summary>One object already in a bucket, as far as the comparison cares.</summary>
    public class RemoteObject
    {
        public string Key;
        public long Size;
        /// <summary>Seconds since the epoch.</summary>
        public long Mtime;
    }

    /// <summary>Raised for anything AWS refused, already translated into a sentence worth showing.</summary>
    public class S3Exception : Exception
    {
        public S3Exception(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// The four S3 calls this tool makes, over HttpWebRequest with SigV4 signing.
    ///
    /// Per-bucket regions are resolved on demand and cached, so a deploy that spans regions works
    /// without the user having to know or say which bucket lives where.
    /// </summary>
    public class S3Client
    {
        private readonly AwsCredentials credentials;
        private readonly string endpoint;        // empty = real AWS
        private readonly string discoveryRegion;
        private readonly Dictionary<string, string> regionCache = new Dictionary<string, string>();
        private readonly object regionLock = new object();

        static S3Client()
        {
            // .NET allows two connections per endpoint by default, which would serialise an upload
            // that is otherwise mostly waiting on round trips.
            if (ServicePointManager.DefaultConnectionLimit < 64)
                ServicePointManager.DefaultConnectionLimit = 64;
        }

        public S3Client(AwsCredentials credentials, string endpoint, string discoveryRegion)
        {
            this.credentials = credentials;
            this.endpoint = (endpoint ?? "").Trim().TrimEnd('/');
            this.discoveryRegion = string.IsNullOrWhiteSpace(discoveryRegion) ? "us-east-1" : discoveryRegion.Trim();
        }

        /// <summary>True when pointed at something other than real AWS — a local MinIO, for testing.</summary>
        public bool IsLocal => endpoint.Length > 0;

        // ---------- public API ----------

        /// <summary>
        /// Confirm the credentials still work, and return who they belong to. On a local endpoint
        /// there is no STS to ask, so a bucket listing stands in.
        /// </summary>
        public string CheckCredentials()
        {
            if (IsLocal)
            {
                var buckets = ListBuckets();
                return "local S3 — " + buckets.Count + (buckets.Count == 1 ? " bucket" : " buckets");
            }

            var body = "Action=GetCallerIdentity&Version=2011-06-15";
            var bytes = Encoding.UTF8.GetBytes(body);
            var url = "https://sts." + discoveryRegion + ".amazonaws.com/";

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
            request.ContentLength = bytes.Length;
            request.Timeout = 30000;
            SigV4.Sign(request, "sts", discoveryRegion, credentials, "/", "",
                SigV4.Hex(SigV4.Sha256(bytes)));

            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);

            var xml = ReadResponse(request, "check credentials");
            var doc = XDocument.Parse(xml);
            var arn = Descendant(doc.Root, "Arn");
            return string.IsNullOrEmpty(arn) ? "connected" : arn;
        }

        /// <summary>Every bucket the account can see, alphabetically.</summary>
        public List<string> ListBuckets()
        {
            var request = BuildRequest("GET", null, "/", null, discoveryRegion);
            request.Method = "GET";
            var xml = Send(request, "list buckets", SigV4.EmptyPayloadHash, discoveryRegion,
                SigningPath(null, "/"), "");

            var doc = XDocument.Parse(xml);
            var names = doc.Descendants()
                .Where(e => e.Name.LocalName == "Bucket")
                .Select(e => Descendant(e, "Name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Which region a bucket lives in, cached for the session.
        ///
        /// Asked with a HEAD, because S3 returns x-amz-bucket-region on the redirect it sends when
        /// you ask the wrong region — so one request answers it whether or not the guess was right.
        /// </summary>
        public string RegionFor(string bucket)
        {
            if (IsLocal) return discoveryRegion;

            lock (regionLock)
                if (regionCache.TryGetValue(bucket, out var cached)) return cached;

            var region = discoveryRegion;
            try
            {
                var request = BuildRequest("HEAD", bucket, "/", null, discoveryRegion);
                request.Method = "HEAD";
                SigV4.Sign(request, "s3", discoveryRegion, credentials, "/", "", SigV4.EmptyPayloadHash);
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var header = response.Headers["x-amz-bucket-region"];
                    if (!string.IsNullOrEmpty(header)) region = header;
                }
            }
            catch (WebException e)
            {
                var header = (e.Response as HttpWebResponse)?.Headers["x-amz-bucket-region"];
                if (!string.IsNullOrEmpty(header)) region = header;
                else if (IsAuthFailure(e)) throw Translate(e, "resolve the region for " + bucket);
                // Anything else (403 on HeadBucket is common for list-only roles) leaves the
                // discovery region in place, which is right far more often than it is wrong.
            }
            catch (Exception) { /* keep the discovery region */ }

            lock (regionLock) regionCache[bucket] = region;
            return region;
        }

        /// <summary>
        /// Everything already under a prefix, keyed by the key with the prefix stripped, so it lines
        /// up with the relative paths from the build folder.
        /// </summary>
        public Dictionary<string, RemoteObject> ListObjects(string bucket, string prefix, CancellationToken cancel)
        {
            var region = RegionFor(bucket);
            var result = new Dictionary<string, RemoteObject>(StringComparer.Ordinal);
            string continuation = null;

            do
            {
                cancel.ThrowIfCancellationRequested();

                var query = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("list-type", "2"),
                    new KeyValuePair<string, string>("max-keys", "1000"),
                };
                if (!string.IsNullOrEmpty(prefix))
                    query.Add(new KeyValuePair<string, string>("prefix", prefix));
                if (!string.IsNullOrEmpty(continuation))
                    query.Add(new KeyValuePair<string, string>("continuation-token", continuation));

                var canonicalQuery = SigV4.CanonicalQuery(query);
                var request = BuildRequest("GET", bucket, "/", canonicalQuery, region);
                request.Method = "GET";
                var xml = Send(request, "list " + bucket, SigV4.EmptyPayloadHash, region,
                    SigningPath(bucket, "/"), canonicalQuery);

                var doc = XDocument.Parse(xml);
                foreach (var contents in doc.Descendants().Where(e => e.Name.LocalName == "Contents"))
                {
                    var key = Descendant(contents, "Key");
                    if (string.IsNullOrEmpty(key) || key.EndsWith("/", StringComparison.Ordinal)) continue;

                    var rel = string.IsNullOrEmpty(prefix) ? key
                        : (key.StartsWith(prefix, StringComparison.Ordinal) ? key.Substring(prefix.Length) : null);
                    if (string.IsNullOrEmpty(rel)) continue;

                    long.TryParse(Descendant(contents, "Size"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var size);

                    long mtime = 0;
                    if (DateTimeOffset.TryParse(Descendant(contents, "LastModified"),
                            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var modified))
                        mtime = modified.ToUnixTimeSeconds();

                    result[rel] = new RemoteObject { Key = key, Size = size, Mtime = mtime };
                }

                var truncated = Descendant(doc.Root, "IsTruncated");
                continuation = string.Equals(truncated, "true", StringComparison.OrdinalIgnoreCase)
                    ? Descendant(doc.Root, "NextContinuationToken")
                    : null;
            }
            while (!string.IsNullOrEmpty(continuation));

            return result;
        }

        /// <summary>HeadObject requests in flight during a probe.</summary>
        private const int HeadConcurrency = 32;

        /// <summary>
        /// Ask about exactly the keys this build has, all at once, instead of enumerating the prefix.
        ///
        /// The two strategies scale in opposite directions, which is the whole point of having both.
        /// ListObjectsV2 returns 1000 keys per request and chains them on a continuation token, so
        /// enumerating costs remote_objects/1000 *sequential* round trips and nothing about it can be
        /// parallelised. HeadObject costs one request per *local* file, but they are independent, so
        /// it costs local_files/HeadConcurrency waves. Which is cheaper depends entirely on the ratio
        /// between the two sides — and a shared CDN bucket holds every campaign ever deployed while a
        /// build folder holds a handful of files.
        ///
        /// Returns null when the credentials may list but not read: HeadObject needs s3:GetObject
        /// while listing needs s3:ListBucket, and a role can easily have one without the other. The
        /// caller falls back to enumerating rather than failing a bucket that listing handles fine.
        /// </summary>
        public Dictionary<string, RemoteObject> HeadProbe(string bucket, string prefix,
            IList<string> relatives, CancellationToken cancel)
        {
            var region = RegionFor(bucket);
            var found = new Dictionary<string, RemoteObject>(StringComparer.Ordinal);
            var queue = new ConcurrentQueue<string>(relatives);
            var forbidden = 0;
            var failure = (Exception)null;

            var workers = Math.Max(1, Math.Min(HeadConcurrency, relatives.Count));
            var threads = new List<Thread>(workers);

            for (var i = 0; i < workers; i++)
            {
                var thread = new Thread(() =>
                {
                    while (!cancel.IsCancellationRequested &&
                           Volatile.Read(ref forbidden) == 0 &&
                           queue.TryDequeue(out var relative))
                    {
                        try
                        {
                            var found_ = Head(bucket, region, prefix + relative);
                            if (found_ == null) continue;   // 404: the bucket does not have it
                            lock (found) found[relative] = found_;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // No s3:GetObject. Abandon the probe; the caller enumerates instead.
                            Interlocked.Exchange(ref forbidden, 1);
                        }
                        catch (Exception e)
                        {
                            Interlocked.CompareExchange(ref failure, e, null);
                            Interlocked.Exchange(ref forbidden, 1);
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "EasyUpload probe " + bucket,
                };
                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads) thread.Join();
            cancel.ThrowIfCancellationRequested();

            if (failure != null) throw failure;
            return Volatile.Read(ref forbidden) != 0 ? null : found;
        }

        /// <summary>
        /// One HeadObject. Null when the object is not there; throws
        /// <see cref="UnauthorizedAccessException"/> when the credentials cannot read it.
        /// </summary>
        private RemoteObject Head(string bucket, string region, string key)
        {
            var canonicalPath = "/" + SigV4.UriEncode(key, false);
            var request = BuildRequest("HEAD", bucket, canonicalPath, null, region);
            request.Method = "HEAD";
            SigV4.Sign(request, "s3", region, credentials, SigningPath(bucket, canonicalPath), "",
                SigV4.EmptyPayloadHash);

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                    return FromHeaders(response);
            }
            catch (WebException e)
            {
                var status = (int?)(e.Response as HttpWebResponse)?.StatusCode ?? 0;
                if (status == 404) return null;
                if (status == 403 || status == 401) throw new UnauthorizedAccessException(key);
                throw Translate(e, "check " + key);
            }
        }

        private static RemoteObject FromHeaders(HttpWebResponse response)
        {
            long.TryParse(response.Headers["Content-Length"], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var size);

            long mtime = 0;
            if (DateTimeOffset.TryParse(response.Headers["Last-Modified"], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var modified))
                mtime = modified.ToUnixTimeSeconds();

            return new RemoteObject { Size = size, Mtime = mtime };
        }

        /// <summary>
        /// Upload one file. <paramref name="onProgress"/> is called with bytes sent so far, from
        /// whichever thread is doing the sending.
        /// </summary>
        public void PutObject(string bucket, string key, string localPath, Action<long> onProgress, CancellationToken cancel)
        {
            var region = RegionFor(bucket);
            var canonicalPath = "/" + SigV4.UriEncode(key, false);
            var length = new FileInfo(localPath).Length;

            // The body is not hashed for the signature. Hashing it would mean reading every file
            // twice — once for SHA-256 and once to send it — which on a build folder is a second
            // full pass over the data before a single byte goes out. UNSIGNED-PAYLOAD is the value
            // S3 defines for exactly this case; TLS still covers the transfer, and a truncated
            // upload shows up as a size mismatch on the next review.
            var payloadHash = SigV4.UnsignedPayload;

            var request = BuildRequest("PUT", bucket, canonicalPath, null, region);
            request.Method = "PUT";
            request.ContentLength = length;

            var contentType = ContentTypeFor(localPath);
            if (contentType != null) request.ContentType = contentType;

            request.AllowWriteStreamBuffering = false;   // stream it; a build folder will not fit in memory
            request.Timeout = 60000;
            request.ReadWriteTimeout = 300000;
            request.ServicePoint.Expect100Continue = false;
            SigV4.Sign(request, "s3", region, credentials, SigningPath(bucket, canonicalPath), "", payloadHash);

            try
            {
                using (var source = File.OpenRead(localPath))
                using (var destination = request.GetRequestStream())
                {
                    var buffer = new byte[81920];
                    long sent = 0;
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        destination.Write(buffer, 0, read);
                        sent += read;
                        onProgress?.Invoke(sent);
                    }
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if ((int)response.StatusCode >= 300)
                        throw new S3Exception("Upload of " + key + " failed: HTTP " + (int)response.StatusCode);
                }
            }
            catch (WebException e)
            {
                throw Translate(e, "upload " + key);
            }
        }

        // ---------- plumbing ----------

        /// <summary>
        /// The path the signature has to cover, which is not always the path the caller asked for:
        /// under path-style addressing the bucket sits in the URL path rather than the host, and
        /// signing without it produces a signature for a request that was never sent.
        /// </summary>
        private string SigningPath(string bucket, string canonicalPath) =>
            IsLocal && !string.IsNullOrEmpty(bucket) ? "/" + bucket + canonicalPath : canonicalPath;

        /// <summary>
        /// Virtual-host addressing on real AWS, path-style on anything else: a local MinIO would
        /// have to resolve bucket.localhost, which it does not serve.
        /// </summary>
        private HttpWebRequest BuildRequest(string method, string bucket, string canonicalPath, string canonicalQuery, string region)
        {
            string url;
            if (IsLocal)
            {
                url = endpoint + (string.IsNullOrEmpty(bucket) ? "" : "/" + bucket) + canonicalPath;
            }
            else if (string.IsNullOrEmpty(bucket))
            {
                url = "https://s3." + region + ".amazonaws.com" + canonicalPath;
            }
            else
            {
                url = "https://" + bucket + ".s3." + region + ".amazonaws.com" + canonicalPath;
            }

            if (!string.IsNullOrEmpty(canonicalQuery)) url += "?" + canonicalQuery;

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = 30000;
            request.KeepAlive = true;
            request.UserAgent = "EasyUpload-Unity";
            return request;
        }

        /// <summary>Sign, send, and hand back the body. Retries the failures that are worth retrying.</summary>
        private string Send(HttpWebRequest request, string what, string payloadHash, string region, string canonicalPath, string canonicalQuery)
        {
            const int attempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                if (attempt > 1)
                {
                    // A signed request cannot be replayed: x-amz-date is inside the signature and a
                    // stale one is rejected. Rebuild from the same inputs instead.
                    var retry = (HttpWebRequest)WebRequest.Create(request.RequestUri);
                    retry.Method = request.Method;
                    retry.Timeout = request.Timeout;
                    retry.KeepAlive = request.KeepAlive;
                    retry.UserAgent = request.UserAgent;
                    request = retry;
                }

                SigV4.Sign(request, "s3", region, credentials, canonicalPath, canonicalQuery, payloadHash);
                try
                {
                    return ReadResponse(request, what);
                }
                catch (S3Exception e) when (attempt < attempts && IsWorthRetrying(e))
                {
                    Thread.Sleep(400 * attempt);
                }
            }
        }

        private static string ReadResponse(HttpWebRequest request, string what)
        {
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    if (stream == null) return "";
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch (WebException e)
            {
                throw Translate(e, what);
            }
        }

        /// <summary>
        /// Rejected credentials and denied permissions will be rejected identically a second later,
        /// so retrying them only delays telling the user what is wrong.
        /// </summary>
        private static bool IsWorthRetrying(S3Exception e)
        {
            var message = e.Message.ToLowerInvariant();
            return !message.Contains("expired") &&
                   !message.Contains("rejected these credentials") &&
                   !message.Contains("access denied") &&
                   !message.Contains("no such bucket");
        }

        /// <summary>
        /// Guessed from the extension so objects are served with a usable type — the same table the
        /// desktop app and the AWS CLI use. Without it everything lands as application/octet-stream
        /// and browsers download instead of render.
        /// </summary>
        public static string ContentTypeFor(string path)
        {
            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return null;

            switch (extension.TrimStart('.').ToLowerInvariant())
            {
                case "json": return "application/json";
                case "js":
                case "mjs": return "text/javascript";
                case "wasm": return "application/wasm";
                case "html":
                case "htm": return "text/html";
                case "css": return "text/css";
                case "xml": return "application/xml";
                case "txt":
                case "log": return "text/plain";
                case "csv": return "text/csv";
                case "svg": return "image/svg+xml";
                case "png": return "image/png";
                case "jpg":
                case "jpeg": return "image/jpeg";
                case "gif": return "image/gif";
                case "webp": return "image/webp";
                case "avif": return "image/avif";
                case "ico": return "image/x-icon";
                case "mp3": return "audio/mpeg";
                case "ogg": return "audio/ogg";
                case "wav": return "audio/wav";
                case "mp4": return "video/mp4";
                case "webm": return "video/webm";
                case "pdf": return "application/pdf";
                case "zip": return "application/zip";
                case "gz": return "application/gzip";
                case "br": return "application/brotli";
                case "woff": return "font/woff";
                case "woff2": return "font/woff2";
                case "ttf": return "font/ttf";
                case "otf": return "font/otf";
                default: return null;
            }
        }

        private static bool IsAuthFailure(WebException e)
        {
            var status = (int?)(e.Response as HttpWebResponse)?.StatusCode ?? 0;
            return status == 401 || status == 403;
        }

        /// <summary>
        /// Turn a WebException into the sentence a user can act on. S3 puts the useful part in an
        /// XML body that never reaches the exception message, so read it.
        /// </summary>
        private static S3Exception Translate(WebException e, string what)
        {
            var body = "";
            var status = 0;
            if (e.Response is HttpWebResponse response)
            {
                status = (int)response.StatusCode;
                try
                {
                    using (var stream = response.GetResponseStream())
                        if (stream != null)
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                                body = reader.ReadToEnd();
                }
                catch (Exception) { /* no body to be had */ }
            }

            var code = "";
            var message = "";
            if (body.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                try
                {
                    var doc = XDocument.Parse(body);
                    code = Descendant(doc.Root, "Code") ?? "";
                    message = Descendant(doc.Root, "Message") ?? "";
                }
                catch (Exception) { /* not the XML we expected */ }
            }

            var haystack = (code + " " + message + " " + e.Message).ToLowerInvariant();

            if (haystack.Contains("expiredtoken") || haystack.Contains("token has expired") ||
                haystack.Contains("token included in the request is expired"))
                return new S3Exception("Your AWS session has expired. Paste a fresh credentials block in Settings.", e);

            if (haystack.Contains("invalidclienttokenid") || haystack.Contains("signaturedoesnotmatch") ||
                haystack.Contains("invalidaccesskeyid"))
                return new S3Exception("AWS rejected these credentials. Check you pasted the whole block.", e);

            if (status == 403 || haystack.Contains("accessdenied"))
                return new S3Exception("Could not " + what + ": access denied. This account may not have permission for that bucket.", e);

            if (status == 404 || haystack.Contains("nosuchbucket"))
                return new S3Exception("Could not " + what + ": no such bucket.", e);

            if (e.Status == WebExceptionStatus.NameResolutionFailure ||
                e.Status == WebExceptionStatus.ConnectFailure ||
                e.Status == WebExceptionStatus.Timeout)
                return new S3Exception("Could not " + what + ": could not reach AWS. Check your internet connection.", e);

            var detail = !string.IsNullOrEmpty(message) ? message : e.Message;
            return new S3Exception("Could not " + what + ": " + detail, e);
        }

        private static string Descendant(XElement root, string localName) =>
            root?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
    }
}
