using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MaoIntegration
{
    public enum MaoGmailCommandKind
    {
        ConfigureClient,
        Connect,
        Status,
        Disconnect,
        ListUnread,
        SearchSender
    }

    public sealed class MaoGmailCommand
    {
        public MaoGmailCommandKind Kind { get; }
        public string Value { get; }

        public MaoGmailCommand(MaoGmailCommandKind kind, string value = "")
        {
            Kind = kind;
            Value = value ?? string.Empty;
        }
    }

    public static class MaoGmailCommandParser
    {
        private const string Prefix = @"\s*(?:(?:miku|mao)(?:\s+(?:ơi|oi))?\s*[,!:\-]?\s*)?";
        private static readonly Regex Configure = new Regex(
            @"^\s*/gmail\s+client\s+(?<value>\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Connect = new Regex(
            "^" + Prefix + @"(?:kết\s+nối|ket\s+noi|connect)(?:\s+với|\s+voi)?\s+gmail(?:\s+(?:đi|di|nhé|nhe|nha))?\s*[.!?~]*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Disconnect = new Regex(
            "^" + Prefix + @"(?:ngắt\s+kết\s+nối|ngat\s+ket\s+noi|disconnect)\s+gmail(?:\s+(?:đi|di|nhé|nhe|nha))?\s*[.!?~]*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Status = new Regex(
            @"^\s*(?:(?:miku|mao)(?:\s+(?:ơi|oi))?\s*[,!:\-]?\s*)?gmail\s+(?:đã|da|đang|dang)\s+kết\s+nối\s+(?:chưa|chua|không|khong)\s*[.!?~]*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Unread = new Regex(
            "^" + Prefix + @"(?:kiểm\s+tra|kiem\s+tra|xem|đọc|doc)\s+(?:gmail|mail)(?:\s+(?:mới|moi|chưa\s+đọc|chua\s+doc|chưa\s+xem|chua\s+xem))?(?:\s+(?:đi|di|giúp|giup|mình|minh|nhé|nhe|nha))?\s*[.!?~]*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SearchSender = new Regex(
            "^" + Prefix + @"(?:tìm|tim|search)\s+(?:gmail|mail)\s+(?:từ|tu|from)\s+(?<sender>[^\r\n]{1,80}?)\s*[.!?~]*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Negation = new Regex(
            @"(?:^|\s)(?:đừng|dung|chớ|không\s+muốn|khong\s+muon|do\s+not|don't|dont)(?:\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SafeSender = new Regex(
            @"^[\p{L}\p{M}\p{N}@._+\- ]{1,80}$", RegexOptions.CultureInvariant);

        public static bool TryParse(string input, out MaoGmailCommand command)
        {
            command = null;
            string value = (input ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 256 || Negation.IsMatch(value)) return false;

            Match match = Configure.Match(value);
            if (match.Success)
            {
                string clientId = match.Groups["value"].Value;
                if (!MaoGmailSecurity.IsValidDesktopClientId(clientId)) return false;
                command = new MaoGmailCommand(MaoGmailCommandKind.ConfigureClient, clientId);
                return true;
            }
            if (Connect.IsMatch(value)) return Build(MaoGmailCommandKind.Connect, out command);
            if (Disconnect.IsMatch(value)) return Build(MaoGmailCommandKind.Disconnect, out command);
            if (Status.IsMatch(value)) return Build(MaoGmailCommandKind.Status, out command);
            if (Unread.IsMatch(value)) return Build(MaoGmailCommandKind.ListUnread, out command);

            match = SearchSender.Match(value);
            if (!match.Success) return false;
            string sender = Regex.Replace(match.Groups["sender"].Value.Trim(), @"\s+", " ");
            if (!SafeSender.IsMatch(sender) || sender.IndexOf(" OR ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sender.IndexOf(" AND ", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            command = new MaoGmailCommand(MaoGmailCommandKind.SearchSender, sender);
            return true;
        }

        public static string BuildSenderQuery(string sender)
        {
            if (string.IsNullOrWhiteSpace(sender) || !SafeSender.IsMatch(sender))
                throw new ArgumentException("Invalid Gmail sender.", nameof(sender));
            return "from:\"" + sender.Trim() + "\"";
        }

        private static bool Build(MaoGmailCommandKind kind, out MaoGmailCommand command)
        {
            command = new MaoGmailCommand(kind);
            return true;
        }
    }

    public static class MaoGmailSecurity
    {
        private static readonly Regex DesktopClientId = new Regex(
            @"^[A-Za-z0-9_-]{10,200}\.apps\.googleusercontent\.com$",
            RegexOptions.CultureInvariant);

        public static bool IsValidDesktopClientId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && DesktopClientId.IsMatch(value.Trim());
        }

        public static bool IsAllowedMessagesApiUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps &&
                   uri.Host.Equals("gmail.googleapis.com", StringComparison.OrdinalIgnoreCase) &&
                   uri.Port == 443 &&
                   (uri.AbsolutePath.Equals("/gmail/v1/users/me/messages", StringComparison.Ordinal) ||
                    uri.AbsolutePath.StartsWith("/gmail/v1/users/me/messages/", StringComparison.Ordinal));
        }

        public static bool FixedTimeEquals(string left, string right)
        {
            byte[] leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
            byte[] rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
            int difference = leftBytes.Length ^ rightBytes.Length;
            int length = Math.Max(leftBytes.Length, rightBytes.Length);
            for (int index = 0; index < length; index++)
            {
                byte leftByte = index < leftBytes.Length ? leftBytes[index] : (byte)0;
                byte rightByte = index < rightBytes.Length ? rightBytes[index] : (byte)0;
                difference |= leftByte ^ rightByte;
            }
            return difference == 0;
        }
    }

    public static class MaoGmailOAuth
    {
        public const string ReadonlyScope = "https://www.googleapis.com/auth/gmail.readonly";
        public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

        // Google Desktop OAuth uses the system browser, loopback redirect, state and PKCE.
        // Source: https://developers.google.com/identity/protocols/oauth2/native-app
        public static string BuildAuthorizationUrl(string clientId, int loopbackPort, string state, string challenge)
        {
            if (!MaoGmailSecurity.IsValidDesktopClientId(clientId))
                throw new ArgumentException("Invalid Google Desktop OAuth client ID.", nameof(clientId));
            if (loopbackPort < 1024 || loopbackPort > 65535) throw new ArgumentOutOfRangeException(nameof(loopbackPort));
            if (!IsUrlToken(state, 8, 256)) throw new ArgumentException("Invalid OAuth state.", nameof(state));
            if (!IsUrlToken(challenge, 43, 128)) throw new ArgumentException("Invalid PKCE challenge.", nameof(challenge));

            string redirectUri = "http://127.0.0.1:" + loopbackPort + "/";
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = ReadonlyScope,
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["access_type"] = "offline",
                ["prompt"] = "consent"
            };
            return AuthorizationEndpoint + "?" + string.Join("&", parameters.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        }

        public static string CreateCodeVerifier()
        {
            byte[] random = new byte[32];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
            return Base64Url(random);
        }

        public static string CreateState()
        {
            byte[] random = new byte[32];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
            return Base64Url(random);
        }

        public static string CreateCodeChallenge(string verifier)
        {
            if (!IsUrlToken(verifier, 43, 128)) throw new ArgumentException("Invalid PKCE verifier.", nameof(verifier));
            byte[] digest;
            using (SHA256 sha256 = SHA256.Create()) digest = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            return Base64Url(digest);
        }

        public static bool TryParseCallback(string callbackUrl, string expectedState, out string code, out string error)
        {
            code = string.Empty;
            error = string.Empty;
            if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out Uri uri) ||
                uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1" || uri.Port <= 0 || uri.AbsolutePath != "/")
                return false;

            Dictionary<string, string> query = ParseQuery(uri.Query);
            if (!query.TryGetValue("state", out string returnedState) ||
                !MaoGmailSecurity.FixedTimeEquals(returnedState, expectedState))
                return false;
            if (query.TryGetValue("error", out string oauthError))
            {
                error = SanitizeToken(oauthError, 128);
                return false;
            }
            if (!query.TryGetValue("code", out string returnedCode) || returnedCode.Length == 0 ||
                returnedCode.Length > 4096 || returnedCode.Any(char.IsControl))
                return false;
            code = returnedCode;
            return true;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in (query ?? string.Empty).TrimStart('?').Split('&'))
            {
                if (string.IsNullOrWhiteSpace(pair)) continue;
                int separator = pair.IndexOf('=');
                string key = Uri.UnescapeDataString((separator < 0 ? pair : pair.Substring(0, separator)).Replace("+", " "));
                string value = separator < 0 ? string.Empty :
                    Uri.UnescapeDataString(pair.Substring(separator + 1).Replace("+", " "));
                if (!result.ContainsKey(key)) result[key] = value;
            }
            return result;
        }

        private static bool IsUrlToken(string value, int minimum, int maximum)
        {
            return value != null && value.Length >= minimum && value.Length <= maximum &&
                   Regex.IsMatch(value, @"^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string SanitizeToken(string value, int maximum)
        {
            string sanitized = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9_.-]", string.Empty);
            return sanitized.Length <= maximum ? sanitized : sanitized.Substring(0, maximum);
        }
    }

    public sealed class MaoGmailMessageSummary
    {
        public string Id { get; }
        public string ThreadId { get; }
        public string Sender { get; }
        public string Subject { get; }
        public string Snippet { get; }
        public bool IsUnread { get; }

        public MaoGmailMessageSummary(string id, string threadId, string sender, string subject, string snippet, bool isUnread)
        {
            Id = id ?? string.Empty;
            ThreadId = threadId ?? string.Empty;
            Sender = sender ?? string.Empty;
            Subject = subject ?? string.Empty;
            Snippet = snippet ?? string.Empty;
            IsUnread = isUnread;
        }
    }

    public sealed class MaoGmailTokenSet
    {
        public string AccessToken { get; }
        public string RefreshToken { get; }
        public int ExpiresInSeconds { get; }

        public MaoGmailTokenSet(string accessToken, string refreshToken, int expiresInSeconds)
        {
            AccessToken = accessToken ?? string.Empty;
            RefreshToken = refreshToken ?? string.Empty;
            ExpiresInSeconds = expiresInSeconds;
        }
    }

    public static class MaoGmailTokenResponseParser
    {
        private const int MaximumTokenResponseCharacters = 32 * 1024;
        private const int MaximumTokenCharacters = 8192;

        public static MaoGmailTokenSet Parse(string json, bool requireRefreshToken)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumTokenResponseCharacters)
                throw new FormatException("Invalid OAuth token response size.");
            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception exception) { throw new FormatException("Invalid OAuth token response.", exception); }

            string accessToken = root["access_token"]?.Value<string>() ?? string.Empty;
            string refreshToken = root["refresh_token"]?.Value<string>() ?? string.Empty;
            string tokenType = root["token_type"]?.Value<string>() ?? "Bearer";
            string scope = root["scope"]?.Value<string>() ?? MaoGmailOAuth.ReadonlyScope;
            int expiresIn = root["expires_in"]?.Value<int?>() ?? 0;
            if (!IsSafeToken(accessToken) || (requireRefreshToken && !IsSafeToken(refreshToken)) ||
                (refreshToken.Length > 0 && !IsSafeToken(refreshToken)) ||
                !tokenType.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
                expiresIn < 60 || expiresIn > 86400 ||
                !scope.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Contains(MaoGmailOAuth.ReadonlyScope, StringComparer.Ordinal))
                throw new FormatException("OAuth token response failed validation.");
            return new MaoGmailTokenSet(accessToken, refreshToken, expiresIn);
        }

        private static bool IsSafeToken(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && token.Length <= MaximumTokenCharacters &&
                   token.All(character => character >= 0x21 && character <= 0x7e);
        }
    }

    public static class MaoGmailResponseParser
    {
        public const int MaximumResponseCharacters = 256 * 1024;
        private static readonly Regex ResourceId = new Regex(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);
        // Strip markup tags without treating an ordinary RFC-style sender
        // value such as "Alice <alice@example.com>" as HTML.
        private static readonly Regex DisplayHtmlTag = new Regex(
            @"</?(?:a|abbr|b|br|code|div|em|i|li|p|pre|script|span|strong|style|u)(?:\s+[^>]{0,256})?\s*/?>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex HtmlEntityPattern = new Regex(
            @"&(?:#x[0-9a-fA-F]+|#[0-9]+|[A-Za-z][A-Za-z0-9]+);",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static IReadOnlyList<string> ParseMessageIds(string json, int maximum)
        {
            if (maximum < 1 || maximum > 5) throw new ArgumentOutOfRangeException(nameof(maximum));
            JObject root = ParseObject(json);
            var result = new List<string>();
            JArray messages = root["messages"] as JArray;
            if (messages == null) return result;
            foreach (JToken item in messages)
            {
                string id = item?["id"]?.Value<string>() ?? string.Empty;
                if (!ResourceId.IsMatch(id)) throw new FormatException("Invalid Gmail message ID.");
                result.Add(id);
                if (result.Count == maximum) break;
            }
            return result;
        }

        public static MaoGmailMessageSummary ParseMessage(string json)
        {
            JObject root = ParseObject(json);
            string id = root["id"]?.Value<string>() ?? string.Empty;
            string threadId = root["threadId"]?.Value<string>() ?? string.Empty;
            if (!ResourceId.IsMatch(id) || !ResourceId.IsMatch(threadId))
                throw new FormatException("Invalid Gmail message resource.");

            string sender = "Không rõ người gửi";
            string subject = "(Không có tiêu đề)";
            JArray headers = root["payload"]?["headers"] as JArray;
            if (headers != null)
            {
                foreach (JToken header in headers.Take(100))
                {
                    string name = header?["name"]?.Value<string>() ?? string.Empty;
                    string value = header?["value"]?.Value<string>() ?? string.Empty;
                    if (name.Equals("From", StringComparison.OrdinalIgnoreCase)) sender = SanitizeDisplay(value, 180);
                    if (name.Equals("Subject", StringComparison.OrdinalIgnoreCase)) subject = SanitizeDisplay(value, 220);
                }
            }
            string snippet = SanitizeDisplay(root["snippet"]?.Value<string>() ?? string.Empty, 280);
            bool unread = (root["labelIds"] as JArray)?.Values<string>()
                .Any(label => string.Equals(label, "UNREAD", StringComparison.Ordinal)) == true;
            return new MaoGmailMessageSummary(id, threadId, sender, subject, snippet, unread);
        }

        private static JObject ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseCharacters)
                throw new FormatException("Invalid Gmail response size.");
            try
            {
                JObject root = JObject.Parse(json);
                if (root.Count > 100) throw new FormatException("Invalid Gmail response shape.");
                return root;
            }
            catch (FormatException) { throw; }
            catch (Exception exception) { throw new FormatException("Invalid Gmail JSON response.", exception); }
        }

        private static string SanitizeDisplay(string value, int maximum)
        {
            string decoded = DecodeHtmlEntities(value ?? string.Empty);
            decoded = DisplayHtmlTag.Replace(decoded, string.Empty);
            decoded = new string(decoded.Where(character => !char.IsControl(character) || character == ' ').ToArray());
            decoded = Regex.Replace(decoded, @"\s+", " ").Trim();
            if (decoded.Length > maximum) decoded = decoded.Substring(0, maximum).TrimEnd() + "…";
            return decoded;
        }

        private static string DecodeHtmlEntities(string value)
        {
            MatchCollection matches = HtmlEntityPattern.Matches(value);
            if (matches.Count == 0) return value;
            var builder = new StringBuilder(value.Length);
            int cursor = 0;
            foreach (Match match in matches)
            {
                if (match.Index > cursor) builder.Append(value, cursor, match.Index - cursor);
                builder.Append(DecodeHtmlEntity(match.Value));
                cursor = match.Index + match.Length;
            }
            if (cursor < value.Length) builder.Append(value, cursor, value.Length - cursor);
            return builder.ToString();
        }

        private static string DecodeHtmlEntity(string entity)
        {
            string token = entity.Substring(1, entity.Length - 2);
            if (token.Equals("amp", StringComparison.OrdinalIgnoreCase)) return "&";
            if (token.Equals("lt", StringComparison.OrdinalIgnoreCase)) return "<";
            if (token.Equals("gt", StringComparison.OrdinalIgnoreCase)) return ">";
            if (token.Equals("quot", StringComparison.OrdinalIgnoreCase)) return "\"";
            if (token.Equals("apos", StringComparison.OrdinalIgnoreCase)) return "'";
            int radix = 10;
            string digits = token;
            if (digits.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            {
                radix = 16;
                digits = digits.Substring(2);
            }
            else if (digits.StartsWith("#", StringComparison.Ordinal)) digits = digits.Substring(1);
            if (token.StartsWith("#", StringComparison.Ordinal))
            {
                int codePoint;
                NumberStyles style = radix == 16 ? NumberStyles.HexNumber : NumberStyles.Integer;
                if (int.TryParse(digits, style, CultureInfo.InvariantCulture, out codePoint) &&
                    codePoint >= 0 && codePoint <= 0x10FFFF &&
                    !(codePoint >= 0xD800 && codePoint <= 0xDFFF))
                {
                    try { return char.ConvertFromUtf32(codePoint); }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
            return entity;
        }
    }

    public static class MaoGmailFormatter
    {
        public static string Format(IEnumerable<MaoGmailMessageSummary> messages, string heading)
        {
            MaoGmailMessageSummary[] safeMessages = (messages ?? Enumerable.Empty<MaoGmailMessageSummary>())
                .Where(message => message != null).Take(5).ToArray();
            if (safeMessages.Length == 0) return "Miku không thấy thư nào phù hợp nè.";

            var builder = new StringBuilder();
            builder.Append(string.IsNullOrWhiteSpace(heading) ? "Gmail" : heading.Trim())
                .Append(" (").Append(safeMessages.Length).Append("):");
            for (int index = 0; index < safeMessages.Length; index++)
            {
                MaoGmailMessageSummary message = safeMessages[index];
                builder.Append('\n').Append(index + 1).Append(". ")
                    .Append(message.Sender).Append(" — ").Append(message.Subject);
                if (!string.IsNullOrWhiteSpace(message.Snippet)) builder.Append(": ").Append(message.Snippet);
                if (builder.Length > 1150) break;
            }
            string result = builder.ToString();
            return result.Length <= 1200 ? result : result.Substring(0, 1199).TrimEnd() + "…";
        }
    }
}
