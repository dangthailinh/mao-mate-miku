using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MaoIntegration
{
    [Serializable]
    public sealed class MaoHarnessNote { public string id, title, content, createdUtc; }
    [Serializable]
    public sealed class MaoHarnessNoteCollection { public List<MaoHarnessNote> notes = new List<MaoHarnessNote>(); }

    public sealed class MaoHarnessNotes
    {
        private readonly string path;
        public MaoHarnessNotes(string path) { this.path = path; }

        public bool Execute(string kind, string title, string content, string id, out string reply)
        {
            reply = "Miku chưa xử lý được ghi chú.";
            var data = Load();
            if (kind == "list_notes")
            {
                reply = data.notes.Count == 0 ? "Chưa có ghi chú nào." : string.Join("\n", data.notes.TakeLast(20).Select(n => n.id + ": " + n.title));
                return true;
            }
            if (kind == "read_note")
            {
                if (!Guid.TryParse(id, out _)) return false;
                var note = data.notes.FirstOrDefault(n => n.id == id);
                if (note == null) { reply = "Miku không tìm thấy ghi chú đó."; return false; }
                reply = note.title + "\n" + note.content; return true;
            }
            if (kind != "save_note" || string.IsNullOrWhiteSpace(title) || title.Length > 120 ||
                string.IsNullOrWhiteSpace(content) || content.Length > 8000) return false;
            if (data.notes.Count >= 100) { reply = "Đã có 100 ghi chú; Miku chưa lưu thêm để tránh đầy bộ nhớ."; return false; }
            data.notes.Add(new MaoHarnessNote { id = Guid.NewGuid().ToString(), title = title.Trim(), content = content.Trim(), createdUtc = DateTime.UtcNow.ToString("o") });
            string json = JsonUtility.ToJson(data, true);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 512 * 1024) { reply = "Kho ghi chú đã đầy."; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            if (File.Exists(path)) File.Replace(temporary, path, path + ".backup"); else File.Move(temporary, path);
            reply = "Đã lưu ghi chú “" + title.Trim() + "”."; return true;
        }

        private MaoHarnessNoteCollection Load()
        {
            if (!File.Exists(path)) return new MaoHarnessNoteCollection();
            if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("Note store exceeds size limit.");
            var data = JsonUtility.FromJson<MaoHarnessNoteCollection>(File.ReadAllText(path));
            if (data == null || data.notes == null || data.notes.Count > 100) throw new InvalidDataException("Invalid note store.");
            if (data.notes.Any(n => n == null || !Guid.TryParse(n.id, out _) || n.title == null || n.title.Length > 120 || n.content == null || n.content.Length > 8000))
                throw new InvalidDataException("Invalid note record.");
            return data;
        }
    }
}
