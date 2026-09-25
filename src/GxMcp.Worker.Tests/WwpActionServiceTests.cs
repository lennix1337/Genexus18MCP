using System.Xml.Linq;
using System.Linq;
using System;
using System.IO;
using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpActionServiceTests
    {
        [Fact]
        public void WebComponentReplacement_RejectsDifferentReferencesWithSameSuffix()
        {
            var args = new JObject
            {
                ["sourceName"] = "CompanySelector",
                ["tablePath"] = "Root>CompanySelector",
                ["gxobject"] = "Other-1234",
                ["caption"] = "&CompanyName"
            };
            var document = XDocument.Parse("<PatternInstance><table name='Root'><webComponent name='CompanySelector' gxobject='WebComponent-1234'/></table></PatternInstance>");
            var result = WwpActionService.ApplyReplacementXml(document, args);
            Assert.Equal("GxObjectMismatch", result["code"]?.ToString());
        }

        [Fact]
        public void WebComponentReplacement_ProjectionRequiresExactNamedControlAndCaption()
        {
            var method = typeof(WwpActionService).GetMethod("VerifyReplacementProjection", BindingFlags.Static | BindingFlags.NonPublic);
            var requestType = typeof(WwpActionService).GetNestedType("WebComponentReplacementRequest", BindingFlags.NonPublic);
            var request = Activator.CreateInstance(requestType, nonPublic: true);
            requestType.GetField("UserActionName", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(request, "CompanySelector");
            requestType.GetField("Caption", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(request, "&CompanyName");
            var webForm = "<WebForm><control name='OtherCompanySelector' caption='&amp;CompanyName'/><control name='ddc_CompanySelector' caption='&amp;Other'/></WebForm>";
            var result = (JObject)method.Invoke(null, new object[] { webForm, request });
            Assert.Equal("WwpProjectionNotConfirmed", result["code"]?.ToString());
        }

        [Theory]
        [InlineData(null, "WorkWithPlus", "WorkWithPlus")]
        [InlineData("", "WorkWithPlus", "WorkWithPlus")]
        [InlineData(" ", "WorkWithPlus", "WorkWithPlus")]
        [InlineData("ExplicitTarget", "OtherName", "ExplicitTarget")]
        [InlineData("WorkWithPlusOrder", null, "WorkWithPlusOrder")]
        [InlineData(null, null, null)]
        public void LegacyEnvelopeResolvesNameWithoutReplacingExplicitTarget(string target, string name, string expected)
        {
            var args = new JObject { ["name"] = name, ["guid"] = "11111111-2222-3333-4444-555555555555" };
            var original = args.DeepClone();

            Assert.Equal(expected, WwpActionService.ResolveTarget(target, args));
            Assert.True(JToken.DeepEquals(original, args));
        }

        // Issue #260: an existing object without a WorkWithPlus instance is not "not found";
        // the error names the other pattern instances it has and routes to genexus_read.
        [Fact]
        public void WwpInstanceNotFound_ListsOtherPatternInstances()
        {
            var registry = new PatternRegistry(new[]
            {
                PatternRegistry.ParseManifest(
                    "<Pattern Id=\"589d4b49-e3f9-4d49-aaf4-fad023028eb1\" Name=\"K2BEntityServices\">" +
                    "<Definition><InstanceName>K2BEntityServices{0}</InstanceName>" +
                    "<ParentObjects><ParentObject Type=\"Transaction\" /></ParentObjects></Definition></Pattern>", "es.Pattern")
            });
            var detected = PatternAnalysisService.MatchPatternInstances(new[]
            {
                new PatternInstanceCandidate("K2BEntityServicesCustomer", "K2BEntityServices", new Guid("589d4b49-e3f9-4d49-aaf4-fad023028eb1")),
                new PatternInstanceCandidate("CustomerHelper", "Procedure", Guid.Empty)
            }, registry);

            var env = JObject.Parse(WwpActionService.BuildWwpInstanceNotFound("Customer", "Customer", "Transaction", detected));

            Assert.Equal("WWPInstanceNotFound", env["error"]?["code"]?.ToString());
            Assert.Equal("Customer", env["objectName"]?.ToString());
            Assert.Equal("Transaction", env["objectType"]?.ToString());
            var patterns = (JArray)env["detectedPatterns"]!;
            Assert.Single(patterns);
            Assert.Equal("K2BEntityServicesCustomer", patterns[0]["name"]?.ToString());
            Assert.Equal("K2BEntityServices", patterns[0]["pattern"]?.ToString());
            Assert.Contains("genexus_read", env["error"]?["hint"]?.ToString());
            var next = env["error"]?["nextSteps"]?[0];
            Assert.Equal("genexus_read", next?["tool"]?.ToString());
            Assert.Equal("K2BEntityServicesCustomer", next?["args"]?["name"]?.ToString());
        }

        [Fact]
        public void WwpInstanceNotFound_NoPatternInstances_KeepsCodeWithEmptyList()
        {
            var env = JObject.Parse(WwpActionService.BuildWwpInstanceNotFound("Invoice", "Invoice", "Transaction", null));

            Assert.Equal("WWPInstanceNotFound", env["error"]?["code"]?.ToString());
            Assert.Empty((JArray)env["detectedPatterns"]!);
        }

        [Fact]
        public void MissingEnvelopeAndNameKeepIdentityOnlyResolutionAvailable()
        {
            Assert.Null(WwpActionService.ResolveTarget(null, null));
            Assert.Null(WwpActionService.ResolveTarget(null, new JObject
            {
                ["entityKey"] = "11111111-2222-3333-4444-555555555555-1"
            }));
        }

        [Fact]
        public void AddGridAction_WritesTypedAttributesAndNeverAddsSecurity()
        {
            var doc = XDocument.Parse("<instance><grid><actionGroup name='Actions'/></grid></instance>");
            JObject result = WwpActionService.Apply(doc, "add_grid_action", new JObject
            {
                ["group"] = "Actions", ["actionName"] = "Approve", ["description"] = "Approve order",
                ["selection"] = "multiple", ["enabledWhen"] = "Status = 1", ["confirmation"] = "Continue?"
            }, null);

            Assert.Null(result["error"]);
            XElement action = doc.Root.Element("grid").Element("actionGroup").Element("userAction");
            Assert.Equal("Approve", (string)action.Attribute("name"));
            Assert.Equal("True", (string)action.Attribute("multiRowSelection"));
            Assert.Null(action.Attribute("SecFuntionKey"));
            Assert.Null(action.Attribute("addSecurityToCall"));
            Assert.Equal("Continue?", (string)action.Attribute("confirmMessage"));
        }

        [Fact]
        public void AddFormUserAction_InsertsDirectlyIntoTableActionsAndDerivesEvent()
        {
            var doc = XDocument.Parse("<instance><table name='TableActions' childrenOrderedList='standardAction,footer'><standardAction name='Cancel' caption='Cancel'/><footer /></table></instance>");
            JObject result = WwpActionService.Apply(doc, "add_user_action", new JObject
            {
                ["containerName"] = "TableActions",
                ["actionName"] = "BaixarConfiguracao",
                ["caption"] = "Baixar Configuração"
            }, null);

            Assert.Null(result["error"]);
            Assert.Equal("DoBaixarConfiguracao", result["event"]?.ToString());
            XElement container = doc.Root.Element("table");
            XElement action = container.Elements("userAction").Single();
            Assert.Equal("BaixarConfiguracao", (string)action.Attribute("name"));
            Assert.Equal("Baixar Configuração", (string)action.Attribute("caption"));
            Assert.Null(action.Attribute("event"));
            Assert.Equal("standardAction,footer", (string)container.Attribute("childrenOrderedList"));

            var project = typeof(WwpActionService).GetMethod("Project", BindingFlags.Static | BindingFlags.NonPublic);
            JObject catalog = (JObject)project.Invoke(null, new object[] { doc });
            JObject projectedAction = (JObject)catalog["formContainers"]![0]!["actions"]![1];
            Assert.Equal("BaixarConfiguracao", projectedAction["name"]?.ToString());
            Assert.Equal("DoBaixarConfiguracao", projectedAction["event"]?.ToString());
        }

        [Fact]
        public void AddFormUserAction_RejectsInvalidNamesDuplicatesAndUnknownContainers()
        {
            var invalidName = XDocument.Parse("<instance><table name='TableActions' /></instance>");
            JObject invalid = WwpActionService.Apply(invalidName, "add_user_action", new JObject
            {
                ["actionName"] = "Baixar Configuracao", ["caption"] = "Baixar"
            }, null);
            Assert.Equal("InvalidActionName", invalid["code"]?.ToString());

            var duplicate = XDocument.Parse("<instance><table name='TableActions'><userAction name='BaixarConfiguracao' /></table></instance>");
            JObject alreadyExists = WwpActionService.Apply(duplicate, "add_user_action", new JObject
            {
                ["actionName"] = "BaixarConfiguracao", ["caption"] = "Baixar"
            }, null);
            Assert.Equal("ActionAlreadyExists", alreadyExists["code"]?.ToString());

            JObject missingContainer = WwpActionService.Apply(duplicate, "add_user_action", new JObject
            {
                ["containerName"] = "MissingActions", ["actionName"] = "Retry", ["caption"] = "Retry"
            }, null);
            Assert.Equal("FormActionContainerNotFound", missingContainer["code"]?.ToString());
            Assert.Contains("TableActions", missingContainer["availableContainers"]?.Values<string>() ?? Enumerable.Empty<string>());
        }

        [Fact]
        public void AddFormUserAction_UsesDefaultForBlankContainerAndMatchesControlName()
        {
            var document = XDocument.Parse("<instance><table controlName='TableActions'><standardAction name='Cancel' /></table></instance>");

            JObject result = WwpActionService.Apply(document, "add_user_action", new JObject
            {
                ["containerName"] = " ",
                ["actionName"] = "Download",
                ["caption"] = "Download"
            }, null);

            Assert.Null(result["error"]);
            Assert.Single(document.Descendants("userAction"));
        }

        [Fact]
        public void AddFormUserAction_RejectsAmbiguousContainerInsteadOfChoosingFirst()
        {
            var document = XDocument.Parse("<instance><table name='TableActions'><standardAction name='Cancel' /></table><table controlName='TableActions'><standardAction name='Close' /></table></instance>");

            JObject result = WwpActionService.Apply(document, "add_user_action", new JObject
            {
                ["containerName"] = "TableActions",
                ["actionName"] = "Download",
                ["caption"] = "Download"
            }, null);

            Assert.Equal("FormActionContainerAmbiguous", result["code"]?.ToString());
            Assert.Equal(2, result["matchingContainers"]?.Count());
            Assert.Empty(document.Descendants("userAction"));
        }

        [Fact]
        public void FormUserActionPersistencePathUsesNativeSdkInsteadOfGenericPatternWriter()
        {
            string serviceSource = File.ReadAllText(FindWorkerServiceFile("WwpActionService.cs"));
            string nativeSource = File.ReadAllText(FindWorkerServiceFile("WwpActionService.FormActions.cs"));

            Assert.Contains("RunFormUserActionOperation", serviceSource);
            Assert.Contains("ApplyNativeFormUserAction", nativeSource);
            Assert.Contains("CreateNativeChild(container, \"userAction\")", nativeSource);
            Assert.Contains("SaveNativePattern(currentInstance, currentPart)", nativeSource);
            Assert.DoesNotContain("_write.WriteObject(target, new JObject", nativeSource);
        }

        [Fact]
        public void FormUserActionVerificationRequiresDerivedEventWithoutXmlEventAttribute()
        {
            var before = XDocument.Parse("<instance><table name='TableActions'><standardAction name='Cancel' /></table></instance>");
            var persisted = XDocument.Parse("<instance><table name='TableActions'><standardAction name='Cancel'/><userAction name='Download' caption='Download'/></table></instance>");

            JObject verified = WwpActionService.VerifyFormUserAction(
                before, persisted, "TableActions", "Download", "Download");
            Assert.True(verified["confirmed"]?.ToObject<bool>());
            Assert.Equal("DoDownload", verified["event"]?.ToString());

            persisted.Descendants("userAction").Single().SetAttributeValue("event", "DoDownload");
            JObject rejected = WwpActionService.VerifyFormUserAction(
                before, persisted, "TableActions", "Download", "Download");
            Assert.Equal("WwpFormActionIntegrityFailed", rejected["code"]?.ToString());
        }

        [Theory]
        [InlineData(null, "list_actions")]
        [InlineData("list", "list_actions")]
        [InlineData("add_action", "add_grid_action")]
        [InlineData("add_tab", "add_tab")]
        [InlineData("move_grid_column", "move_grid_column")]
        [InlineData("add_grid_variable", "add_grid_variable")]
        public void PublishedActionNamesNormalizeToWorkerOperations(string action, string expected)
        {
            Assert.Equal(expected, WwpActionService.NormalizeOperation(action));
        }

        [Fact]
        public void MoveAndRemoveAction_KeepRequestedOrder()
        {
            var doc = XDocument.Parse("<instance><grid><actionGroup name='A'><userAction name='One'/><userAction name='Two'/></actionGroup><actionGroup name='B'/></grid></instance>");
            WwpActionService.Apply(doc, "move_action", new JObject
            { ["group"] = "A", ["actionName"] = "Two", ["newGroup"] = "B", ["position"] = 0 }, null);
            Assert.Equal("Two", (string)doc.Descendants("actionGroup").Last().Element("userAction").Attribute("name"));

            WwpActionService.Apply(doc, "remove_action", new JObject
            { ["group"] = "A", ["actionName"] = "One" }, null);
            Assert.Empty(doc.Descendants("actionGroup").First().Elements("userAction"));
        }

        [Fact]
        public void PublishedActionFieldsMapToPatternAttributes()
        {
            var doc = XDocument.Parse("<instance><grid><actionGroup name='A'><userAction name='Retry'/></actionGroup><actionGroup name='B'/></grid></instance>");
            WwpActionService.Apply(doc, "update_action", new JObject
            {
                ["group"] = "A",
                ["actionName"] = "Retry",
                ["caption"] = "Retry order",
                ["buttonClass"] = "ButtonPrimary",
                ["toGroup"] = "B"
            }, null);

            XElement action = doc.Descendants("actionGroup").Last().Element("userAction");
            Assert.Equal("Retry order", (string)action.Attribute("caption"));
            Assert.Equal("ButtonPrimary", (string)action.Attribute("buttonClass"));
        }

        [Fact]
        public void AddTab_BuildsCanonicalResponsiveTableAndTypedChildren()
        {
            var doc = XDocument.Parse("<instance><WPRoot><tabs><tab ControlName='One' title='One'/><tab ControlName='Three' title='Three'/></tabs></WPRoot></instance>");
            JObject result = WwpActionService.ApplyTabXml(doc, "add_tab", new JObject
            {
                ["controlName"] = "Two",
                ["title"] = "Second",
                ["position"] = 1,
                ["children"] = new JArray
                {
                    new JObject { ["type"] = "variable", ["name"] = "Choice", ["basicType"] = "VarChar", ["length"] = 40, ["description"] = "Choice" },
                    new JObject { ["type"] = "userAction", ["name"] = "Send", ["caption"] = "Send now" }
                }
            });

            Assert.Null(result["error"]);
            XElement[] tabs = doc.Descendants("tab").ToArray();
            Assert.Equal(new[] { "One", "Two", "Three" }, tabs.Select(t => (string)t.Attribute("ControlName")));
            XElement table = tabs[1].Element("table");
            Assert.Equal("Responsive", (string)table.Attribute("type"));
            Assert.Equal("Choice", (string)table.Element("variable").Attribute("name"));
            Assert.Equal("40", (string)table.Element("variable").Attribute("basicCLength"));
            Assert.Equal("Send", (string)table.Element("userAction").Attribute("name"));
            Assert.Null(table.Attribute("childrenOrderedList"));
        }

        [Fact]
        public void MoveAndRemoveTab_PreserveOrderAndReportMissingSeparately()
        {
            var doc = XDocument.Parse("<instance><tabs><tab ControlName='One'/><tab ControlName='Two'/><tab ControlName='Three'/></tabs></instance>");
            JObject moved = WwpActionService.ApplyTabXml(doc, "move_tab",
                new JObject { ["controlName"] = "Three", ["position"] = 0 });
            Assert.Null(moved["error"]);
            Assert.Equal(new[] { "Three", "One", "Two" }, doc.Descendants("tab").Select(t => (string)t.Attribute("ControlName")));

            JObject removed = WwpActionService.ApplyTabXml(doc, "remove_tab",
                new JObject { ["controlName"] = "One" });
            Assert.Null(removed["error"]);
            Assert.Equal(new[] { "Three", "Two" }, doc.Descendants("tab").Select(t => (string)t.Attribute("ControlName")));

            JObject missing = WwpActionService.ApplyTabXml(doc, "remove_tab",
                new JObject { ["controlName"] = "Missing" });
            Assert.Equal("TabNotFound", (string)missing["code"]);
            JObject invalid = WwpActionService.ApplyTabXml(doc, "rename_tab",
                new JObject { ["controlName"] = "Two" });
            Assert.Equal("UnknownWwpActionOperation", (string)invalid["code"]);
        }

        [Fact]
        public void WebFormProjectionMatchingIsDelimitedAndStructural()
        {
            JObject expected = new JObject
            {
                ["tabs"] = new JArray
                {
                    new JObject { ["controlName"] = "Tab1", ["children"] = new JArray
                    {
                        new JObject { ["type"] = "userAction", ["name"] = "Send" }
                    } },
                    new JObject { ["controlName"] = "Tab10", ["children"] = new JArray() }
                }
            };
            string webForm = "<form><tab id='Tab10'/><tab id='Tab1'><button event='OnSendNow'/></tab></form>";
            JObject result = WwpActionService.VerifyWebFormProjectionForTests(webForm, expected, "add_tab", "Tab1");
            Assert.False(result["confirmed"].Value<bool>());
            Assert.False(result["actionEventConfirmed"].Value<bool>());
            Assert.False(WwpActionService.WebFormContainsEventForTests(webForm, "Send"));
            Assert.True(WwpActionService.WebFormContainsEventForTests("<form><button event='On.Send'/></form>", "Send"));
        }

        [Fact]
        public void WebFormProjectionDoesNotConfuseTabPrefixWithExactControlName()
        {
            JObject expected = new JObject { ["tabs"] = new JArray
            {
                new JObject { ["controlName"] = "Tab1", ["children"] = new JArray() }
            } };
            JObject result = WwpActionService.VerifyWebFormProjectionForTests(
                "<form><tab id='Tab10'/></form>", expected, "add_tab", "Tab1");
            Assert.False(result["confirmed"].Value<bool>());
            Assert.False(result["targetPresent"].Value<bool>());
        }

        [Theory]
        [InlineData(null, "current", true)]
        [InlineData("current", "current", true)]
        [InlineData("stale", "current", false)]
        public void CommonWwpActionVersionPreconditionRejectsStale(string expected, string current, bool valid)
        {
            Assert.Equal(valid, WwpActionService.IsExpectedVersion(expected, current));
        }

        [Fact]
        public void AddGridAttribute_ChangesOnlyRequestedAttribute()
        {
            var before = XDocument.Parse("<instance childrenOrderedList='grid,footer'><grid childrenOrderedList='A,B'><gridAttribute attribute='1-Existing' description='Existing'/></grid><footer childrenOrderedList='X,Y'/></instance>");
            var after = XDocument.Parse(before.ToString(SaveOptions.DisableFormatting));

            JObject result = WwpActionService.ApplyGridAttributeXml(after,
                "2-ProcessingComplete", "ProcessingComplete", "Processed");

            Assert.Null(result["error"]);
            XElement added = after.Descendants("gridAttribute").Last();
            Assert.Equal("2-ProcessingComplete", (string)added.Attribute("attribute"));
            Assert.Equal("Processed", (string)added.Attribute("description"));
            Assert.Empty(WwpActionService.FindGridAttributeUnrelatedChanges(
                before, after, "ProcessingComplete", existedBefore: false));
            Assert.Equal("grid,footer", (string)after.Root.Attribute("childrenOrderedList"));
            Assert.Equal("A,B", (string)after.Root.Element("grid").Attribute("childrenOrderedList"));
            Assert.Equal("X,Y", (string)after.Root.Element("footer").Attribute("childrenOrderedList"));
        }

        [Fact]
        public void GridAuthoringMarkerIsCarriedIntoThePatternWritePreflight()
        {
            var args = JObject.Parse("{\"mode\":\"full\",\"part\":\"PatternInstance\",\"patternEditMode\":\"grid-column\",\"content\":\"<instance/>\"}");
            var normalized = WriteService.NormalizeFacadeArgs(args);

            Assert.True(normalized.AllowGridStructure);
        }

        [Fact]
        public void Project_IncludesGridOrderAndIdentityForPostWriteVerification()
        {
            var document = XDocument.Parse(
                "<instance><grid name='Main' controlName='MainGrid'>" +
                "<gridVariable variable='11111111-1111-1111-1111-111111111111-Name' length='10' />" +
                "<gridAttribute attribute='2-Code' description='Code' />" +
                "</grid></instance>");
            var project = typeof(WwpActionService).GetMethod("Project",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var catalog = (JObject)project.Invoke(null, new object[] { document });

            var grid = (JObject)catalog["grids"]![0]!;
            Assert.Equal("Main", grid["name"]?.ToString());
            Assert.Equal(2, ((JArray)grid["columns"]!).Count);
            Assert.Equal("gridVariable", grid["columns"]![0]!["kind"]?.ToString());
            Assert.Equal("11111111-1111-1111-1111-111111111111-Name",
                grid["columns"]![0]!["attributes"]!["variable"]?.ToString());
        }

        [Fact]
        public void MoveGridColumn_AmbiguousIdentityIsRejected()
        {
            var document = XDocument.Parse(
                "<instance><grid name='Main'>" +
                "<gridVariable variable='11111111-1111-1111-1111-111111111111-Name' />" +
                "<gridVariable variable='11111111-1111-1111-1111-111111111111-Name' />" +
                "</grid></instance>");
            var result = WwpActionService.ApplyMoveGridColumnXml(document, new JObject
            {
                ["attribute"] = "11111111-1111-1111-1111-111111111111-Name",
                ["position"] = "first"
            });

            Assert.Equal("AmbiguousGridColumn", result["code"]?.ToString());
        }

        [Fact]
        public void AddGridAttribute_ReconcilesOnlyRequestedCaption()
        {
            var before = XDocument.Parse("<instance><grid childrenOrderedList='A,B'><gridAttribute attribute='2-ProcessingComplete' description='=Attribute.ContextualTitle' custom='keep'/></grid></instance>");
            var after = XDocument.Parse(before.ToString(SaveOptions.DisableFormatting));

            WwpActionService.ApplyGridAttributeXml(after,
                "2-ProcessingComplete", "ProcessingComplete", "Processed");

            XElement item = after.Descendants("gridAttribute").Single();
            Assert.Equal("Processed", (string)item.Attribute("description"));
            Assert.Equal("keep", (string)item.Attribute("custom"));
            Assert.Empty(WwpActionService.FindGridAttributeUnrelatedChanges(
                before, after, "ProcessingComplete", existedBefore: true));
        }

        [Fact]
        public void AddGridAttribute_VerificationRejectsUnrelatedChildrenOrderChange()
        {
            var before = XDocument.Parse("<instance><grid childrenOrderedList='A,B'><gridAttribute attribute='1-Existing'/></grid><footer childrenOrderedList='X,Y'/></instance>");
            var persisted = XDocument.Parse(before.ToString(SaveOptions.DisableFormatting));
            WwpActionService.ApplyGridAttributeXml(persisted,
                "2-ProcessingComplete", "ProcessingComplete", "Processed");
            persisted.Root.Element("footer").SetAttributeValue("childrenOrderedList", "Y,X");

            JArray unrelated = WwpActionService.FindGridAttributeUnrelatedChanges(
                before, persisted, "ProcessingComplete", existedBefore: false);

            Assert.Single(unrelated);
        }

        [Fact]
        public void MoveGridColumn_ReordersOnlyTheRequestedColumn()
        {
            var document = XDocument.Parse("<PatternInstance><grid><gridAttribute attribute='1-First'/><gridAttribute attribute='2-Second' description='Second'/><gridAttribute attribute='3-Third'/></grid></PatternInstance>");
            JObject result = WwpActionService.ApplyMoveGridColumnXml(document, new JObject
            {
                ["attribute"] = "3-Third",
                ["before"] = "1-First",
                ["caption"] = "Moved"
            });

            Assert.Null(result["error"]);
            Assert.Equal(new[] { "3-Third", "1-First", "2-Second" },
                document.Descendants("gridAttribute").Select(e => (string)e.Attribute("attribute")));
            Assert.Equal("Moved", (string)document.Descendants("gridAttribute").First().Attribute("description"));
        }

        [Fact]
        public void AddGridVariable_RequiresIdentityAndPreservesColumnOrder()
        {
            var document = XDocument.Parse("<PatternInstance><grid><gridAttribute attribute='1-First'/></grid></PatternInstance>");
            JObject missing = WwpActionService.ApplyAddGridVariableXml(document, new JObject
            {
                ["variable"] = "Selected",
                ["caption"] = "Selected"
            });
            Assert.Equal("GridVariableIdentityRequired", (string)missing["code"]);

            JObject result = WwpActionService.ApplyAddGridVariableXml(document, new JObject
            {
                ["variable"] = "Selected",
                ["variableReference"] = "guid-Selected",
                ["caption"] = "Selected",
                ["basicType"] = "VarChar",
                ["length"] = 40,
                ["before"] = "1-First"
            });
            Assert.Null(result["error"]);
            XElement added = document.Descendants("gridVariable").Single();
            Assert.Equal("guid-Selected", (string)added.Attribute("variable"));
            Assert.Equal("40", (string)added.Attribute("basicCLength"));
            Assert.Equal("1-First", (string)document.Descendants("gridAttribute").First().Attribute("attribute"));
        }

        [Fact]
        public void GridVariableVerificationRequiresTheRequestedGuidAndNameTogether()
        {
            const string requestedGuid = "11111111-1111-1111-1111-111111111111";
            const string homonymGuid = "22222222-2222-2222-2222-222222222222";
            string reference = requestedGuid + "-Selected";

            Assert.False(WwpActionService.GridVariableIdentityMatches(
                "Selected", null, "Selected", reference));
            Assert.False(WwpActionService.GridVariableIdentityMatches(
                "Selected", homonymGuid, "Selected", reference));
            Assert.True(WwpActionService.GridVariableIdentityMatches(
                "Selected", requestedGuid, "Selected", reference));
            Assert.False(WwpActionService.GridVariableIdentityMatches(
                "Other", requestedGuid, "Selected", reference));
            Assert.False(WwpActionService.GridVariableIdentityMatches(
                "Selected", requestedGuid, "Selected", requestedGuid));
        }

        [Fact]
        public void ReplaceWebComponent_UsesExplicitPathAndPreservesUnrelatedPatternNodes()
        {
            var doc = XDocument.Parse("<instance childrenOrderedList='root'><WPRoot><table name='TableHeader' childrenOrderedList='header'><table name='TableUserRole' themeClass='keep' childrenOrderedList='role'><webComponent name='EmpresaSelector' gxobject='guid-WWP_MasterPageEmpresaSelectorWC'><parameters /></webComponent><userAction name='RuntimeDesignSettings' ControlType='DropDownComponent' /></table></table><table name='TableContent' /></WPRoot></instance>");

            JObject result = WwpActionService.ApplyReplacementXml(doc, new JObject
            {
                ["sourceName"] = "EmpresaSelector",
                ["userActionName"] = "EmpresaSelector",
                ["tablePath"] = "TableHeader > TableUserRole > EmpresaSelector",
                ["gxobject"] = "WWP_MasterPageEmpresaSelectorWC",
                ["controlType"] = "DropDownComponent",
                ["caption"] = "&Context.EmpresaDescricao",
                ["webComponentLoad"] = "On every click",
                ["trigger"] = "Click"
            });

            Assert.Null(result["error"]);
            XElement role = doc.Descendants("table").Single(e => (string)e.Attribute("name") == "TableUserRole");
            XElement replacement = role.Elements().Single(e => e.Name.LocalName == "userAction" && (string)e.Attribute("name") == "EmpresaSelector");
            Assert.Equal("DropDownComponent", (string)replacement.Attribute("ControlType"));
            Assert.Equal("guid-WWP_MasterPageEmpresaSelectorWC", (string)replacement.Attribute("gxobject"));
            Assert.Equal("&Context.EmpresaDescricao", (string)replacement.Attribute("caption"));
            Assert.Equal("On every click", (string)replacement.Attribute("webComponentLoad"));
            Assert.Equal("Click", (string)replacement.Attribute("trigger"));
            Assert.Equal("keep", (string)role.Attribute("themeClass"));
            Assert.Equal("role", (string)role.Attribute("childrenOrderedList"));
            Assert.Single(doc.Descendants("table"), e => (string)e.Attribute("name") == "TableContent");
        }

        [Fact]
        public void ReplaceWebComponent_RefusesTargetMismatchAndNonEmptyChildren()
        {
            var mismatch = XDocument.Parse("<instance><table name='TableHeader'><table name='TableUserRole'><webComponent name='EmpresaSelector' gxobject='guid-OtherWC' /></table></table></instance>");
            JObject wrongTarget = WwpActionService.ApplyReplacementXml(mismatch, new JObject
            {
                ["sourceName"] = "EmpresaSelector", ["tablePath"] = "TableHeader > TableUserRole > EmpresaSelector",
                ["gxobject"] = "WWP_MasterPageEmpresaSelectorWC", ["controlType"] = "DropDownComponent", ["caption"] = "Empresa"
            });
            Assert.Equal("GxObjectMismatch", (string)wrongTarget["code"]);

            var withParameters = XDocument.Parse("<instance><table name='TableHeader'><table name='TableUserRole'><webComponent name='EmpresaSelector' gxobject='guid-WC'><parameters><parameter name='x' /></parameters></webComponent></table></table></instance>");
            JObject droppedChild = WwpActionService.ApplyReplacementXml(withParameters, new JObject
            {
                ["sourceName"] = "EmpresaSelector", ["tablePath"] = "TableHeader > TableUserRole > EmpresaSelector",
                ["gxobject"] = "guid-WC", ["controlType"] = "DropDownComponent", ["caption"] = "Empresa"
            });
            Assert.Equal("WwpReplacementChildrenUnsupported", (string)droppedChild["code"]);
        }

        [Fact]
        public void SetTableType_ChangesOnlyRequestedTypeAndPreservesPatternIdentity()
        {
            const string source = "<instance><WPRoot><table name='TableMain' type='Responsive' defaultType='Responsive' childrenOrderedList='1;2'><table name='Content' type='Responsive'><variable name='UserName' controlName='UserName' /></table></table></WPRoot></instance>";
            var before = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
            var after = XDocument.Parse(source, LoadOptions.PreserveWhitespace);

            JObject result = WwpActionService.ApplyTableTypeXml(after, "TableMain", "Regular");

            Assert.Null(result["error"]);
            Assert.True(result["changed"].ToObject<bool>());
            XElement table = after.Descendants("table").First();
            Assert.Equal("Regular", (string)table.Attribute("type"));
            Assert.Equal("Responsive", (string)table.Attribute("defaultType"));
            Assert.Equal("1;2", (string)table.Attribute("childrenOrderedList"));
            Assert.True(WwpActionService.VerifyOnlyTableTypeChanged(before, after, "TableMain", "Regular", out string error), error);
        }

        [Fact]
        public void SetTableType_RejectsAmbiguousUnnamedTablePath()
        {
            var doc = XDocument.Parse("<instance><WPRoot><table type='Responsive' /><table type='Responsive' /></WPRoot></instance>");

            JObject result = WwpActionService.ApplyTableTypeXml(doc, "WPRoot > table", "Regular");

            Assert.Equal("WwpTableNotFound", result["code"]?.ToString());
            Assert.True((result["error"]?.ToString() ?? string.Empty)
                .IndexOf("ambiguous", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void SetTableType_AcceptsIndexedTableBreadcrumb()
        {
            var doc = XDocument.Parse("<instance><WPRoot><table type='Responsive' /><table type='Responsive' /></WPRoot></instance>");

            JObject result = WwpActionService.ApplyTableTypeXml(doc, "WPRoot > table[1]", "Regular");

            Assert.Null(result["error"]);
            Assert.Equal("Responsive", (string)doc.Descendants("table").First().Attribute("type"));
            Assert.Equal("Regular", (string)doc.Descendants("table").Skip(1).First().Attribute("type"));
        }

        private static string FindWorkerServiceFile(string fileName)
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "GxMcp.Worker", "Services", fileName);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate " + fileName + " starting from " + AppDomain.CurrentDomain.BaseDirectory);
        }
        [Fact]
        public void WwpInstanceNotFound_ParentOfWwpInstance_PointsToTheInstanceInsteadOfEditingIt()
        {
            var registry = new PatternRegistry(new PatternManifest[0]);
            var detected = new[]
            {
                new PatternInstanceMatch(new PatternInstanceCandidate("WorkWithPlusCustomer", "WorkWithPlus", Guid.Empty),
                    registry.FindById(PatternRegistry.WorkWithPlusPatternId))
            };

            var env = JObject.Parse(WwpActionService.BuildWwpInstanceNotFound("Customer", "Customer", "Transaction", detected));

            Assert.Equal("WWPInstanceNotFound", env["error"]?["code"]?.ToString());
            Assert.Contains("WorkWithPlusCustomer", env["error"]?["message"]?.ToString());
            var next = env["error"]?["nextSteps"]?[0];
            Assert.Equal("genexus_wwp", next?["tool"]?.ToString());
            Assert.Equal("WorkWithPlusCustomer", next?["args"]?["name"]?.ToString());
        }
        [Fact]
        public void Run_UntypedLookupOnlyExplainsTheError_NeverReachesMutation_ViaConvention()
        {
            // Issue #260 review: an untyped homonym must never be edited by genexus_wwp.
            string source = File.ReadAllText(Path.Combine(TestFixtures.FindRepoRoot(),
                "src", "GxMcp.Worker", "Services", "WwpActionService.cs"));

            Assert.Contains("return BuildWwpInstanceNotFound(target, existing);", source);
            Assert.DoesNotContain("requestedObject = _objects.FindObject(\n                        target,\n                        guid:", source.Replace("\r\n", "\n"));
        }
    }
}
