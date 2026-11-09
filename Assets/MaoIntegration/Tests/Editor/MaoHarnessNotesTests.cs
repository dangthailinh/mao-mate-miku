using System;
using System.IO;
using MaoIntegration;
using NUnit.Framework;
using UnityEngine;

namespace MaoIntegration.Tests
{
    public sealed class MaoHarnessNotesTests
    {
        [Test]
        public void NotesPersistAndOnlyGuidReadsAreAccepted()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MaoHarnessTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "notes.json");
                var store = new MaoHarnessNotes(path);
                Assert.IsTrue(store.Execute("save_note", "Hôm nay", "Chào Miku!", null, out _));
                var data = JsonUtility.FromJson<MaoHarnessNoteCollection>(File.ReadAllText(path));
                Assert.IsTrue(new MaoHarnessNotes(path).Execute("read_note", null, null, data.notes[0].id, out string reply));
                StringAssert.Contains("Chào Miku!", reply);
                Assert.IsFalse(store.Execute("read_note", null, null, "../secrets", out _));
                Assert.IsTrue(store.Execute("save_note", "Ngày mai", "Việc cần làm", null, out _));
                Assert.IsTrue(File.Exists(path + ".backup"));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void OversizedNotesDoNotCreateOrOverwriteStore()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MaoHarnessTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "notes.json");
                var store = new MaoHarnessNotes(path);
                Assert.IsFalse(store.Execute("save_note", "Too long", new string('x', 8001), null, out _));
                Assert.IsFalse(File.Exists(path));
                Assert.IsTrue(store.Execute("save_note", "Keep", "original", null, out _));
                string before = File.ReadAllText(path);
                Assert.IsFalse(store.Execute("save_note", new string('x', 121), "bad", null, out _));
                Assert.AreEqual(before, File.ReadAllText(path));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
