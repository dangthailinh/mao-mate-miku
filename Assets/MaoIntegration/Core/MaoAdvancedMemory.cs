using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MaoIntegration
{
    [Serializable]
    public sealed class MaoMemoryFact
    {
        public string Id;
        public string Content;
        public List<string> Keywords = new List<string>();
        public float Importance;
        public float EmotionalWeight;
        public bool IsPinned;
        public string CreatedUtc;
        public string LastSeenUtc;
        public int RecallCount;

        public MaoMemoryFact() { }

        public MaoMemoryFact(string id, string content, float importance, bool isPinned, DateTime createdUtc)
        {
            Id = id ?? string.Empty;
            Content = content ?? string.Empty;
            Importance = Clamp01(importance);
            IsPinned = isPinned;
            CreatedUtc = createdUtc.ToUniversalTime().ToString("o");
            LastSeenUtc = CreatedUtc;
            Keywords = MaoAdvancedMemoryRetriever.Tokenize(Content).ToList();
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
        }
    }

    [Serializable]
    public sealed class MaoMemorySummary
    {
        public string Content;
        public string CreatedUtc;

        public MaoMemorySummary() { }

        public MaoMemorySummary(string content, DateTime createdUtc)
        {
            Content = content ?? string.Empty;
            CreatedUtc = createdUtc.ToUniversalTime().ToString("o");
        }
    }

    [Serializable]
    public sealed class MaoAdvancedMemory
    {
        public int SchemaVersion = 2;
        public string LegacySummary = string.Empty;
        public List<MaoMemoryFact> Facts = new List<MaoMemoryFact>();
        public List<MaoMemorySummary> Summaries = new List<MaoMemorySummary>();
        public string RelationshipSummary = string.Empty;
        public string UpdatedUtc = DateTime.UtcNow.ToString("o");
    }

    public sealed class MaoMemoryContext
    {
        public IReadOnlyList<MaoMemoryFact> Facts { get; }
        public string PromptText { get; }

        public MaoMemoryContext(IReadOnlyList<MaoMemoryFact> facts, string promptText)
        {
            Facts = facts ?? Array.Empty<MaoMemoryFact>();
            PromptText = promptText ?? string.Empty;
        }
    }

    public static class MaoAdvancedMemoryMigration
    {
        public static MaoAdvancedMemory FromLegacy(string legacySummary, DateTime nowUtc)
        {
            string summary = MaoAdvancedMemoryParser.Normalize(legacySummary, 3000);
            if (!MaoAdvancedMemoryParser.IsSafeContent(summary)) summary = string.Empty;
            var memory = new MaoAdvancedMemory
            {
                LegacySummary = summary,
                UpdatedUtc = nowUtc.ToUniversalTime().ToString("o")
            };
            if (summary.Length > 0) memory.Summaries.Add(new MaoMemorySummary(summary, nowUtc));
            return memory;
        }
    }

    public enum MaoMemoryManagementCommandKind { List, Pin, Unpin }

    public sealed class MaoMemoryManagementCommand
    {
        public MaoMemoryManagementCommandKind Kind { get; }
        public string Value { get; }

        public MaoMemoryManagementCommand(MaoMemoryManagementCommandKind kind, string value)
        {
            Kind = kind;
            Value = value ?? string.Empty;
        }
    }

    public static class MaoMemoryManagementCommandParser
    {
        private static readonly Regex Pattern = new Regex(
            @"^\s*/memory\s+(list|pin|unpin)(?:\s+([\s\S]{1,320}))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool TryParse(string input, out MaoMemoryManagementCommand command)
        {
            command = null;
            Match match = Pattern.Match(input ?? string.Empty);
            if (!match.Success) return false;
            string action = match.Groups[1].Value.ToLowerInvariant();
            string value = MaoAdvancedMemoryParser.Normalize(match.Groups[2].Value, 320);
            if (action != "list" && value.Length == 0) return false;
            command = new MaoMemoryManagementCommand(
                action == "pin" ? MaoMemoryManagementCommandKind.Pin :
                action == "unpin" ? MaoMemoryManagementCommandKind.Unpin : MaoMemoryManagementCommandKind.List,
                value);
            return true;
        }
    }

    public static class MaoAdvancedMemoryPinning
    {
        public static bool TryPin(MaoAdvancedMemory memory, string content, DateTime nowUtc, out MaoMemoryFact pinned)
        {
            pinned = null;
            string safeContent = MaoAdvancedMemoryParser.Normalize(content, 320);
            if (memory == null || safeContent.Length == 0 || !MaoAdvancedMemoryParser.IsSafeContent(safeContent))
                return false;
            pinned = (memory.Facts ?? (memory.Facts = new List<MaoMemoryFact>()))
                .FirstOrDefault(fact => fact != null && string.Equals(
                    MaoAdvancedMemoryParser.Normalize(fact.Content, 320), safeContent, StringComparison.OrdinalIgnoreCase));
            if (pinned == null)
            {
                if (memory.Facts.Count(fact => fact != null && fact.IsPinned) >= MaoAdvancedMemoryParser.MaxFacts)
                    return false;
                pinned = new MaoMemoryFact(MaoAdvancedMemoryParser.CreateStableId(safeContent), safeContent, 1f, true, nowUtc);
                memory.Facts.Add(pinned);
            }
            pinned.IsPinned = true;
            pinned.Importance = 1f;
            memory.UpdatedUtc = nowUtc.ToUniversalTime().ToString("o");
            return true;
        }

        public static bool TryUnpin(MaoAdvancedMemory memory, string id)
        {
            MaoMemoryFact fact = memory?.Facts?.FirstOrDefault(candidate => candidate != null &&
                string.Equals(candidate.Id, (id ?? string.Empty).Trim(), StringComparison.Ordinal));
            if (fact == null || !fact.IsPinned) return false;
            fact.IsPinned = false;
            return true;
        }
    }

    public static class MaoAdvancedMemoryParser
    {
        public const int MaxFacts = 128;
        public const int MaxSummaries = 24;
        private const int MaxFactCharacters = 320;
        private const int MaxSummaryCharacters = 1500;

        public static MaoAdvancedMemory Parse(string rawModelText, MaoAdvancedMemory previous, DateTime nowUtc)
        {
            MaoAdvancedMemory baseline = CloneSafe(previous);
            try
            {
                string json = StripFence(rawModelText);
                JObject root = JObject.Parse(json);
                var removedIds = new HashSet<string>(
                    root["removedFactIds"]?.Values<string>().Select(value => Normalize(value, 80))
                    ?? Enumerable.Empty<string>(),
                    StringComparer.Ordinal);

                baseline.Facts.RemoveAll(fact => fact != null && !fact.IsPinned && removedIds.Contains(fact.Id));
                JArray factArray = root["facts"] as JArray;
                if (factArray != null)
                {
                    foreach (JToken token in factArray)
                    {
                        JObject item = token as JObject;
                        if (item == null) continue;
                        string content = Normalize(item["content"]?.Value<string>(), MaxFactCharacters);
                        if (content.Length == 0 || LooksSensitive(content) ||
                            !MaoMemorySafetyPolicy.IsHealthyContent(content)) continue;
                        string suppliedId = Normalize(item["id"]?.Value<string>(), 80);
                        string id = suppliedId.Length > 0 ? suppliedId : StableId(content);
                        float importance = Clamp01(item["importance"]?.Value<float?>() ?? 0.6f);
                        float emotionalWeight = Clamp01(item["emotionalWeight"]?.Value<float?>() ?? 0f);
                        // Pinning is a privileged local action. Model output may preserve an existing pin,
                        // but only an explicit /memory pin command can create a new one.
                        bool requestedPin = false;
                        MaoMemoryFact existing = FindDuplicate(baseline.Facts, id, content);
                        if (existing == null)
                        {
                            existing = new MaoMemoryFact(id, content, importance, requestedPin, nowUtc);
                            baseline.Facts.Add(existing);
                        }
                        else
                        {
                            existing.Content = content;
                            existing.Importance = Math.Max(existing.Importance, importance);
                            existing.EmotionalWeight = Math.Max(existing.EmotionalWeight, emotionalWeight);
                            existing.IsPinned = existing.IsPinned || requestedPin;
                            existing.Keywords = MaoAdvancedMemoryRetriever.Tokenize(content).ToList();
                        }
                    }
                }

                string summary = Normalize(root["summary"]?.Value<string>(), MaxSummaryCharacters);
                if (summary.Length > 0 && !LooksSensitive(summary) &&
                    MaoMemorySafetyPolicy.IsHealthyContent(summary))
                {
                    if (baseline.Summaries.Count == 0 || !string.Equals(
                            baseline.Summaries[baseline.Summaries.Count - 1].Content,
                            summary,
                            StringComparison.OrdinalIgnoreCase))
                        baseline.Summaries.Add(new MaoMemorySummary(summary, nowUtc));
                    baseline.LegacySummary = summary;
                }

                string relationship = Normalize(root["relationship"]?.Value<string>(), 800);
                if (relationship.Length > 0 && !LooksSensitive(relationship) &&
                    MaoMemorySafetyPolicy.IsHealthyContent(relationship))
                    baseline.RelationshipSummary = relationship;
            }
            catch
            {
                return baseline;
            }

            baseline.SchemaVersion = 2;
            baseline.UpdatedUtc = nowUtc.ToUniversalTime().ToString("o");
            baseline.Facts = baseline.Facts
                .Where(fact => fact != null && !string.IsNullOrWhiteSpace(fact.Content))
                .OrderByDescending(fact => fact.IsPinned)
                .ThenByDescending(fact => fact.Importance)
                .Take(MaxFacts)
                .ToList();
            if (baseline.Summaries.Count > MaxSummaries)
                baseline.Summaries.RemoveRange(0, baseline.Summaries.Count - MaxSummaries);
            return baseline;
        }

        /// <summary>
        /// Normalizes memory loaded from disk without routing it through a
        /// synthetic JSON/model response.  The latter is deliberately avoided
        /// here because the Unity Mono Newtonsoft build can reflect over
        /// anonymous compiler-generated types differently from the editor
        /// runtime (and may reject otherwise valid memory with a misleading
        /// "Type" property error).
        /// </summary>
        public static MaoAdvancedMemory NormalizeLoaded(MaoAdvancedMemory loaded, DateTime nowUtc)
        {
            MaoAdvancedMemory normalized = CloneSafe(loaded);
            normalized.SchemaVersion = 2;
            normalized.UpdatedUtc = nowUtc.ToUniversalTime().ToString("o");
            normalized.Facts = normalized.Facts
                .Where(fact => fact != null && !string.IsNullOrWhiteSpace(fact.Content))
                .OrderByDescending(fact => fact.IsPinned)
                .ThenByDescending(fact => fact.Importance)
                .Take(MaxFacts)
                .ToList();
            if (normalized.Summaries.Count > MaxSummaries)
                normalized.Summaries.RemoveRange(0, normalized.Summaries.Count - MaxSummaries);
            return normalized;
        }

        public static string Normalize(string value, int maximumCharacters)
        {
            string result = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            return result.Length <= maximumCharacters
                ? result
                : result.Substring(0, maximumCharacters).TrimEnd();
        }

        public static bool IsSafeContent(string text)
        {
            return !LooksSensitive(text) && MaoMemorySafetyPolicy.IsHealthyContent(text);
        }

        public static string CreateStableId(string content)
        {
            return StableId(Normalize(content, MaxFactCharacters));
        }

        private static MaoAdvancedMemory CloneSafe(MaoAdvancedMemory source)
        {
            if (source == null) return new MaoAdvancedMemory();
            string legacySummary = Normalize(source.LegacySummary, 3000);
            string relationshipSummary = Normalize(source.RelationshipSummary, 800);
            var clone = new MaoAdvancedMemory
            {
                SchemaVersion = 2,
                LegacySummary = LooksSensitive(legacySummary) || !MaoMemorySafetyPolicy.IsHealthyContent(legacySummary)
                    ? string.Empty : legacySummary,
                RelationshipSummary = LooksSensitive(relationshipSummary) ||
                    !MaoMemorySafetyPolicy.IsHealthyContent(relationshipSummary)
                    ? string.Empty : relationshipSummary,
                UpdatedUtc = source.UpdatedUtc ?? DateTime.UtcNow.ToString("o")
            };
            foreach (MaoMemoryFact fact in source.Facts ?? new List<MaoMemoryFact>())
            {
                if (fact == null) continue;
                string content = Normalize(fact.Content, MaxFactCharacters);
                if (content.Length == 0 || LooksSensitive(content) ||
                    !MaoMemorySafetyPolicy.IsHealthyContent(content)) continue;
                clone.Facts.Add(new MaoMemoryFact
                {
                    Id = Normalize(fact.Id, 80),
                    Content = content,
                    Keywords = MaoAdvancedMemoryRetriever.Tokenize(content).ToList(),
                    Importance = Clamp01(fact.Importance),
                    EmotionalWeight = Clamp01(fact.EmotionalWeight),
                    IsPinned = fact.IsPinned,
                    CreatedUtc = fact.CreatedUtc,
                    LastSeenUtc = fact.LastSeenUtc,
                    RecallCount = Math.Max(0, fact.RecallCount)
                });
            }
            foreach (MaoMemorySummary summary in source.Summaries ?? new List<MaoMemorySummary>())
            {
                if (summary == null) continue;
                string content = Normalize(summary.Content, MaxSummaryCharacters);
                if (content.Length > 0 && !LooksSensitive(content) &&
                    MaoMemorySafetyPolicy.IsHealthyContent(content))
                    clone.Summaries.Add(new MaoMemorySummary(content, ParseDate(summary.CreatedUtc, DateTime.UtcNow)));
            }
            return clone;
        }

        private static MaoMemoryFact FindDuplicate(IEnumerable<MaoMemoryFact> facts, string id, string content)
        {
            string normalized = Normalize(content, MaxFactCharacters);
            return facts.FirstOrDefault(fact => fact != null &&
                (string.Equals(fact.Id, id, StringComparison.Ordinal) ||
                 string.Equals(Normalize(fact.Content, MaxFactCharacters), normalized, StringComparison.OrdinalIgnoreCase)));
        }

        private static string StripFence(string text)
        {
            string value = (text ?? string.Empty).Trim();
            Match fenced = Regex.Match(value, @"^```(?:json)?\s*([\s\S]*?)\s*```$", RegexOptions.IgnoreCase);
            return fenced.Success ? fenced.Groups[1].Value : value;
        }

        private static bool LooksSensitive(string text)
        {
            return Regex.IsMatch(text ?? string.Empty,
                @"(?:\b(api[ _-]?key|password|passcode|access[ _-]?token|secret|credit[ _-]?card|private[ _-]?key)\b|" +
                @"\bbearer\s+[a-z0-9._~-]{8,}|\bsk-[a-z0-9_-]{8,}|\b(?:ghp_|github_pat_|glpat-)[a-z0-9_-]{8,}|\bAKIA[A-Z0-9]{12,})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string StableId(string content)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in content.ToLowerInvariant()) hash = (hash ^ character) * 16777619;
                return "fact-" + hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        private static float Clamp01(float value) { return Math.Max(0f, Math.Min(1f, value)); }

        private static DateTime ParseDate(string value, DateTime fallback)
        {
            DateTime parsed;
            return DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime()
                : fallback.ToUniversalTime();
        }
    }

    public static class MaoAdvancedMemoryRetriever
    {
        private static readonly Regex WordPattern = new Regex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "how", "i",
            "in", "is", "it", "me", "my", "of", "on", "or", "that", "the", "this", "to",
            "was", "we", "what", "when", "where", "which", "who", "why", "with", "you", "your",
            "cua", "cho", "la", "mot", "nhung", "nhu", "o", "tai", "theo", "thi", "tu", "va",
            "vay", "voi", "sao", "gi", "nao", "nay", "do", "duoc", "khong", "minh", "ban"
        };

        public static MaoMemoryContext Retrieve(MaoAdvancedMemory memory, string query, DateTime nowUtc, int maximumFacts = 8)
        {
            if (memory == null) return new MaoMemoryContext(Array.Empty<MaoMemoryFact>(), string.Empty);
            List<MaoMemoryFact> facts = (memory.Facts ?? new List<MaoMemoryFact>())
                .Where(fact => fact != null && !string.IsNullOrWhiteSpace(fact.Content))
                .ToList();
            HashSet<string> queryTokens = Tokenize(query);
            Dictionary<string, int> documentFrequency = BuildDocumentFrequency(facts);
            float averageLength = facts.Count == 0
                ? 1f
                : (float)facts.Average(fact => Math.Max(1, Tokenize(fact.Content).Count));

            List<MaoMemoryFact> selected = facts
                .Select(fact => new
                {
                    Fact = fact,
                    Score = Score(fact, queryTokens, documentFrequency, facts.Count, averageLength, nowUtc)
                })
                .Where(item => item.Fact.IsPinned || item.Score > 0.25f)
                .OrderByDescending(item => item.Score)
                .Take(Math.Max(1, maximumFacts))
                .Select(item => item.Fact)
                .ToList();

            foreach (MaoMemoryFact fact in selected)
            {
                fact.RecallCount = Math.Min(100000, fact.RecallCount + 1);
                fact.LastSeenUtc = nowUtc.ToUniversalTime().ToString("o");
            }

            return new MaoMemoryContext(selected, BuildPrompt(memory, selected));
        }

        public static HashSet<string> Tokenize(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in WordPattern.Matches((text ?? string.Empty).ToLowerInvariant()))
            {
                string word = match.Value.Trim();
                string folded = RemoveDiacritics(word);
                if (word.Length < 2 || StopWords.Contains(word) || StopWords.Contains(folded)) continue;
                tokens.Add(word);
                if (folded.Length >= 2 && !string.Equals(word, folded, StringComparison.Ordinal))
                    tokens.Add(folded);
            }
            return tokens;
        }

        private static string RemoveDiacritics(string value)
        {
            string decomposed = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            foreach (char character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                    result.Append(character == 'đ' ? 'd' : character);
            }
            return result.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        public static float ComputeRetention(MaoMemoryFact fact, DateTime nowUtc)
        {
            if (fact == null) return 0f;
            if (fact.IsPinned) return 1f;
            DateTime lastSeen = ParseDate(fact.LastSeenUtc, ParseDate(fact.CreatedUtc, nowUtc));
            double ageDays = Math.Max(0d, (nowUtc.ToUniversalTime() - lastSeen).TotalDays);
            double stability = 1d + Math.Min(20, Math.Max(0, fact.RecallCount)) * 0.35d +
                               Clamp01(fact.Importance) * 2d + Clamp01(fact.EmotionalWeight) * 2d;
            return (float)Math.Exp(-ageDays / stability);
        }

        private static float Score(
            MaoMemoryFact fact,
            HashSet<string> queryTokens,
            Dictionary<string, int> documentFrequency,
            int documentCount,
            float averageLength,
            DateTime nowUtc)
        {
            HashSet<string> tokens = Tokenize(fact.Content);
            float bm25 = 0f;
            const float k1 = 1.2f;
            const float b = 0.75f;
            foreach (string token in queryTokens)
            {
                if (!tokens.Contains(token)) continue;
                int df = documentFrequency.TryGetValue(token, out int count) ? count : 0;
                float idf = (float)Math.Log(1d + (documentCount - df + 0.5d) / (df + 0.5d));
                float lengthNorm = 1f - b + b * tokens.Count / Math.Max(1f, averageLength);
                bm25 += idf * (k1 + 1f) / (1f + k1 * lengthNorm);
            }
            return bm25 * 2.5f + ComputeRetention(fact, nowUtc) * 0.35f +
                   Clamp01(fact.Importance) * 0.45f + Clamp01(fact.EmotionalWeight) * 0.2f +
                   (fact.IsPinned ? 0.75f : 0f);
        }

        private static Dictionary<string, int> BuildDocumentFrequency(IEnumerable<MaoMemoryFact> facts)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MaoMemoryFact fact in facts)
                foreach (string token in Tokenize(fact.Content))
                    result[token] = result.TryGetValue(token, out int count) ? count + 1 : 1;
            return result;
        }

        private static string BuildPrompt(MaoAdvancedMemory memory, IReadOnlyList<MaoMemoryFact> facts)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(memory.RelationshipSummary))
                builder.AppendLine("Relationship: " + memory.RelationshipSummary);
            MaoMemorySummary latest = memory.Summaries?.LastOrDefault();
            if (latest != null && !string.IsNullOrWhiteSpace(latest.Content))
                builder.AppendLine("Recent summary: " + latest.Content);
            if (facts.Count > 0) builder.AppendLine("Relevant facts:");
            foreach (MaoMemoryFact fact in facts)
                builder.Append("- ").Append(fact.IsPinned ? "[pinned] " : string.Empty).AppendLine(fact.Content);
            string result = builder.ToString().Trim();
            return result.Length <= 6000 ? result : result.Substring(0, 6000).TrimEnd();
        }

        private static DateTime ParseDate(string value, DateTime fallback)
        {
            DateTime parsed;
            return DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime()
                : fallback.ToUniversalTime();
        }

        private static float Clamp01(float value) { return Math.Max(0f, Math.Min(1f, value)); }
    }
}
