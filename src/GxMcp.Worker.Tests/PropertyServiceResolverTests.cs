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
                versionToken: "token_v2");

            var json = JObject.Parse(raw);
            Assert.Equal("ok", json["status"]?.ToString());
            var res = json["result"] as JObject;
            Assert.NotNull(res);
            Assert.Equal("token_v2", res["versionToken"]?.ToString());
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
