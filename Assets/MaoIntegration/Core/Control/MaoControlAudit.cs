using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace MaoIntegration
{
    public static class MaoControlRedaction
    {
        private static readonly Regex Secrets = new Regex(@"(?i)(?:authorization\s*[:=]\s*(?:(?:Basic|Bearer)\s+)?(?:""[^""]*""|'[^']*'|[^\s,;]+)|(?:Basic|Bearer)\s+\S+|(?:api[_ -]?key|token|password|secret)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)|sk-[a-z0-9_-]{6,}|AIza[a-z0-9_-]{12,}|-----BEGIN[\s\S]*?-----END[^\r\n]*-----)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        public static string Clean(string value, int limit = 160)
        { return CleanContent(value, limit).Replace('\r', ' ').Replace('\n', ' '); }
        public static string CleanContent(string value, int limit = 160)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            // Bound input before regex work, but redact before truncating the output.
            if (value.Length > 16384) value = value.Substring(0, 16384);
            try { value = Secrets.Replace(value, "[redacted]"); }
            catch (RegexMatchTimeoutException) { return "[redacted]"; }
            return value.Length <= limit ? value : value.Substring(0, limit);
        }
        public static string ApprovalPreview(string value)
        {
            const int limit = 12000;
            string safe = CleanContent(value, limit);
            return value != null && value.Length > limit
                ? safe + "\n[Đã rút gọn phần hiển thị. Quyền một lần vẫn áp dụng cho toàn bộ nội dung; hãy từ chối và yêu cầu chia nhỏ nếu chưa xem đủ.]"
                : safe;
        }
        public static string Label(string value)
        {
            value = Clean(value, 96);
            return Regex.Replace(value, @"[^a-zA-Z0-9_.:\-\[\]]", "_");
        }
    }
    public sealed class MaoControlAudit : IMaoControlAudit
    {
        private const long MaxFileBytes = 2 * 1024 * 1024;
        private const int MaxFiles = 14;
        private readonly string directory;
        public MaoControlAudit(string directory) { this.directory = Path.GetFullPath(directory); }
        public bool TryWrite(MaoControlAuditEntry entry)
        {
            if (entry == null) return false;
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes) return false;
                var safe = new MaoControlAuditEntry {
                    utc = DateTime.UtcNow.ToString("O"), taskId = MaoControlRedaction.Label(entry.taskId),
                    agent = MaoControlRedaction.Label(entry.agent), tool = MaoControlRedaction.Label(entry.tool),
                    target = MaoControlRedaction.Label(entry.target), outcome = MaoControlRedaction.Label(entry.outcome),
                    risk = Math.Max(0, Math.Min(3, entry.risk)), durationMs = Math.Max(0, entry.durationMs) };
                File.AppendAllText(path, JsonConvert.SerializeObject(safe) + Environment.NewLine, new UTF8Encoding(false));
                // Only audit files in this dedicated directory are rotated; never user notes/history.
                var files = new DirectoryInfo(directory).GetFiles("????-??-??.jsonl");
                Array.Sort(files, (left, right) => string.CompareOrdinal(right.Name, left.Name));
                for (int index = MaxFiles; index < files.Length; index++) files[index].Delete();
                return true;
            }
            catch (Exception) { return false; }
        }
        public IReadOnlyList<MaoControlAuditEntry> ReadToday(int limit = 20)
        {
            var result = new Queue<MaoControlAuditEntry>();
            limit = Math.Max(1, Math.Min(100, limit));
            try
            {
                string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
                if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes + 4096) return result.ToArray();
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length > 4096) continue;
                    MaoControlAuditEntry entry;
                    try { entry = JsonConvert.DeserializeObject<MaoControlAuditEntry>(line); } catch (JsonException) { continue; }
                    if (entry == null) continue;
                    // Files are data, including after an external edit.
                    entry.taskId = MaoControlRedaction.Label(entry.taskId); entry.outcome = MaoControlRedaction.Label(entry.outcome);
                    entry.tool = MaoControlRedaction.Label(entry.tool); entry.target = MaoControlRedaction.Label(entry.target);
                    entry.agent = MaoControlRedaction.Label(entry.agent); entry.utc = MaoControlRedaction.Clean(entry.utc, 40);
                    if (result.Count == limit) result.Dequeue();
                    result.Enqueue(entry);
                }
            }
            catch (Exception) { }
            return result.ToArray();
        }
    }
}
