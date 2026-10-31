using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A clone carries the object-level properties that the parts do not: a Data Provider cloned part by part
    /// came back with an empty OutputSDT, so it no longer produced its collection. The properties are carried
    /// after the parts, and a clone that cannot take them is rolled back like one that cannot take a part.
    /// </summary>
    public class SaveAsObjectPropertiesTests
    {
        private sealed class PropertyCarryingCloner : SaveAsService.IObjectCloner, SaveAsService.IObjectPropertyCloner
        {
            public string PropertiesAnswer = "{\"status\":\"ok\",\"code\":\"PropertiesCloned\",\"result\":{\"properties\":[\"OutputSDT\"]}}";
            public List<string> Calls = new List<string>();
            public List<string> Deletes = new List<string>();
            public bool TargetCreated;

            public SaveAsService.SourceDescriptor FindSource(string name, string typeFilter) =>
                new SaveAsService.SourceDescriptor { Name = name, Type = "DataProvider", Parts = new[] { "Source" } };
            public bool TargetExists(string newName) => TargetCreated;
            public string CreateObject(string type, string newName) { TargetCreated = true; Calls.Add("create"); return "{\"status\":\"ok\"}"; }
            public string ClonePart(string sourceName, string newName, string partName, string typeFilter) { Calls.Add("part:" + partName); return "{\"status\":\"ok\"}"; }
            public string DeleteTarget(string newName, string typeFilter) { Deletes.Add(newName); TargetCreated = false; return "{\"status\":\"ok\"}"; }
            public SaveAsService.PatternInstanceDescriptor FindWwpInstance(string sourceName) => null;
            public string ApplyWwpPattern(string newName, SaveAsService.PatternInstanceDescriptor sourceInstance) => "{\"status\":\"ok\"}";
            public string CloneObjectProperties(string sourceName, string newName, string typeFilter) { Calls.Add("properties"); return PropertiesAnswer; }
        }

        [Fact]
        public void SaveAs_CarriesTheObjectPropertiesAfterTheParts_AndReportsThem()
        {
            var cloner = new PropertyCarryingCloner();
            var answer = JObject.Parse(new SaveAsService(cloner).SaveAs(new JObject { ["name"] = "Dp", ["newName"] = "DpCopy" }));

            Assert.Equal("SavedAs", answer["code"]?.ToString());
            Assert.Equal(new[] { "create", "part:Source", "properties" }, cloner.Calls);
            Assert.Equal("OutputSDT", answer["result"]?["created"]?["propertiesCloned"]?[0]?.ToString());
        }

        [Fact]
        public void SaveAs_RollsTheCloneBack_WhenItsPropertiesCannotBeCarried()
        {
            var cloner = new PropertyCarryingCloner
            {
                PropertiesAnswer = "{\"status\":\"error\",\"error\":{\"code\":\"OutputSdtNotFound\",\"message\":\"no SDT\"}}"
            };
            var answer = JObject.Parse(new SaveAsService(cloner).SaveAs(new JObject { ["name"] = "Dp", ["newName"] = "DpCopy" }));

            Assert.NotEqual("SavedAs", answer["code"]?.ToString());
            Assert.Equal(new[] { "DpCopy" }, cloner.Deletes);
        }

        [Fact]
        public void SaveAs_StaysAsBefore_WhenTheClonerCarriesNoProperties()
        {
            var cloner = new PropertyCarryingCloner { PropertiesAnswer = "{\"status\":\"ok\",\"code\":\"Skipped\",\"result\":{\"skipped\":true}}" };
            var answer = JObject.Parse(new SaveAsService(cloner).SaveAs(new JObject { ["name"] = "Dp", ["newName"] = "DpCopy" }));

            Assert.Equal("SavedAs", answer["code"]?.ToString());
            Assert.Null(answer["result"]?["created"]?["propertiesCloned"]);
        }
    }
}
