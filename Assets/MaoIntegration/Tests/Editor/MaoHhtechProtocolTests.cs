using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using MaoIntegration;
using NUnit.Framework;
using UnityEngine;

namespace MaoIntegration.Tests
{
    public sealed class MaoHhtechProtocolTests
    {
        [Test]
        public void HhtechCredentialsNeverBorrowAnotherProvidersKey()
        {
            const string otherKeys = "TOKENROUTER_API_KEY=router-test\nDEEPSEEK_API_KEY=deepseek-test\nGEMINI_API_KEY=google-test";
            Assert.AreEqual(string.Empty, ParseKey(otherKeys, null));
            Assert.AreEqual("hhtech-test", ParseKey(otherKeys + "\nHHTECH_API_KEY=\"hhtech-test\"", null));
        }

        [Test]
        public void NamedProcessCredentialOverridesTheLocalFile()
        {
            Assert.AreEqual("process-test", ParseKey("HHTECH_API_KEY=file-test", "process-test"));
        }

        [Test]
        public void InvalidCredentialsCannotEnterAuthorizationHeaders()
        {
            Assert.AreEqual(string.Empty, ParseKey(null, "bad\r\nheader"));
            Assert.AreEqual(string.Empty, ParseKey(null, new string('x', 4097)));
            Assert.AreEqual(string.Empty, ParseKey("HHTECH_API_KEY=", null));
        }

        [Test]
        public void RequestUsesStructuredModelOverrideWithoutChangingUserText()
        {
            const string content = "Keep this exact text: \"model\":\"qwen/qwen3.8-max-free\".";
            var turns = new[] { new MaoChatTurn("user", content), new MaoChatTurn("assistant", "Miku.") };
            string json = (string)Call("MaoHhtechProtocol", "BuildRequestJson", "You are Miku.", turns);
            RequestShape request = JsonUtility.FromJson<RequestShape>(json);
            Assert.AreEqual("deepseek-v4", request.model);
            Assert.IsFalse(request.stream);
            Assert.AreEqual(4096, request.max_tokens);
            Assert.AreEqual(content, request.messages[1].content);
            Assert.AreEqual("assistant", request.messages[2].role);
            StringAssert.DoesNotContain("hhtech-test", json);
        }

        [Test]
        public void ReplyUsesOnlyVisibleContentNeverReasoning()
        {
            MaoGeminiReply reply = ParseReply("{\"choices\":[{\"message\":{\"content\":\" Chào anh! \",\"reasoning_content\":\"private reasoning\"},\"finish_reason\":\"stop\"}]}");
            Assert.AreEqual("Chào anh!", reply.Text);
            Assert.AreEqual("stop", reply.FinishReason);
        }

        [Test]
        public void BalancedReasoningWrappersNeverReachVisibleReply()
        {
            foreach (string tag in new[] { "think", "analysis", "reasoning" })
            {
                string json = "{\"choices\":[{\"message\":{\"content\":\"<" + tag + ">PRIVATE_REASONING_TEST</" + tag + "> Chào anh!\"},\"finish_reason\":\"stop\"}]}";
                Assert.AreEqual("Chào anh!", ParseReply(json).Text);
            }
        }

        [Test]
        public void UnbalancedOrOnlyReasoningFailsClosed()
        {
            foreach (string content in new[] { "<think>PRIVATE_REASONING_TEST", "PRIVATE_REASONING_TEST</think>", "<think>PRIVATE_REASONING_TEST</think>", "<think>outer<think>inner</think>outer</think>" })
                Assert.Throws<FormatException>(() => ParseReply("{\"choices\":[{\"message\":{\"content\":\"" + content + "\"}}]}"));
        }

        [Test]
        public void UnclosedNestedWrappersCannotExposePrivateTail()
        {
            foreach (string outer in new[] { "think", "analysis", "reasoning" })
                foreach (string inner in new[] { "think", "analysis", "reasoning" })
                {
                    string content = "<" + outer + ">outer <" + inner + ">PRIVATE_INNER</" + outer + ">PRIVATE_TAIL";
                    Assert.Throws<FormatException>(() => ParseReply("{\"choices\":[{\"message\":{\"content\":\"" + content + "\"}}]}"));
                }
        }

        [Test]
        public void MismatchedOrBalancedNestedWrappersFailBeforeRemoval()
        {
            foreach (string content in new[]
            {
                "<think><analysis>PRIVATE</reasoning></think>Visible",
                "<think><analysis>PRIVATE</analysis></think>Visible",
                "<analysis><reasoning>PRIVATE</think></analysis>Visible",
                "<think broken <analysis>PRIVATE</analysis>Visible"
            })
                Assert.Throws<FormatException>(() => ParseReply("{\"choices\":[{\"message\":{\"content\":\"" + content + "\"}}]}"));
        }

        [Test]
        public void SeparateBalancedWrappersPreserveOnlyVisibleText()
        {
            const string content = "<THINK>PRIVATE_1</THINK>Chào anh!< analysis mode='test'>PRIVATE_2</ analysis > Miku đây.";
            MaoGeminiReply reply = ParseReply("{\"choices\":[{\"message\":{\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}");
            Assert.AreEqual("Chào anh!  Miku đây.", reply.Text);
        }

        [Test]
        public void RemovingWrappersCannotReconstructReasoningMarkers()
        {
            foreach (string content in new[]
            {
                "<<think>PRIVATE</think>analysis>PRIVATE_TAIL",
                "<<analysis>PRIVATE</analysis>/think>Visible"
            })
                Assert.Throws<FormatException>(() => ParseReply("{\"choices\":[{\"message\":{\"content\":\"" + content + "\"}}]}"));
        }

        [Test]
        public void IsoTimestampStringsRemainExactVisibleStrings()
        {
            const string timestamp = "2026-10-07T00:00:00Z";
            Assert.AreEqual(timestamp, ParseReply("{\"choices\":[{\"message\":{\"content\":\"" + timestamp + "\"},\"finish_reason\":\"stop\"}]}").Text);
        }

        [Test]
        public void ExtraJsonObjectsCannotBeIgnoredAfterAValidResponse()
        {
            Assert.Throws<FormatException>(() => ParseReply("{\"choices\":[{\"message\":{\"content\":\"Hello\"}}]} {}"));
        }

        [Test]
        public void OutputLimitFinishReasonIsPreservedForExistingContinuationPolicy()
        {
            MaoGeminiReply reply = ParseReply("{\"choices\":[{\"message\":{\"content\":\"More to say\"},\"finish_reason\":\"length\"}]}");
            Assert.IsTrue(MaoTextBackendPolicy.RequiresContinuation(reply.FinishReason));
        }

        [Test]
        public void ProviderErrorsAreGenericAndNeverEchoResponseData()
        {
            FormatException error = Assert.Throws<FormatException>(() => ParseReply("{\"error\":{\"message\":\"secret-test-provider-response\"}}"));
            StringAssert.DoesNotContain("secret-test-provider-response", error.Message);
            Assert.Less(error.Message.Length, 160);
        }

        [Test]
        public void ReasoningOnlyAndMalformedContentCannotBecomeReplies()
        {
            foreach (string json in new[] { "{}", "[]", "not-json", "{\"choices\":[{\"message\":{\"reasoning_content\":\"thinking only\"}}]}", "{\"choices\":[{\"message\":{\"content\":42}}]}" })
                Assert.Throws<FormatException>(() => ParseReply(json));
        }

        [Test]
        public void ResponseAndVisibleReplyHaveFiniteBounds()
        {
            Assert.Throws<FormatException>(() => ParseReply(new string('x', 1024 * 1024 + 1)));
            Assert.Throws<FormatException>(() => ParseReply("{\"choices\":[{\"message\":{\"content\":\"" + new string('x', 12001) + "\"}}]}"));
        }

        [Test]
        public void TextPrefersHhtechWhileImagesKeepGeminiAndExistingFallbacks()
        {
            CollectionAssert.AreEqual(new[] { "Hhtech", "Qwen", "Gemini" }, Order(true, true, true, false));
            CollectionAssert.AreEqual(new[] { "Gemini", "Hhtech", "Qwen" }, Order(true, true, true, true));
            CollectionAssert.AreEqual(new[] { "Qwen", "Gemini" }, Order(false, true, true, false));
            CollectionAssert.AreEqual(new[] { "Hhtech", "Qwen" }, Order(true, true, false, true));
            Assert.IsEmpty(Order(false, false, false, false));
        }

        [Test]
        public void EndpointModelAndCredentialNameAreFixedWithBoundedTimeout()
        {
            Type protocol = Contract("MaoHhtechProtocol");
            Assert.AreEqual("https://hhtechapi.net/v1/chat/completions", protocol.GetField("Endpoint").GetRawConstantValue());
            Assert.AreEqual("deepseek-v4", protocol.GetField("Model").GetRawConstantValue());
            Assert.AreEqual("HHTECH_API_KEY", protocol.GetField("ApiKeyEnvironmentVariable").GetRawConstantValue());
            Assert.AreEqual(45, protocol.GetField("RequestTimeoutSeconds").GetRawConstantValue());
        }

        private static string ParseKey(string envText, string processKey) =>
            (string)Call("MaoHhtechProtocol", "ParseApiKey", envText, processKey);
        private static MaoGeminiReply ParseReply(string json) =>
            (MaoGeminiReply)Call("MaoHhtechProtocol", "ParseResponse", json);
        private static string[] Order(bool hhtech, bool qwen, bool gemini, bool image) =>
            ((IEnumerable)Call("MaoTextProviderPolicy", "GetAttemptOrder", hhtech, qwen, gemini, image))
                .Cast<object>().Select(value => value.ToString()).ToArray();

        [Serializable]
        private sealed class RequestShape
        {
            public string model;
            public bool stream;
            public int max_tokens;
            public MessageShape[] messages;
        }

        [Serializable]
        private sealed class MessageShape
        {
            public string role;
            public string content;
        }

        // Reflection keeps the RED contract tests compilable before the new core types exist.
        private static Type Contract(string name)
        {
            string qualified = "MaoIntegration." + name;
            Type type = typeof(MaoGeminiReply).Assembly.GetType(qualified) ?? typeof(MaoHhtechProtocolTests).Assembly.GetType(qualified);
            Assert.IsNotNull(type, "Missing provider contract: " + qualified);
            return type;
        }

        private static object Call(string name, string method, params object[] arguments)
        {
            try { return Contract(name).GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, arguments); }
            catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
        }
    }
}
