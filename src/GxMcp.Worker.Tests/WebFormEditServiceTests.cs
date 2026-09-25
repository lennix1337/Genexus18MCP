using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Item 19 (mcp-improvements-2026-05-22) — semantic WebForm edits.
    /// Pure-XML mutation tests + service orchestration via a fake backend.
    /// </summary>
    public class WebFormEditServiceTests
    {
        private const string MinimalWebForm =
            "<GxMultiForm><Form type=\"layout\"><body><Table><TR><TD>" +
            "<gxTextBlock ControlName=\"Hello\" Caption=\"Hi\" /></TD></TR></Table></body></Form></GxMultiForm>";

        private const string FormWithButton =
            "<GxMultiForm><Form type=\"layout\"><body><Table><TR><TD>" +
            "<gxButton ControlName=\"BtnConfirm\" Caption=\"OK\" /></TD></TR></Table></body></Form></GxMultiForm>";

        private const string LegacyWebForm =
            "<BODY><TABLE id=\"Main\" align=\"center\"><TR><TD>" +
            "<gxButton id=\"btnA\" Event=\"'EventA'\" CaptionExpression=\"&lt;Tokens&gt;&lt;Token&gt;&lt;Type&gt;Constant&lt;/Type&gt;&lt;Data&gt;&lt;![CDATA[Label A]]&gt;&lt;/Data&gt;&lt;/Token&gt;&lt;/Tokens&gt;\" />" +
            "</TD></TR></TABLE></BODY>";

        private sealed class FakeBackend : WebFormEditService.IWebFormBackend
        {
            private int _versionSequence = 1;

            public string Xml { get; set; } = MinimalWebForm;
            public string LastWritten { get; private set; }
            public bool LastDryRun { get; private set; }
            public bool SimulateReadbackMismatch { get; set; }
            public bool SimulateIndependentEditAfterWrite { get; set; }
            public string XmlAfterWrite { get; set; }
            public JObject LastOptions { get; private set; }
            public string WriteResponse { get; set; }
            public Queue<string> WriteResponses { get; } = new Queue<string>();
            public List<JObject> WriteOptionsHistory { get; } = new List<JObject>();
            public int WriteCount { get; private set; }
            public int ReadCount { get; private set; }
            public bool ReturnReadEnvelope { get; set; }
            public string VersionToken => "v" + _versionSequence;
            public Exception ReadExceptionAfterFirstRead { get; set; }

            public string ReadWebFormXml(string target)
            {
                ReadCount++;
                if (ReadExceptionAfterFirstRead != null && ReadCount > 1)
                    throw ReadExceptionAfterFirstRead;

                if (!ReturnReadEnvelope) return Xml;
                return new JObject
                {
                    ["source"] = Xml,
                    ["truncated"] = false,
                    ["isTruncatedByWorker"] = false,
                    ["verificationSource"] = "fresh-sdk-read",
                    ["versionToken"] = VersionToken
                }.ToString(Newtonsoft.Json.Formatting.None);
            }

            public string WriteWebFormXml(string target, string xml, bool dryRun, JObject options)
            {
                WriteCount++;
                LastWritten = xml;
                LastDryRun = dryRun;
                LastOptions = options?.DeepClone() as JObject;
                WriteOptionsHistory.Add(LastOptions);
                if (!dryRun && !SimulateReadbackMismatch)
                {
                    Xml = xml;
                    _versionSequence++;
                    if (SimulateIndependentEditAfterWrite && !string.IsNullOrWhiteSpace(XmlAfterWrite))
                    {
                        Xml = XmlAfterWrite;
                        _versionSequence++;
                    }
                }
                if (WriteResponses.Count > 0) return WriteResponses.Dequeue();
                return WriteResponse ?? "{\"status\":\"Success\"}";
            }
        }

        // --- Pure XML mutation ---

        [Fact]
        public void DetectMarkupFlavor_DistinguishesBodyAndGxMultiForm()
        {
            Assert.Equal(
                WebFormXmlHelper.WebFormMarkupFlavor.LegacyHtml,
                WebFormXmlHelper.DetectMarkupFlavor(LegacyWebForm));
            Assert.Equal(
                WebFormXmlHelper.WebFormMarkupFlavor.ModernGxMultiForm,
                WebFormXmlHelper.DetectMarkupFlavor(MinimalWebForm));
            Assert.Equal(
                WebFormXmlHelper.WebFormMarkupFlavor.ModernGxMultiForm,
                WebFormXmlHelper.DetectMarkupFlavor(
                    "<GxMultiForm><Form><body><gxButton id=\"b\" /></body></Form></GxMultiForm>"));
        }

        [Fact]
        public void ApplyAction_AddTextBlock_AppendsElementAndAssignsId()
        {
            var added = new List<string>();
            var args = new JObject { ["caption"] = "Welcome" };
            string after = WebFormEditService.ApplyAction(MinimalWebForm, "add_textblock", args, null, added);
            var doc = XDocument.Parse(after);
            var blocks = doc.Descendants("gxTextBlock").ToList();
            Assert.Equal(2, blocks.Count);
            Assert.Equal("Welcome", (string)blocks.Last().Attribute("Caption"));
            Assert.Single(added);
        }

        [Fact]
        public void ApplyAction_AddButton_SetsOnClickEventDescriptor()
        {
            var added = new List<string>();
            var args = new JObject
            {
                ["caption"] = "Confirm",
                ["event"] = "Enter",
                ["controlId"] = "BtnConfirm2"
            };
            string after = WebFormEditService.ApplyAction(MinimalWebForm, "add_button", args, null, added);
            var btn = XDocument.Parse(after).Descendants("gxButton").Single();
            Assert.Equal("BtnConfirm2", (string)btn.Attribute("ControlName"));
            // Descriptor name is preserved; WebFormTypedPropertyWriter routes to canonical 'Event'.
            Assert.Equal("'Enter'", (string)btn.Attribute("OnClickEvent"));
            Assert.Contains("BtnConfirm2", added);
        }

        [Fact]
        public void ApplyAction_AddButton_LegacyBody_UsesCaptionTokensAndEventAttribute()
        {
            var args = new JObject
            {
                ["caption"] = "Events",
                ["event"] = "'EventB'",
                ["controlId"] = "btnB",
                ["position"] = "after:btnA"
            };

            string after = WebFormEditService.ApplyAction(LegacyWebForm, "add_button", args);
            var buttons = XDocument.Parse(after).Descendants("gxButton").ToList();
            var added = buttons.Single(e => (string)e.Attribute("id") == "btnB");
            var untouched = buttons.Single(e => (string)e.Attribute("id") == "btnA");

            Assert.Equal("'EventB'", (string)added.Attribute("Event"));
            Assert.Null(added.Attribute("OnClickEvent"));
            Assert.Null(added.Attribute("Caption"));
            Assert.Contains("<Tokens>", (string)added.Attribute("CaptionExpression"));
            Assert.Contains("Events", (string)added.Attribute("CaptionExpression"));
            Assert.Equal(
                "<Tokens><Token><Type>Constant</Type><Data><![CDATA[Label A]]></Data></Token></Tokens>",
                (string)untouched.Attribute("CaptionExpression"));
        }

        [Theory]
        [InlineData("EventB")]
        [InlineData("'EventB'")]
        public void ApplyAction_AddButton_NormalizesQuotedAndUnquotedEvents(string eventValue)
        {
            var args = new JObject
            {
                ["caption"] = "Events",
                ["event"] = eventValue,
                ["controlId"] = "btnB"
            };

            string after = WebFormEditService.ApplyAction(LegacyWebForm, "add_button", args);
            var added = XDocument.Parse(after).Descendants("gxButton")
                .Single(e => (string)e.Attribute("id") == "btnB");
            Assert.Equal("'EventB'", (string)added.Attribute("Event"));
        }

        [Fact]
        public void ApplyAction_AddTextBlock_LegacyBody_UsesCaptionTokens()
        {
            var args = new JObject
            {
                ["caption"] = "Events",
                ["controlId"] = "txtEvents"
            };

            string after = WebFormEditService.ApplyAction(LegacyWebForm, "add_textblock", args);
            var added = XDocument.Parse(after).Descendants("gxTextBlock")
                .Single(e => (string)e.Attribute("id") == "txtEvents");
            Assert.Null(added.Attribute("Caption"));
            Assert.Contains("<Tokens>", (string)added.Attribute("CaptionExpression"));
            Assert.Contains("Events", (string)added.Attribute("CaptionExpression"));
        }

        [Fact]
        public void ApplyAction_ExplicitControlIdCollision_IsRejected()
        {
            var args = new JObject
            {
                ["caption"] = "Events",
                ["controlId"] = "btnA"
            };

            var error = Assert.Throws<System.ArgumentException>(() =>
                WebFormEditService.ApplyAction(LegacyWebForm, "add_button", args));
            Assert.Contains("already exists", error.Message);
        }

        [Fact]
        public void ApplyAction_SetVisibility_SetsVisibleAttribute()
        {
            var args = new JObject { ["controlId"] = "BtnConfirm", ["visible"] = false };
            string after = WebFormEditService.ApplyAction(FormWithButton, "set_visibility", args);
            var btn = XDocument.Parse(after).Descendants("gxButton").Single();
            Assert.Equal("False", (string)btn.Attribute("Visible"));
        }

        [Fact]
        public void ApplyAction_RemoveControl_DropsElement()
        {
            var args = new JObject { ["controlId"] = "BtnConfirm" };
            string after = WebFormEditService.ApplyAction(FormWithButton, "remove_control", args);
            Assert.Empty(XDocument.Parse(after).Descendants("gxButton"));
        }

        [Fact]
        public void ApplyAction_WrapInFieldset_MovesControlsIntoFieldset()
        {
            string xml = "<Form><Body><Table>"
                + "<gxTextBlock ControlName=\"A\" />"
                + "<gxTextBlock ControlName=\"B\" />"
                + "<gxTextBlock ControlName=\"C\" />"
                + "</Table></Body></Form>";
            var args = new JObject
            {
                ["controlIds"] = new JArray("A", "B"),
                ["legend"] = "First two"
            };
            string after = WebFormEditService.ApplyAction(xml, "wrap_in_fieldset", args);
            var doc = XDocument.Parse(after);
            var fs = doc.Descendants("gxFieldSet").Single();
            Assert.Equal("First two", (string)fs.Attribute("Caption"));
            var inside = fs.Descendants("gxTextBlock").Select(e => (string)e.Attribute("ControlName")).ToList();
            Assert.Equal(new[] { "A", "B" }, inside);
            // C should remain a sibling at the Table level
            var siblings = doc.Descendants("Table").Single().Elements("gxTextBlock").Select(e => (string)e.Attribute("ControlName")).ToList();
            Assert.Single(siblings);
            Assert.Equal("C", siblings[0]);
        }

        [Fact]
        public void ApplyAction_SetVisibility_UnknownControl_Throws()
        {
            var args = new JObject { ["controlId"] = "Missing", ["visible"] = true };
            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                WebFormEditService.ApplyAction(MinimalWebForm, "set_visibility", args));
            Assert.Contains("not found", ex.Message);
        }

        [Fact]
        public void ApplyAction_UnknownAction_Throws()
        {
            Assert.Throws<System.ArgumentException>(() =>
                WebFormEditService.ApplyAction(MinimalWebForm, "do_magic", new JObject()));
        }

        // --- Service orchestration ---

        [Fact]
        public void Execute_AddTextBlock_RoutesThroughBackend()
        {
            var backend = new FakeBackend();
            var svc = new WebFormEditService(backend);
            var resp = JObject.Parse(svc.Execute("add_textblock", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "Hello there"
            }));
            Assert.Equal("ok", resp["status"]?.ToString());
            Assert.NotNull(backend.LastWritten);
            Assert.Contains("Hello there", backend.LastWritten);
            Assert.False(backend.LastDryRun);
        }

        [Fact]
        public void Execute_DryRun_PassesFlagThrough()
        {
            var backend = new FakeBackend();
            var svc = new WebFormEditService(backend);
            svc.Execute("add_textblock", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "X",
                ["dryRun"] = true
            });
            Assert.True(backend.LastDryRun);
        }

        [Fact]
        public void Execute_TruncatedBaselineReadFailsClosedBeforeWriting()
        {
            var backend = new FakeBackend
            {
                Xml = new JObject
                {
                    ["source"] = MinimalWebForm,
                    ["truncated"] = true,
                    ["isTruncatedByWorker"] = true,
                    ["offset"] = 0,
                    ["limit"] = 200,
                    ["totalLines"] = 800,
                    ["suggestedNextOffset"] = 200
                }.ToString(Newtonsoft.Json.Formatting.None)
            };

            var response = JObject.Parse(new WebFormEditService(backend).Execute("add_button", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "Events"
            }));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("WebFormReadTruncated", response["error"]?["code"]?.ToString());
            Assert.False(response["readEvidence"]?["readComplete"]?.ToObject<bool>());
            Assert.True(response["readEvidence"]?["truncated"]?.ToObject<bool>());
            Assert.Equal(200, response["readEvidence"]?["suggestedNextOffset"]?.ToObject<int>());
            Assert.Equal(0, backend.WriteCount);
        }

        [Fact]
        public void Execute_UnavailableBaselineReadReturnsStructuredEvidence()
        {
            var backend = new FakeBackend
            {
                Xml = new JObject
                {
                    ["status"] = "error",
                    ["error"] = new JObject
                    {
                        ["code"] = "FreshReadUnavailable",
                        ["message"] = "The SDK could not refresh the WebForm part."
                    }
                }.ToString(Newtonsoft.Json.Formatting.None)
            };

            var response = JObject.Parse(new WebFormEditService(backend).Execute("add_button", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "Events"
            }));

            Assert.Equal("WebFormReadUnavailable", response["error"]?["code"]?.ToString());
            Assert.Equal("FreshReadUnavailable", response["readEvidence"]?["errorCode"]?.ToString());
            Assert.False(response["readEvidence"]?["readComplete"]?.ToObject<bool>());
            Assert.Equal(0, backend.WriteCount);
        }

        [Fact]
        public void Execute_IndeterminateWriterErrorRequiresReadBackAndReconciliation()
        {
            var backend = new FakeBackend
            {
                ReturnReadEnvelope = true,
                ReadExceptionAfterFirstRead = new System.InvalidOperationException("verification read unavailable"),
                WriteResponse = new JObject
                {
                    ["status"] = "error",
                    ["error"] = new JObject
                    {
                        ["code"] = "WriteFailed",
                        ["message"] = "Writer failed after the save attempt."
                    },
                    ["saveAttempted"] = true,
                    ["persistenceState"] = "Indeterminate"
                }.ToString(Newtonsoft.Json.Formatting.None)
            };

            var response = JObject.Parse(new WebFormEditService(backend).Execute("add_button", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "Events",
                ["controlId"] = "BtnX"
            }));

            Assert.Equal("WebFormWriteIndeterminate", response["error"]?["code"]?.ToString());
            Assert.True(response["error"]?["reconciliationRequired"]?.ToObject<bool>());
            Assert.True(response["reconciliationRequired"]?.ToObject<bool>());
            Assert.True(response["reReadRequired"]?.ToObject<bool>());
            Assert.True(response["recoveryRequired"]?.ToObject<bool>());
            Assert.Equal("Indeterminate", response["persistenceState"]?.ToString());
            Assert.True(response["persisted"] == null || response["persisted"].Type == JTokenType.Null);
            Assert.False(response["rollbackEvidence"]?["attempted"]?.ToObject<bool>());
            Assert.Equal("VersionTokenUnavailable", response["rollbackEvidence"]?["reason"]?.ToString());
            Assert.Equal(1, backend.WriteCount);
        }

        [Fact]
        public void Execute_WriterErrorWithVersionUsesGuardedBaselineRestore()
        {
            var backend = new FakeBackend { ReturnReadEnvelope = true };
            backend.WriteResponses.Enqueue(new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "WriteFailed",
                    ["message"] = "Writer failed after committing the candidate."
                },
                ["persisted"] = true,
                ["commitState"] = "Committed",
                ["versionToken"] = "v2"
            }.ToString(Newtonsoft.Json.Formatting.None));

            var response = JObject.Parse(new WebFormEditService(backend).Execute("add_button", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "Events",
                ["controlId"] = "BtnX",
                ["rollbackOnFailure"] = true
            }));

            Assert.Equal("WebFormEditFailed", response["error"]?["code"]?.ToString());
            Assert.True(response["stateRestored"]?.ToObject<bool>());
            Assert.True(response["rollbackSucceeded"]?.ToObject<bool>());
            Assert.False(response["reconciliationRequired"]?.ToObject<bool>());
            Assert.Equal(2, backend.WriteCount);
            Assert.Equal("v2", backend.WriteOptionsHistory[1]?["baseVersion"]?.ToString());
            Assert.True(backend.WriteOptionsHistory[1]?["recoveryRestore"]?.ToObject<bool>());
        }

        [Fact]
        public void Execute_DivergentIndependentEditDefersRollbackAndPreservesNewerXml()
        {
            string independentXml = MinimalWebForm.Replace("Caption=\"Hi\"", "Caption=\"Independent\"");
            var backend = new FakeBackend
            {
                ReturnReadEnvelope = true,
                SimulateIndependentEditAfterWrite = true,
                XmlAfterWrite = independentXml
            };
            backend.WriteResponses.Enqueue(new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "WriteFailed",
                    ["message"] = "Writer reported failure after an indeterminate save."
                },
                ["persisted"] = true,
                ["commitState"] = "Committed",
                ["versionToken"] = "v2"
            }.ToString(Newtonsoft.Json.Formatting.None));

            var response = JObject.Parse(new WebFormEditService(backend).Execute("add_button", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "Events",
                ["controlId"] = "BtnX",
                ["rollbackOnFailure"] = true
            }));

            Assert.Equal("WebFormWriteIndeterminate", response["error"]?["code"]?.ToString());
            Assert.True(response["recoveryRequired"]?.ToObject<bool>());
            Assert.True(response["reconciliationRequired"]?.ToObject<bool>());
            Assert.True(response["rollbackDeferred"]?.ToObject<bool>());
            Assert.Equal("CurrentStateDiverged", response["rollbackEvidence"]?["reason"]?.ToString());
            Assert.Equal(1, backend.WriteCount);
            Assert.Equal(independentXml, backend.Xml);
        }

        [Fact]
        public void Execute_DryRun_OmitsFullWriteEchoAndBoundsDiff()
        {
            var backend = new FakeBackend
            {
                Xml = LegacyWebForm,
                WriteResponse = "{\"status\":\"Success\",\"source\":\"" + new string('x', 10000) + "\"}"
            };
            var svc = new WebFormEditService(backend);
            var response = JObject.Parse(svc.Execute("add_button", new JObject
            {
                ["name"] = "LegacyPanel",
                ["caption"] = "Events",
                ["controlId"] = "btnB",
                ["position"] = "after:btnA",
                ["dryRun"] = true
            }));

            Assert.Equal("ok", response["status"]?.ToString());
            var write = response["result"]?["write"] as JObject;
            Assert.NotNull(write);
            Assert.Null(write["source"]);
            Assert.True(write["sourceEchoOmitted"]?.Value<bool>());
            Assert.True(response["result"]?["xmlBeforeAfterDiff"]?.ToString().Length < 2500);
        }

        [Fact]
        public void Execute_ReadbackMismatchIsFailClosedAndDoesNotClaimSuccess()
        {
            var backend = new FakeBackend
            {
                Xml = MinimalWebForm,
                SimulateReadbackMismatch = true
            };
            var response = JObject.Parse(new WebFormEditService(backend).Execute("add_button", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "Events",
                ["controlId"] = "BtnX",
                ["rollbackOnFailure"] = true
            }));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("WebFormWriteVerificationFailed", response["error"]?["code"]?.ToString());
            Assert.True(response["persisted"]?.ToObject<bool>());
            Assert.False(response["verified"]?.ToObject<bool>());
            Assert.True(response["rollbackDeferred"]?.ToObject<bool>());
            Assert.True(backend.LastOptions?["rollbackOnFailure"]?.ToObject<bool>());
        }

        [Fact]
        public void Execute_DefaultRollbackIsForwardedToTheRealWriterContract()
        {
            var backend = new FakeBackend
            {
                Xml = MinimalWebForm,
                SimulateReadbackMismatch = true
            };
            var response = JObject.Parse(new WebFormEditService(backend).Execute("add_button", new JObject
            {
                ["name"] = "PanelX",
                ["caption"] = "Events",
                ["controlId"] = "BtnX"
            }));

            Assert.Equal("WebFormWriteVerificationFailed", response["error"]?["code"]?.ToString());
            Assert.True(backend.LastOptions?["rollbackOnFailure"]?.ToObject<bool>());
        }

        [Fact]
        public void Execute_MissingName_ReturnsStructuredError()
        {
            var backend = new FakeBackend();
            var svc = new WebFormEditService(backend);
            var resp = JObject.Parse(svc.Execute("add_button", new JObject { ["caption"] = "X" }));
            Assert.Equal("error", resp["status"]?.ToString());
            Assert.Equal("MissingName", resp["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Execute_ReadReturnsJsonEnvelope_ExtractsContent()
        {
            var backend = new FakeBackend
            {
                Xml = "{\"content\":\"" + MinimalWebForm.Replace("\"", "\\\"") + "\"}"
            };
            var svc = new WebFormEditService(backend);
            var resp = JObject.Parse(svc.Execute("add_textblock", new JObject
            {
                ["name"] = "Y",
                ["caption"] = "Z"
            }));
            Assert.Equal("ok", resp["status"]?.ToString());
            Assert.Contains("Z", backend.LastWritten);
        }
    }
}
