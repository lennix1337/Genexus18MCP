using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using static GxMcp.Worker.Tests.IsolatedEventsSaveFlowTests;

namespace GxMcp.Worker.Tests
{
    public class PatternProjectionWriteFlowTests
    {
        private const string NativeProjectionForm =
            "<GxMultiForm><Form id='1' type='layout'><detail PanelType='Web'><layout id='11111111-1111-1111-1111-111111111111'>"
            + "<table id='22222222-2222-2222-2222-222222222222' controlName='Main' tableType='Responsive' class='77777777-7777-7777-7777-777777777777-1106'>"
            + "<row id='33333333-3333-3333-3333-333333333333'><cell id='44444444-4444-4444-4444-444444444444'>"
            + "<data id='55555555-5555-5555-5555-555555555555' attribute='att:7' businessGuid='66666666-6666-6666-6666-666666666666'/>"
            + "<action controlName='BtnSave' onClickEvent='DoSave'/></cell></row></table></layout></detail></Form></GxMultiForm>";

        private const string NativeTabMarkup =
            "<ucw gxControlType='42'><table><row><cell><tab id='88888888-8888-8888-8888-888888888888' controlName='Tabs'>"
            + "<item id='99999999-9999-9999-9999-999999999999' itemControlName='General' caption='General'>"
            + "<table><row><cell><action controlName='BtnTab' onClickEvent='TabSelected'/></cell></row></table></item>"
            + "<item id='eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee' itemControlName='Audit' caption='Audit'><table/></item>"
            + "</tab></cell></row></table></ucw>";

        private static string NativeTabForm() => NativeProjectionForm.Replace(
            "<action controlName='BtnSave' onClickEvent='DoSave'/>", NativeTabMarkup);

        private static string RecreatedTabIds(string xml) => xml
            .Replace("id='88888888-8888-8888-8888-888888888888'", "id='ffffffff-ffff-ffff-ffff-ffffffffffff'")
            .Replace("id='99999999-9999-9999-9999-999999999999'", "id='12121212-1212-1212-1212-121212121212'")
            .Replace("id='eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee'", "id='13131313-1313-1313-1313-131313131313'");

        private static string RecreatedLayoutIds(string xml) => xml
            .Replace("11111111-1111-1111-1111-111111111111", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
            .Replace("22222222-2222-2222-2222-222222222222", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
            .Replace("33333333-3333-3333-3333-333333333333", "cccccccc-cccc-cccc-cccc-cccccccccccc")
            .Replace("44444444-4444-4444-4444-444444444444", "dddddddd-dddd-dddd-dddd-dddddddddddd");

        private static JObject Receipt() => new JObject
        {
            ["saveContract"] = PatternProjectionWriteFlow.SaveContract,
            ["commitCompleted"] = false, ["transactionRolledBack"] = false
        };

        [Fact]
        public void ProjectionFailureNeverCommitsAndRollbackRequiresIndependentEvidence()
        {
            int host = 0, parent = 0;
            var calls = new List<string>();
            var receipt = Receipt();
            Exception error = PatternProjectionWriteFlow.Run(receipt,
                () => { calls.Add("save"); host = 1; parent = 1; throw new Exception("parent save refused"); },
                () => calls.Add("verify"), () => calls.Add("commit"),
                () => { calls.Add("rollback"); host = 0; parent = 0; });
            Assert.Equal("parent save refused", error.Message);
            Assert.Equal(new[] { "save", "rollback" }, calls);
            Assert.False(receipt["commitCompleted"].Value<bool>());
            Assert.Null(receipt["rollbackVerified"]);
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, host == 0, parent == 0, null, null, false));
            Assert.True(receipt["rollbackVerified"].Value<bool>());
            Assert.False(receipt["persisted"].Value<bool>());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CallbackPersistenceOrFailedRollbackIsNeverReportedAsRestored(bool rollbackThrows)
        {
            var receipt = Receipt();
            PatternProjectionWriteFlow.Run(receipt,
                () => throw new Exception("projection failed"), () => { }, () => { },
                () => { if (rollbackThrows) throw new Exception("rollback failed"); });
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, false, true, null, null, false));
            Assert.False(receipt["rollbackVerified"].Value<bool>());
            // The write was never confirmed by a re-read: persisted must not claim it. The partial write is
            // reported by partialPersistenceDetected instead.
            Assert.False(receipt["persisted"].Value<bool>());
            Assert.True(receipt["partialPersistenceDetected"].Value<bool>());
            Assert.Equal(rollbackThrows, receipt["rollbackError"] != null);
        }

        [Theory]
        [InlineData("verify")]
        [InlineData("commit")]
        public void VerificationAndCommitFailuresBothRollback(string failure)
        {
            var calls = new List<string>();
            var receipt = Receipt();
            var error = PatternProjectionWriteFlow.Run(receipt,
                () => calls.Add("save"),
                () => { calls.Add("verify"); if (failure == "verify") throw new Exception("mismatch"); },
                () => { calls.Add("commit"); throw new Exception("commit refused"); },
                () => calls.Add("rollback"));
            Assert.NotNull(error);
            Assert.Equal("rollback", calls[calls.Count - 1]);
            Assert.Equal(failure == "commit", calls.Contains("commit"));
            Assert.False(receipt["commitCompleted"].Value<bool>());
        }

        [Fact]
        public void SuccessCommitsOnlyAfterVerificationAndNeedsBothProjectedObjectsOnReadback()
        {
            var calls = new List<string>();
            var receipt = Receipt();
            Assert.Null(PatternProjectionWriteFlow.Run(receipt,
                () => calls.Add("save-project"), () => calls.Add("verify"),
                () => calls.Add("commit"), () => calls.Add("rollback")));
            Assert.Equal(new[] { "save-project", "verify", "commit" }, calls);
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, false, false, true, false, true));
            Assert.True(PatternProjectionWriteFlow.ConfirmReadback(receipt, false, false, true, true, true));
            Assert.True(receipt["verified"].Value<bool>());
        }

        [Fact]
        public void VerifiedProjectionReportsTheReReadTokenAndPersistence()
        {
            var receipt = Receipt();
            Assert.Null(PatternProjectionWriteFlow.Run(receipt, () => { }, () => { }, () => { }, () => { }));
            string freshVersion = "fresh-version";
            receipt["versionToken"] = freshVersion;
            Assert.True(PatternProjectionWriteFlow.ConfirmReadback(receipt, false, false, true, true, true));
            Assert.True(receipt["postSaveVerification"]["reReadConfirmed"].Value<bool>());
            Assert.Equal(freshVersion, (string)receipt["postSaveVerification"]["versionToken"]);
            Assert.True(receipt["persistedVerified"].Value<bool>());
            Assert.True(receipt["persisted"].Value<bool>());
            Assert.True(receipt["saved"].Value<bool>());
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void DivergentReReadNeverClaimsConfirmation(bool parentMatches, bool patternMatches)
        {
            var receipt = Receipt();
            Assert.Null(PatternProjectionWriteFlow.Run(receipt, () => { }, () => { }, () => { }, () => { }));
            string freshVersion = "fresh-version";
            receipt["versionToken"] = freshVersion;
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, false, false, true, parentMatches, patternMatches));
            Assert.False(receipt["postSaveVerification"]["reReadConfirmed"].Value<bool>());
            Assert.False(string.IsNullOrEmpty((string)receipt["postSaveVerification"]["reason"]));
            Assert.Null(receipt["postSaveVerification"]["versionToken"]);
            Assert.False(receipt["persistedVerified"].Value<bool>());
            Assert.False(receipt["persisted"].Value<bool>());
            Assert.True(receipt["partialPersistenceDetected"].Value<bool>());
        }

        [Fact]
        public void CommittedProjectionKeepsSavedTrueWhenTheReReadDiverges()
        {
            // saved/sdkSaveCompleted describe the physical save independently of the verification outcome.
            var receipt = Receipt();
            Assert.Null(PatternProjectionWriteFlow.Run(receipt,
                () => { receipt["sdkSaveCompleted"] = true; }, () => { }, () => { }, () => { }));
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, false, false, true, false, true));
            Assert.True(receipt["commitCompleted"].Value<bool>());
            Assert.True(receipt["saved"].Value<bool>());
            Assert.False(receipt["persisted"].Value<bool>());
            Assert.False(receipt["verified"].Value<bool>());
            Assert.True(receipt["partialPersistenceDetected"].Value<bool>());
        }

        [Fact]
        public void RolledBackProjectionNeverReportsSaved()
        {
            // The SDK save ran inside the transaction, but the commit never happened.
            var receipt = Receipt();
            Assert.NotNull(PatternProjectionWriteFlow.Run(receipt,
                () => { receipt["sdkSaveCompleted"] = true; throw new Exception("parent save refused"); },
                () => { }, () => { }, () => { }));
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, true, true, null, null, false));
            Assert.False(receipt["commitCompleted"].Value<bool>());
            Assert.False(receipt["saved"].Value<bool>());
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void ProjectionReceiptWithoutAReReadIsClassifiedWithoutLosingTheCommit(bool saves, bool dirty)
        {
            var receipt = Receipt();
            Exception failure = PatternProjectionWriteFlow.Run(receipt,
                () => { if (saves) receipt["sdkSaveCompleted"] = true; else throw new Exception("boundary changed"); },
                () => { }, () => { }, () => { });
            Assert.Equal(!saves, failure != null);
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, null, null, null, null, null));
            string error = GxMcp.Worker.Models.McpResponse.Err(code: "PatternProjectionFailed", message: "no re-read",
                target: "Panel", extra: receipt);
            var writer = FormatterServices.GetUninitializedObject(typeof(WriteService));
            var wrap = typeof(WriteService).GetMethod("WrapWithPersistedState", BindingFlags.Instance | BindingFlags.NonPublic);
            string wrapped = (string)wrap.Invoke(writer, new object[]
                { error, "Panel", "PatternInstance", null, "before", "after", null, null, false });
            Assert.Equal(JTokenType.Null, JObject.Parse(wrapped)["persisted"].Type);
            Assert.Equal(dirty, WriteService.ShouldMarkTargetDirty(wrapped));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FailureBeforeCommitNeverClaimsPersistence(bool hostRestored)
        {
            var receipt = Receipt();
            Assert.NotNull(PatternProjectionWriteFlow.Run(receipt,
                () => throw new Exception("projection failed"), () => { }, () => { }, () => { }));
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, hostRestored, true, null, null, null));
            Assert.False(receipt["postSaveVerification"]["reReadConfirmed"].Value<bool>());
            Assert.False(receipt["persistedVerified"].Value<bool>());
            Assert.False(receipt["persisted"].Value<bool>());
            Assert.Equal(!hostRestored, receipt["partialPersistenceDetected"].Value<bool>());
        }

        [Fact]
        public void MissingReadbackRemainsUnknownEvenWhenRollbackReturned()
        {
            var receipt = Receipt();
            receipt["transactionRolledBack"] = true;
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, null, true, null, null, null));
            Assert.False(receipt["persistedStateKnown"].Value<bool>());
            Assert.Equal(JTokenType.Null, receipt["persisted"].Type);
            Assert.False(receipt["rollbackVerified"].Value<bool>());
        }

        [Theory]
        [InlineData("PatternInstance", "<Instance controlType='DVelop Combo' />", "<Instance controlType='Dynamic Combo Box' />")]
        [InlineData("WebForm", "<Root><Control type='DVelop' /></Root>", "<Root><Control type='ComboBox' /></Root>")]
        public void ChangedEditableXmlCannotBeReportedAsRestoredWhenNativeSnapshotMatches(string part, string before, string after)
        {
            var xml = WriteService.CompareProjectionXml(before, after);
            var state = WriteService.ProjectionComparison(ObjectMoveSnapshot.Comparison.Verified("native-hash"), xml, part);
            Assert.True(state["objectSnapshotEqual"].Value<bool>());
            Assert.False(state["equal"].Value<bool>());
            Assert.Contains(part, state["changedParts"].Values<string>());
            Assert.False(xml["equal"].Value<bool>());
            Assert.False(string.IsNullOrWhiteSpace(xml["difference"].Value<string>()));
            Assert.NotEqual(xml["expectedHash"].Value<string>(), xml["persistedHash"].Value<string>());
            Assert.Equal(after.Length, xml["persistedLength"].Value<int>());

            var receipt = Receipt();
            receipt["transactionRolledBack"] = true;
            bool hostRestored = part == "PatternInstance" ? state["equal"].Value<bool>() : true;
            bool parentRestored = part == "WebForm" ? state["equal"].Value<bool>() : true;
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, hostRestored, parentRestored, null, null, false));
            Assert.False(receipt["rollbackVerified"].Value<bool>());
            Assert.True(receipt["partialPersistenceDetected"].Value<bool>());
        }

        [Fact]
        public void MissingEditableXmlNeverVerifiesAndEquivalentXmlKeepsBothHashes()
        {
            Assert.False(WriteService.CompareProjectionXml(null, null)["equal"].Value<bool>());
            Assert.True(WriteService.CompareProjectionXml(null, null, allowAbsent: true, projectedWebForm: true)["equal"].Value<bool>());
            var xml = WriteService.CompareProjectionXml("<Instance a='1' b='2'/>", "<Instance b='2' a='1' />");
            Assert.True(xml["equal"].Value<bool>());
            Assert.NotEqual(xml["expectedHash"].Value<string>(), xml["persistedHash"].Value<string>());
            Assert.True(WriteService.ProjectionComparison(ObjectMoveSnapshot.Comparison.Verified("native-hash"), xml, "PatternInstance")["equal"].Value<bool>());
        }

        [Fact]
        public void NonVisualParentsCompareAbsenceWithoutAcceptingOneSidedOrUnreadableXml()
        {
            var absent = WriteService.CompareProjectionXml(null, null, allowAbsent: true);
            Assert.True(absent["equal"].Value<bool>());
            Assert.False(absent["expectedPresent"].Value<bool>());
            Assert.False(absent["persistedPresent"].Value<bool>());
            Assert.False(WriteService.CompareProjectionXml(null, "<Root/>", allowAbsent: true)["equal"].Value<bool>());
            Assert.False(WriteService.CompareProjectionXml("<Root/>", null, allowAbsent: true)["equal"].Value<bool>());
            Assert.False(WriteService.CompareProjectionXml("", "", allowAbsent: true)["equal"].Value<bool>());
            Assert.False(WriteService.CompareProjectionXml(null, null)["equal"].Value<bool>());
        }

        [Fact]
        public void ReportSubsetWriteToleranceCannotHideLostVisualContent()
        {
            const string full = "<Report><PrintBlock Name='Header'/><PrintBlock Name='Footer'/></Report>";
            const string lost = "<Report><PrintBlock Name='Header'/></Report>";
            Assert.True(XmlEquivalence.AreEquivalent(full, lost, out _));
            Assert.False(WriteService.CompareProjectionXml(full, lost, allowAbsent: true)["equal"].Value<bool>());
            Assert.False(WriteService.CompareProjectionXml(lost, full, allowAbsent: true)["equal"].Value<bool>());
            Assert.False(WriteService.CompareProjectionXml(full, lost, projectedWebForm: true)["equal"].Value<bool>());
            Assert.False(WriteService.CompareProjectionXml(lost, full, projectedWebForm: true)["equal"].Value<bool>());
        }

        [Fact]
        public void ProjectedWebFormAcceptsOnlyStructuralIdRecreationAndKeepsRawHashes()
        {
            string fresh = RecreatedLayoutIds(NativeProjectionForm);
            var projected = WriteService.CompareProjectionXml(NativeProjectionForm, fresh,
                allowAbsent: true, projectedWebForm: true);
            Assert.True(projected["equal"].Value<bool>());
            Assert.Equal("projected-webform-structural-ids", projected["comparisonMode"].Value<string>());
            Assert.NotEqual(projected["expectedHash"].Value<string>(), projected["persistedHash"].Value<string>());
            Assert.Equal(NativeProjectionForm.Length, projected["expectedLength"].Value<int>());
            Assert.Equal(fresh.Length, projected["persistedLength"].Value<int>());

            // An id change is still a baseline/rollback difference, even if the
            // generic SDK snapshot omitted the visual content.
            var strict = WriteService.CompareProjectionXml(NativeProjectionForm, fresh, allowAbsent: true);
            Assert.False(strict["equal"].Value<bool>());
            var state = WriteService.ProjectionComparison(ObjectMoveSnapshot.Comparison.Verified("hash"), strict, "WebForm");
            var receipt = Receipt();
            receipt["transactionRolledBack"] = true;
            Assert.False(PatternProjectionWriteFlow.ConfirmReadback(receipt, true, state["equal"].Value<bool>(), null, null, false));
            Assert.False(receipt["rollbackVerified"].Value<bool>());
        }

        [Theory]
        [InlineData("native-structural-guid", false)]
        [InlineData("native-structural-guid", true)]
        [InlineData("native-structural-guid_", false)]
        [InlineData("native-structural-guid_", true)]
        public void LiteralStructuralIdsCannotCollideWithTheNormalizationMarker(string literal, bool reverse)
        {
            string literalForm = NativeProjectionForm.Replace("11111111-1111-1111-1111-111111111111", literal);
            string expected = reverse ? NativeProjectionForm : literalForm;
            string actual = reverse ? literalForm : NativeProjectionForm;
            Assert.False(WriteService.CompareProjectionXml(expected, actual,
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Fact]
        public void LiteralMarkerInUnchangedLeafAndDataDoesNotBlockStructuralIdRecreation()
        {
            string before = NativeProjectionForm
                .Replace("55555555-5555-5555-5555-555555555555", "native-structural-guid")
                .Replace("<data id=", "<data caption='native-structural-guid_' id=");
            var comparison = WriteService.CompareProjectionXml(before, RecreatedLayoutIds(before),
                projectedWebForm: true);
            Assert.True(comparison["equal"].Value<bool>());
            Assert.NotEqual(comparison["expectedHash"].Value<string>(), comparison["persistedHash"].Value<string>());
        }

        // Issue #948: the WorkWithPlus engine emits new grid and column ids on every
        // projection, so a property-only instance write on a host with a grid could
        // never pass the projected-WebForm comparison.
        private const string NativeGridMarkup =
            "<simplegrid id='14141414-1414-1414-1414-141414141414' controlName='Grid' class='77777777-7777-7777-7777-777777777777-175'>"
            + "<item id='15151515-1515-1515-1515-151515151515' attribute='att:4' titleExp='=Id.ContextualTitle'/>"
            + "<item id='16161616-1616-1616-1616-161616161616' attribute='var:5'/></simplegrid>";

        private static string NativeGridForm() => NativeProjectionForm.Replace(
            "<action controlName='BtnSave' onClickEvent='DoSave'/>", NativeGridMarkup);

        private static string RecreatedGridIds(string xml) => xml
            .Replace("14141414-1414-1414-1414-141414141414", "0f248b7a-f3fe-47f7-8d45-23f23c64e161")
            .Replace("15151515-1515-1515-1515-151515151515", "17171717-1717-1717-1717-171717171717")
            .Replace("16161616-1616-1616-1616-161616161616", "18181818-1818-1818-1818-181818181818");

        [Fact]
        public void ProjectedWebFormAcceptsRecreatedGridAndColumnIds()
        {
            string before = NativeGridForm();
            string after = RecreatedLayoutIds(RecreatedGridIds(before));
            var comparison = WriteService.CompareProjectionXml(before, after, projectedWebForm: true);
            Assert.True(comparison["equal"].Value<bool>(), comparison["difference"]?.ToString());
            Assert.NotEqual(comparison["expectedHash"].Value<string>(), comparison["persistedHash"].Value<string>());
            Assert.False(WriteService.CompareProjectionXml(before, after)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("controlName='Grid'", "controlName='OtherGrid'")]
        [InlineData("attribute='att:4'", "attribute='att:9'")]
        [InlineData("titleExp='=Id.ContextualTitle'", "titleExp='=Other'")]
        [InlineData("-175'", "-176'")]
        public void GridIdentityColumnsAndClassesRemainSignificant(string original, string changed)
        {
            string before = NativeGridForm();
            string after = RecreatedGridIds(before).Replace(original, changed);
            Assert.NotEqual(RecreatedGridIds(before), after);
            Assert.False(WriteService.CompareProjectionXml(before, after, projectedWebForm: true)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("14141414-1414-1414-1414-141414141414")]
        [InlineData("15151515-1515-1515-1515-151515151515")]
        public void ReferencedGridAndColumnIdsCannotBeSilentlyRecreated(string referencedId)
        {
            string before = NativeGridForm().Replace("<simplegrid ", "<data target='" + referencedId + "'/><simplegrid ");
            Assert.False(WriteService.CompareProjectionXml(before, RecreatedGridIds(before),
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("controlName='Grid'", "14141414-1414-1414-1414-141414141414")]
        [InlineData("attribute='att:4'", "15151515-1515-1515-1515-151515151515")]
        public void GridWrappersWithoutTheirNativeIdentityRemainStrict(string missingIdentity, string changedId)
        {
            string before = NativeGridForm().Replace(missingIdentity, "");
            string after = before.Replace(changedId, "abababab-abab-abab-abab-abababababab");
            Assert.False(WriteService.CompareProjectionXml(before, after,
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Fact]
        public void GridIdsOutsideLayoutFormsRemainStrict()
        {
            string htmlGrid = NativeProjectionForm.Replace("</GxMultiForm>",
                "<Form id='2' type='html'><detail>" + NativeGridMarkup + "</detail></Form></GxMultiForm>");
            Assert.False(WriteService.CompareProjectionXml(htmlGrid, RecreatedGridIds(htmlGrid),
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Fact]
        public void ProjectedWebFormAcceptsRecreatedTabAndDirectPageIdsInsideNestedUserControl()
        {
            string before = NativeTabForm();
            string after = RecreatedLayoutIds(RecreatedTabIds(before));
            var comparison = WriteService.CompareProjectionXml(before, after, projectedWebForm: true);
            Assert.True(comparison["equal"].Value<bool>());
            Assert.NotEqual(comparison["expectedHash"].Value<string>(), comparison["persistedHash"].Value<string>());
            Assert.False(WriteService.CompareProjectionXml(before, after)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("controlName='Tabs'", "controlName='OtherTabs'")]
        [InlineData("itemControlName='General'", "itemControlName='OtherPage'")]
        [InlineData("caption='General'", "caption='Changed'")]
        [InlineData("onClickEvent='TabSelected'", "onClickEvent='OtherEvent'")]
        public void TabIdentityCaptionAndEventsRemainSignificant(string original, string changed)
        {
            string before = NativeTabForm();
            string after = RecreatedTabIds(before).Replace(original, changed);
            Assert.NotEqual(RecreatedTabIds(before), after);
            Assert.False(WriteService.CompareProjectionXml(before, after, projectedWebForm: true)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("88888888-8888-8888-8888-888888888888")]
        [InlineData("99999999-9999-9999-9999-999999999999")]
        public void ReferencedTabAndPageIdsCannotBeSilentlyRecreated(string referencedId)
        {
            string before = NativeTabForm().Replace("<ucw ", "<ucw target='" + referencedId + "' ");
            Assert.False(WriteService.CompareProjectionXml(before, RecreatedTabIds(before),
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Fact]
        public void TabIdsOutsideLayoutFormsAndGenericItemIdsRemainStrict()
        {
            string htmlTab = NativeProjectionForm.Replace("</GxMultiForm>",
                "<Form id='2' type='html'><detail>" + NativeTabMarkup + "</detail></Form></GxMultiForm>");
            Assert.False(WriteService.CompareProjectionXml(htmlTab, RecreatedTabIds(htmlTab),
                projectedWebForm: true)["equal"].Value<bool>());
            string genericItem = NativeTabForm().Replace("</tab>",
                "<payload><item id='abababab-abab-abab-abab-abababababab' itemControlName='Business'/></payload></tab>");
            Assert.False(WriteService.CompareProjectionXml(genericItem,
                genericItem.Replace("abababab-abab-abab-abab-abababababab", "cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd"),
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("controlName='Tabs'", "88888888-8888-8888-8888-888888888888")]
        [InlineData("itemControlName='General'", "99999999-9999-9999-9999-999999999999")]
        public void TabWrappersWithoutTheirNativeControlIdentityRemainStrict(string missingIdentity, string changedId)
        {
            string before = NativeTabForm().Replace(missingIdentity, "");
            string after = before.Replace("id='" + changedId + "'", "id='abababab-abab-abab-abab-abababababab'");
            Assert.False(WriteService.CompareProjectionXml(before, after,
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("attribute='att:7'", "attribute='att:8'")]
        [InlineData("businessGuid='66666666-6666-6666-6666-666666666666'", "businessGuid='88888888-8888-8888-8888-888888888888'")]
        [InlineData("id='55555555-5555-5555-5555-555555555555'", "id='99999999-9999-9999-9999-999999999999'")]
        [InlineData("class='77777777-7777-7777-7777-777777777777-1106'", "class='88888888-8888-8888-8888-888888888888-1106'")]
        [InlineData("controlName='Main'", "controlName='Other'")]
        [InlineData("tableType='Responsive'", "tableType='Fixed'")]
        [InlineData("onClickEvent='DoSave'", "onClickEvent='DoOther'")]
        [InlineData("PanelType='Web'", "PanelType='SmartDevice'")]
        [InlineData("Form id='1'", "Form id='2'")]
        public void StructuralIdsCannotHideAnotherProjectedWebFormChange(string before, string after)
        {
            string fresh = RecreatedLayoutIds(NativeProjectionForm).Replace(before, after);
            Assert.NotEqual(RecreatedLayoutIds(NativeProjectionForm), fresh);
            Assert.False(WriteService.CompareProjectionXml(NativeProjectionForm, fresh,
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("PanelType='Web'", "")]
        [InlineData("", "PanelType='Web'")]
        [InlineData("", "")]
        public void SdkDefaultPanelTypeIsToleratedOnlyWhenOneSideOmitsIt(string expectedPanel, string actualPanel)
        {
            string expected = NativeProjectionForm.Replace("detail PanelType='Web'", "detail " + expectedPanel);
            string actual = NativeProjectionForm.Replace("detail PanelType='Web'", "detail " + actualPanel);
            Assert.True(WriteService.CompareProjectionXml(expected, actual, projectedWebForm: true)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("PanelType='SmartDevice'", "")]
        [InlineData("", "PanelType='SmartDevice'")]
        [InlineData("PanelType='SmartDevice'", "PanelType='Web'")]
        [InlineData("PanelType='Web'", "PanelType='Foo'")]
        public void OtherPanelTypeValuesAreStillCompared(string expectedPanel, string actualPanel)
        {
            string expected = NativeProjectionForm.Replace("detail PanelType='Web'", "detail " + expectedPanel);
            string actual = NativeProjectionForm.Replace("detail PanelType='Web'", "detail " + actualPanel);
            Assert.False(WriteService.CompareProjectionXml(expected, actual, projectedWebForm: true)["equal"].Value<bool>());
        }

        [Fact]
        public void OmittedDefaultPanelTypeDoesNotHideAnotherProjectedChange()
        {
            string actual = NativeProjectionForm.Replace("detail PanelType='Web'", "detail")
                .Replace("controlName='Main'", "controlName='Other'");
            Assert.False(WriteService.CompareProjectionXml(NativeProjectionForm, actual, projectedWebForm: true)["equal"].Value<bool>());
        }

        [Fact]
        public void StrictComparisonKeepsTheDefaultPanelType()
        {
            string actual = NativeProjectionForm.Replace("detail PanelType='Web'", "detail");
            Assert.False(WriteService.CompareProjectionXml(NativeProjectionForm, actual)["equal"].Value<bool>());
        }

        [Theory]
        [InlineData("attribute", false)]
        [InlineData("attribute", true)]
        [InlineData("text", false)]
        [InlineData("text", true)]
        [InlineData("payload", false)]
        [InlineData("payload", true)]
        public void ReferencedStructuralIdsRemainSignificant(string referenceKind, bool updateReference)
        {
            const string originalId = "11111111-1111-1111-1111-111111111111";
            const string freshId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            string before = referenceKind == "text"
                ? NativeProjectionForm.Replace("</layout>", "<reference>" + originalId + "</reference></layout>")
                : referenceKind == "payload"
                    ? NativeProjectionForm.Replace("businessGuid=", "PATTERN_ELEMENT_CUSTOM_PROPERTIES='&lt;reference&gt;" + originalId + "&lt;/reference&gt;' businessGuid=")
                    : NativeProjectionForm.Replace("<layout id=", "<layout target='" + originalId + "' id=");
            string after = before.Replace("id='" + originalId + "'", "id='" + freshId + "'");
            if (updateReference) after = after.Replace(originalId, freshId);
            Assert.False(WriteService.CompareProjectionXml(before, after,
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Fact]
        public void StructuralIdToleranceDoesNotApplyToPatternHtmlNonGuidOrMissingIds()
        {
            const string guid = "11111111-1111-1111-1111-111111111111";
            string pattern = "<instance><layout id='" + guid + "'/></instance>";
            Assert.False(WriteService.CompareProjectionXml(pattern, RecreatedLayoutIds(pattern),
                projectedWebForm: true)["equal"].Value<bool>());
            string html = NativeProjectionForm.Replace("type='layout'", "type='html'");
            Assert.False(WriteService.CompareProjectionXml(html, RecreatedLayoutIds(html),
                projectedWebForm: true)["equal"].Value<bool>());
            string namedId = NativeProjectionForm.Replace(guid, "layout-name");
            Assert.False(WriteService.CompareProjectionXml(namedId, namedId.Replace("layout-name", "other-name"),
                projectedWebForm: true)["equal"].Value<bool>());
            Assert.False(WriteService.CompareProjectionXml(NativeProjectionForm, NativeProjectionForm.Replace(" id='" + guid + "'", ""),
                projectedWebForm: true)["equal"].Value<bool>());
            Assert.False(WriteService.CompareProjectionXml(NativeProjectionForm, "<GxMultiForm>",
                projectedWebForm: true)["equal"].Value<bool>());
        }

        [Fact]
        public void VisualXmlReadsMaterializeFreshAndAlreadySavedObjectsBeforeComparison()
        {
            bool warm = true; // Save has already materialized the projected object.
            var calls = new List<string>();
            Action materialize = () => { calls.Add("materialize"); warm = true; };
            Func<string> read = () =>
            {
                calls.Add("read");
                return warm ? "<detail PanelType='Web'/>" : "<detail/>";
            };
            string projected = WriteService.ReadMaterializedProjectionXml(materialize, read);
            warm = false; // An independent native read starts cold.
            string fresh = WriteService.ReadMaterializedProjectionXml(materialize, read);
            Assert.Equal(new[] { "materialize", "read", "materialize", "read" }, calls);
            Assert.True(WriteService.CompareProjectionXml(projected, fresh, allowAbsent: true)["equal"].Value<bool>());
            Assert.Throws<InvalidOperationException>(() => WriteService.ReadMaterializedProjectionXml(() => { }, () => ""));
            Assert.Throws<InvalidOperationException>(() => WriteService.ReadMaterializedProjectionXml(
                () => throw new InvalidOperationException("native serialization unavailable"), read));
        }

        [Fact]
        public void GenericVerificationAndRollbackWrappersPreserveTheTwoObjectReceipt()
        {
            var receipt = Receipt();
            receipt["rollbackAttempted"] = true;
            receipt["transactionRolledBack"] = true;
            PatternProjectionWriteFlow.ConfirmReadback(receipt, true, false, null, null, false);
            var response = new JObject { ["status"] = "error", ["result"] = receipt };
            var writer = FormatterServices.GetUninitializedObject(typeof(WriteService));
            string raw = response.ToString();
            foreach (string method in new[] { "WrapWithPersistedState", "RollbackFullWriteFailure" })
            {
                var info = typeof(WriteService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
                object[] args = method == "WrapWithPersistedState"
                    ? new object[] { raw, "Panel", "PatternInstance", null, "before", "after", null, null, false }
                    : new object[] { raw, "Panel", "PatternInstance", null, "before" };
                var actual = JObject.Parse((string)info.Invoke(writer, args));
                Assert.False(actual["rollbackVerified"].Value<bool>());
                Assert.True(actual["partialPersistenceDetected"].Value<bool>());
                // A failed re-read never confirms persistence; the partial write is reported by partialPersistenceDetected.
                Assert.False(actual["persisted"].Value<bool>());
                Assert.False(actual["verified"].Value<bool>());
            }
        }

        [Fact]
        public void ParentSaveFailureKeepsSdkOutputAndBothAttemptsWithoutRunningValidation()
        {
            var output = new[] { new FakeSdkMessage { ErrorCode = "src0265", Text = "Control Combo_Sample is not defined", Level = "Error" } };
            var result = new WwpProjectionHelper.ProjectionResult();
            WwpProjectionHelper.RecordSaveFailure(result, new FakeValidationException("Erro", output), "parent-save", null);
            WwpProjectionHelper.RecordSaveFailure(result, new Exception("EnsureSave refused"), "parent-ensure-save", null);
            var receipt = result.ToJson();
            Assert.Equal(2, ((JArray)receipt["saveFailures"]).Count);
            Assert.Contains("src0265", receipt["saveFailures"].ToString());
            Assert.Contains("Combo_Sample", result.Failure);
            Assert.Contains("EnsureSave refused", result.Failure);
            Assert.False(receipt["parentSaved"].Value<bool>());
            Assert.Equal("Failed", receipt["projectionStatus"].Value<string>());
        }

        private sealed class LazySource : ISource
        {
            private string materialized;
            public string Source { get => materialized ?? (materialized = "Event Start\nEndEvent"); set => materialized = value; }
            internal string Serialized => materialized ?? string.Empty;
        }

        [Fact]
        public void SnapshotPreparationReadsLazySourceBeforeNativeSerialization()
        {
            var source = new LazySource();
            Assert.Equal(string.Empty, source.Serialized);
            WriteService.PrepareProjectionSnapshotSources(new ISource[] { source });
            Assert.Equal("Event Start\nEndEvent", source.Serialized);
        }
    }
}
