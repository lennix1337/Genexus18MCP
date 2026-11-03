using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Friction 2026-05-22 #62: every warning/gotcha emit site must carry a
    // stable `code` (PascalCase, prefixed Gotcha* or Lint*) and a `docUrl`
    // pointing at genexus://kb/tool-help/gotchas/<code>. This test enumerates
    // the codes the worker emits and asserts both invariants.
    public class GotchaCodeStandardizationTests
    {
        // Every code the worker is allowed to emit (audited 2026-05-22).
        // Adding a new emit site? Append the code here so the next test
        // ensures it has Gotcha/Lint prefix + a doc URL.
        public static IEnumerable<object[]> AllEmittedCodes()
        {
            yield return new object[] { GotchaCodes.LintKbCharsetLossy };
            yield return new object[] { GotchaCodes.LintSpc0150ForEachAttributeWrite };
            yield return new object[] { GotchaCodes.GotchaGxButtonHtmlFormCustomEvent };
            yield return new object[] { GotchaCodes.GotchaGxAttributeHtmlFormDiscreteReadOnly };
            yield return new object[] { GotchaCodes.GotchaGxAttributeMissingDataField };
            yield return new object[] { GotchaCodes.GotchaUnknownControlType };
            yield return new object[] { GotchaCodes.GotchaWebComponentMissingObjectCall };
            yield return new object[] { GotchaCodes.GotchaHtmlFormatScriptStripped };
            yield return new object[] { GotchaCodes.GotchaCellOutsideTable };
            yield return new object[] { GotchaCodes.GotchaDuplicateControlName };
            yield return new object[] { GotchaCodes.GotchaGridConditionIgnored };
            yield return new object[] { GotchaCodes.GotchaIdeObjectOpenInEditor };
            yield return new object[] { GotchaCodes.GotchaIdeActiveOnKb };
        }

        [Theory]
        [MemberData(nameof(AllEmittedCodes))]
        public void EveryEmittedCode_IsPascalCase_AndPrefixedGotchaOrLint(string code)
        {
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.True(code.StartsWith("Gotcha", StringComparison.Ordinal)
                        || code.StartsWith("Lint", StringComparison.Ordinal),
                $"Code '{code}' must be prefixed with 'Gotcha' or 'Lint'.");
            // PascalCase: first char upper, no underscores/dashes.
            Assert.True(char.IsUpper(code[0]), $"Code '{code}' must start with an uppercase letter.");
            Assert.DoesNotContain("_", code);
            Assert.DoesNotContain("-", code);
        }

        [Theory]
        [MemberData(nameof(AllEmittedCodes))]
        public void EveryEmittedCode_ResolvesToCanonicalDocUrl(string code)
        {
            string docUrl = GotchaCodes.DocUrlFor(code);
            Assert.Equal($"genexus://kb/tool-help/gotchas/{code}", docUrl);
        }

        [Fact]
        public void LayoutGotchaScanner_AllGotchaCodes_AreInTheRegistry()
        {
            // Cross-check: every code the scanner actually emits during a scan
            // must be present in GotchaCodes (so the doc URL helper resolves).
            // Inputs cover the scanner's hot rules in one pass.
            string layoutXml = @"<Form type='html'>
  <gxButton id='b1' OnClickEvent=""'MyEvent'"" />
  <gxAttribute ControlType='Radio Button' AttID='var:1' id='r1' />
  <gxAttribute id='nobind' />
  <gxAttribute ControlType='RadioButton' AttID='var:2' id='bad' />
  <gxEmbeddedPage id='emb' />
  <gxTextBlock Format='HTML' id='tb'><![CDATA[<script>alert(1)</script>]]></gxTextBlock>
  <cell id='orphan' />
  <gxButton id='dup' />
  <gxButton id='dup' />
</Form>";
            var hits = LayoutGotchaScanner.Scan(layoutXml, _ => null);
            Assert.NotEmpty(hits);
            var allCodes = new HashSet<string>(StringComparer.Ordinal)
            {
                GotchaCodes.LintKbCharsetLossy,
                GotchaCodes.LintSpc0150ForEachAttributeWrite,
                GotchaCodes.GotchaGxButtonHtmlFormCustomEvent,
                GotchaCodes.GotchaGxAttributeHtmlFormDiscreteReadOnly,
                GotchaCodes.GotchaGxAttributeMissingDataField,
                GotchaCodes.GotchaUnknownControlType,
                GotchaCodes.GotchaWebComponentMissingObjectCall,
                GotchaCodes.GotchaHtmlFormatScriptStripped,
                GotchaCodes.GotchaCellOutsideTable,
                GotchaCodes.GotchaDuplicateControlName,
                GotchaCodes.GotchaGridConditionIgnored
            };
            foreach (var h in hits)
            {
                Assert.True(allCodes.Contains(h.Code), $"Scanner emitted unregistered code '{h.Code}'.");
                Assert.Equal($"genexus://kb/tool-help/gotchas/{h.Code}", h.DocUrl);
            }
        }
    }
}
