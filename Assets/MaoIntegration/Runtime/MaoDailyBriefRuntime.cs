using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

using MaoIntegration;

/// <summary>
/// Local-first daily context and reminder service. Network requests are limited
/// to Open-Meteo and the VnExpress latest-news RSS feed; all user commands are
/// parsed locally before a request is made.
/// </summary>
public sealed class MaoDailyBriefRuntime : MonoBehaviour
{
    private const string StateFileName = "mao-daily-brief.json";
    private const string GeocodingEndpoint = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";
    private const string NewsEndpoint = "https://vnexpress.net/rss/tin-moi-nhat.rss";
    private const int RequestTimeoutSeconds = 12;
    private const int MaxResponseBytes = 512 * 1024;

    [Serializable]
    private sealed class RuntimeState
    {
        public int SchemaVersion = 1;
        public MaoDailyLocation Location;
        public MaoReminderState Reminders = new MaoReminderState();
        public MaoDailyBriefDeliveryState Delivery = new MaoDailyBriefDeliveryState();
        public bool WindowsNotifications = true;
    }

    private RuntimeState state;
    private MaoGeminiChatProvider provider;
    private float nextReminderPoll;
    private bool dailyBriefStarted;
    private string cachedNews;
    private float cachedNewsAt = float.NegativeInfinity;
    private const float NewsCacheTtlSeconds = 600f;

    public bool WindowsNotificationsEnabled => state != null && state.WindowsNotifications;
    public MaoDailyLocation Location => state?.Location;
    public MaoReminderState ReminderState => state?.Reminders;

    private string StatePath => Path.Combine(Application.persistentDataPath, StateFileName);

    private void Awake()
    {
        provider = GetComponent<MaoGeminiChatProvider>();
        LoadState();
        Debug.Log("[Mao Daily] event=initialized location=" + (state.Location == null ? "none" : state.Location.DisplayName) +
                  " reminders=" + state.Reminders.Reminders.Count +
                  " windows_notifications=" + state.WindowsNotifications.ToString().ToLowerInvariant());
    }

    private void Start()
    {
        StartCoroutine(DeliverFirstLaunchBrief());
    }

    private void Update()
    {
        if (Time.unscaledTime < nextReminderPoll) return;
        nextReminderPoll = Time.unscaledTime + 1f;
        DeliverDueReminders(DateTime.Now);
    }

    public bool TryParseCommand(string text, out MaoDailyBriefCommand command)
    {
        return MaoDailyBriefCommandParser.TryParse(text, DateTime.Now, out command);
    }

    public IEnumerator ExecuteCommandRoutine(MaoDailyBriefCommand command, Action<string> onReady)
    {
        string result = string.Empty;
        if (command == null)
        {
            onReady?.Invoke("Miku chưa hiểu lệnh ngày hôm nay.");
            yield break;
        }

        switch (command.Kind)
        {
            case MaoDailyBriefCommandKind.ShowToday:
                yield return BuildBriefRoutine(DateTime.Now, value => result = value);
                break;
            case MaoDailyBriefCommandKind.News:
                yield return LoadNewsRoutine(value => result = value);
                break;
            case MaoDailyBriefCommandKind.ShowLocation:
                result = state.Location == null
                    ? "Miku chưa có thành phố thời tiết. Dùng /daily location <thành phố> nhé."
                    : "Thành phố thời tiết hiện tại: " + state.Location.DisplayName + ".";
                break;
            case MaoDailyBriefCommandKind.SetLocation:
                yield return SetLocationRoutine(command.Value, value => result = value);
                break;
            case MaoDailyBriefCommandKind.ClearLocation:
                state.Location = null;
                SaveState();
                result = "Miku đã xóa thành phố thời tiết rồi.";
                break;
            case MaoDailyBriefCommandKind.AddReminder:
                if (command.Reminder == null)
                    result = "Miku cần thời gian và nội dung lời nhắc nhé.";
                else
                {
                    state.Reminders.Reminders.RemoveAll(reminder => reminder == null || reminder.Cancelled || reminder.Delivered);
                    state.Reminders.Reminders.Add(command.Reminder);
                    SaveState();
                    result = "Đã nhắc Linh lúc " + command.Reminder.DueLocal.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture) +
                             ": " + command.Reminder.Text;
                }
                break;
            case MaoDailyBriefCommandKind.ListReminders:
                result = FormatReminderList(false, DateTime.Now);
                break;
            case MaoDailyBriefCommandKind.Agenda:
                result = FormatReminderList(true, DateTime.Now);
                break;
            case MaoDailyBriefCommandKind.CancelReminder:
                result = MaoReminderScheduler.Cancel(state.Reminders, command.Value)
                    ? "Miku đã hủy lời nhắc " + command.Value + "."
                    : "Miku không tìm thấy lời nhắc đang chờ với mã " + command.Value + ".";
                SaveState();
                break;
            case MaoDailyBriefCommandKind.NotificationsStatus:
                result = "Thông báo Windows đang " + (WindowsNotificationsEnabled ? "bật." : "tắt.");
                break;
            case MaoDailyBriefCommandKind.WindowsNotificationsOn:
                state.WindowsNotifications = true;
                SaveState();
                result = "Đã bật thông báo Windows cho lời nhắc.";
                break;
            case MaoDailyBriefCommandKind.WindowsNotificationsOff:
                state.WindowsNotifications = false;
                SaveState();
                result = "Đã tắt thông báo Windows; lời nhắc vẫn hiện trong console.";
                break;
        }
        onReady?.Invoke(result);
    }

    private IEnumerator DeliverFirstLaunchBrief()
    {
        yield return new WaitForSecondsRealtime(2f);
        for (int attempt = 0; attempt < 40 && provider == null; attempt++)
        {
            provider = GetComponent<MaoGeminiChatProvider>();
            if (provider != null) break;
            yield return new WaitForSecondsRealtime(0.25f);
        }
        DateTime localNow = DateTime.Now;
        if (dailyBriefStarted || !MaoDailyBriefDeliveryPolicy.ShouldDeliver(state.Delivery, localNow.Date)) yield break;
        dailyBriefStarted = true;
        string brief = string.Empty;
        yield return BuildBriefRoutine(localNow, value => brief = value);
        if (string.IsNullOrWhiteSpace(brief)) brief = MaoDailyBriefFormatter.Format(localNow, state.Location, MaoWeatherSnapshot.Unavailable(localNow.Date), PendingReminders(localNow));
        if (provider == null || !provider.PublishDailyBriefMessage(brief))
        {
            dailyBriefStarted = false;
            Debug.LogWarning("[Mao Daily] event=brief_deferred reason=provider_unavailable");
            yield break;
        }
        MaoDailyBriefDeliveryPolicy.MarkDelivered(state.Delivery, localNow.Date);
        SaveState();
        Debug.Log("[Mao Daily] event=brief_delivered local_date=" + localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private IEnumerator BuildBriefRoutine(DateTime localNow, Action<string> onReady)
    {
        MaoWeatherSnapshot weather = MaoWeatherSnapshot.Unavailable(localNow.Date);
        if (state.Location != null)
        {
            string json = null;
            yield return RequestText(BuildForecastUrl(state.Location), value => json = value);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try { weather = MaoWeatherParser.ParseForecast(json, localNow.Date); }
                catch (FormatException exception) { Debug.LogWarning("[Mao Daily] weather_parse_failed type=" + exception.GetType().Name); }
            }
        }
        onReady?.Invoke(MaoDailyBriefFormatter.Format(localNow, state.Location, weather, PendingReminders(localNow)));
    }

    private IEnumerator SetLocationRoutine(string city, Action<string> onReady)
    {
        string json = null;
        yield return RequestText(GeocodingEndpoint + "?name=" + Uri.EscapeDataString(city) + "&count=1&language=vi&format=json", value => json = value);
        if (string.IsNullOrWhiteSpace(json))
        {
            onReady?.Invoke("Miku chưa tìm được thành phố đó. Kiểm tra lại tên giúp Miku nhé.");
            yield break;
        }
        try
        {
            state.Location = MaoWeatherParser.ParseGeocoding(json);
            SaveState();
            onReady?.Invoke("Đã đặt thành phố thời tiết là " + state.Location.DisplayName + ".");
        }
        catch (FormatException exception)
        {
            Debug.LogWarning("[Mao Daily] geocoding_parse_failed type=" + exception.GetType().Name);
            onReady?.Invoke("Miku chưa đọc được kết quả thành phố lúc này.");
        }
    }

    private IEnumerator LoadNewsRoutine(Action<string> onReady)
    {
        if (!string.IsNullOrWhiteSpace(cachedNews) && Time.unscaledTime - cachedNewsAt < NewsCacheTtlSeconds)
        {
            Debug.Log("[Mao Daily] event=news_cache_hit");
            onReady?.Invoke(cachedNews);
            yield break;
        }
        Debug.Log("[Mao Daily] event=news_cache_miss");
        string xml = null;
        yield return RequestText(NewsEndpoint, value => xml = value);
        if (string.IsNullOrWhiteSpace(xml))
        {
            onReady?.Invoke("Miku chưa lấy được tin mới từ VnExpress lúc này.");
            yield break;
        }
        try
        {
            cachedNews = MaoNewsFormatter.Format(MaoNewsRssParser.Parse(xml, 3));
            cachedNewsAt = Time.unscaledTime;
            onReady?.Invoke(cachedNews);
        }
        catch (FormatException exception)
        {
            Debug.LogWarning("[Mao Daily] news_parse_failed type=" + exception.GetType().Name);
            onReady?.Invoke("Miku chưa đọc được tin mới từ VnExpress lúc này.");
        }
    }

    private IEnumerator RequestText(string url, Action<string> onReady)
    {
        if (!IsAllowedRequest(url))
        {
            Debug.LogWarning("[Mao Daily] request_rejected host=" + SafeHost(url));
            onReady?.Invoke(null);
            yield break;
        }
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = RequestTimeoutSeconds;
            request.redirectLimit = 3;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success || request.downloadHandler == null ||
                request.downloadHandler.data == null || request.downloadHandler.data.Length > MaxResponseBytes)
            {
                Debug.LogWarning("[Mao Daily] request_failed host=" + SafeHost(url) + " result=" + request.result + " status=" + request.responseCode);
                onReady?.Invoke(null);
                yield break;
            }
            onReady?.Invoke(request.downloadHandler.text);
        }
    }

    private void DeliverDueReminders(DateTime localNow)
    {
        IReadOnlyList<MaoReminder> due = MaoReminderScheduler.GetDue(state.Reminders, localNow);
        foreach (MaoReminder reminder in due.ToArray())
        {
            string message = "Nhắc Linh nè: " + reminder.Text;
            bool published = provider != null && provider.PublishReminderMessage(message);
            bool trayNotificationSent = false;
#if UNITY_STANDALONE_WIN
            if (WindowsNotificationsEnabled && Utils.TrayIcon.IsInitialized)
            {
                Utils.TrayIcon.ShowBalloonTip("Miku", message, Utils.TrayIcon.ToolTipIcon.Info, true);
                trayNotificationSent = true;
            }
#endif
            // Commit delivery only after the provider accepted the bounded message.
            // Keeping an undelivered item pending makes crashes/provider-startup races
            // retry on the next poll instead of silently dropping a reminder.
            if (!published && !trayNotificationSent)
            {
                Debug.LogWarning("[Mao Daily] event=reminder_deferred id=" + reminder.Id + " reason=no_delivery_sink");
                continue;
            }
            if (!MaoReminderScheduler.MarkDelivered(state.Reminders, reminder.Id, localNow)) continue;
            SaveState();
            Debug.Log("[Mao Daily] event=reminder_delivered id=" + reminder.Id +
                      " windows=" + WindowsNotificationsEnabled.ToString().ToLowerInvariant() +
                      " tray_notification=" + trayNotificationSent.ToString().ToLowerInvariant());
        }
    }

    private MaoReminder[] PendingReminders(DateTime now) => state.Reminders.Reminders
        .Where(reminder => reminder != null && !reminder.Delivered && !reminder.Cancelled && reminder.DueLocal.Date == now.Date)
        .OrderBy(reminder => reminder.DueLocal).ToArray();

    private string FormatReminderList(bool todayOnly, DateTime now)
    {
        IEnumerable<MaoReminder> reminders = state.Reminders.Reminders.Where(reminder => reminder != null && !reminder.Delivered && !reminder.Cancelled);
        if (todayOnly) reminders = reminders.Where(reminder => reminder.DueLocal.Date == now.Date);
        MaoReminder[] list = reminders.OrderBy(reminder => reminder.DueLocal).Take(10).ToArray();
        if (list.Length == 0) return todayOnly ? "Hôm nay chưa có lời nhắc nào." : "Miku chưa có lời nhắc nào đang chờ.";
        var lines = new List<string> { todayOnly ? "Lịch nhắc hôm nay:" : "Các lời nhắc đang chờ:" };
        foreach (MaoReminder reminder in list)
            lines.Add("- " + reminder.Id + " · " + reminder.DueLocal.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture) + " · " + reminder.Text);
        return string.Join("\n", lines);
    }

    private static string BuildForecastUrl(MaoDailyLocation location)
    {
        return ForecastEndpoint + "?latitude=" + location.Latitude.ToString(CultureInfo.InvariantCulture) +
               "&longitude=" + location.Longitude.ToString(CultureInfo.InvariantCulture) +
               "&current=temperature_2m,weather_code&daily=temperature_2m_min,temperature_2m_max&timezone=auto&forecast_days=1";
    }

    private void LoadState()
    {
        state = new RuntimeState();
        try
        {
            if (!File.Exists(StatePath)) return;
            FileInfo file = new FileInfo(StatePath);
            if (file.Length <= 0 || file.Length > 256 * 1024) return;
            RuntimeState loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<RuntimeState>(File.ReadAllText(StatePath));
            if (loaded == null) return;
            state = loaded;
            if (state.Reminders == null) state.Reminders = new MaoReminderState();
            if (state.Reminders.Reminders == null) state.Reminders.Reminders = new List<MaoReminder>();
            if (state.Delivery == null) state.Delivery = new MaoDailyBriefDeliveryState();
            if (state.Location != null &&
                (double.IsNaN(state.Location.Latitude) || double.IsNaN(state.Location.Longitude) ||
                 state.Location.Latitude < -90 || state.Location.Latitude > 90 ||
                 state.Location.Longitude < -180 || state.Location.Longitude > 180 ||
                 string.IsNullOrWhiteSpace(state.Location.DisplayName)))
                state.Location = null;
            int removedReminders = MaoReminderScheduler.SanitizeState(state.Reminders);
            if (removedReminders > 0)
            {
                SaveState();
                Debug.LogWarning("[Mao Daily] event=reminders_normalized removed=" + removedReminders);
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Daily] state_load_failed type=" + exception.GetType().Name);
            state = new RuntimeState();
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            string temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, Newtonsoft.Json.JsonConvert.SerializeObject(state));
            if (File.Exists(StatePath))
            {
                try { File.Replace(temporary, StatePath, null); }
                catch
                {
                    File.Copy(temporary, StatePath, true);
                    File.Delete(temporary);
                }
            }
            else File.Move(temporary, StatePath);
        }
        catch (Exception exception) { Debug.LogWarning("[Mao Daily] state_save_failed type=" + exception.GetType().Name); }
    }

    private static string SafeHost(string url)
    {
        try { return new Uri(url).Host; }
        catch { return "invalid"; }
    }

    private static bool IsAllowedRequest(string url)
    {
        try
        {
            Uri uri = new Uri(url);
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, System.StringComparison.OrdinalIgnoreCase)) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "geocoding-api.open-meteo.com" || host == "api.open-meteo.com" ||
                   host == "vnexpress.net" || host.EndsWith(".vnexpress.net", System.StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
