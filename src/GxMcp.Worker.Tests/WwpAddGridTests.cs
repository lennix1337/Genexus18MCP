using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #420: add_grid over an SDT collection.
    public class WwpAddGridTests
    {
        private const string Reference = "447527b5-9210-4523-898b-5dccb17be60a-Sales.OrderLine";

        private static XDocument Instance() => XDocument.Parse("<instance><table name='TableContent'/><table name='TableActions'/></instance>");

        private static JObject Args(string extra = "")
        {
            var args = JObject.Parse("{collection:'&Lines',sdt:'OrderLine',containerName:'TableContent',deleteAction:true," +
                "columns:[{item:'ProdId',description:'Code'},'ProdName',{item:'Quantity'}]" + extra + "}");
            args["_sdtReference"] = Reference;
            args["_sdtItems"] = new JArray("ProdId", "ProdName", "Quantity", "Price");
            return args;
        }

        private static string Code(JObject result) => result["code"]?.ToString();

        [Fact]
        public void WritesTheWizardShape()
        {
            var doc = Instance();
            JObject result = WwpActionService.ApplyAddGridXml(doc, Args());
            Assert.True(result["error"] == null, result.ToString());

            XElement grid = doc.Descendants("grid").Single();
            Assert.Equal("&Lines", grid.Attribute("SDTCollection")!.Value);
            Assert.Equal("SectionGrid GridNoBorderCell", grid.Attribute("cellThemeClass")!.Value);
            var columns = grid.Elements("gridVariable").ToList();
            Assert.Equal(new[] { "ProdId", "ProdName", "Quantity" }, columns.Select(c => c.Attribute("sdtItem")!.Value));
            Assert.Equal(new[] { "Code", "ProdName", "Quantity" }, columns.Select(c => c.Attribute("description")!.Value));
            Assert.All(columns, c => { Assert.Equal("Lines", c.Attribute("name")!.Value); Assert.Equal(Reference, c.Attribute("domain")!.Value); });
            XElement delete = grid.Element("userAction")!;
            Assert.Equal("UDelete", delete.Attribute("name")!.Value);
            Assert.Equal("Font icon", delete.Attribute("imageType")!.Value);
            Assert.Equal("TableContent", grid.Parent!.Attribute("name")!.Value);
        }

        [Fact]
        public void NoDeleteActionUnlessAsked()
        {
            var doc = Instance();
            WwpActionService.ApplyAddGridXml(doc, Args(",deleteAction:false"));
            Assert.Empty(doc.Descendants("userAction"));
        }

        [Theory]
        [InlineData("{collection:'Lines',sdt:'S',containerName:'T',columns:['A']}", "InvalidGridCollection")]
        [InlineData("{sdt:'S',containerName:'T',columns:['A']}", "InvalidGridCollection")]
        [InlineData("{collection:'&L',containerName:'T',columns:['A']}", "MissingGridSdt")]
        [InlineData("{collection:'&L',sdt:'S',columns:['A']}", "MissingGridContainer")]
        [InlineData("{collection:'&L',sdt:'S',containerName:'T'}", "MissingGridColumns")]
        [InlineData("{collection:'&L',sdt:'S',containerName:'T',columns:[]}", "MissingGridColumns")]
        [InlineData("{collection:'&L',sdt:'S',containerName:'T',columns:['A','a']}", "DuplicateGridColumn")]
        [InlineData("{collection:'&L',sdt:'S',containerName:'T',columns:[{description:'x'}]}", "InvalidGridColumn")]
        [InlineData("{collection:'&L',sdt:'S',containerName:'T',columns:['A'],deleteAction:'yes'}", "InvalidDeleteAction")]
        public void InvalidRequestsAreRefused(string json, string code)
            => Assert.Equal(code, Code(WwpActionService.ApplyAddGridXml(Instance(), JObject.Parse(json))));

        [Fact]
        public void UnresolvedSdtColumnNotInSdtAndMissingContainerAreRefused()
        {
            var noSdt = Args(); noSdt.Remove("_sdtReference");
            Assert.Equal("GridSdtUnresolved", Code(WwpActionService.ApplyAddGridXml(Instance(), noSdt)));
            var wrongItem = Args(); wrongItem["columns"] = new JArray("Nope");
            Assert.Equal("GridColumnNotInSdt", Code(WwpActionService.ApplyAddGridXml(Instance(), wrongItem)));
            var missing = Args(); missing["containerName"] = "Nope";
            Assert.Equal("GridContainerNotFound", Code(WwpActionService.ApplyAddGridXml(Instance(), missing)));
        }

        [Fact]
        public void ActionGroupAsContainerAndSecondGridOverTheSameCollectionAreRefused()
        {
            var group = XDocument.Parse("<instance><actionGroup name='TableContent'/></instance>");
            Assert.Equal("GridContainerNotTable", Code(WwpActionService.ApplyAddGridXml(group, Args())));

            var doc = Instance();
            Assert.Null(WwpActionService.ApplyAddGridXml(doc, Args())["error"]);
            Assert.Equal("GridAlreadyExists", Code(WwpActionService.ApplyAddGridXml(doc, Args())));
        }

        private static (XDocument doc, List<WwpActionService.GridColumn> columns) Written()
        {
            var doc = Instance();
            var args = Args();
            WwpActionService.ApplyAddGridXml(doc, args);
            WwpActionService.ValidateAddGridArgs(args, out _, out var columns);
            return (doc, columns);
        }

        [Fact]
        public void VerificationAcceptsTheWrittenGrid()
        {
            var (doc, columns) = Written();
            Assert.True((bool)WwpActionService.VerifyAddGrid(doc, "&Lines", Reference, columns, true)["confirmed"]!);
        }

        [Fact]
        public void VerificationRejectsALostReorderedOrRenamedColumnALostSdtOrALostDeleteAction()
        {
            var (doc, columns) = Written();
            XElement Grid(XDocument d) => d.Descendants("grid").Single();
            bool Ok(XDocument d, bool delete = true) => WwpActionService.VerifyAddGrid(d, "&Lines", Reference, columns, delete)["confirmed"] != null;

            var lost = new XDocument(doc); Grid(lost).Elements("gridVariable").Last().Remove();
            Assert.False(Ok(lost));

            var reordered = new XDocument(doc); var first = Grid(reordered).Elements("gridVariable").First(); first.Remove(); Grid(reordered).Elements("gridVariable").Last().AddAfterSelf(first);
            Assert.False(Ok(reordered));

            var renamed = new XDocument(doc); Grid(renamed).Elements("gridVariable").First().SetAttributeValue("sdtItem", "Other");
            Assert.False(Ok(renamed));

            var noSdt = new XDocument(doc); Grid(noSdt).Elements("gridVariable").First().SetAttributeValue("domain", "x-Other");
            Assert.False(Ok(noSdt));

            var noDelete = new XDocument(doc); Grid(noDelete).Element("userAction")!.Remove();
            Assert.False(Ok(noDelete));
            Assert.True(Ok(noDelete, delete: false));

            Assert.False(WwpActionService.VerifyAddGrid(new XDocument(Instance()), "&Lines", Reference, columns, true)["confirmed"] != null);
        }

        [Fact]
        public void TypedAttributesAreWrittenWithTheirTypeAndNotAsDisplayedText()
        {
            // Source guard: domain is a KBObject and cellThemeClass/imageClass are
            // DropDownValueOptionCustomType; writing their text fails on load with InvalidCastException.
            string source = File.ReadAllText(FindSource("WwpActionService.AddGrid.cs"));
            Assert.Contains("ApplySemanticAttributeObject(variable, \"domain\", sdt)", source);
            Assert.Contains("SetNativeDropDown(grid, \"cellThemeClass\"", source);
            Assert.Contains("SetNativeDropDown(delete, \"imageClass\"", source);
            Assert.DoesNotContain("SetNativeAttribute(variable, \"domain\"", source);
            Assert.DoesNotContain("SetNativeAttribute(grid, \"cellThemeClass\"", source);
            Assert.DoesNotContain("SetNativeAttribute(delete, \"imageClass\"", source);
        }

        [Fact]
        public void ProjectionThatLeavesTheParentWebFormUnchangedFails()
        {
            string source = File.ReadAllText(FindSource("WwpActionService.AddGrid.cs"));
            Assert.Contains("did not change after projection", source);
        }

        [Fact]
        public void SetNativeDropDownResolvesOptionTypeFromLoadedPackageAssembly()
        {
            var assemblyName = new AssemblyName("WwpFakePackage_" + Guid.NewGuid().ToString("N"));
            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
            var typeBuilder = moduleBuilder.DefineType("DVelop.Patterns.WorkWithPlus.DropDownValueOptionCustomType", TypeAttributes.Public);
            var field = typeBuilder.DefineField("_value", typeof(string), FieldAttributes.Private);
            var property = typeBuilder.DefineProperty("Value", PropertyAttributes.HasDefault, typeof(string), null);
            var getter = typeBuilder.DefineMethod("get_Value", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(string), Type.EmptyTypes);
            var getterIl = getter.GetILGenerator();
            getterIl.Emit(OpCodes.Ldarg_0);
            getterIl.Emit(OpCodes.Ldfld, field);
            getterIl.Emit(OpCodes.Ret);
            var setter = typeBuilder.DefineMethod("set_Value", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, new[] { typeof(string) });
            var setterIl = setter.GetILGenerator();
            setterIl.Emit(OpCodes.Ldarg_0);
            setterIl.Emit(OpCodes.Ldarg_1);
            setterIl.Emit(OpCodes.Stfld, field);
            setterIl.Emit(OpCodes.Ret);
            property.SetGetMethod(getter);
            property.SetSetMethod(setter);
            Type optionType = typeBuilder.CreateType();

            var grid = new WwpEmptyNativeCommandTests.Element("grid");
            WwpActionService.SetNativeDropDown(grid, "cellThemeClass", "SectionGrid GridNoBorderCell");

            object applied = grid.Attributes.GetPropertyValue("cellThemeClass");
            Assert.NotNull(applied);
            Assert.Equal(optionType, applied.GetType());
            Assert.Equal("SectionGrid GridNoBorderCell", optionType.GetProperty("Value")!.GetValue(applied, null));
        }

        private static string FindSource(string file)
        {
            string dir = Directory.GetCurrentDirectory();
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "GxMcp.Worker", "Services", file)))
                dir = Path.GetDirectoryName(dir);
            return Path.Combine(dir!, "src", "GxMcp.Worker", "Services", file);
        }
    }
}
