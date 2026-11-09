using MaoIntegration;
using NUnit.Framework;

namespace MaoIntegration.Tests
{
    public sealed class MaoHarnessPolicyTests
    {
        [Test]
        public void RoutesNaturalMultipleTargets()
        {
            Assert.IsTrue(MaoHarnessPolicy.ShouldRoute("Miku ơi mở Notepad và Calculator giúp anh"));
            Assert.IsTrue(MaoHarnessPolicy.ShouldRoute("Mở YouTube rồi vào GitHub nhé"));
        }

        [Test]
        public void CasualMentionsAndNegationsDoNotExecute()
        {
            Assert.IsFalse(MaoHarnessPolicy.ShouldRoute("Anh đang xem YouTube"));
            Assert.IsFalse(MaoHarnessPolicy.ShouldRoute("Anh đang mở Notepad"));
            Assert.IsFalse(MaoHarnessPolicy.ShouldRoute("Hôm qua em mở YouTube rồi"));
            Assert.IsFalse(MaoHarnessPolicy.ShouldRoute("giải thích cách mở Notepad"));
            Assert.IsTrue(MaoHarnessPolicy.ShouldRoute("Miku có thể mở YouTube giúp anh không"));
            Assert.IsTrue(MaoHarnessPolicy.ShouldRoute("Anh muốn em mở Notepad giúp anh"));
            Assert.IsFalse(MaoHarnessPolicy.ShouldRoute("đừng mở YouTube nhé"));
            Assert.IsFalse(MaoHarnessPolicy.ShouldRoute("giải thích cách mở Notepad"));
            Assert.IsFalse(MaoHarnessPolicy.ShouldRoute("chào Miku"));
            Assert.IsTrue(MaoHarnessPolicy.RequiresToolExecution("mở Notepad"));
            Assert.IsFalse(MaoHarnessPolicy.RequiresToolExecution("harness chat chào Miku"));
        }

        [Test]
        public void ControlsWorkWithoutSlashCommands()
        {
            Assert.AreEqual(MaoHarnessControl.Status, MaoHarnessPolicy.Control("harness đang chạy không"));
            Assert.AreEqual(MaoHarnessControl.Cancel, MaoHarnessPolicy.Control("Miku dừng tác vụ lại đi"));
            Assert.AreEqual(MaoHarnessControl.Enable, MaoHarnessPolicy.Control("bật harness"));
            Assert.AreEqual(MaoHarnessControl.Disable, MaoHarnessPolicy.Control("tắt harness"));
        }

        [Test]
        public void NotesRequireCurrentUserIntent()
        {
            Assert.IsTrue(MaoHarnessPolicy.CanUseNote("ghi lại một ghi chú về hôm nay", "save_note"));
            Assert.IsTrue(MaoHarnessPolicy.CanUseNote("xem danh sách ghi chú của anh", "list_notes"));
            Assert.IsTrue(MaoHarnessPolicy.CanUseNote("đọc ghi chú về hôm nay", "read_note"));
            Assert.IsFalse(MaoHarnessPolicy.CanUseNote("chào em", "save_note"));
            Assert.IsFalse(MaoHarnessPolicy.CanUseNote("đừng lưu ghi chú này", "save_note"));
            Assert.IsTrue(MaoHarnessPolicy.CanUseNote("ghi lại một ghi chú tên QA với nội dung Miku kết nối thành công", "save_note"));
            Assert.IsTrue(MaoHarnessPolicy.CanUseNote("cho Miku ghi lại một ghi chú về hôm nay", "save_note"));
            Assert.IsFalse(MaoHarnessPolicy.CanUseNote("don't read notes", "read_note"));
            Assert.IsFalse(MaoHarnessPolicy.CanUseNote("do not list notes", "list_notes"));
            Assert.IsFalse(MaoHarnessPolicy.CanUseNote("đừng đọc nội dung ghi chú", "read_note"));
            Assert.IsFalse(MaoHarnessPolicy.CanUseNote("dung doc noi dung ghi chu", "read_note"));
        }

        [Test]
        public void ListOnlyNoteRequestsCannotAuthorizeContentReads()
        {
            foreach (string input in new[]
            {
                "xem danh sách ghi chú của anh", "liệt kê ghi chú", "list notes",
                "read list notes", "tìm ghi chú về hôm nay"
            })
            {
                Assert.IsTrue(MaoHarnessPolicy.CanUseNote(input, "list_notes"), input);
                Assert.IsFalse(MaoHarnessPolicy.CanUseNote(input, "read_note"), input);
                Assert.IsFalse(MaoHarnessPolicy.CanUseNote(input, "save_note"), input);
            }
        }

        [Test]
        public void ExplicitNoteReadIntentDoesNotGrantWritePermission()
        {
            foreach (string input in new[]
            {
                "đọc ghi chú về hôm nay", "xem nội dung ghi chú hôm nay",
                "read the note about today", "list notes then read note A"
            })
            {
                Assert.IsTrue(MaoHarnessPolicy.CanUseNote(input, "read_note"), input);
                Assert.IsFalse(MaoHarnessPolicy.CanUseNote(input, "save_note"), input);
            }
        }

        [Test]
        public void TrialBuildCannotReplaceOfficialStartup()
        {
            const string install = @"C:\Users\Linh\AppData\Local\Programs\Mao Mate Engine\Mao Mate Engine.exe";
            Assert.IsTrue(MaoStartupPolicy.CanWrite(install, @"C:\Users\Linh\AppData\Local"));
            Assert.IsFalse(MaoStartupPolicy.CanWrite(@"C:\Trial\Mao Mate Engine.exe", @"C:\Users\Linh\AppData\Local"));
        }
    }
}
