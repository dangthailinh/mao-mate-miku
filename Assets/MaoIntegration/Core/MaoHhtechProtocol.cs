using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MaoIntegration
{
    public static class MaoHhtechProtocol
    {
        public const string Endpoint = "https://hhtechapi.net/v1/chat/completions";
        public const string Model = "deepseek-v4";
        public const string ApiKeyEnvironmentVariable = "HHTECH_API_KEY";
        public const int RequestTimeoutSeconds = 45;
        private const int MaximumResponseCharacters = 1024 * 1024;
        private const int MaximumReplyCharacters = 12000;
        private static readonly Regex ReasoningTokens = new Regex(
            @"<\s*(/?)\s*(think|analysis|reasoning)\b([^<>]*)>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        private static readonly Regex ReasoningMarkers = new Regex(
            @"<\s*/?\s*(?:think|analysis|reasoning)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

        public static string ParseApiKey(string envText, string processKey)
        {
            if (!string.IsNullOrWhiteSpace(processKey)) return NormalizeKey(processKey);
            foreach (string rawLine in (envText ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line.Substring(7).TrimStart();
                int equals = line.IndexOf('=');
                if (equals <= 0 || line.Substring(0, equals).Trim() != ApiKeyEnvironmentVariable) continue;
                string key = NormalizeKey(line.Substring(equals + 1));
                if (key.Length > 0) return key;
            }
            return string.Empty;
        }

        public static string BuildRequestJson(string systemPrompt, IReadOnlyList<MaoChatTurn> turns)
        {
            JObject request = JObject.Parse(MaoQwenProtocol.BuildRequestJson(systemPrompt, turns));
            request["model"] = Model;
            return request.ToString(Formatting.None);
        }

        public static MaoGeminiReply ParseResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseCharacters) throw InvalidResponse();
            JObject root;
            try
            {
                // Preserve wire strings: Json.NET otherwise coerces ISO dates into Date tokens.
                // https://www.newtonsoft.com/json/help/html/P_Newtonsoft_Json_JsonReader_DateParseHandling.htm
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader);
                    if (reader.Read()) throw InvalidResponse();
                }
            }
            catch (JsonException) { throw InvalidResponse(); }
            if (root["error"] != null && root["error"].Type != JTokenType.Null) throw InvalidResponse();
            JArray choices = root["choices"] as JArray;
            JObject choice = choices != null && choices.Count > 0 ? choices[0] as JObject : null;
            JObject message = choice?["message"] as JObject;
            JToken content = message?["content"];
            if (content == null || content.Type != JTokenType.String) throw InvalidResponse();
            string text = content.Value<string>();
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumReplyCharacters) throw InvalidResponse();
            try
            {
                text = StripReasoningWrappers(text);
                if (ReasoningMarkers.IsMatch(text) || string.IsNullOrWhiteSpace(text)) throw InvalidResponse();
            }
            catch (RegexMatchTimeoutException) { throw InvalidResponse(); }
            JToken finishReason = choice["finish_reason"];
            if (finishReason != null && finishReason.Type != JTokenType.Null &&
                (finishReason.Type != JTokenType.String || finishReason.Value<string>().Length > 64)) throw InvalidResponse();
            // Do not reparse with another helper and reintroduce date coercion or removed reasoning.
            return new MaoGeminiReply(text, finishReason?.Value<string>() ?? string.Empty);
        }

        private static string StripReasoningWrappers(string text)
        {
            MatchCollection tokens = ReasoningTokens.Matches(text);
            // Validate the original sequence before removing anything. A regex replacement
            // could hide an unclosed inner marker inside a matched outer block.
            if (ReasoningMarkers.Matches(text).Count != tokens.Count) throw InvalidResponse();
            string activeTag = null;
            int visibleStart = 0;
            var visible = new StringBuilder(text.Length);
            foreach (Match token in tokens)
            {
                string tag = token.Groups[2].Value;
                string suffix = token.Groups[3].Value.Trim();
                if (token.Groups[1].Value.Length == 0)
                {
                    if (activeTag != null || suffix.EndsWith("/", StringComparison.Ordinal)) throw InvalidResponse();
                    visible.Append(text, visibleStart, token.Index - visibleStart);
                    activeTag = tag;
                }
                else
                {
                    if (activeTag == null || suffix.Length != 0 ||
                        !string.Equals(activeTag, tag, StringComparison.OrdinalIgnoreCase)) throw InvalidResponse();
                    activeTag = null;
                    visibleStart = token.Index + token.Length;
                    visible.Append(' ');
                }
            }
            if (activeTag != null) throw InvalidResponse();
            visible.Append(text, visibleStart, text.Length - visibleStart);
            return visible.ToString().Trim();
        }

        private static string NormalizeKey(string value)
        {
            string key = (value ?? string.Empty).Trim().Trim('"', '\'');
            if (key.Length == 0 || key.Length > 4096) return string.Empty;
            foreach (char character in key) if (char.IsControl(character)) return string.Empty;
            return key;
        }

        private static FormatException InvalidResponse() => new FormatException("HHTech returned an invalid response.");
    }
}
