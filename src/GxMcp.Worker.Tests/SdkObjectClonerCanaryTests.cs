using System;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Canary tests for SdkObjectCloner — guards against silent breakage when
    /// PatternApplyService or KBObject SDK surface gets renamed/refactored.
    ///
    /// Background: SdkObjectCloner historically used reflective lookups by method name
    /// (`HasWorkWithPlusInstance`, `Apply`) — neither of which existed on
    /// PatternApplyService. The reflection silently returned null, so
    /// `genexus_save_as includePatternInstance=true` was a quiet no-op.
    /// Direct calls now bind at compile time, but if PatternApplyService.ApplyPattern
    /// ever changes its signature these tests will fail loudly instead of returning
    /// a silent envelope.
    /// </summary>
    public class SdkObjectClonerCanaryTests
    {
        [Fact]
        public void PatternApplyService_ApplyPattern_HasCanonicalSignature()
        {
            // Production wiring: SdkObjectCloner.ApplyWwpPattern calls
            // _patterns.ApplyPattern(name, key, settings: null).
            // If this signature ever changes, fix the cloner.
            var t = typeof(PatternApplyService);
            var mi = t.GetMethod(
                "ApplyPattern",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(string), typeof(string), typeof(Newtonsoft.Json.Linq.JObject) },
                modifiers: null);

            Assert.NotNull(mi);
            Assert.Equal(typeof(string), mi.ReturnType);
        }

        [Fact]
        public void KBObjectPart_NameProperty_ExistsForPatternInstanceWalk()
        {
            // PatternAnalysisService.FindPatternPart walks obj.Parts and inspects p.Name; the linked-instance lookup
            // of SdkObjectCloner.FindWwpInstance (issue #800) starts from it.
            var t = typeof(Artech.Architecture.Common.Objects.KBObjectPart);
            var nameProp = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(nameProp);
            Assert.Equal(typeof(string), nameProp.PropertyType);
        }

        [Fact]
        public void ObjectService_CloneReportLayoutPart_HasCanonicalSignature()
        {
            // issue #406: SdkObjectCloner routes a report Procedure's Layout here instead of
            // through the WebForm write path. Renaming it would silently restore SaveAsPartFailed.
            var mi = typeof(ObjectService).GetMethod(
                "CloneReportLayoutPart",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(string), typeof(string), typeof(string) },
                modifiers: null);

            Assert.NotNull(mi);
            Assert.Equal(typeof(string), mi.ReturnType);
        }

        [Fact]
        public void SdkObjectCloner_PublicSurface_StableAcrossRefactors()
        {
            // The SaveAsService dispatcher constructs the cloner with three SDK services.
            // Guard the public ctor + IObjectCloner interface contract.
            var ctor = typeof(SdkObjectCloner).GetConstructor(new[] {
                typeof(ObjectService), typeof(WriteService), typeof(PatternApplyService)
            });
            Assert.NotNull(ctor);

            Assert.True(typeof(SaveAsService.IObjectCloner).IsAssignableFrom(typeof(SdkObjectCloner)),
                "SdkObjectCloner must implement SaveAsService.IObjectCloner.");
        }
    }
}
