using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;

namespace MaoIntegration
{
    public enum MaoDailyBriefCommandKind
    {
        ShowToday,
        News,
        ShowLocation,
        SetLocation,
        ClearLocation,
        AddReminder,
        ListReminders,
        Agenda,
        CancelReminder,
        NotificationsStatus,
        WindowsNotificationsOn,
        WindowsNotificationsOff
    }

    public sealed class MaoDailyBriefCommand
    {
        public MaoDailyBriefCommandKind Kind { get; }
        public string Value { get; }
        public MaoReminder Reminder { get; }

        public MaoDailyBriefCommand(MaoDailyBriefCommandKind kind, string value = null, MaoReminder reminder = null)
        {
            Kind = kind;
            Value = value ?? string.Empty;
            Reminder = reminder;
        }
    }

    public static class MaoDailyBriefCommandParser
    {
        private static readonly Regex RelativeSlash = new Regex(
            @"^/remind\s+(?<amount>\d+)\s*(?<unit>m|min|phut|phút|h|hr|gio|giờ|d|day|ngay|ngày)\s+(?<text>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NaturalRelative = new Regex(
            @"^nhắc\s+(?:tôi|mình|mk)\s+(?<amount>\d+)\s*(?<unit>phút|phut|giờ|gio|tiếng|tieng|ngày|ngay)\s+nữa\s+(?<text>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NaturalRelativeAscii = new Regex(
            @"^nhac\s+(?:toi|minh|mk)\s+(?<amount>\d+)\s*(?<unit>phut|gio|tieng|ngay)\s+nua\s+(?<text>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Clock = new Regex(
            @"^(?<hour>\d{1,2}):(?<minute>\d{2})\s+(?<text>.+)$",
            RegexOptions.CultureInvariant);
        private static readonly Regex Absolute = new Regex(
            @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<hour>\d{1,2}):(?<minute>\d{2})\s+(?<text>.+)$",
            RegexOptions.CultureInvariant);

        public static bool TryParse(string input, DateTime nowLocal, out MaoDailyBriefCommand command)
        {
            command = null;
            string raw = (input ?? string.Empty).Trim();
            if (raw.Length == 0) return false;
            string normalized = Normalize(raw);

            if (normalized == "/today" || normalized == "today" || normalized == "hom nay co gi" ||
                normalized == "hom nay co gi miku")
            {
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.ShowToday);
                return true;
            }
            if (normalized == "/news" || normalized == "tin hot hom nay" || normalized == "tin moi hom nay" ||
                normalized == "hom nay co gi hot" || normalized == "hom nay co gi hot miku")
            {
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.News);
                return true;
            }
            if (normalized == "/daily location" || normalized == "daily location")
            {
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.ShowLocation);
                return true;
            }
            if (normalized.StartsWith("/daily location ", StringComparison.Ordinal) ||
                normalized.StartsWith("daily location ", StringComparison.Ordinal))
            {
                string value = raw.Substring(raw.IndexOf("location", StringComparison.OrdinalIgnoreCase) + 8).Trim();
                if (value.Equals("clear", StringComparison.OrdinalIgnoreCase) || value.Equals("xoa", StringComparison.OrdinalIgnoreCase))
                    command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.ClearLocation);
                else if (!string.IsNullOrWhiteSpace(value) && value.Length <= 120)
                    command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.SetLocation, value);
                return command != null;
            }
            if (normalized == "/reminders" || normalized == "reminders")
            {
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.ListReminders);
                return true;
            }
            if (normalized == "/agenda" || normalized == "agenda")
            {
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.Agenda);
                return true;
            }
            if (normalized == "/notifications status" || normalized == "notifications status")
            {
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.NotificationsStatus);
                return true;
            }
            if (normalized == "/notifications windows on" || normalized == "bat thong bao windows di" ||
                normalized == "bat thong bao windows")
            {
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.WindowsNotificationsOn);
                return true;
            }
            if (normalized == "/notifications windows off" || normalized == "tat thong bao windows di" ||
                normalized == "tat thong bao windows")
            {
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.WindowsNotificationsOff);
                return true;
            }
            if (normalized.StartsWith("/remind cancel ", StringComparison.Ordinal))
            {
                string id = raw.Substring(15).Trim();
                if (IsSafeId(id)) command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.CancelReminder, id);
                return command != null;
            }

            Match match = NaturalRelative.Match(raw);
            if (!match.Success) match = NaturalRelativeAscii.Match(normalized);
            if (match.Success) return BuildRelative(match, nowLocal, out command);
            match = RelativeSlash.Match(raw);
            if (match.Success) return BuildRelative(match, nowLocal, out command);
            string absoluteInput = raw.StartsWith("/remind ", StringComparison.OrdinalIgnoreCase)
                ? raw.Substring(8).Trim()
                : raw;
            match = Absolute.Match(absoluteInput);
            if (match.Success)
            {
                int hour = ParseInt(match.Groups["hour"].Value);
                int minute = ParseInt(match.Groups["minute"].Value);
                DateTime date;
                if (!DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date) || !ValidClock(hour, minute)) return false;
                DateTime due = DateTime.SpecifyKind(date.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
                if (due <= nowLocal) return false;
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.AddReminder,
                    reminder: MaoReminder.Create(NewId(), CleanText(match.Groups["text"].Value), due));
                return true;
            }
            match = Clock.Match(raw.StartsWith("/remind ", StringComparison.OrdinalIgnoreCase) ? raw.Substring(8).Trim() : string.Empty);
            if (match.Success)
            {
                int hour = ParseInt(match.Groups["hour"].Value);
                int minute = ParseInt(match.Groups["minute"].Value);
                if (!ValidClock(hour, minute)) return false;
                DateTime due = DateTime.SpecifyKind(nowLocal.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
                if (due <= nowLocal) due = due.AddDays(1);
                command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.AddReminder,
                    reminder: MaoReminder.Create(NewId(), CleanText(match.Groups["text"].Value), due));
                return true;
            }
            return false;
        }

        private static bool BuildRelative(Match match, DateTime nowLocal, out MaoDailyBriefCommand command)
        {
            command = null;
            int amount = ParseInt(match.Groups["amount"].Value);
            if (amount <= 0 || amount > 3660) return false;
            string unit = Normalize(match.Groups["unit"].Value);
            TimeSpan delta;
            if (unit == "m" || unit == "min" || unit == "phut") delta = TimeSpan.FromMinutes(amount);
            else if (unit == "h" || unit == "hr" || unit == "gio" || unit == "tieng") delta = TimeSpan.FromHours(amount);
            else delta = TimeSpan.FromDays(amount);
            string text = CleanText(match.Groups["text"].Value);
            if (text.Length == 0 || text.Length > 300) return false;
            command = new MaoDailyBriefCommand(MaoDailyBriefCommandKind.AddReminder,
                reminder: MaoReminder.Create(NewId(), text, DateTime.SpecifyKind(nowLocal.Add(delta), DateTimeKind.Unspecified)));
            return true;
        }

        private static string Normalize(string value)
        {
            string form = (value ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(form.Length);
            foreach (char c in form)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    builder.Append(c);
            return builder.ToString().Normalize(NormalizationForm.FormC)
                .Replace('đ', 'd')
                // Chat input commonly carries a trailing question mark or
                // sentence punctuation. It should not prevent fixed intents
                // such as “hôm nay có gì hot?” from being recognized.
                .TrimEnd('?', '!', '.', '。', '！', '？');
        }

        private static string CleanText(string value) => Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        private static bool ValidClock(int hour, int minute) => hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59;
        private static int ParseInt(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) ? result : -1;
        private static bool IsSafeId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && Regex.IsMatch(value, "^[A-Za-z0-9_-]+$");
        private static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    [Serializable]
    public sealed class MaoReminder
    {
        public string Id;
        public string Text;
        public DateTime DueLocal;
        public bool Delivered;
        public bool Cancelled;

        public static MaoReminder Create(string id, string text, DateTime dueLocal) => new MaoReminder
        {
            Id = id ?? string.Empty, Text = text ?? string.Empty,
            DueLocal = DateTime.SpecifyKind(dueLocal, DateTimeKind.Unspecified)
        };
    }

    [Serializable]
    public sealed class MaoReminderState
    {
        public int SchemaVersion = 1;
        public List<MaoReminder> Reminders = new List<MaoReminder>();
    }

    public static class MaoReminderScheduler
    {
        /// <summary>
        /// Sanitizes persisted reminder data before it is exposed to the UI or
        /// scheduler.  This removes null/invalid/duplicate entries while
        /// keeping at most a bounded number of reminders, so a corrupted local
        /// JSON file cannot cause duplicate notifications or unbounded memory.
        /// </summary>
        public static int SanitizeState(MaoReminderState state)
        {
            if (state == null) return 0;
            int removed = 0;
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var safe = new List<MaoReminder>();
            foreach (MaoReminder reminder in state.Reminders ?? new List<MaoReminder>())
            {
                string id = reminder?.Id?.Trim() ?? string.Empty;
                if (reminder == null || !IsSafeId(id) || string.IsNullOrWhiteSpace(reminder.Text) ||
                    reminder.Text.Length > 300 || reminder.DueLocal == DateTime.MinValue ||
                    reminder.DueLocal == DateTime.MaxValue || !seenIds.Add(id))
                {
                    removed++;
                    continue;
                }
                reminder.Id = id;
                reminder.Text = Regex.Replace(reminder.Text.Trim(), @"\s+", " ");
                reminder.DueLocal = DateTime.SpecifyKind(reminder.DueLocal, DateTimeKind.Unspecified);
                safe.Add(reminder);
                if (safe.Count >= 100)
                {
                    removed += Math.Max(0, (state.Reminders?.Count ?? 0) - safe.Count);
                    break;
                }
            }
            state.SchemaVersion = 1;
            state.Reminders = safe;
            return removed;
        }

        public static IReadOnlyList<MaoReminder> GetDue(MaoReminderState state, DateTime nowLocal)
        {
            if (state == null || state.Reminders == null) return Array.Empty<MaoReminder>();
            return state.Reminders.Where(r => r != null && !r.Delivered && !r.Cancelled && r.DueLocal <= nowLocal).ToArray();
        }

        public static bool MarkDelivered(MaoReminderState state, string id, DateTime nowLocal)
        {
            MaoReminder reminder = Find(state, id);
            if (reminder == null || reminder.Delivered || reminder.Cancelled || reminder.DueLocal > nowLocal) return false;
            reminder.Delivered = true;
            return true;
        }

        public static bool Cancel(MaoReminderState state, string id)
        {
            MaoReminder reminder = Find(state, id);
            if (reminder == null || reminder.Delivered || reminder.Cancelled) return false;
            reminder.Cancelled = true;
            return true;
        }

        private static MaoReminder Find(MaoReminderState state, string id) =>
            state?.Reminders?.FirstOrDefault(r => r != null && string.Equals(r.Id, id, StringComparison.Ordinal));

        private static bool IsSafeId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                   Regex.IsMatch(value, "^[A-Za-z0-9_-]+$");
        }
    }

    [Serializable]
    public sealed class MaoDailyBriefDeliveryState
    {
        public int SchemaVersion = 1;
        public string LastDeliveredLocalDate = string.Empty;
    }

    public static class MaoDailyBriefDeliveryPolicy
    {
        public static bool ShouldDeliver(MaoDailyBriefDeliveryState state, DateTime localDate)
        {
            return state == null || !DateTime.TryParseExact(state.LastDeliveredLocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime delivered) || delivered.Date != localDate.Date;
        }

        public static void MarkDelivered(MaoDailyBriefDeliveryState state, DateTime localDate)
        {
            if (state != null) state.LastDeliveredLocalDate = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    [Serializable]
    public sealed class MaoDailyLocation
    {
        public string DisplayName;
        public double Latitude;
        public double Longitude;
        public string Timezone;
        public MaoDailyLocation() { }
        public MaoDailyLocation(string displayName, double latitude, double longitude, string timezone)
        { DisplayName = displayName; Latitude = latitude; Longitude = longitude; Timezone = timezone; }
    }

    public sealed class MaoWeatherSnapshot
    {
        public bool Available { get; }
        public float TemperatureC { get; }
        public float MinimumC { get; }
        public float MaximumC { get; }
        public int WeatherCode { get; }
        public string Description { get; }
        public DateTime LocalDate { get; }

        public MaoWeatherSnapshot(bool available, float temperatureC, float minimumC, float maximumC, int weatherCode, string description, DateTime localDate)
        { Available = available; TemperatureC = temperatureC; MinimumC = minimumC; MaximumC = maximumC; WeatherCode = weatherCode; Description = description ?? string.Empty; LocalDate = localDate.Date; }
        public static MaoWeatherSnapshot Unavailable(DateTime date) => new MaoWeatherSnapshot(false, 0, 0, 0, -1, string.Empty, date);
    }

    public static class MaoWeatherParser
    {
        public static MaoDailyLocation ParseGeocoding(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 512 * 1024) throw new FormatException("Invalid geocoding payload.");
            try
            {
                JArray results = JObject.Parse(json)["results"] as JArray;
                if (results == null) throw new FormatException("Geocoding results are missing.");
                foreach (JToken result in results)
                {
                    double latitude = (double?)result["latitude"] ?? double.NaN;
                    double longitude = (double?)result["longitude"] ?? double.NaN;
                    string name = (string)result["name"];
                    if (!double.IsNaN(latitude) && !double.IsNaN(longitude) && latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180 && !string.IsNullOrWhiteSpace(name))
                        return new MaoDailyLocation(name.Trim(), latitude, longitude, (string)result["timezone"] ?? "auto");
                }
            }
            catch (FormatException) { throw; }
            catch (Exception exception) { throw new FormatException("Invalid geocoding payload.", exception); }
            throw new FormatException("No valid geocoding result.");
        }

        public static MaoWeatherSnapshot ParseForecast(string json, DateTime localDate)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 512 * 1024) throw new FormatException("Invalid weather payload.");
            try
            {
                JObject root = JObject.Parse(json);
                double temperature = (double?)root["current"]?["temperature_2m"] ?? double.NaN;
                int code = (int?)root["current"]?["weather_code"] ?? -1;
                double minimum = (double?)root["daily"]?["temperature_2m_min"]?[0] ?? temperature;
                double maximum = (double?)root["daily"]?["temperature_2m_max"]?[0] ?? temperature;
                if (double.IsNaN(temperature) || temperature < -100 || temperature > 70 || minimum < -100 || minimum > 70 || maximum < -100 || maximum > 70 || code < 0 || code > 99)
                    throw new FormatException("Weather values are out of bounds.");
                return new MaoWeatherSnapshot(true, (float)temperature, (float)minimum, (float)maximum, code, DescribeCode(code), localDate);
            }
            catch (FormatException) { throw; }
            catch (Exception exception) { throw new FormatException("Invalid weather payload.", exception); }
        }

        public static string DescribeCode(int code)
        {
            if (code == 0) return "trời quang";
            if (code <= 3) return "mây nhẹ";
            if (code <= 48) return "sương mù";
            if (code <= 57) return "mưa phùn";
            if (code <= 67) return "mưa nhẹ";
            if (code <= 77) return "tuyết";
            if (code <= 82) return "mưa rào";
            if (code <= 86) return "mưa tuyết";
            return "dông";
        }
    }

    public sealed class MaoNewsItem
    {
        public string Title { get; }
        public string Description { get; }
        public DateTime PublishedLocal { get; }
        public string Link { get; }
        public MaoNewsItem(string title, string description, DateTime publishedLocal, string link)
        { Title = title ?? string.Empty; Description = description ?? string.Empty; PublishedLocal = publishedLocal; Link = link ?? string.Empty; }
    }

    public static class MaoNewsRssParser
    {
        private static readonly Regex HtmlEntityPattern = new Regex(
            @"&(?:#x[0-9a-fA-F]+|#[0-9]+|[A-Za-z][A-Za-z0-9]+);",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static IEnumerable<MaoNewsItem> Parse(string xml, int maxItems = 3)
        {
            if (string.IsNullOrWhiteSpace(xml) || xml.Length > 512 * 1024) throw new FormatException("RSS payload is invalid or too large.");
            if (maxItems < 1 || maxItems > 10) maxItems = 3;
            var parsedItems = new List<MaoNewsItem>();
            try
            {
                // DTDs are rejected before load and the resolver is disabled as
                // defense in depth. Use the DOM rather than XmlReader.Create /
                // ReadSubtree: those overloads differ between Unity Mono
                // profiles and can silently skip valid RSS fields.
                if (xml.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new FormatException("RSS DTDs are not allowed.");

                var document = new XmlDocument { XmlResolver = null };
                document.LoadXml(xml);
                foreach (XmlNode item in CollectItems(document))
                {
                    if (parsedItems.Count >= maxItems) break;
                    string title = string.Empty;
                    string description = string.Empty;
                    string link = string.Empty;
                    string published = string.Empty;
                    foreach (XmlNode child in item.ChildNodes)
                    {
                        if (child.NodeType != XmlNodeType.Element) continue;
                        string name = child.LocalName.ToLowerInvariant();
                        string value = child.InnerText ?? string.Empty;
                        if (name == "title") title = value;
                        else if (name == "description" || name == "summary") description = value;
                        else if (name == "link") link = value;
                        else if (name == "pubdate" || name == "published") published = value;
                    }
                    link = DecodeHtmlEntities(link).Trim();
                    if (!Uri.TryCreate(link, UriKind.Absolute, out Uri uri) ||
                        uri.Scheme != Uri.UriSchemeHttps) continue;
                    title = Sanitize(title, 240);
                    if (title.Length == 0) continue;
                    description = Sanitize(description, 600);
                    DateTime.TryParse(published, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out DateTime publishedUtc);
                    parsedItems.Add(new MaoNewsItem(title, description,
                        publishedUtc.ToLocalTime(), link));
                }
            }
            catch (Exception exception) when (!(exception is FormatException)) { throw new FormatException("RSS payload could not be parsed.", exception); }
            return parsedItems;
        }

        private static IEnumerable<XmlNode> CollectItems(XmlNode node)
        {
            if (node == null) yield break;
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element &&
                    string.Equals(child.LocalName, "item", StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                    continue;
                }
                foreach (XmlNode nested in CollectItems(child)) yield return nested;
            }
        }

        private static string Sanitize(string value, int max)
        {
            string stripped = Regex.Replace(DecodeHtmlEntities(value ?? string.Empty), "<[^>]+>", " ");
            stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
            return stripped.Length > max ? stripped.Substring(0, max - 1).TrimEnd() + "…" : stripped;
        }

        // System.Net.WebUtility is not present in the Unity Mono profile used
        // by the shipped player. Decode the small, safe HTML entity subset
        // commonly found in RSS titles/descriptions without adding a runtime
        // dependency or evaluating arbitrary markup.
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
            else if (digits.StartsWith("#", StringComparison.Ordinal))
            {
                digits = digits.Substring(1);
            }
            if (token.StartsWith("#", StringComparison.Ordinal))
            {
                int codePoint;
                NumberStyles numberStyle = radix == 16 ? NumberStyles.HexNumber : NumberStyles.Integer;
                if (int.TryParse(digits, numberStyle, CultureInfo.InvariantCulture, out codePoint) &&
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

    public static class MaoDailyBriefFormatter
    {
        private static readonly string[] Weekdays = { "Chủ nhật", "thứ Hai", "thứ Ba", "thứ Tư", "thứ Năm", "thứ Sáu", "thứ Bảy" };
        public static string Format(DateTime localNow, MaoDailyLocation location, MaoWeatherSnapshot weather, IEnumerable<MaoReminder> reminders)
        {
            var due = (reminders ?? Array.Empty<MaoReminder>()).Where(r => r != null && !r.Cancelled && !r.Delivered).ToArray();
            // Use a time-appropriate greeting. The daily brief can be delivered
            // on the first launch at any hour, so saying "buổi sáng" at night
            // would feel incorrect and overly scripted.
            string greeting = localNow.Hour < 11
                ? "Chào buổi sáng Linh~"
                : localNow.Hour < 18
                    ? "Chào buổi chiều Linh~"
                    : "Chào buổi tối Linh~";
            var builder = new StringBuilder(greeting + " Hôm nay là ");
            builder.Append(Weekdays[(int)localNow.DayOfWeek]).Append(", ").Append(localNow.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)).Append('.');
            if (location == null) builder.Append(" Miku chưa có thành phố nên không lấy được thời tiết; dùng /daily location <thành phố> nhé.");
            else if (weather == null || !weather.Available) builder.Append(" ").Append(location.DisplayName).Append(": không lấy được thời tiết lúc này.");
            else builder.Append(' ').Append(location.DisplayName).Append(' ').Append(weather.TemperatureC.ToString("0.#", CultureInfo.InvariantCulture)).Append("°C, ").Append(weather.Description).Append(" (thấp ").Append(weather.MinimumC.ToString("0.#", CultureInfo.InvariantCulture)).Append("°C, cao ").Append(weather.MaximumC.ToString("0.#", CultureInfo.InvariantCulture)).Append("°C).");
            builder.Append(" ").Append(due.Length == 0 ? "Hôm nay chưa có lời nhắc." : due.Length == 1 ? "Hôm nay có 1 lời nhắc." : "Hôm nay có " + due.Length + " lời nhắc.");
            return builder.ToString();
        }
    }

    public static class MaoNewsFormatter
    {
        public static string Format(IEnumerable<MaoNewsItem> items)
        {
            MaoNewsItem[] news = (items ?? Array.Empty<MaoNewsItem>()).Where(i => i != null).Take(3).ToArray();
            if (news.Length == 0) return "Miku chưa lấy được tin mới từ VnExpress lúc này.";
            var builder = new StringBuilder("Tin mới từ VnExpress nè:");
            for (int i = 0; i < news.Length; i++)
            {
                builder.Append("\n").Append(i + 1).Append(". ").Append(news[i].Title);
                if (!string.IsNullOrWhiteSpace(news[i].Description)) builder.Append(" — ").Append(news[i].Description);
                if (!string.IsNullOrWhiteSpace(news[i].Link)) builder.Append("\n").Append(news[i].Link);
            }
            return builder.ToString();
        }
    }
}
