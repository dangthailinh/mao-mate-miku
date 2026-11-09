using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MaoIntegration
{
    /// <summary>
    /// Conversation depth is selected from the user's request, rather than from a
    /// fixed character limit.  This keeps a greeting light while still allowing a
    /// complete answer when the user asks for an explanation.
    /// </summary>
    public enum MaoConversationDepth
    {
        Brief,
        Standard,
        Detailed
    }

    public sealed class MaoResponseBudget
    {
        public MaoConversationDepth Depth { get; }
        public int MaximumCharacters { get; }
        public int MaximumSentences { get; }
        public bool AskFollowUpWhenUseful { get; }

        public MaoResponseBudget(
            MaoConversationDepth depth,
            int maximumCharacters,
            int maximumSentences,
            bool askFollowUpWhenUseful)
        {
            Depth = depth;
            MaximumCharacters = Math.Max(1, maximumCharacters);
            MaximumSentences = Math.Max(1, maximumSentences);
            AskFollowUpWhenUseful = askFollowUpWhenUseful;
        }
    }

    /// <summary>
    /// Small, deterministic decisions shared by the model prompt and the local
    /// fallback path.  The model is still responsible for the answer; this class
    /// only supplies a humane communication contract and a final output budget.
    /// </summary>
    public static class MaoHumanStylePolicy
    {
        private static readonly string[] DetailedMarkers =
        {
            "giai thich", "huong dan", "phan tich", "so sanh", "chi tiet", "cac buoc",
            "vi sao", "tai sao", "lam the nao", "sua loi", "loi gi", "viet code", "code",
            "api", "bug", "debug", "ky thuat", "lap trinh", "cau hinh", "thiet ke",
            "danh sach", "ke chuyen", "viet truyen", "viet tho", "explain", "guide",
            "analyze", "compare", "step by step", "how to", "why", "detail", "noi dai", "noi ro",
            "mo ta chi tiet", "tom tat ky"
        };

        private static readonly string[] FollowUpMarkers =
        {
            "giup minh", "lam sao", "the nao", "vi sao", "tai sao", "goi y", "nen lam",
            "muon biet", "can giup", "loi gi", "bi bug", "khong hieu", "ke nghe", "tu van", "hoi y"
        };

        private static readonly string[] MechanicalOpenings =
        {
            "chao mung linh quay lai", "miku vui vi ban quay lai", "ban da im lang",
            "miku da doi", "miku nho ban", "miku cho ban", "welcome back", "you were gone",
            "been away", "kept you waiting"
        };

        public static MaoResponseBudget ChooseBudget(string userText)
        {
            string normalized = Normalize(userText);
            if (normalized.Length == 0 || IsGreeting(normalized))
                return new MaoResponseBudget(MaoConversationDepth.Brief, 220, 2, false);

            bool detailed = DetailedMarkers.Any(marker => ContainsPhrase(normalized, marker));
            bool asksForHelp = FollowUpMarkers.Any(marker => ContainsPhrase(normalized, marker));
            if (!detailed && !asksForHelp && normalized.Length <= 90 && WordCount(normalized) <= 12)
                return new MaoResponseBudget(MaoConversationDepth.Brief, 220, 2, false);
            if (detailed || normalized.Length > 160 || WordCount(normalized) > 24)
                return new MaoResponseBudget(MaoConversationDepth.Detailed, 1800, 8, true);

            return new MaoResponseBudget(MaoConversationDepth.Standard, 520, 4, asksForHelp);
        }

        public static string BuildPromptInstruction(string userText, string recentInteractionContext = null)
        {
            MaoResponseBudget budget = ChooseBudget(userText);
            string context = string.IsNullOrWhiteSpace(recentInteractionContext)
                ? string.Empty
                : " Recent interaction context is background only: " + Normalize(recentInteractionContext) + ".";

            if (budget.Depth == MaoConversationDepth.Brief)
                return "Nói tự nhiên như nhắn tin với một người bạn Việt Nam. Trả lời thẳng trong 1–2 câu ngắn, " +
                       "không mở đầu bằng lời chào máy móc hay nhắc việc người dùng im lặng. Không hỏi thêm nếu câu trả lời đã đủ." + context;
            if (budget.Depth == MaoConversationDepth.Standard)
                return "Trả lời thân thiện, tự nhiên và vừa đủ. Nêu ý chính trước, thêm tối đa một chi tiết hữu ích; " +
                       "chỉ hỏi một câu nối tiếp nếu nó thật sự giúp cuộc trò chuyện đi tiếp. Đừng kéo dài cho đủ chữ." + context;
            return "Đây là yêu cầu cần giải thích. Trình bày có cấu trúc, chính xác và đầy đủ nhưng tránh wall-of-text: " +
                   "chia thành các ý ngắn, nói rõ giả định và điểm chưa chắc chắn; kết thúc bằng một bước tiếp theo thực tế." + context;
        }

        public static string ShapeVisibleReply(string userText, string reply)
        {
            string text = Regex.Replace(reply ?? string.Empty, @"\s+", " ").Trim();
            if (text.Length == 0) return string.Empty;
            MaoResponseBudget budget = ChooseBudget(userText);

            string[] sentences = SplitSentences(text);
            if (budget.Depth == MaoConversationDepth.Brief)
            {
                int start = 0;
                while (start < sentences.Length - 1 && IsMechanicalOpening(sentences[start])) start++;
                text = string.Join(" ", sentences.Skip(start).Take(budget.MaximumSentences));
            }
            else if (sentences.Length > budget.MaximumSentences)
            {
                text = string.Join(" ", sentences.Take(budget.MaximumSentences));
            }

            if (text.Length <= budget.MaximumCharacters) return text;
            int boundary = text.LastIndexOf(' ', budget.MaximumCharacters - 1);
            if (boundary < budget.MaximumCharacters / 2) boundary = budget.MaximumCharacters;
            return text.Substring(0, Math.Min(boundary, text.Length)).TrimEnd(',', ';', ':', '-', ' ') + "…";
        }

        public static bool IsGreeting(string text)
        {
            string normalized = Normalize(text);
            return normalized == "hi" || normalized == "hello" || normalized == "hey" ||
                   normalized == "chao" || normalized == "alo" || normalized == "miku oi" ||
                   normalized == "e miku";
        }

        private static bool IsMechanicalOpening(string sentence)
        {
            string normalized = Normalize(sentence);
            return MechanicalOpenings.Any(marker => normalized.Contains(marker));
        }

        private static bool ContainsPhrase(string normalizedText, string marker)
        {
            if (marker.IndexOf(' ') >= 0) return normalizedText.Contains(marker);
            return Regex.IsMatch(normalizedText, @"(^|\s)" + Regex.Escape(marker) + @"($|\s)");
        }

        private static string[] SplitSentences(string text)
        {
            var sentences = new List<string>();
            int start = 0;
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if ((character != '.' && character != '!' && character != '?' && character != '。' &&
                     character != '！' && character != '？' && character != '~') ||
                    index + 1 >= text.Length || !char.IsWhiteSpace(text[index + 1])) continue;
                sentences.Add(text.Substring(start, index - start + 1).Trim());
                while (index + 1 < text.Length && char.IsWhiteSpace(text[index + 1])) index++;
                start = index + 1;
            }
            if (start < text.Length) sentences.Add(text.Substring(start).Trim());
            return sentences.Where(item => item.Length > 0).ToArray();
        }

        private static int WordCount(string text)
        {
            // Keep this path independent of Regex.Match/MatchCollection API
            // differences between the desktop compiler and Unity's bundled
            // Mono runtime.  The classifier only needs a bounded word count,
            // so a Unicode letter/digit scanner is both safer and cheaper.
            int count = 0;
            bool inWord = false;
            foreach (char character in text ?? string.Empty)
            {
                bool isWordCharacter = char.IsLetterOrDigit(character);
                if (isWordCharacter && !inWord) count++;
                inWord = isWordCharacter;
            }
            return count;
        }

        internal static string Normalize(string text)
        {
            string decomposed = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim()
                .ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (char character in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) !=
                    System.Globalization.UnicodeCategory.NonSpacingMark)
                    builder.Append(character == 'đ' ? 'd' : character);
            }
            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }

    public enum MaoContextualActivity
    {
        General,
        Coding,
        Video,
        Gaming,
        Browsing,
        Away
    }

    /// <summary>
    /// Short local copy for outages, rate limits, or a deliberate quiet mode.
    /// These lines follow the same tone contract as the model, so falling back
    /// never makes Miku suddenly sound like a different product.
    /// </summary>
    public static class MaoVietnameseFallback
    {
        private static readonly string[][] Lines =
        {
            new[] { "Mọi thứ ổn không? Miku ngồi đây với bạn nè.", "Ê, có gì vui kể Miku nghe với." },
            new[] { "Đang code hả? Kẹt chỗ nào thì quăng qua Miku xem cùng nha.", "Bug này làm bạn đau đầu à? Miku phụ soi thử nhé." },
            new[] { "Đang xem gì đó? Khúc hay nhớ kể Miku nghe với.", "Bộ này cuốn không? Cho Miku ké một đoạn nha." },
            new[] { "Trận này căng nha. Cần cổ vũ thì gọi Miku một tiếng.", "Thắng chưa? Miku đang hóng kết quả nè." },
            new[] { "Lướt gì vui vậy? Có món hay thì share Miku coi.", "Tìm được gì thú vị chưa? Miku tò mò ké với." },
            new[] { "Ê, Miku vẫn ở đây nè. Rảnh thì nói tiếp nha.", "Có gì mới kể Miku nghe với." }
        };

        public static string GetLine(MaoContextualActivity activity, int variant = 0)
        {
            int index = (int)activity;
            if (index < 0 || index >= Lines.Length) index = 0;
            string[] candidates = Lines[index];
            // Avoid Math.Abs(int.MinValue) overflow if a caller supplies a hash-derived variant.
            int safeVariant = variant & int.MaxValue;
            return candidates[safeVariant % candidates.Length];
        }

        public static bool IsNaturalAndBounded(string text)
        {
            string normalized = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
            return normalized.Length > 0 && normalized.Length <= 140 &&
                   normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length <= 24;
        }

        /// <summary>
        /// Deterministic local reply used when every configured provider is
        /// unavailable.  It keeps the chat responsive without pretending that a
        /// network-backed answer was generated and never echoes the user's
        /// untrusted text.
        /// </summary>
        public static string GetUnavailableReply(string userText)
        {
            string normalized = MaoHumanStylePolicy.Normalize(userText);
            if (MaoHumanStylePolicy.IsGreeting(normalized))
                return "Yo Linh, Miku vẫn ở đây nè =))";
            if (normalized.Contains("tin moi") || normalized.Contains("hom nay") ||
                normalized.Contains("thoi tiet") || normalized.Contains("weather"))
                return "Mạng đang nghẽn nên Miku chưa lấy được thông tin mới. Thử lại xíu nha.";
            if (normalized.Contains("giai thich") || normalized.Contains("huong dan") ||
                normalized.Contains("vi sao") || normalized.Contains("tai sao"))
                return "AI đang nghẽn nên Miku chưa giải thích trọn vẹn được. Gửi lại sau xíu nha.";
            return "Mạng AI đang nghẽn một chút; Miku vẫn nghe nè, gửi lại câu này sau xíu nha.";
        }
    }

    /// <summary>
    /// Memory is a source of continuity, not a relationship-control mechanism.
    /// Coercive/possessive phrases are rejected before they can become durable
    /// facts.  Secrets are still handled by MaoAdvancedMemoryParser as before.
    /// </summary>
    public static class MaoMemorySafetyPolicy
    {
        private static readonly string[] PressureMarkers =
        {
            "trai tim cua ban thuoc ve", "trai tim cua ban phai", "ban chi can miku",
            "miku la nguoi duy nhat", "dung bo miku",
            "khong duoc bo miku", "never leave miku", "you belong to miku", "only need miku",
            "absolute favorite", "heart belongs", "belongs entirely", "kept you waiting",
            "promised miku will remain", "miku se mai la"
        };

        public static bool IsHealthyContent(string content)
        {
            string normalized = MaoHumanStylePolicy.Normalize(content);
            return normalized.Length > 0 && !PressureMarkers.Any(marker => normalized.Contains(marker));
        }

        public static string SanitizeRelationship(string relationship)
        {
            string normalized = MaoAdvancedMemoryParser.Normalize(relationship, 800);
            if (normalized.Length == 0) return string.Empty;
            return IsHealthyContent(normalized) ? normalized : string.Empty;
        }
    }
}
