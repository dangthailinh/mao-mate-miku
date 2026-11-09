using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MaoIntegration
{
    public enum MaoHarnessControl { None, Enable, Disable, Status, Cancel }

    public static class MaoHarnessPolicy
    {
        private static readonly string[] Targets = { "notepad", "calculator", "explorer", "windows", "youtube", "google", "github", "chatgpt", "booth" };
        public static bool IsKnownTarget(string target) => Array.IndexOf(Targets, target) >= 0;

        public static MaoHarnessControl Control(string input)
        {
            string text = Normalize(input).Trim(' ', '/', '.', '!', '?');
            text = Regex.Replace(text, @"^miku(?:\s+oi)?\s*[,!:]?\s*", "");
            if (Regex.IsMatch(text, @"^(?:harness\s+(?:on|bat)|bat\s+(?:deepseek\s+)?harness)(?:\s+(?:di|nhe|nha))*$")) return MaoHarnessControl.Enable;
            if (Regex.IsMatch(text, @"^(?:harness\s+(?:off|tat)|tat\s+(?:deepseek\s+)?harness)(?:\s+(?:di|nhe|nha))*$")) return MaoHarnessControl.Disable;
            if (Regex.IsMatch(text, @"^(?:harness\s+(?:status|dang\s+chay\s+khong)|(?:kiem\s+tra|trang\s+thai)\s+harness)$")) return MaoHarnessControl.Status;
            if (Regex.IsMatch(text, @"^(?:harness\s+(?:cancel|stop)|(?:dung|huy)\s+(?:tac\s+vu|viec\s+dang\s+lam))(?:\s+(?:lai|di|nhe|nha))*$")) return MaoHarnessControl.Cancel;
            return MaoHarnessControl.None;
        }

        public static bool ShouldRoute(string input)
        {
            string text = Normalize(input);
            if (Control(input) != MaoHarnessControl.None) return true;
            if (Regex.IsMatch(text, @"^/?harness\s+(?:chat|lam)\s+\S")) return true;
            if (Regex.IsMatch(text, @"(?:^|\s)(?:huong\s+dan|giai\s+thich|cach|lam\s+sao|tai\s+sao|how\s+to|why)(?:\s|$)")) return false;
            foreach (string id in Targets)
            {
                MaoComputerAction action;
                if ((MaoComputerActionCatalog.TryResolveApplication(id, out action) ||
                     MaoComputerActionCatalog.TryResolveWebsite(id, out action) ||
                     MaoComputerActionCatalog.TryResolveSettings(id, out action)) && MaoComputerActionIntentPolicy.CanExecute(input, action)) return true;
            }
            if (Regex.IsMatch(text, @"(?:^|\s)(?:dung|khong|don't|do not)\s+(?:chay|tao|sua|xoa|mo|cai|doc|ghi)(?:\s|$)")) return false;
            return CanUseNote(input, "save_note") || CanUseNote(input, "list_notes") || CanUseNote(input, "read_note") ||
                Regex.IsMatch(text, @"(?:^|\s)(?:tao|sua|ghi|doc|xem|liet ke|tim|mo|chay|cai|build|test|kiem tra|doi|xoa|copy|sao chep)(?:\s+\S+){0,5}\s+(?:file|tep|thu muc|folder|directory|powershell|script|du an|project|repo|repository|ung dung|app|trang web|website|https?://\S+)(?:\s|$)");
        }

        public static bool CanUseNote(string input, string kind)
        {
            string text = Normalize(input);
            // "noi dung" means content; its last word is not the unaccented "dung" negation.
            string negationText = Regex.Replace(input ?? string.Empty, @"\b(?:nội|noi)\s+dung\b", " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (Regex.IsMatch(negationText,
                @"(?:^|\s)(?:đừng|dung|không|khong|chớ|don't|do\s+not)(?:\s+\S+){0,2}\s+(?:ghi|lưu|luu|tạo|tao|viết|viet|đọc|doc|xem|read|list|save|write)(?:\s|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
            if (kind == "save_note")
            {
                // The noun "ghi chu" inside a read/list request does not authorize writing.
                return Regex.IsMatch(text, @"(?:^|\s)(?:ghi\s+lai|luu\s+(?:lai\s+)?(?:ghi\s+chu|note)|tao\s+(?:mot\s+)?(?:ghi\s+chu|note)|viet\s+(?:mot\s+)?(?:ghi\s+chu|note))(?:\s|$)");
            }
            if (kind == "list_notes")
            {
                return Regex.IsMatch(text, @"(?:^|\s)(?:xem|doc|tim|liet\s+ke|danh\s+sach|list|read)(?:\s+\S+){0,4}\s+(?:ghi\s+chu|note|notes)(?:\s|$)");
            }
            if (kind == "read_note")
            {
                // Listing can resolve identifiers for an explicit read, but listing alone
                // must not disclose note bodies to the model.
                foreach (Match match in Regex.Matches(text,
                    @"(?:^|\s)(?:xem|doc|read)(?<scope>(?:\s+\S+){0,4})\s+(?:ghi\s+chu|note|notes)(?:\s|$)"))
                {
                    if (!Regex.IsMatch(match.Groups["scope"].Value,
                        @"(?:^|\s)(?:danh\s+sach|liet\s+ke|list)(?:\s|$)"))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool RequiresToolExecution(string input)
        {
            return ShouldRoute(input) && Control(input) == MaoHarnessControl.None &&
                !Regex.IsMatch(Normalize(input), @"^/?harness\s+chat\s+");
        }

        public static string Normalize(string value)
        {
            var output = new StringBuilder();
            foreach (char c in (value ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) output.Append(c == 'đ' ? 'd' : c);
            return Regex.Replace(output.ToString(), @"\s+", " ").Trim();
        }
    }

    public static class MaoStartupPolicy
    {
        public static bool CanWrite(string executable, string localAppData)
        {
            if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(localAppData)) return false;
            try
            {
                string official = System.IO.Path.Combine(localAppData, "Programs", "Mao Mate Engine", "Mao Mate Engine.exe");
                return string.Equals(System.IO.Path.GetFullPath(executable.Trim('"')), System.IO.Path.GetFullPath(official), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }
    }
}
