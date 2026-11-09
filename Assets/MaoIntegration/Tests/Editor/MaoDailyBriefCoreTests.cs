using System;
using System.Linq;
using NUnit.Framework;

namespace MaoIntegration.Tests
{
    public sealed class MaoDailyBriefCoreTests
    {
        private static readonly DateTime LocalMorning =
            new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Unspecified);

        [TestCase("/today", MaoDailyBriefCommandKind.ShowToday)]
        [TestCase("hôm nay có gì hot", MaoDailyBriefCommandKind.News)]
        [TestCase("hôm nay có gì hot?", MaoDailyBriefCommandKind.News)]
        [TestCase("/news", MaoDailyBriefCommandKind.News)]
        [TestCase("/daily location", MaoDailyBriefCommandKind.ShowLocation)]
        [TestCase("/daily location clear", MaoDailyBriefCommandKind.ClearLocation)]
        [TestCase("/reminders", MaoDailyBriefCommandKind.ListReminders)]
        [TestCase("/agenda", MaoDailyBriefCommandKind.Agenda)]
        [TestCase("/notifications status", MaoDailyBriefCommandKind.NotificationsStatus)]
        public void DailyCommands_ParseFixedForms(string input, MaoDailyBriefCommandKind expected)
        {
            Assert.IsTrue(MaoDailyBriefCommandParser.TryParse(input, LocalMorning, out MaoDailyBriefCommand command));
            Assert.AreEqual(expected, command.Kind);
        }

        [Test]
        public void DailyCommands_ParseAndNormalizeLocation()
        {
            Assert.IsTrue(MaoDailyBriefCommandParser.TryParse(
                "/daily location   Đà Nẵng  ", LocalMorning, out MaoDailyBriefCommand command));

            Assert.AreEqual(MaoDailyBriefCommandKind.SetLocation, command.Kind);
            Assert.AreEqual("Đà Nẵng", command.Value);
        }

        [Test]
        public void DailyCommands_ParseNotificationToggle()
        {
            Assert.IsTrue(MaoDailyBriefCommandParser.TryParse(
                "bật thông báo Windows đi", LocalMorning, out MaoDailyBriefCommand command));
            Assert.AreEqual(MaoDailyBriefCommandKind.WindowsNotificationsOn, command.Kind);
            Assert.IsTrue(MaoDailyBriefCommandParser.TryParse(
                "/notifications windows off", LocalMorning, out command));
            Assert.AreEqual(MaoDailyBriefCommandKind.WindowsNotificationsOff, command.Kind);
        }

        [Test]
        public void ReminderCommands_ParseRelativeTimeAndNaturalVietnamese()
        {
            Assert.IsTrue(MaoDailyBriefCommandParser.TryParse(
                "/remind 10m uống nước", LocalMorning, out MaoDailyBriefCommand slash));
            Assert.AreEqual(MaoDailyBriefCommandKind.AddReminder, slash.Kind);
            Assert.AreEqual(LocalMorning.AddMinutes(10), slash.Reminder.DueLocal);
            Assert.AreEqual("uống nước", slash.Reminder.Text);

            Assert.IsTrue(MaoDailyBriefCommandParser.TryParse(
                "nhắc tôi 10 phút nữa uống nước", LocalMorning, out MaoDailyBriefCommand natural));
            Assert.AreEqual(LocalMorning.AddMinutes(10), natural.Reminder.DueLocal);
            Assert.AreEqual("uống nước", natural.Reminder.Text);
        }

        [Test]
        public void ReminderCommands_RollLocalClockToNextDay()
        {
            Assert.IsTrue(MaoDailyBriefCommandParser.TryParse(
                "/remind 08:30 họp nhóm", LocalMorning, out MaoDailyBriefCommand command));
            Assert.AreEqual(LocalMorning.Date.AddDays(1).AddHours(8.5), command.Reminder.DueLocal);
        }

        [Test]
        public void ReminderCommands_ParseAbsoluteAndCancel()
        {
            Assert.IsTrue(MaoDailyBriefCommandParser.TryParse(
                "/remind 2026-09-29 14:05 họp nhóm", LocalMorning, out MaoDailyBriefCommand add));
            Assert.AreEqual(new DateTime(2026, 9, 29, 14, 5, 0), add.Reminder.DueLocal);
            Assert.IsFalse(string.IsNullOrWhiteSpace(add.Reminder.Id));

            Assert.IsTrue(MaoDailyBriefCommandParser.TryParse(
                "/remind cancel abc123", LocalMorning, out MaoDailyBriefCommand cancel));
            Assert.AreEqual(MaoDailyBriefCommandKind.CancelReminder, cancel.Kind);
            Assert.AreEqual("abc123", cancel.Value);
        }

        [TestCase("/remind 10m")]
        [TestCase("nhắc tôi ngày mai uống nước")]
        [TestCase("/remind 25:90 sai giờ")]
        [TestCase("/remind 2026-09-27 09:00 quá khứ")]
        public void ReminderCommands_RejectAmbiguousInvalidOrPastInput(string input)
        {
            Assert.IsFalse(MaoDailyBriefCommandParser.TryParse(input, LocalMorning, out _));
        }

        [Test]
        public void ReminderScheduler_DeliversEachReminderOnlyOnce()
        {
            var state = new MaoReminderState();
            MaoReminder reminder = MaoReminder.Create("r1", "uống nước", LocalMorning.AddMinutes(-1));
            state.Reminders.Add(reminder);

            Assert.AreEqual(1, MaoReminderScheduler.GetDue(state, LocalMorning).Count);
            Assert.IsTrue(MaoReminderScheduler.MarkDelivered(state, "r1", LocalMorning));
            Assert.IsFalse(MaoReminderScheduler.MarkDelivered(state, "r1", LocalMorning));
            Assert.AreEqual(0, MaoReminderScheduler.GetDue(state, LocalMorning).Count);
        }

        [Test]
        public void ReminderScheduler_CancelledReminderIsNeverDue()
        {
            var state = new MaoReminderState();
            state.Reminders.Add(MaoReminder.Create("r1", "việc", LocalMorning.AddMinutes(-1)));
            Assert.IsTrue(MaoReminderScheduler.Cancel(state, "r1"));
            Assert.IsFalse(MaoReminderScheduler.Cancel(state, "r1"));
            Assert.IsEmpty(MaoReminderScheduler.GetDue(state, LocalMorning));
        }

        [Test]
        public void ReminderScheduler_SanitizeStateRemovesInvalidAndDuplicateEntries()
        {
            var state = new MaoReminderState();
            state.Reminders.Add(MaoReminder.Create("ok", "  uống   nước  ", LocalMorning.AddMinutes(5)));
            state.Reminders.Add(MaoReminder.Create("ok", "duplicate", LocalMorning.AddMinutes(10)));
            state.Reminders.Add(MaoReminder.Create("../bad", "path-like id", LocalMorning.AddMinutes(10)));
            state.Reminders.Add(MaoReminder.Create("too-long", new string('x', 301), LocalMorning.AddMinutes(10)));
            state.Reminders.Add(null);

            int removed = MaoReminderScheduler.SanitizeState(state);

            Assert.AreEqual(4, removed);
            Assert.AreEqual(1, state.Reminders.Count);
            Assert.AreEqual("uống nước", state.Reminders[0].Text);
            Assert.AreEqual(DateTimeKind.Unspecified, state.Reminders[0].DueLocal.Kind);
        }

        [Test]
        public void DailyDelivery_IsExactlyOncePerLocalDate()
        {
            var state = new MaoDailyBriefDeliveryState();
            Assert.IsTrue(MaoDailyBriefDeliveryPolicy.ShouldDeliver(state, LocalMorning.Date));
            MaoDailyBriefDeliveryPolicy.MarkDelivered(state, LocalMorning.Date);
            Assert.IsFalse(MaoDailyBriefDeliveryPolicy.ShouldDeliver(state, LocalMorning.Date));
            Assert.IsTrue(MaoDailyBriefDeliveryPolicy.ShouldDeliver(state, LocalMorning.Date.AddDays(1)));
        }

        [Test]
        public void WeatherParser_ValidatesBoundsAndMapsCode()
        {
            MaoWeatherSnapshot weather = MaoWeatherParser.ParseForecast(
                "{\"current\":{\"temperature_2m\":29.4,\"weather_code\":61}," +
                "\"daily\":{\"time\":[\"2026-09-28\"],\"temperature_2m_min\":[24.1],\"temperature_2m_max\":[32.8]}}",
                LocalMorning.Date);

            Assert.AreEqual(29.4f, weather.TemperatureC, 0.001f);
            Assert.AreEqual(24.1f, weather.MinimumC, 0.001f);
            Assert.AreEqual(32.8f, weather.MaximumC, 0.001f);
            Assert.AreEqual("mưa nhẹ", weather.Description);
            Assert.Throws<FormatException>(() => MaoWeatherParser.ParseForecast(
                "{\"current\":{\"temperature_2m\":999,\"weather_code\":0}}", LocalMorning.Date));
        }

        [Test]
        public void GeocodingParser_RejectsOutOfRangeCoordinates()
        {
            MaoDailyLocation location = MaoWeatherParser.ParseGeocoding(
                "{\"results\":[{\"name\":\"Da Nang\",\"latitude\":16.05," +
                "\"longitude\":108.2,\"timezone\":\"Asia/Ho_Chi_Minh\"}]}");
            Assert.AreEqual("Da Nang", location.DisplayName);
            Assert.Throws<FormatException>(() => MaoWeatherParser.ParseGeocoding(
                "{\"results\":[{\"name\":\"bad\",\"latitude\":91,\"longitude\":0}]}")
            );
        }

        [Test]
        public void NewsRssParser_BoundsItemsSanitizesHtmlAndSkipsUnsafeLinks()
        {
            string xml = "<?xml version=\"1.0\"?><rss><channel>" +
                "<item><title>  Tin &amp; mới </title><description><![CDATA[<b>Mô tả</b> &amp; thêm]]></description>" +
                "<pubDate>Mon, 28 Sep 2026 02:00:00 GMT</pubDate><link>https://vnexpress.net/a</link></item>" +
                "<item><title>Unsafe</title><link>javascript:alert(1)</link></item>" +
                "</channel></rss>";
            MaoNewsItem[] items = MaoNewsRssParser.Parse(xml, 3).ToArray();
            Assert.AreEqual(1, items.Length);
            Assert.AreEqual("Tin & mới", items[0].Title);
            Assert.AreEqual("Mô tả & thêm", items[0].Description);
            Assert.AreEqual("https://vnexpress.net/a", items[0].Link);
        }

        [Test]
        public void NewsRssParser_RejectsExternalDtdAndOversizedXml()
        {
            string hostile = "<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///secret'>]><rss><channel>" +
                             "<item><title>&x;</title></item></channel></rss>";
            Assert.Throws<FormatException>(() => MaoNewsRssParser.Parse(hostile).ToArray());
            Assert.Throws<FormatException>(() => MaoNewsRssParser.Parse(new string('x', 600000)).ToArray());
        }

        [Test]
        public void Formatters_ProduceCompactVietnameseBriefAndNews()
        {
            var location = new MaoDailyLocation("Đà Nẵng", 16.05, 108.2, "Asia/Ho_Chi_Minh");
            var weather = new MaoWeatherSnapshot(true, 29f, 24f, 33f, 61, "mưa nhẹ", LocalMorning.Date);
            var reminders = new[] { MaoReminder.Create("r", "gọi mẹ", LocalMorning.AddHours(2)) };
            string brief = MaoDailyBriefFormatter.Format(LocalMorning, location, weather, reminders);
            StringAssert.Contains("thứ Hai", brief);
            StringAssert.Contains("Đà Nẵng", brief);
            StringAssert.Contains("29°C", brief);
            StringAssert.Contains("1 lời nhắc", brief);

            string news = MaoNewsFormatter.Format(new[]
            {
                new MaoNewsItem("Tin mới", "Mô tả", LocalMorning, "https://vnexpress.net/a")
            });
            StringAssert.Contains("VnExpress", news);
            StringAssert.Contains("Tin mới", news);
            StringAssert.Contains("https://vnexpress.net/a", news);
        }

        [Test]
        public void Formatters_ExplainMissingLocationAndOfflineWeather()
        {
            string brief = MaoDailyBriefFormatter.Format(
                LocalMorning, null, MaoWeatherSnapshot.Unavailable(LocalMorning.Date), Array.Empty<MaoReminder>());
            StringAssert.Contains("chưa có thành phố", brief);
            StringAssert.Contains("không lấy được thời tiết", brief);
        }
    }
}
