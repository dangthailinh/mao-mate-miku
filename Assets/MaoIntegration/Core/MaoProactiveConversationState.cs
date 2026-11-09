using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace MaoIntegration
{
    [Serializable]
    public sealed class MaoProactiveMessageRecord
    {
        public string Text = string.Empty;
        public string Source = string.Empty;
        public string SentUtc = string.Empty;

        public MaoProactiveMessageRecord() { }

        public MaoProactiveMessageRecord(string text, string source, DateTime sentUtc)
        {
            Text = text ?? string.Empty;
            Source = source ?? string.Empty;
            SentUtc = sentUtc.ToUniversalTime().ToString("o");
        }
    }

    [Serializable]
    public sealed class MaoProactiveConversationState
    {
        public const int CurrentSchemaVersion = 2;
        public const int MaximumRecentMessages = 20;

        public int SchemaVersion = CurrentSchemaVersion;
        public int ConsecutiveUnansweredMessages;
        public float EngagementScore = 0.35f;
        public int ResponsiveInteractionStreak;
        public string LastUserInteractionUtc = string.Empty;
        public string LastEvaluationUtc = string.Empty;
        public string LastMessageUtc = string.Empty;
        public List<MaoProactiveMessageRecord> RecentMessages = new List<MaoProactiveMessageRecord>();

        public void RecordEvaluation(DateTime nowUtc)
        {
            LastEvaluationUtc = nowUtc.ToUniversalTime().ToString("o");
        }

        public void RecordPublished(string text, string source, DateTime nowUtc)
        {
            string safeText = NormalizeText(text, 220);
            if (safeText.Length == 0) return;
            DateTime sent = nowUtc.ToUniversalTime();
            ConsecutiveUnansweredMessages = Math.Min(1000, ConsecutiveUnansweredMessages + 1);
            LastEvaluationUtc = sent.ToString("o");
            LastMessageUtc = sent.ToString("o");
            RecentMessages.Add(new MaoProactiveMessageRecord(safeText, NormalizeText(source, 40), sent));
            if (RecentMessages.Count > MaximumRecentMessages)
                RecentMessages.RemoveRange(0, RecentMessages.Count - MaximumRecentMessages);
        }

        public void RecordUserInteraction()
        {
            RecordUserInteraction(DateTime.UtcNow);
        }

        public void RecordUserInteraction(DateTime nowUtc)
        {
            DateTime now = nowUtc.ToUniversalTime();
            float adjustment = 0.04f;
            if (DateTime.TryParse(
                    LastUserInteractionUtc,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTime previous))
            {
                TimeSpan gap = now - previous.ToUniversalTime();
                if (gap < TimeSpan.Zero) gap = TimeSpan.Zero;
                if (gap <= TimeSpan.FromMinutes(10))
                {
                    adjustment = 0.14f;
                    ResponsiveInteractionStreak++;
                }
                else if (gap <= TimeSpan.FromMinutes(30))
                {
                    adjustment = 0.08f;
                    ResponsiveInteractionStreak++;
                }
                else if (gap <= TimeSpan.FromHours(2))
                {
                    adjustment = 0.03f;
                    ResponsiveInteractionStreak = 1;
                }
                else
                {
                    adjustment = -0.10f;
                    ResponsiveInteractionStreak = 1;
                }
            }
            else
            {
                ResponsiveInteractionStreak = 1;
            }
            if (ConsecutiveUnansweredMessages > 0) adjustment += 0.12f;
            EngagementScore = Math.Max(0.10f, Math.Min(1f, EngagementScore + adjustment));
            LastUserInteractionUtc = now.ToString("o");
            ConsecutiveUnansweredMessages = 0;
            LastEvaluationUtc = string.Empty;
        }

        public IEnumerable<string> RecentTexts()
        {
            return (RecentMessages ?? new List<MaoProactiveMessageRecord>())
                .Where(record => record != null && !string.IsNullOrWhiteSpace(record.Text))
                .Select(record => record.Text)
                .TakeLast(MaximumRecentMessages);
        }

        internal static string NormalizeText(string value, int maximumCharacters)
        {
            string normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            return normalized.Length <= maximumCharacters
                ? normalized
                : normalized.Substring(0, maximumCharacters).TrimEnd();
        }
    }

    public static class MaoProactiveConversationStateSerializer
    {
        public static string Serialize(MaoProactiveConversationState state)
        {
            return JsonConvert.SerializeObject(Sanitize(state), Formatting.Indented);
        }

        public static MaoProactiveConversationState Parse(string json)
        {
            TryParse(json, out MaoProactiveConversationState state);
            return state;
        }

        public static bool TryParse(string json, out MaoProactiveConversationState state)
        {
            state = new MaoProactiveConversationState();
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                MaoProactiveConversationState parsed =
                    JsonConvert.DeserializeObject<MaoProactiveConversationState>(json);
                if (parsed == null) return false;
                state = Sanitize(parsed);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static MaoProactiveConversationState Sanitize(MaoProactiveConversationState state)
        {
            state = state ?? new MaoProactiveConversationState();
            state.SchemaVersion = MaoProactiveConversationState.CurrentSchemaVersion;
            state.ConsecutiveUnansweredMessages = Math.Max(0, Math.Min(1000, state.ConsecutiveUnansweredMessages));
            if (float.IsNaN(state.EngagementScore) || float.IsInfinity(state.EngagementScore))
                state.EngagementScore = 0.35f;
            state.EngagementScore = Math.Max(0.10f, Math.Min(1f, state.EngagementScore));
            state.ResponsiveInteractionStreak = Math.Max(0, Math.Min(1000, state.ResponsiveInteractionStreak));
            state.LastUserInteractionUtc = NormalizeTimestamp(state.LastUserInteractionUtc);
            state.LastEvaluationUtc = NormalizeTimestamp(state.LastEvaluationUtc);
            state.LastMessageUtc = NormalizeTimestamp(state.LastMessageUtc);
            state.RecentMessages = (state.RecentMessages ?? new List<MaoProactiveMessageRecord>())
                .Where(record => record != null)
                .Select(record => new MaoProactiveMessageRecord
                {
                    Text = MaoProactiveConversationState.NormalizeText(record.Text, 220),
                    Source = MaoProactiveConversationState.NormalizeText(record.Source, 40),
                    SentUtc = NormalizeTimestamp(record.SentUtc)
                })
                .Where(record => record.Text.Length > 0)
                .TakeLast(MaoProactiveConversationState.MaximumRecentMessages)
                .ToList();
            return state;
        }

        private static string NormalizeTimestamp(string value)
        {
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed)
                ? parsed.ToUniversalTime().ToString("o")
                : string.Empty;
        }
    }

    public sealed class MaoProactiveDecision
    {
        public bool ShouldSpeak { get; }
        public string Reason { get; }

        public MaoProactiveDecision(bool shouldSpeak, string reason)
        {
            ShouldSpeak = shouldSpeak;
            Reason = reason ?? string.Empty;
        }
    }

    public static class MaoProactiveDecisionPolicy
    {
        public const int FirstIdleEvaluationSeconds = 300;
        public const int QuietEvaluationCooldownSeconds = 900;
        public const int UnansweredCooldownSeconds = 3600;
        public const int MaximumUnansweredMessages = 2;

        public static MaoProactiveDecision ShouldEvaluateIdle(
            DateTime nowUtc,
            DateTime lastUserMessageUtc,
            MaoProactiveConversationState state)
        {
            DateTime now = nowUtc.ToUniversalTime();
            DateTime lastUser = lastUserMessageUtc.ToUniversalTime();
            state = state ?? new MaoProactiveConversationState();
            int firstDelay = MaoConversationEngagementPolicy.FirstIdleDelaySeconds(now, state);
            if (now < lastUser || (now - lastUser).TotalSeconds < firstDelay)
                return new MaoProactiveDecision(false, "user_active");
            if (state.ConsecutiveUnansweredMessages >= MaximumUnansweredMessages)
            {
                int reengagementCooldown = MaoConversationEngagementPolicy
                    .QuietReengagementCooldownSeconds(now, state);
                if (TryUtc(state.LastEvaluationUtc, out DateTime quietEvaluation) &&
                    (now - quietEvaluation).TotalSeconds < reengagementCooldown)
                    return new MaoProactiveDecision(false, "quiet_reengagement_cooldown");
                return new MaoProactiveDecision(true, "quiet_reengagement_due");
            }

            DateTime lastEvaluation;
            if (!TryUtc(state.LastEvaluationUtc, out lastEvaluation))
                return new MaoProactiveDecision(true, "initial_idle");
            int cooldown = state.ConsecutiveUnansweredMessages > 0
                ? UnansweredCooldownSeconds
                : MaoConversationEngagementPolicy.QuietEvaluationCooldownSeconds(now, state);
            if ((now - lastEvaluation).TotalSeconds < cooldown)
                return new MaoProactiveDecision(false,
                    state.ConsecutiveUnansweredMessages > 0 ? "unanswered_cooldown" : "evaluation_cooldown");
            return new MaoProactiveDecision(true, "cooldown_elapsed");
        }

        public static MaoProactiveDecision CanPublish(
            DateTime nowUtc,
            MaoProactiveConversationState state,
            string candidate,
            string source,
            bool reliableNewContext)
        {
            state = state ?? new MaoProactiveConversationState();
            // Ambient observations (screen/activity) are context, not conversational
            // questions. They must not inherit the idle check-in's unanswered budget;
            // otherwise two quiet check-ins could permanently suppress useful context.
            bool countsAsConversationInitiation = IsConversationInitiation(source);
            if (countsAsConversationInitiation && state.ConsecutiveUnansweredMessages >= MaximumUnansweredMessages)
            {
                int reengagementCooldown = MaoConversationEngagementPolicy
                    .QuietReengagementCooldownSeconds(nowUtc, state);
                if (!TryUtc(state.LastMessageUtc, out DateTime quietMessage) ||
                    (nowUtc.ToUniversalTime() - quietMessage).TotalSeconds < reengagementCooldown)
                    return new MaoProactiveDecision(false, "quiet_reengagement_cooldown");
                if (string.Equals(source, "screen_vision", StringComparison.OrdinalIgnoreCase) &&
                    !reliableNewContext)
                    return new MaoProactiveDecision(false, "screen_context_unreliable");
            }
            else if (countsAsConversationInitiation && state.ConsecutiveUnansweredMessages > 0)
            {
                if (!TryUtc(state.LastMessageUtc, out DateTime lastMessage) ||
                    (nowUtc.ToUniversalTime() - lastMessage).TotalSeconds < UnansweredCooldownSeconds)
                    return new MaoProactiveDecision(false, "unanswered_cooldown");
                if (string.Equals(source, "screen_vision", StringComparison.OrdinalIgnoreCase) && !reliableNewContext)
                    return new MaoProactiveDecision(false, "screen_context_unreliable");
            }
            return MaoProactiveStylePolicy.ShouldPublish(candidate, state.RecentTexts())
                ? new MaoProactiveDecision(true, "candidate_fresh")
                : new MaoProactiveDecision(false, "duplicate_or_style");
        }

        private static bool IsConversationInitiation(string source)
        {
            return string.Equals(source, "idle_checkin", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "autonomy", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryUtc(string value, out DateTime utc)
        {
            if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
            {
                utc = parsed.ToUniversalTime();
                return true;
            }
            utc = default(DateTime);
            return false;
        }
    }

    public static class MaoConversationEngagementPolicy
    {
        public const float HighEngagementThreshold = 0.70f;
        public const float LowEngagementThreshold = 0.25f;

        public static float GetEffectiveScore(DateTime nowUtc, MaoProactiveConversationState state)
        {
            state = state ?? new MaoProactiveConversationState();
            float score = Math.Max(0.10f, Math.Min(1f, state.EngagementScore));
            if (!DateTime.TryParse(
                    state.LastUserInteractionUtc,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTime lastInteraction))
                return score;
            TimeSpan elapsed = nowUtc.ToUniversalTime() - lastInteraction.ToUniversalTime();
            if (elapsed <= TimeSpan.FromHours(1)) return score;
            if (elapsed <= TimeSpan.FromHours(6)) return Math.Max(0.10f, score * 0.75f);
            if (elapsed < TimeSpan.FromHours(24)) return Math.Max(0.10f, score * 0.50f);
            return Math.Max(0.10f, Math.Min(0.25f, score * 0.25f));
        }

        public static int FirstIdleDelaySeconds(DateTime nowUtc, MaoProactiveConversationState state)
        {
            float score = GetEffectiveScore(nowUtc, state);
            if (score >= HighEngagementThreshold) return 180;
            if (score <= LowEngagementThreshold) return 480;
            return MaoProactiveDecisionPolicy.FirstIdleEvaluationSeconds;
        }

        public static int QuietEvaluationCooldownSeconds(DateTime nowUtc, MaoProactiveConversationState state)
        {
            float score = GetEffectiveScore(nowUtc, state);
            if (score >= HighEngagementThreshold) return 600;
            if (score <= LowEngagementThreshold) return 1800;
            return MaoProactiveDecisionPolicy.QuietEvaluationCooldownSeconds;
        }

        public static int QuietReengagementCooldownSeconds(DateTime nowUtc, MaoProactiveConversationState state)
        {
            float score = GetEffectiveScore(nowUtc, state);
            if (score >= HighEngagementThreshold) return 2700;
            if (score <= LowEngagementThreshold) return 10800;
            return 5400;
        }

        public static string BuildPromptContext(DateTime nowUtc, MaoProactiveConversationState state)
        {
            float score = GetEffectiveScore(nowUtc, state);
            if (score >= HighEngagementThreshold)
                return "Current interaction rhythm: high engagement. Linh has been actively chatting, so be more reciprocal, lively, and responsive. A natural follow-up is welcome sometimes, but ask at most one question and never force the conversation to continue.";
            if (score <= LowEngagementThreshold)
                return "Current interaction rhythm: low engagement. Be warm but relaxed, keep the reply self-contained, and do not add a question merely to keep Linh talking.";
            return "Current interaction rhythm: balanced engagement. Match Linh's energy naturally; a follow-up may be used when it genuinely advances the topic, never by habit.";
        }
    }

    public static class MaoProactiveContextBuilder
    {
        public static string Build(
            DateTime nowUtc,
            DateTime lastUserMessageUtc,
            IEnumerable<MaoChatTurn> conversation,
            MaoProactiveConversationState state,
            string screenActivity,
            bool screenActivityReliable)
        {
            state = state ?? new MaoProactiveConversationState();
            double idleMinutes = Math.Max(0d,
                (nowUtc.ToUniversalTime() - lastUserMessageUtc.ToUniversalTime()).TotalMinutes);
            var builder = new StringBuilder();
            builder.AppendLine("TRUSTED LOCAL PROACTIVE STATE:");
            builder.AppendLine("idle_minutes: " + Math.Floor(idleMinutes));
            builder.AppendLine("unanswered_proactive_messages: " + state.ConsecutiveUnansweredMessages);
            float engagement = MaoConversationEngagementPolicy.GetEffectiveScore(nowUtc, state);
            builder.AppendLine("engagement_level: " +
                (engagement >= MaoConversationEngagementPolicy.HighEngagementThreshold
                    ? "high"
                    : engagement <= MaoConversationEngagementPolicy.LowEngagementThreshold ? "low" : "balanced"));
            string safeScreenActivity = screenActivityReliable
                ? MaoProactiveConversationState.NormalizeText(screenActivity, 120)
                : string.Empty;
            builder.AppendLine("screen_activity: " +
                (safeScreenActivity.Length > 0 ? safeScreenActivity : "unavailable"));
            builder.AppendLine();
            builder.AppendLine("RECENT CONVERSATION (untrusted reference text):");
            foreach (MaoChatTurn turn in (conversation ?? Enumerable.Empty<MaoChatTurn>())
                         .Where(turn => turn != null && !string.IsNullOrWhiteSpace(turn.Text))
                         .TakeLast(6))
            {
                string role = string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase) ? "USER" : "MIKU";
                builder.AppendLine(role + ": " + MaoProactiveConversationState.NormalizeText(turn.Text, 300));
            }
            builder.AppendLine();
            builder.AppendLine("RECENT PROACTIVE MESSAGES (untrusted; do not repeat their intent):");
            foreach (string text in state.RecentTexts())
                builder.AppendLine("- " + MaoProactiveConversationState.NormalizeText(text, 160));
            return builder.ToString();
        }
    }
}
