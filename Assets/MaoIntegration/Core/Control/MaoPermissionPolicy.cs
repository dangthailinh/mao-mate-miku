using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MaoIntegration
{
    public enum MaoPermissionMode { ReadOnly, Ask, FullAccess }
    public sealed class MaoPermissionPolicy
    {
        public MaoPermissionMode Mode { get; private set; }
        public MaoToolCall Pending { get; private set; }
        public bool Rejected { get; private set; }
        private string approved;
        public MaoPermissionPolicy(MaoPermissionMode mode = MaoPermissionMode.Ask) { SetMode(mode); }
        public string WireMode => Mode == MaoPermissionMode.FullAccess ? "full-access" : Mode == MaoPermissionMode.Ask ? "ask" : "read-only";
        public int Budget => Mode == MaoPermissionMode.FullAccess ? 32 : Mode == MaoPermissionMode.Ask ? 16 : 6;
        public void SetMode(MaoPermissionMode mode)
        { Mode = Enum.IsDefined(typeof(MaoPermissionMode), mode) ? mode : MaoPermissionMode.ReadOnly; Clear(); }
        public void Clear() { Pending = null; approved = null; Rejected = false; }
        public static bool DeadlineOpen(long expiresAtUnixMs, long nowUnixMs) => expiresAtUnixMs > nowUnixMs;
        public bool Request(MaoToolCall call)
        {
            if (Mode != MaoPermissionMode.Ask || Pending != null || call == null) return false;
            Pending = call; approved = null; Rejected = false; return true;
        }
        public bool Decide(string callId, bool allow)
        {
            if (Pending == null || Pending.CallId != callId || approved != null || Rejected) return false;
            if (allow) approved = Fingerprint(Pending); else Rejected = true;
            return true;
        }
        public bool IsApproved(MaoToolCall call) => call != null && approved != null && approved == Fingerprint(call);
        public bool Authorize(MaoToolCall call, MaoToolRisk risk)
        {
            if (call == null || !Enum.IsDefined(typeof(MaoToolRisk), risk)) return false;
            if (Mode == MaoPermissionMode.FullAccess || risk == MaoToolRisk.ReadOnly) return true;
            if (Mode != MaoPermissionMode.Ask || !IsApproved(call)) return false;
            Clear(); return true;
        }
        private static string Fingerprint(MaoToolCall call)
        {
            var value = new StringBuilder();
            foreach (string part in new[] { call.TaskId, call.CallId, call.Tool, call.Target, call.UserRequest }
                .Concat(call.Arguments.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => new[] { p.Key, p.Value })))
            { string text = part ?? string.Empty; value.Append(text.Length).Append(':').Append(text); }
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(value.ToString())));
        }
        public static string Control(string input)
        {
            string text = MaoHarnessPolicy.Normalize(input).Trim(' ', '/', '.', '!', '?');
            text = Regex.Replace(text, @"^miku(?:\s+oi)?\s*[,!:]?\s*", "");
            if (text == "permissions" || text == "permissions status" || text == "quyen hien tai" || text == "miku co quyen gi") return "status";
            if (text == "permissions full" || text == "permissions full-access" || Regex.IsMatch(text, @"^(?:cap|bat)\s+(?:full quyen|full access)(?:\s+cho miku)?(?:\s+di)?$")) return "full";
            if (text == "permissions ask" || text == "hoi truoc khi chay" || text == "bat che do hoi truoc") return "ask";
            if (text == "permissions read-only" || text == "chi doc thoi" || text == "thu hoi full quyen" || text == "tat full access") return "read-only";
            if (text == "cho phep lan nay" || text == "cho phep" || text == "dong y buoc nay" || text == "xac nhan buoc nay" || text == "permissions allow") return "allow";
            if (text == "tu choi" || text == "permissions deny") return "deny";
            if (text.StartsWith("permissions allow ")) return "allow:" + text.Substring(18);
            if (text.StartsWith("permissions deny ")) return "deny:" + text.Substring(17);
            return null;
        }
    }
}
