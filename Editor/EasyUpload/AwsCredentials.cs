using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Utilities.Editor.EasyUpload
{
    /// <summary>
    /// One set of AWS credentials, plus the parsing and storage around them.
    ///
    /// Field names match the JSON the EasyUpload desktop app writes, because both tools read and
    /// write the same file (see <see cref="StorePath"/>). Connect in either one and the other is
    /// connected too — these are short-lived STS session credentials and having to paste them
    /// twice per session was the whole reason to share the file.
    /// </summary>
    [Serializable]
    public class AwsCredentials
    {
        public string accessKeyId = "";
        public string secretAccessKey = "";

        /// <summary>Absent for long-lived IAM keys; present for STS/SSO session credentials.</summary>
        public string sessionToken = "";

        /// <summary>RFC3339, when the paste happened to say when the session ends. Usually empty.</summary>
        public string expiration = "";

        /// <summary>Unix seconds, stamped when the credentials were stored.</summary>
        public long savedAt;

        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(accessKeyId) && !string.IsNullOrWhiteSpace(secretAccessKey);

        /// <summary>Last four characters of the key id — enough to confirm which credentials are
        /// loaded without putting the whole thing on screen.</summary>
        public string Hint
        {
            get
            {
                var k = (accessKeyId ?? "").Trim();
                return k.Length <= 4 ? k : "…" + k.Substring(k.Length - 4);
            }
        }

        public AwsCredentials Clone() => (AwsCredentials)MemberwiseClone();

        // ---------- parsing ----------

        /// <summary>
        /// Parse a pasted credentials block. Tolerant on purpose: people paste from the SSO portal,
        /// from `aws configure export-credentials`, from a Slack message that substituted smart
        /// quotes, or a fragment of ~/.aws/credentials.
        /// </summary>
        /// <param name="error">Plain-language reason, naming which part is missing.</param>
        public static AwsCredentials Parse(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Nothing was pasted.";
                return null;
            }

            var fromJson = ParseJson(text);
            if (fromJson != null && fromJson.IsComplete) return fromJson;

            // Normalise the quote characters chat apps substitute in.
            var normalised = text
                .Replace('“', '"').Replace('”', '"')
                .Replace('‘', '\'').Replace('’', '\'');

            var result = new AwsCredentials();
            foreach (var rawLine in normalised.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;

                var eq = line.IndexOf('=');
                if (eq < 0) continue;

                var field = CanonicalKey(line.Substring(0, eq));
                if (field == null) continue;

                var value = CleanValue(line.Substring(eq + 1));
                if (value.Length == 0) continue;

                switch (field)
                {
                    case "access_key_id": result.accessKeyId = value; break;
                    case "secret_access_key": result.secretAccessKey = value; break;
                    case "session_token": result.sessionToken = value; break;
                    case "expiration": result.expiration = value; break;
                }
            }

            if (result.IsComplete) return result;

            // Say which part is missing — "invalid credentials" sends people in circles.
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(result.accessKeyId)) missing.Add("AWS_ACCESS_KEY_ID");
            if (string.IsNullOrWhiteSpace(result.secretAccessKey)) missing.Add("AWS_SECRET_ACCESS_KEY");
            error = "Could not find " + string.Join(" and ", missing.ToArray()) +
                    " in what you pasted. Paste the whole credentials block, including the export lines.";
            return null;
        }

        /// <summary>Strip one layer of matching quotes, then trailing shell/JSON punctuation.</summary>
        private static string CleanValue(string raw)
        {
            var v = raw.Trim();
            while (v.EndsWith(";", StringComparison.Ordinal) || v.EndsWith(",", StringComparison.Ordinal))
                v = v.Substring(0, v.Length - 1).TrimEnd();

            foreach (var q in new[] { '"', '\'' })
            {
                if (v.Length >= 2 && v[0] == q && v[v.Length - 1] == q)
                {
                    v = v.Substring(1, v.Length - 2);
                    break;
                }
            }
            return v.Trim();
        }

        /// <summary>
        /// Reduce a key to a canonical field so every paste dialect lands on the same three values:
        /// `export AWS_ACCESS_KEY_ID`, `$env:AWS_ACCESS_KEY_ID`, `set AWS_ACCESS_KEY_ID` and the
        /// credentials-file `aws_access_key_id` all normalise together.
        /// </summary>
        private static string CanonicalKey(string raw)
        {
            var k = raw.Trim().ToUpperInvariant();
            foreach (var prefix in new[] { "EXPORT ", "SET ", "SETX ", "$ENV:", "ENV:", "DECLARE -X " })
            {
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                    k = k.Substring(prefix.Length).Trim();
            }
            k = k.TrimStart('$').Trim();

            var sb = new StringBuilder(k.Length);
            foreach (var c in k)
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_') sb.Append(c);
            k = sb.ToString();

            switch (k)
            {
                case "AWS_ACCESS_KEY_ID":
                case "ACCESS_KEY_ID":
                case "AWSACCESSKEYID":
                    return "access_key_id";
                case "AWS_SECRET_ACCESS_KEY":
                case "SECRET_ACCESS_KEY":
                case "AWSSECRETACCESSKEY":
                    return "secret_access_key";
                case "AWS_SESSION_TOKEN":
                case "SESSION_TOKEN":
                case "AWS_SECURITY_TOKEN":
                case "AWSSESSIONTOKEN":
                    return "session_token";
                case "AWS_CREDENTIAL_EXPIRATION":
                case "EXPIRATION":
                    return "expiration";
                default:
                    return null;
            }
        }

        // Populated by JsonUtility through reflection, which the compiler cannot see.
#pragma warning disable 0649
        [Serializable]
        private class StsEnvelope { public StsBody Credentials; }

        [Serializable]
        private class StsBody
        {
            // Both casings, because `aws sts` prints PascalCase and some tools print camelCase.
            public string AccessKeyId, SecretAccessKey, SessionToken, Expiration;
            public string accessKeyId, secretAccessKey, sessionToken, expiration;
        }
#pragma warning restore 0649

        /// <summary>The JSON that `aws sts assume-role` / `get-session-token` prints, nested or not.</summary>
        private static AwsCredentials ParseJson(string text)
        {
            var trimmed = text.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal)) return null;

            StsBody body = null;
            try
            {
                var envelope = JsonUtility.FromJson<StsEnvelope>(trimmed);
                if (envelope?.Credentials != null) body = envelope.Credentials;
            }
            catch (Exception) { /* not the nested shape; fall through */ }

            if (body == null)
            {
                try { body = JsonUtility.FromJson<StsBody>(trimmed); }
                catch (Exception) { return null; }
            }
            if (body == null) return null;

            var c = new AwsCredentials
            {
                accessKeyId = First(body.AccessKeyId, body.accessKeyId),
                secretAccessKey = First(body.SecretAccessKey, body.secretAccessKey),
                sessionToken = First(body.SessionToken, body.sessionToken),
                expiration = First(body.Expiration, body.expiration),
            };
            return c.IsComplete ? c : null;
        }

        private static string First(string a, string b) =>
            !string.IsNullOrEmpty(a) ? a : (b ?? "");

        // ---------- storage ----------
        //
        // The same owner-only file the EasyUpload desktop app uses, so the two tools share one
        // connection. Any process running as this user can read it — the same exposure
        // ~/.aws/credentials already carries, so on a machine with the AWS CLI this adds nothing
        // new. Turning off "Remember credentials" keeps them in memory for the session instead.

        private const string AppIdentifier = "co.superplay.easyupload";

        /// <summary>The config folder EasyUpload uses on this OS.</summary>
        public static string ConfigDir
        {
            get
            {
#if UNITY_EDITOR_WIN
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(roaming, AppIdentifier);
#elif UNITY_EDITOR_OSX
                var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                return Path.Combine(home, "Library/Application Support/" + AppIdentifier);
#else
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(xdg))
                    xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config");
                return Path.Combine(xdg, AppIdentifier);
#endif
            }
        }

        public static string StorePath => Path.Combine(ConfigDir, "credentials.json");

        /// <summary>
        /// Credentials from the shared file, or failing that from the environment. Null when there
        /// are none to be had — the caller shows the paste box rather than treating it as an error.
        /// </summary>
        public static AwsCredentials Load()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    var c = JsonUtility.FromJson<AwsCredentials>(File.ReadAllText(StorePath));
                    if (c != null && c.IsComplete) return c;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EasyUpload] Could not read stored credentials: " + e.Message);
            }

            var envKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
            var envSecret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
            if (!string.IsNullOrEmpty(envKey) && !string.IsNullOrEmpty(envSecret))
            {
                return new AwsCredentials
                {
                    accessKeyId = envKey,
                    secretAccessKey = envSecret,
                    sessionToken = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN") ?? "",
                };
            }

            return null;
        }

        /// <summary>
        /// Write to the shared file. Hand-rolled rather than JsonUtility because the desktop app's
        /// optional fields must be absent when empty, not present as "" — it deserialises into
        /// Option types and an empty string is not the same as nothing.
        /// </summary>
        public static void Save(AwsCredentials c)
        {
            if (c == null || !c.IsComplete) return;

            c.savedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"accessKeyId\": ").Append(JsonString(c.accessKeyId.Trim())).Append(",\n");
            sb.Append("  \"secretAccessKey\": ").Append(JsonString(c.secretAccessKey.Trim())).Append(",\n");
            if (!string.IsNullOrWhiteSpace(c.sessionToken))
                sb.Append("  \"sessionToken\": ").Append(JsonString(c.sessionToken.Trim())).Append(",\n");
            if (!string.IsNullOrWhiteSpace(c.expiration))
                sb.Append("  \"expiration\": ").Append(JsonString(c.expiration.Trim())).Append(",\n");
            sb.Append("  \"savedAt\": ").Append(c.savedAt).Append("\n");
            sb.Append("}\n");

            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(StorePath, sb.ToString());
            MakeOwnerOnly(StorePath);
        }

        public static void Forget()
        {
            try { if (File.Exists(StorePath)) File.Delete(StorePath); }
            catch (Exception e) { Debug.LogWarning("[EasyUpload] Could not delete stored credentials: " + e.Message); }
        }

        /// <summary>
        /// chmod 600. On Windows the file inherits the user profile's ACL, which already excludes
        /// other users, so there is nothing to do there.
        /// </summary>
        private static void MakeOwnerOnly(string path)
        {
#if UNITY_EDITOR_WIN
            // Nothing to do.
#else
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/bin/chmod", "600 \"" + path + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p?.WaitForExit(2000);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EasyUpload] Could not restrict permissions on the credentials file: " + e.Message);
            }
#endif
        }

        private static string JsonString(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
