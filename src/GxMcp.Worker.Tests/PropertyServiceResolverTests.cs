using System;
using System.Dynamic;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class PropertyServiceResolverTests
    {
        [Theory]
        [InlineData(false, "Formula")]
        [InlineData(true, "formula")]
        [InlineData(true, " Formula ")]
        public void FormulaBatchIsRejectedBeforeSdkAccess(bool dryRun, string propertyName)
        {
            var service = (PropertyService)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PropertyService));
            var result = JObject.Parse(service.SetProperties("Result", new JObject
            {
                ["Description"] = "Must not be written",
                [propertyName] = "Value * 2\n IF Value > 0;"
            }, typeFilter: "Attribute", dryRun: dryRun));

            Assert.Equal("FormulaNotScalarWritable", result["error"]?["code"]?.ToString());
            Assert.Contains("set_attribute", result["error"]?["hint"]?.ToString());
        }

        [Fact]
        public void FormulaSingleWriteIsRejectedBeforeSdkAccess()
        {
            var service = (PropertyService)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PropertyService));
            var result = JObject.Parse(service.SetProperty("Result", "Formula", "Value * 2", typeFilter: "Attribute"));

            Assert.Equal("FormulaNotScalarWritable", result["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ReadOnlyDescriptorIsRejectedBeforeAnySave()
        {
            dynamic container = Container(
                Property("Caption", "old", typeof(string), readOnly: true));

            string result = PropertyService.ValidatePropertyWrite(container, "Caption", "new");

            Assert.StartsWith("PropertyReadOnly", result, StringComparison.Ordinal);
        }

        [Fact]
        public void BatchGet_PreservesOrderAndReturnsPerTargetErrors()
        {
            var targets = new JArray
            {
                new JObject { ["name"] = "ProcA", ["type"] = "Procedure" },
                new JObject { ["name"] = "Missing", ["type"] = "Procedure" }
            };
            var calls = new List<string>();

            string raw = PropertyService.ShapeGetPropertiesBatchResult(targets, (name, type) =>
            {
                calls.Add(name);
                if (name == "ProcA")
                {
                    return new JObject
                    {
                        ["status"] = "ok",
                        ["result"] = new JObject
                        {
                            ["values"] = new JObject { ["MainProgram"] = true },
                            ["missingProperties"] = new JArray("Description"),
                            ["versionToken"] = "version-a"
                        }
                    }.ToString();
                }

                return new JObject
                {
                    ["status"] = "error",
                    ["error"] = new JObject { ["code"] = "ObjectNotFound", ["message"] = "Object not found." }
                }.ToString();
            });

            var json = JObject.Parse(raw);
            var result = json["result"] as JObject;
            var results = result?["results"] as JArray;
            Assert.Equal(new[] { "ProcA", "Missing" }, calls);
            Assert.Equal("partial", json["status"]?.ToString());
            Assert.Equal("ProcA", results?[0]?["name"]?.ToString());
            Assert.Equal("ok", results?[0]?["status"]?.ToString());
            Assert.Equal("True", results?[0]?["values"]?["MainProgram"]?.ToString());
            Assert.Equal("Description", results?[0]?["missingProperties"]?[0]?.ToString());
            Assert.Equal("version-a", results?[0]?["versionToken"]?.ToString());
            Assert.Equal("Missing", results?[1]?["name"]?.ToString());
            Assert.Equal("ObjectNotFound", results?[1]?["error"]?["code"]?.ToString());
        }

        [Fact]
        public void BatchGet_StopsAtLimitAndMarksRemainingTargets()
        {
            var targets = new JArray
            {
                new JObject { ["name"] = "ProcA" },
                new JObject { ["name"] = "ProcB" },
                new JObject { ["name"] = "ProcC" }
            };
            int calls = 0;

            string raw = PropertyService.ShapeGetPropertiesBatchResult(
                targets,
                (name, type) =>
                {
                    calls++;
                    return new JObject
                    {
                        ["status"] = "ok",
                        ["result"] = new JObject { ["values"] = new JObject { ["MainProgram"] = name == "ProcA" } }
                    }.ToString();
                },
                maxTargets: 2);

            var json = JObject.Parse(raw);
            Assert.Equal(2, calls);
            Assert.Equal(2, (int)json["result"]?["processedCount"]);
            Assert.Equal(3, (int)json["result"]?["requestedCount"]);
            Assert.True((bool)json["result"]?["truncated"]);
            Assert.Equal("partial", json["status"]?.ToString());
        }

        [Fact]
        public void InvalidTypedValueIsRejectedBeforeAnySave()
        {
            dynamic container = Container(
                Property("Visible", true, typeof(bool), readOnly: false));

            string result = PropertyService.ValidatePropertyWrite(container, "Visible", "maybe");

            Assert.StartsWith("InvalidPropertyValue", result, StringComparison.Ordinal);
        }

        [Fact]
        public void ValidTypedValueAndUnknownDescriptorAreDistinguished()
        {
            dynamic container = Container(
                Property("Visible", true, typeof(bool), readOnly: false));

            Assert.Null(PropertyService.ValidatePropertyWrite(container, "Visible", "false"));
            Assert.StartsWith("PropertyNotFound", PropertyService.ValidatePropertyWrite(container, "Missing", "x"), StringComparison.Ordinal);
        }

        [Fact]
        public void SingleProperty_Found_ReturnsSinglePropertyShapeAndVersionToken()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                propertyName: "Description",
                versionToken: string.Concat("token", "_abc123"));

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("PropertiesRead", json["code"]?.ToString());
            var res = json["result"] as JObject;
            Assert.NotNull(res);
            Assert.Equal("Description", res["propertyName"]?.ToString());
            Assert.Equal("Customer transaction", res["value"]?.ToString());
            Assert.Equal("token_abc123", res["versionToken"]?.ToString());
            Assert.Equal("Description", res["property"]?["name"]?.ToString());
            Assert.Equal("Customer transaction", res["values"]?["Description"]?.ToString());
            var props = res["properties"] as JArray;
            Assert.NotNull(props);
            Assert.Single(props);
            Assert.Equal("Description", props[0]?["name"]?.ToString());
        }

        [Fact]
        public void SingleProperty_NotFound_ReturnsDidYouMeanSuggestions_AndNextSteps()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                propertyName: "Desc");

            var json = JObject.Parse(raw);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("PropertyNotFound", json["error"]?["code"]?.ToString());
            Assert.Contains("Did you mean: 'Description'?", json["error"]?["message"]?.ToString());
            var dym = json["error"]?["didYouMean"] as JArray;
            Assert.NotNull(dym);
            Assert.Contains("Description", dym.Select(t => t.ToString()));
            var nextSteps = json["error"]?["nextSteps"] as JArray;
            Assert.NotNull(nextSteps);
            Assert.NotEmpty(nextSteps);
            Assert.Equal("Description", nextSteps[0]?["args"]?["propertyName"]?.ToString());
        }

        [Fact]
        public void Query_SubstringMatching_ReturnsMatchingPropertiesAndValues()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                query: "Commit");

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("PropertiesRead", json["code"]?.ToString());
            var res = json["result"] as JObject;
            Assert.NotNull(res);
            Assert.Equal("Commit", res["query"]?.ToString());
            Assert.Equal(1, (int)res["count"]);
            Assert.Equal("True", res["values"]?["CommitOnExit"]?.ToString());
            var props = res["properties"] as JArray;
            Assert.NotNull(props);
            Assert.Single(props);
            Assert.Equal("CommitOnExit", props[0]?["name"]?.ToString());
        }

        [Fact]
        public void Query_WildcardMatching_ReturnsMatchingProperties()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                propertyName: "*Custom*");

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            var res = json["result"] as JObject;
            Assert.NotNull(res);
            Assert.Equal("*Custom*", res["query"]?.ToString());
            Assert.Equal(1, (int)res["count"]);
            Assert.Equal("CustomVal", res["values"]?["CustomProperty1"]?.ToString());
        }

        [Fact]
        public void Query_NotFound_ReturnsDidYouMeanSuggestions()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                query: "Descriptx");

            var json = JObject.Parse(raw);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("PropertyNotFound", json["error"]?["code"]?.ToString());
            Assert.Contains("Did you mean: 'Description'?", json["error"]?["message"]?.ToString());
            var dym = json["error"]?["didYouMean"] as JArray;
            Assert.NotNull(dym);
            Assert.Contains("Description", dym.Select(t => t.ToString()));
        }

        [Fact]
        public void SingleProperty_CaseInsensitiveMatching_Succeeds()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                propertyName: "description");

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("Description", json["result"]?["propertyName"]?.ToString());
            Assert.Equal("Customer transaction", json["result"]?["value"]?.ToString());
        }

        [Fact]
        public void SingleProperty_NotFound_ReturnsPropertyNotFoundCode()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                propertyName: "NonExistent");

            var json = JObject.Parse(raw);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("PropertyNotFound", json["error"]?["code"]?.ToString());
            Assert.Contains("NonExistent", json["error"]?["message"]?.ToString());
            Assert.Contains("Customer", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void MultiProperty_CommaSeparated_AllFound()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                propertyName: "Description, Name");

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("PropertiesRead", json["code"]?.ToString());
            var res = json["result"] as JObject;
            Assert.NotNull(res);
            var props = res["properties"] as JArray;
            Assert.NotNull(props);
            Assert.Equal(2, props.Count);
            Assert.Null(res["missingProperties"]);
        }

        [Fact]
        public void MultiProperty_PropertyNamesArray_PartialFound_ReturnsMatchedAndMissing()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                propertyNames: new[] { "Description", "MissingOne" },
                versionToken: string.Concat("tok", "en_v2"));

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            var res = json["result"] as JObject;
            Assert.NotNull(res);
            Assert.Equal(string.Concat("tok", "en_v2"), res["versionToken"]?.ToString());
            var props = res["properties"] as JArray;
            Assert.NotNull(props);
            Assert.Single(props);
            Assert.Equal("Description", props[0]?["name"]?.ToString());
            var missing = res["missingProperties"] as JArray;
            Assert.NotNull(missing);
            Assert.Single(missing);
            Assert.Equal("MissingOne", missing[0]?.ToString());
        }

        [Fact]
        public void MultiProperty_PropertyNamesArray_AllMissing_ReturnsPropertyNotFoundCode()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                propertyNames: new[] { "Bogus1", "Bogus2" });

            var json = JObject.Parse(raw);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("PropertyNotFound", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Projection_Minimal_ReturnsOnlyMinimalSubset()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                projection: "minimal");

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            var res = json["result"] as JObject;
            Assert.NotNull(res);
            Assert.Equal("minimal", res["projection"]?.ToString());
            var props = res["properties"] as JArray;
            Assert.NotNull(props);
            Assert.Equal(2, props.Count);
            var names = props.Select(p => p["name"]?.ToString()).ToList();
            Assert.Contains("Description", names);
            Assert.Contains("Name", names);
            Assert.DoesNotContain("CommitOnExit", names);
            Assert.DoesNotContain("CustomProperty1", names);
        }

        [Fact]
        public void Projection_Standard_ReturnsStandardSubset()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                projection: "standard");

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            var res = json["result"] as JObject;
            Assert.NotNull(res);
            Assert.Equal("standard", res["projection"]?.ToString());
            var props = res["properties"] as JArray;
            Assert.NotNull(props);
            Assert.Equal(3, props.Count);
            var names = props.Select(p => p["name"]?.ToString()).ToList();
            Assert.Contains("Description", names);
            Assert.Contains("Name", names);
            Assert.Contains("CommitOnExit", names);
            Assert.DoesNotContain("CustomProperty1", names);
        }

        [Fact]
        public void Projection_Full_ReturnsAllProperties()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(),
                target: "Customer",
                projection: "full");

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            var res = json["result"] as JObject;
            Assert.NotNull(res);
            var props = res["properties"] as JArray;
            Assert.NotNull(props);
            Assert.Equal(4, props.Count);
        }

        [Fact]
        public void DomainAlias_MatchesDomainBasedOnProperty()
        {
            var sample = new JObject
            {
                ["name"] = "CustomerId",
                ["control"] = "",
                ["properties"] = new JArray
                {
                    new JObject { ["name"] = "DomainBasedOn", ["value"] = "Id", ["type"] = "String" }
                }
            };

            string raw = PropertyService.ShapeGetPropertiesResult(
                sample,
                target: "CustomerId",
                propertyName: "BasedOn");

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("DomainBasedOn", json["result"]?["propertyName"]?.ToString());
            Assert.Equal("Id", json["result"]?["value"]?.ToString());
        }

        // ------------------------------------------------ issue #830: a grid's Conditions are ControlWhere

        // The property names a layout form lists for a grid (the listing read from a real one: 39 names). It has ControlWhere
        // and no Condition, and the closest spelling of Condition in it is "collection".
        private static readonly string[] GridPropertyNames =
        {
            "id", "controlName", "class", "collection", "rows", "dataSelectorReference", "dataSelectorParametersCode",
            "defaultTable", "[ElementName]", "SDSmartGrid_scrollDirection", "SDSmartGrid_SnapToGrid", "SDSmartGrid_multipleItems",
            "GxObjClass", "RenderingMode", "SAVE_STATE", "ControlBaseTable", "ControlOrder", "ControlWhere", "ControlUnique",
            "AllowDrop", "AllowDrag", "NotifyContextChange", "title", "align", "GxWidth", "GxHeight", "cellPadding", "cellSpacing",
            "SflRender", "multi_columns", "multi_columns_xs", "multi_columns_sm", "multi_columns_md", "multi_columns_lg",
            "PROP_EMPTYDATASETTEXT", "AllowCollapsing", "Collapsed", "idIsSDControl", "IsAbstractLayoutControl"
        };

        // A control that has no ControlWhere, such as a table of a layout form.
        private static readonly string[] TablePropertyNames = { "id", "controlName", "class", "[ElementName]", "width", "height" };

        private static JObject ControlProps(string[] names, string control = "GridProva")
        {
            return new JObject
            {
                ["name"] = "PainelLista",
                ["control"] = control,
                ["properties"] = new JArray(names.Select(n =>
                    new JObject { ["name"] = n, ["value"] = n == "controlName" ? control : "", ["type"] = "System.String", ["readOnly"] = false }))
            };
        }

        private static JObject GridControlProps() => ControlProps(GridPropertyNames);

        [Theory]
        [InlineData("Condition")]
        [InlineData("condition")]
        [InlineData("Conditions")]
        [InlineData(" Where ")]
        [InlineData("Grid_Condition")]
        public void SingleProperty_ConditionsLikeNameOnAControlThatListsControlWhere_OffersControlWhereFirst(string requested)
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                GridControlProps(), target: "PainelLista", controlName: "GridProva", propertyName: requested);

            var error = JObject.Parse(raw)["error"] as JObject;
            Assert.Equal("PropertyNotFound", error["code"]?.ToString());
            var suggestions = ((JArray)error["didYouMean"]).Select(t => t.ToString()).ToList();
            Assert.Equal("ControlWhere", suggestions[0]);
            Assert.True(suggestions.Count <= 3);
            Assert.Contains("'ControlWhere'", error["message"]?.ToString());
            Assert.Contains("ControlWhere", error["hint"]?.ToString());
            Assert.Contains("expectedVersion", error["hint"]?.ToString());
            Assert.Contains("<condition>", error["hint"]?.ToString());
            var first = error["nextSteps"][0];
            Assert.Equal("genexus_properties", first["tool"]?.ToString());
            Assert.Equal("ControlWhere", first["args"]?["propertyName"]?.ToString());
            Assert.Equal("GridProva", first["args"]?["control"]?.ToString());
        }

        [Fact]
        public void ControlWhere_IsAPropertyOfTheGrid_AndIsFound()
        {
            var json = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                GridControlProps(), target: "PainelLista", controlName: "GridProva", propertyName: "ControlWhere"));

            Assert.Equal("PropertiesRead", json["code"]?.ToString());
            Assert.Null(json["error"]);
        }

        [Fact]
        public void MultiProperty_AllMissingWithAConditionsName_OffersControlWhere_AndKeepsTheSuggestionOfTheOtherName()
        {
            // "Where" is a piece of ControlWhere and "Order" a piece of ControlOrder.
            string raw = PropertyService.ShapeGetPropertiesResult(
                GridControlProps(), target: "PainelLista", controlName: "GridProva",
                propertyNames: new[] { "Where", "Order" });

            var error = JObject.Parse(raw)["error"] as JObject;
            Assert.Equal("PropertyNotFound", error["code"]?.ToString());
            var suggestions = ((JArray)error["didYouMean"]).Select(t => t.ToString()).ToList();
            Assert.Equal("ControlWhere", suggestions[0]);
            Assert.Contains("ControlOrder", suggestions);
            Assert.True(suggestions.Count <= 3);
            Assert.Contains("ControlWhere", error["hint"]?.ToString());
        }

        [Fact]
        public void Query_ConditionsNameOnAControlThatListsControlWhere_OffersControlWhere()
        {
            string raw = PropertyService.ShapeGetPropertiesResult(
                GridControlProps(), target: "PainelLista", controlName: "GridProva", query: "Condition");

            var error = JObject.Parse(raw)["error"] as JObject;
            Assert.Equal("PropertyNotFound", error["code"]?.ToString());
            Assert.Equal("ControlWhere", ((JArray)error["didYouMean"])[0].ToString());
            Assert.Contains("ControlWhere", error["hint"]?.ToString());
        }

        // What is offered follows the list of the control, not the kind of element it is: a control whose list has no
        // ControlWhere (a table) is not sent to a property it does not have.
        [Theory]
        [InlineData("Condition")]
        [InlineData("Conditions")]
        [InlineData("ControlWhere")]
        public void ConditionsLikeName_OnAControlWhoseListHasNoControlWhere_KeepsTheGenericRefusal(string requested)
        {
            var error = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                ControlProps(TablePropertyNames, "TabelaProva"), target: "PainelLista", controlName: "TabelaProva", propertyName: requested))["error"] as JObject;

            Assert.Equal("PropertyNotFound", error["code"]?.ToString());
            Assert.DoesNotContain("ControlWhere", error["hint"]?.ToString());
            Assert.DoesNotContain("ControlWhere", (error["didYouMean"] as JArray)?.Select(t => t.ToString()) ?? Enumerable.Empty<string>());
            Assert.DoesNotContain("ControlWhere", error["nextSteps"].Select(s => s["args"]?["propertyName"]?.ToString() ?? string.Empty));
        }

        [Fact]
        public void ConditionsLikeName_OnAnObjectOrAVariable_KeepsTheGenericRefusal()
        {
            var onObject = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(), target: "Customer", propertyName: "Condition"))["error"] as JObject;
            Assert.DoesNotContain("ControlWhere", onObject["hint"]?.ToString());

            var onVariable = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                GridControlProps(), target: "PainelLista", controlName: "&Filtro", propertyName: "Condition"))["error"] as JObject;
            Assert.DoesNotContain("ControlWhere", onVariable["hint"]?.ToString());
        }

        // The refusal of a classic grid and of a simplegrid is the same: both list ControlWhere, and nothing here sends the
        // caller to the Conditions part (nothing proves that part belongs to a grid).
        [Fact]
        public void TheRefusalOfAControlThatListsControlWhere_NeverSendsTheCallerToTheConditionsPart()
        {
            var error = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                GridControlProps(), target: "PainelLista", controlName: "GridProva", propertyName: "Condition"))["error"] as JObject;

            Assert.DoesNotContain("part=Conditions", error["hint"]?.ToString());
            Assert.DoesNotContain("classic", error["hint"]?.ToString());
            Assert.DoesNotContain(error["nextSteps"].Select(s => s["tool"]?.ToString()), tool => tool == "genexus_read");
        }

        [Fact]
        public void SomethingElseMissingOnAControl_KeepsTheGenericRefusal()
        {
            var error = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                GridControlProps(), target: "PainelLista", controlName: "GridProva", propertyName: "Bogus"))["error"] as JObject;

            Assert.Equal("PropertyNotFound", error["code"]?.ToString());
            Assert.DoesNotContain("ControlWhere", error["hint"]?.ToString());
        }

        [Theory]
        [InlineData("minimal")]
        [InlineData("standard")]
        public void Projection_WithNoneOfItsNamesOnTheControl_SaysHowManyPropertiesItHasAndWhichOnes(string projection)
        {
            // None of these names is in the minimal or the standard set (the standard one lists "Collection").
            var grid = new JObject
            {
                ["name"] = "PainelLista",
                ["control"] = "GridProva",
                ["properties"] = new JArray
                {
                    new JObject { ["name"] = "controlName", ["value"] = "GridProva", ["type"] = "System.String", ["readOnly"] = false },
                    new JObject { ["name"] = "MaxRows", ["value"] = "10", ["type"] = "System.Int32", ["readOnly"] = false },
                    new JObject { ["name"] = "InfiniteScrolling", ["value"] = "One page at a time", ["type"] = "System.String", ["readOnly"] = false }
                }
            };

            var json = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                grid, target: "PainelLista", controlName: "GridProva", projection: projection));

            Assert.Equal("ok", json["status"]?.ToString());
            var result = (JObject)json["result"];
            Assert.Empty((JArray)result["properties"]);
            Assert.Equal(3, (int)result["omittedProperties"]);
            Assert.Contains("3 properties", result["note"]?.ToString());
            Assert.Contains("projection=" + projection, result["note"]?.ToString());
            var names = ((JArray)result["availablePropertyNames"]).Select(t => t.ToString()).ToList();
            Assert.Equal(new[] { "controlName", "MaxRows", "InfiniteScrolling" }, names);
            Assert.Null(result["availablePropertyNamesTruncated"]);
        }

        [Fact]
        public void Projection_WithManyUnlistedProperties_CapsTheNamesItOffers()
        {
            var props = new JArray();
            for (int i = 0; i < 75; i++) props.Add(new JObject { ["name"] = "Custom" + i, ["value"] = "v", ["type"] = "System.String", ["readOnly"] = false });
            var full = new JObject { ["name"] = "Big", ["control"] = "", ["properties"] = props };

            var result = (JObject)JObject.Parse(PropertyService.ShapeGetPropertiesResult(full, target: "Big", projection: "minimal"))["result"];

            Assert.Equal(75, (int)result["omittedProperties"]);
            Assert.Equal(60, ((JArray)result["availablePropertyNames"]).Count);
            Assert.True((bool)result["availablePropertyNamesTruncated"]);
        }

        [Fact]
        public void Projection_ThatKeepsSomePropertiesAnswersAsItAlwaysDid()
        {
            // The common case (a projection of an object that has some of the common names) gets no new field.
            var result = (JObject)JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                SamplePropsResult(), target: "Customer", projection: "minimal"))["result"];

            Assert.Equal(2, ((JArray)result["properties"]).Count);
            Assert.Null(result["omittedProperties"]);
            Assert.Null(result["note"]);
            Assert.Null(result["availablePropertyNames"]);
        }

        [Fact]
        public void Projection_ThatKeepsEverything_AddsNoOmissionFields()
        {
            var props = new JObject
            {
                ["name"] = "Customer",
                ["control"] = "",
                ["properties"] = new JArray
                {
                    new JObject { ["name"] = "Description", ["value"] = "d", ["type"] = "String", ["readOnly"] = false },
                    new JObject { ["name"] = "Name", ["value"] = "n", ["type"] = "String", ["readOnly"] = false }
                }
            };

            var result = (JObject)JObject.Parse(PropertyService.ShapeGetPropertiesResult(props, target: "Customer", projection: "minimal"))["result"];

            Assert.Equal(2, ((JArray)result["properties"]).Count);
            Assert.Null(result["omittedProperties"]);
            Assert.Null(result["note"]);
        }

        [Fact]
        public void Projection_OnAnObjectWithNoPropertiesAtAll_StaysAnEmptyAnswerWithoutANote()
        {
            var result = (JObject)JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                new JObject { ["name"] = "Empty", ["control"] = "", ["properties"] = new JArray() }, target: "Empty", projection: "minimal"))["result"];

            Assert.Empty((JArray)result["properties"]);
            Assert.Null(result["note"]);
            Assert.Null(result["omittedProperties"]);
        }

        private static JObject SamplePropsResult()
        {
            return new JObject
            {
                ["name"] = "Customer",
                ["control"] = "",
                ["properties"] = new JArray
                {
                    new JObject { ["name"] = "Description", ["value"] = "Customer transaction", ["type"] = "String", ["readOnly"] = false },
                    new JObject { ["name"] = "Name", ["value"] = "Customer", ["type"] = "String", ["readOnly"] = false },
                    new JObject { ["name"] = "CommitOnExit", ["value"] = "True", ["type"] = "Boolean", ["readOnly"] = false },
                    new JObject { ["name"] = "CustomProperty1", ["value"] = "CustomVal", ["type"] = "String", ["readOnly"] = false }
                }
            };
        }

        // issue #701: File objects expose NetExtractFolder/NetCoreExtractFolder as the SDK
        // DirectoryType, a class with a string TypeConverter. Values the IDE stores and
        // `get` returns must pass validation and coerce to a DirectoryType.
        [Theory]
        [InlineData("NetExtractFolder", "bin")]
        [InlineData("NetCoreExtractFolder", "bin")]
        [InlineData("NetExtractFolder", ".")]
        [InlineData("NetCoreExtractFolder", ".\\")]
        [InlineData("NetExtractFolder", "")]
        public void DirectoryTypeValuesAreAcceptedAndCoerced(string propertyName, string value)
        {
            var type = typeof(Artech.Genexus.Common.CustomTypes.DirectoryType);
            dynamic container = Container(
                Property(propertyName, new Artech.Genexus.Common.CustomTypes.DirectoryType("bin"), type, readOnly: false));

            Assert.Null(PropertyService.ValidatePropertyWrite(container, propertyName, value));

            Assert.True(PropertyService.TryConvertToType(value, type, out object converted));
            var directory = Assert.IsType<Artech.Genexus.Common.CustomTypes.DirectoryType>(converted);
            Assert.Equal(value, directory.Location);
        }

        // issue #701, live: on a File whose folder was never set the SDK Property has no
        // Definition and a null Value, so the raw string was stored into the DirectoryType
        // property, never persisted, and the single-property set still reported success.
        // Uses the real SDK property bag so the entry shape matches the KB.
        [Fact]
        public void UnsetDirectoryTypePropertyCoercesFromTheSdkDescriptor()
        {
            var bag = new Artech.Common.Properties.PropertiesObject();
            bag.AddDefinition(new Artech.Common.Properties.PropDefinition("NetExtractFolder", typeof(Artech.Genexus.Common.CustomTypes.DirectoryType)));
            Assert.Null(bag.GetPropertyValue("NetExtractFolder"));
            dynamic container = Container();
            ((IDictionary<string, object>)container)["Properties"] = bag.Properties;

            Assert.Equal(typeof(Artech.Genexus.Common.CustomTypes.DirectoryType), PropertyService.GetPropertyTargetType(container, "NetExtractFolder"));
            Assert.Equal(typeof(Artech.Genexus.Common.CustomTypes.DirectoryType), PropertyService.ResolvePropertyEntryType(bag.Properties.Single()));
            Assert.True(PropertyService.TryConvertToType(".\\", PropertyService.GetPropertyTargetType(container, "NetExtractFolder"), out object coerced));
            Assert.Equal(".\\", Assert.IsType<Artech.Genexus.Common.CustomTypes.DirectoryType>(coerced).Location);
            Assert.Null(PropertyService.ValidatePropertyWrite(container, "NetExtractFolder", "bin"));
        }

        [Fact]
        public void ClassWithoutStringConverterIsStillRejected()
        {
            dynamic container = Container(
                Property("Opaque", new object(), typeof(System.IO.MemoryStream), readOnly: false));

            Assert.StartsWith("InvalidPropertyValue", PropertyService.ValidatePropertyWrite(container, "Opaque", "bin"), StringComparison.Ordinal);
        }

        private static ExpandoObject Container(params ExpandoObject[] properties)
        {
            dynamic result = new ExpandoObject();
            result.Properties = properties;
            return result;
        }

        private static ExpandoObject Property(string name, object value, Type type, bool readOnly)
        {
            dynamic result = new ExpandoObject();
            dynamic definition = new ExpandoObject();
            definition.Type = type;
            definition.ReadOnly = readOnly;
            result.Name = name;
            result.Value = value;
            result.Definition = definition;
            return result;
        }
    }
}
