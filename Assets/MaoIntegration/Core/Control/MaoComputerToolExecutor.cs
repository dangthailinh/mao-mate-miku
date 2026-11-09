using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MaoIntegration
{
    // Runs only after the Unity registry grants a task-owned execution lease.
    public sealed class MaoComputerToolExecutor
    {
        private readonly string workspace, backupDirectory;
        public MaoComputerToolExecutor(string workspace, string backupDirectory)
        { this.workspace = Path.GetFullPath(workspace); this.backupDirectory = Path.GetFullPath(backupDirectory); }
        public MaoToolResult Execute(MaoToolCall call, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            switch (call.Tool)
            {
                case "list_directory":
                    string directory = Resolve(call.Argument("path"));
                    var items = Directory.EnumerateFileSystemEntries(directory).Take(101).ToArray();
                    return Ok(string.Join("\n", items.Take(100).Select(p => (Directory.Exists(p) ? "[dir] " : "[file] ") + Path.GetFileName(p))) + (items.Length > 100 ? "\n[Only the first 100 entries are shown.]" : ""));
                case "read_file":
                    string path = Resolve(call.Argument("path"));
                    if (IsCredentialPath(path)) return Denied("credential_file", "Không chuyển file chứa credential vào hội thoại AI.");
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length > 65536) return Denied("file_too_large", "File vượt 64 KiB; dùng công cụ PowerShell để xem phần cần thiết.");
                        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                        { string content = reader.ReadToEnd(); token.ThrowIfCancellationRequested(); return Ok(content.Length > 10000 ? content.Substring(0, 10000) + "\n[Truncated at 10000 characters.]" : content); }
                    }
                case "write_file": return Write(call, token);
                case "run_powershell": return RunPowerShell(call, token);
                case "open_url":
                    if (!Uri.TryCreate(call.Argument("url"), UriKind.Absolute, out Uri uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo)) return Denied("invalid_url", "URL phải là http/https và không chứa mật khẩu.");
                    token.ThrowIfCancellationRequested();
                    Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
                    return Ok("Windows đã nhận yêu cầu mở trang web: " + uri.Host);
                default: return Denied("unknown_tool", "Công cụ không được đăng ký.");
            }
        }
        public string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(c => c < 32)) throw new ArgumentException("Invalid path.");
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));
        }
        private static bool IsCredentialPath(string path)
        {
            string normalized = path.Replace('/', '\\').ToLowerInvariant();
            string filename = Path.GetFileName(path).ToLowerInvariant();
            return normalized.Contains("\\.openclaw\\") || normalized.EndsWith("\\.codex\\auth.json") || filename == ".env" || filename.EndsWith(".env") || filename.StartsWith(".env.");
        }
        private MaoToolResult Write(MaoToolCall call, CancellationToken token)
        {
            string path = Resolve(call.Argument("path"));
            if (IsCredentialPath(path)) return Denied("credential_file", "Không sửa credential bằng công cụ file.");
            string content = call.Argument("content");
            if (content == null || content.Length > 32000 || !bool.TryParse(call.Argument("overwrite"), out bool overwrite)) return Denied("invalid_arguments", "Nội dung hoặc overwrite không hợp lệ.");
            bool exists = File.Exists(path);
            if (exists && !overwrite) return Denied("overwrite_required", "File đã có; chỉ ghi đè khi overwrite=true.");
            if (!Directory.Exists(Path.GetDirectoryName(path))) return Denied("parent_missing", "Thư mục cha chưa tồn tại.");
            string backup = null, temp = path + ".mao-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (exists)
                {
                    if (new FileInfo(path).Length > 16 * 1024 * 1024) return Denied("backup_limit", "File quá lớn để tạo backup tự động; chưa ghi đè.");
                    Directory.CreateDirectory(backupDirectory);
                    if (Directory.EnumerateFiles(backupDirectory, "*.bak").Take(101).Count() >= 100) return Denied("backup_limit", "Đã có 100 backup. Chưa ghi đè; anh kiểm tra backup trước nhé.");
                    backup = Path.Combine(backupDirectory, Guid.NewGuid().ToString("N") + ".bak");
                }
                token.ThrowIfCancellationRequested();
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                token.ThrowIfCancellationRequested();
                // Replace is atomic and preserves the actual previous file as the backup.
                if (exists) File.Replace(temp, path, backup); else File.Move(temp, path);
                bool verified = File.ReadAllText(path, Encoding.UTF8) == content;
                return new MaoToolResult(verified, verified ? "completed" : "verification_failed", (verified ? "Đã ghi và đọc lại xác minh file: " : "Chưa xác minh được file: ") + path + (backup == null ? "" : "\nBackup: " + backup));
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private MaoToolResult RunPowerShell(MaoToolCall call, CancellationToken token)
        {
            string command = call.Argument("command");
            if (string.IsNullOrWhiteSpace(command) || command.Length > 12000 || command.Contains("\0")) return Denied("invalid_command", "PowerShell command không hợp lệ.");
            string cwd = string.IsNullOrWhiteSpace(call.Argument("cwd")) ? workspace : Resolve(call.Argument("cwd"));
            if (!Directory.Exists(cwd)) return Denied("cwd_missing", "Thư mục làm việc chưa tồn tại.");
            int seconds = 60;
            if (!string.IsNullOrEmpty(call.Argument("timeoutSeconds")) && (!int.TryParse(call.Argument("timeoutSeconds"), out seconds) || seconds < 1 || seconds > 120)) return Denied("invalid_timeout", "Timeout phải từ 1 đến 120 giây.");
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo { FileName = executable, Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; $global:LASTEXITCODE=0;\n" + command + "\nif ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }")),
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (string key in start.EnvironmentVariables.Keys.Cast<string>().ToArray())
                if (key.IndexOf("KEY", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("TOKEN", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("SECRET", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("PASSWORD", StringComparison.OrdinalIgnoreCase) >= 0) start.EnvironmentVariables.Remove(key);
            using (var process = new Process { StartInfo = start })
            {
                token.ThrowIfCancellationRequested(); process.Start();
                var output = Drain(process.StandardOutput); var error = Drain(process.StandardError);
                using (token.Register(() => Task.Run(() => KillOwned(process))))
                {
                    if (!process.WaitForExit(seconds * 1000)) { KillOwned(process); return Denied("timeout", "Đã dừng PowerShell vì quá thời gian. Một số thay đổi trước đó có thể đã xảy ra."); }
                    token.ThrowIfCancellationRequested();
                    Task.WaitAll(new Task[] { output, error }, 3000);
                    string stdout = output.IsCompleted ? output.Result : "[Output stream still open in a child process.]";
                    string stderr = error.IsCompleted ? error.Result : "[Error stream still open in a child process.]";
                    bool ok = process.ExitCode == 0;
                    return new MaoToolResult(ok, ok ? "completed" : "process_failed", "PowerShell exit=" + process.ExitCode + "\n" + stdout + (stderr.Length == 0 ? "" : "\nSTDERR:\n" + stderr));
                }
            }
        }
        private static async Task<string> Drain(StreamReader reader)
        {
            var result = new StringBuilder(); var buffer = new char[512]; int count; bool truncated = false;
            try { while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0) { int remaining = 4800 - result.Length; if (remaining > 0) result.Append(buffer, 0, Math.Min(count, remaining)); if (count > remaining) truncated = true; } }
            catch (ObjectDisposedException) { } catch (IOException) { }
            return result.ToString() + (truncated ? "\n[Output truncated.]" : "");
        }
        private static void KillOwned(Process process)
        {
            try {
                if (process.HasExited) return;
                using (var killer = Process.Start(new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"), Arguments = "/PID " + process.Id + " /T /F", UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true })) killer?.WaitForExit(5000);
                if (!process.HasExited) process.Kill();
            } catch (Exception) { }
        }
        private static MaoToolResult Ok(string text) => new MaoToolResult(true, "completed", text);
        private static MaoToolResult Denied(string code, string text) => new MaoToolResult(false, code, text);
    }
}
