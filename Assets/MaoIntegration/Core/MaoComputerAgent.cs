using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MaoIntegration
{
    public enum MaoComputerActionType { OpenApplication, OpenWebsite, OpenWindowsSettings }
    public enum MaoComputerCommandKind { Enable, Disable, Status, RequestAction }

    public sealed class MaoComputerAction
    {
        public MaoComputerActionType Type { get; }
        public string TargetId { get; }
        public string DisplayName { get; }
        public string LaunchTarget { get; }

        public MaoComputerAction(MaoComputerActionType type, string targetId, string displayName, string launchTarget)
        {
            Type = type;
            TargetId = targetId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            LaunchTarget = launchTarget ?? string.Empty;
        }
    }

    public sealed class MaoComputerCommand
    {
        public MaoComputerCommandKind Kind { get; }
        public MaoComputerAction Action { get; }

        public MaoComputerCommand(MaoComputerCommandKind kind, MaoComputerAction action = null)
        {
            Kind = kind;
            Action = action;
        }
    }

    public static class MaoComputerActionCatalog
    {
        private static readonly Dictionary<string, MaoComputerAction> Applications =
            new Dictionary<string, MaoComputerAction>(StringComparer.OrdinalIgnoreCase)
            {
                ["notepad"] = new MaoComputerAction(MaoComputerActionType.OpenApplication, "notepad", "Notepad", "notepad.exe"),
                ["calculator"] = new MaoComputerAction(MaoComputerActionType.OpenApplication, "calculator", "Calculator", "calc.exe"),
                ["explorer"] = new MaoComputerAction(MaoComputerActionType.OpenApplication, "explorer", "File Explorer", "explorer.exe")
            };

        private static readonly Dictionary<string, MaoComputerAction> Websites =
            new Dictionary<string, MaoComputerAction>(StringComparer.OrdinalIgnoreCase)
            {
                ["youtube"] = new MaoComputerAction(MaoComputerActionType.OpenWebsite, "youtube", "YouTube", "https://www.youtube.com/"),
                ["google"] = new MaoComputerAction(MaoComputerActionType.OpenWebsite, "google", "Google", "https://www.google.com/"),
                ["github"] = new MaoComputerAction(MaoComputerActionType.OpenWebsite, "github", "GitHub", "https://github.com/"),
                ["chatgpt"] = new MaoComputerAction(MaoComputerActionType.OpenWebsite, "chatgpt", "ChatGPT", "https://chatgpt.com/"),
                ["booth"] = new MaoComputerAction(MaoComputerActionType.OpenWebsite, "booth", "BOOTH", "https://booth.pm/")
            };

        private static readonly MaoComputerAction WindowsSettings =
            new MaoComputerAction(MaoComputerActionType.OpenWindowsSettings, "windows", "Windows Settings", "ms-settings:");

        public static bool TryResolveApplication(string target, out MaoComputerAction action)
        {
            return Applications.TryGetValue(NormalizeId(target), out action);
        }

        public static bool TryResolveWebsite(string target, out MaoComputerAction action)
        {
            return Websites.TryGetValue(NormalizeId(target), out action);
        }

        public static bool TryResolveSettings(string target, out MaoComputerAction action)
        {
            string id = NormalizeId(target);
            if (id == "windows" || id == "settings" || id == "cai-dat")
            {
                action = WindowsSettings;
                return true;
            }
            action = null;
            return false;
        }

        private static string NormalizeId(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }
    }

    public static class MaoComputerCommandParser
    {
        private static readonly Regex Control = new Regex(@"^\s*/?computer\s+(on|off|status)\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex Open = new Regex(@"^\s*/?open\s+(app|web|settings)\s+([a-z0-9-]{1,32})\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex NaturalOpen = new Regex(
            @"^\s*(?:(?:miku|mao)(?:\s+(?:ơi|oi))?\s*[,!:\-]?\s*)?" +
            @"(?:mở|mo|open|khởi\s+động|khoi\s+dong|bật|bat)\s+" +
            @"(?<target>youtube|google|github|chat\s*gpt|booth|notepad|ghi\s+chú|ghi\s+chu|calculator|máy\s+tính|may\s+tinh|file\s+explorer|explorer|windows\s+settings|cài\s+đặt\s+windows|cai\s+dat\s+windows|settings)" +
            @"(?:\s+(?:đi|di|giúp|giup|cho|mình|minh|tôi|toi|em|linh|xem|lên|len|nhé|nhe|nha|với|voi|please|được|duoc|không|khong|miku|mao|ơi|oi))*\s*[.!?~]*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool TryParse(string input, out MaoComputerCommand command)
        {
            command = null;
            string value = input ?? string.Empty;
            Match match = Control.Match(value);
            if (match.Success)
            {
                string action = match.Groups[1].Value.ToLowerInvariant();
                command = new MaoComputerCommand(action == "on" ? MaoComputerCommandKind.Enable :
                    action == "off" ? MaoComputerCommandKind.Disable : MaoComputerCommandKind.Status);
                return true;
            }
            match = Open.Match(value);
            if (match.Success)
                return TryBuildAction(match.Groups[1].Value, match.Groups[2].Value, out command);
            match = NaturalOpen.Match(value);
            if (!match.Success) return false;
            string target = NormalizeNaturalTarget(match.Groups["target"].Value);
            string type = IsWebsite(target) ? "web" : IsSettings(target) ? "settings" : "app";
            return TryBuildAction(type, target, out command);
        }

        private static bool TryBuildAction(string type, string target, out MaoComputerCommand command)
        {
            command = null;
            MaoComputerAction action;
            bool valid = type.Equals("app", StringComparison.OrdinalIgnoreCase)
                ? MaoComputerActionCatalog.TryResolveApplication(target, out action)
                : type.Equals("web", StringComparison.OrdinalIgnoreCase)
                    ? MaoComputerActionCatalog.TryResolveWebsite(target, out action)
                    : MaoComputerActionCatalog.TryResolveSettings(target, out action);
            if (!valid) return false;
            command = new MaoComputerCommand(MaoComputerCommandKind.RequestAction, action);
            return true;
        }

        private static bool IsWebsite(string target)
        {
            MaoComputerAction ignored;
            return MaoComputerActionCatalog.TryResolveWebsite(target, out ignored);
        }

        private static bool IsSettings(string target)
        {
            MaoComputerAction ignored;
            return MaoComputerActionCatalog.TryResolveSettings(target, out ignored);
        }

        private static string NormalizeNaturalTarget(string target)
        {
            string value = Regex.Replace((target ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
            switch (value)
            {
                case "chat gpt": return "chatgpt";
                case "ghi chú":
                case "ghi chu": return "notepad";
                case "máy tính":
                case "may tinh": return "calculator";
                case "file explorer": return "explorer";
                case "windows settings":
                case "cài đặt windows":
                case "cai dat windows":
                case "settings": return "windows";
                default: return value;
            }
        }
    }

    public static class MaoComputerActionIntentPolicy
    {
        private const string OpenVerb = @"(?:mở|mo|open|bật|bat|khởi\s+động|khoi\s+dong|vào|vao|truy\s+cập|truy\s+cap)";
        private static readonly Regex ExplicitOpenIntent = new Regex(
            @"^\s*/?(?:(?:miku|mao|em|bạn|ban)(?:\s+(?:ơi|oi))?\s*[,!:\-]?\s*)?" +
            @"(?:(?:hãy|hay|vui\s+lòng|vui\s+long|làm\s+ơn|lam\s+on|please|có\s+thể|co\s+the|can\s+you|could\s+you)\s+)?" + OpenVerb + @"(?:\s|$)|" +
            @"^\s*(?:anh|mình|minh|tôi|toi)\s+(?:muốn|muon|nhờ|nho)\s+(?:(?:em|miku|bạn|ban)\s+)?" + OpenVerb + @"(?:\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NegatedOpenIntent = new Regex(
            @"(?:^|\s)(?:đừng|dung|không|khong|chớ|do\s+not|don't|dont)(?:\s+[^\s,.!?;]+){0,4}\s+" + OpenVerb + @"(?:\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool CanExecute(string userText, MaoComputerAction action)
        {
            if (action == null) return false;
            string text = Regex.Replace((userText ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
            if (!ExplicitOpenIntent.IsMatch(text) || NegatedOpenIntent.IsMatch(text)) return false;
            string targetPattern = TargetPattern(action.TargetId);
            return targetPattern.Length > 0 && Regex.IsMatch(
                text,
                @"(?:^|\s|[,!.?])(?:" + targetPattern + @")(?:$|\s|[,!.?])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string TargetPattern(string targetId)
        {
            switch ((targetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "youtube": return "youtube";
                case "google": return "google";
                case "github": return "github";
                case "chatgpt": return @"chat\s*gpt";
                case "booth": return "booth";
                case "notepad": return @"notepad|ghi\s+chú|ghi\s+chu";
                case "calculator": return @"calculator|máy\s+tính|may\s+tinh";
                case "explorer": return @"file\s+explorer|explorer|trình\s+quản\s+lý\s+file|trinh\s+quan\s+ly\s+file";
                case "windows": return @"windows\s+settings|cài\s+đặt\s+windows|cai\s+dat\s+windows|settings";
                default: return string.Empty;
            }
        }
    }

    public static class MaoComputerToolParser
    {
        private static readonly Regex Envelope = new Regex(
            @"\[computer_action=(open_app|open_web|open_settings);target=([^\]]{1,64})\]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool TryExtract(string modelText, out string visibleText, out MaoComputerAction action)
        {
            action = null;
            string value = modelText ?? string.Empty;
            MatchCollection matches = Envelope.Matches(value);
            Match match = matches.Count == 1 ? matches[0] : Match.Empty;
            visibleText = Regex.Replace(value, @"\[computer_action=[^\]]*\]", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            if (!match.Success) return false;
            string kind = match.Groups[1].Value.ToLowerInvariant();
            string target = match.Groups[2].Value.Trim();
            if (!Regex.IsMatch(target, @"^[a-z0-9-]{1,32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return false;
            if (kind == "open_app") return MaoComputerActionCatalog.TryResolveApplication(target, out action);
            if (kind == "open_web") return MaoComputerActionCatalog.TryResolveWebsite(target, out action);
            return MaoComputerActionCatalog.TryResolveSettings(target, out action);
        }
    }
}
