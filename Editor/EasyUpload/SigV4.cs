using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Utilities.Editor.EasyUpload
{
    /// <summary>
    /// AWS Signature Version 4, spoken directly rather than through the AWS SDK for .NET.
    ///
    /// The whole surface this tool needs is four calls — list buckets, find a bucket's region, list
    /// a prefix, put an object — and the SDK is a pile of DLLs in Plugins/ plus a System.Net.Http
    /// version fight with Unity's Mono for the privilege. Signing is ~100 lines and never breaks on
    /// a Unity upgrade, so it is written out here.
    /// </summary>
    public static class SigV4
    {
        public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
        public const string EmptyPayloadHash =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        /// <summary>
        /// Percent-encode per RFC 3986, which is stricter than <see cref="Uri.EscapeDataString"/>
        /// on some runtimes. Object keys are path segments, so slashes are kept literal there and
        /// escaped everywhere else.
        /// </summary>
        public static string UriEncode(string value, bool encodeSlash)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length * 2);
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                    c == '-' || c == '_' || c == '.' || c == '~')
                {
                    sb.Append(c);
                }
                else if (c == '/' && !encodeSlash)
                {
                    sb.Append('/');
                }
                else
                {
                    sb.Append('%').Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        /// <summary>Query parameters sorted and encoded into a canonical query string.</summary>
        public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query)
        {
            if (query == null) return "";
            var pairs = new List<string>();
            var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in query) sorted[UriEncode(kv.Key, true)] = UriEncode(kv.Value ?? "", true);
            foreach (var kv in sorted) pairs.Add(kv.Key + "=" + kv.Value);
            return string.Join("&", pairs.ToArray());
        }

        /// <summary>
        /// Sign a request and stamp the resulting headers onto it.
        ///
        /// <paramref name="canonicalPath"/> and <paramref name="canonicalQuery"/> must be the
        /// already-encoded strings the request was built from: re-deriving them from
        /// <see cref="HttpWebRequest.RequestUri"/> is where signing quietly goes wrong, because the
        /// Uri class normalises escaping and the signature then covers a path AWS never saw.
        /// </summary>
        public static void Sign(
            HttpWebRequest request,
            string service,
            string region,
            AwsCredentials credentials,
            string canonicalPath,
            string canonicalQuery,
            string payloadHash)
        {
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

            var host = request.RequestUri.IsDefaultPort
                ? request.RequestUri.Host
                : request.RequestUri.Host + ":" + request.RequestUri.Port;

            var token = (credentials.sessionToken ?? "").Trim();
            var hasToken = token.Length > 0;

            // Only host and the x-amz-* headers are signed. Content-Type is deliberately left out:
            // it is optional under SigV4, and leaving it unsigned means HttpWebRequest's own
            // handling of that header can never disagree with the signature.
            var canonicalHeaders = new StringBuilder();
            canonicalHeaders.Append("host:").Append(host).Append('\n');
            canonicalHeaders.Append("x-amz-content-sha256:").Append(payloadHash).Append('\n');
            canonicalHeaders.Append("x-amz-date:").Append(amzDate).Append('\n');
            if (hasToken) canonicalHeaders.Append("x-amz-security-token:").Append(token).Append('\n');

            var signedHeaders = hasToken
                ? "host;x-amz-content-sha256;x-amz-date;x-amz-security-token"
                : "host;x-amz-content-sha256;x-amz-date";

            var canonicalRequest = string.Join("\n", new[]
            {
                request.Method,
                canonicalPath,
                canonicalQuery ?? "",
                canonicalHeaders.ToString(),
                signedHeaders,
                payloadHash,
            });

            var scope = dateStamp + "/" + region + "/" + service + "/aws4_request";
            var stringToSign = string.Join("\n", new[]
            {
                "AWS4-HMAC-SHA256",
                amzDate,
                scope,
                Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest))),
            });

            var signingKey = SigningKey(credentials.secretAccessKey.Trim(), dateStamp, region, service);
            var signature = Hex(HmacSha256(signingKey, stringToSign));

            request.Headers["x-amz-date"] = amzDate;
            request.Headers["x-amz-content-sha256"] = payloadHash;
            if (hasToken) request.Headers["x-amz-security-token"] = token;
            request.Headers[HttpRequestHeader.Authorization] =
                "AWS4-HMAC-SHA256 Credential=" + credentials.accessKeyId.Trim() + "/" + scope +
                ", SignedHeaders=" + signedHeaders +
                ", Signature=" + signature;
        }

        private static byte[] SigningKey(string secret, string dateStamp, string region, string service)
        {
            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secret), dateStamp);
            var kRegion = HmacSha256(kDate, region);
            var kService = HmacSha256(kRegion, service);
            return HmacSha256(kService, "aws4_request");
        }

        private static byte[] HmacSha256(byte[] key, string data)
        {
            using (var hmac = new HMACSHA256(key))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        public static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
