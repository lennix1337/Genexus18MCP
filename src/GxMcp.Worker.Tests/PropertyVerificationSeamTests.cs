using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The rules of the property verification that decides whether a rollback put the object back, run on snapshots built from plain
    /// data (the object XML, the parts and the variables text). Each test names the rule it pins. The snapshots are built through
    /// their private constructors, the way PropertyServiceWwpTemplateWiringTests builds an ObjectMoveSnapshot.
    /// </summary>
    public class PropertyVerificationSeamTests
    {
        private const string UrlAccessYes = "<Object><Properties><Property><Name>URLAccess</Name><Value>Yes</Value></Property></Properties></Object>";
        private const string UrlAccessNo = "<Object><Properties><Property><Name>URLAccess</Name><Value>No</Value></Property></Properties></Object>";

        private static readonly ConstructorInfo SnapshotConstructor = typeof(ObjectMoveSnapshot).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(Dictionary<string, ObjectMoveSnapshot.PartSnapshot>), typeof(byte[]), typeof(byte[]) }, null);

        private static readonly ConstructorInfo PartConstructor = typeof(ObjectMoveSnapshot.PartSnapshot).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(string), typeof(string), typeof(string), typeof(byte[]), typeof(byte[]), typeof(string) }, null);

        // The variables text is an internal property with a private setter.
        private static readonly PropertyInfo VariablesTextProperty = typeof(ObjectMoveSnapshot).GetProperty(
            "VariablesText", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

        private static ObjectMoveSnapshot Snapshot(string objectXml, string variablesText, params ObjectMoveSnapshot.PartSnapshot[] parts)
        {
            var byKey = parts.ToDictionary(part => part.Key, StringComparer.OrdinalIgnoreCase);
            byte[] xml = Encoding.UTF8.GetBytes(ObjectMoveSnapshot.NormalizeObjectXml(objectXml));
            var snapshot = (ObjectMoveSnapshot)SnapshotConstructor.Invoke(new object[] { byKey, xml, new byte[0] });
            VariablesTextProperty.GetSetMethod(true).Invoke(snapshot, new object[] { variablesText });
            return snapshot;
        }

        // verificationFormat is "source" for a text part (its lines are compared and shown) and "xml" for the others.
        private static ObjectMoveSnapshot.PartSnapshot Part(string key, string name, string verificationFormat, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            return (ObjectMoveSnapshot.PartSnapshot)PartConstructor.Invoke(new object[] { key, name, verificationFormat, data, data, verificationFormat });
        }

        [Fact]
        public void AnIdenticalObjectVerifiesWithNothingChangedAndNothingNotCompared()
        {
            var expected = Snapshot(UrlAccessYes, "&Count Numeric(4)\n", Part("events", "Events", "source", "Event 'Load'\n"));
            var actual = Snapshot(UrlAccessYes, "&Count Numeric(4)\n", Part("events", "Events", "source", "Event 'Load'\n"));

            var result = expected.ComparePropertyVerificationWith(actual);

            Assert.True(result.Equal);
            Assert.Empty(result.ChangedParts);
            Assert.Empty(result.NotComparedParts);
        }

        [Fact]
        public void AChangedPropertyFailsTheVerificationAndIsNamedWithThePropertiesPrefix()
        {
            var expected = Snapshot(UrlAccessYes, string.Empty);
            var actual = Snapshot(UrlAccessNo, string.Empty);

            var result = expected.ComparePropertyVerificationWith(actual);

            Assert.False(result.Equal);
            Assert.Single(result.ChangedParts);
            Assert.StartsWith("Properties: ", result.ChangedParts[0].ToString());
        }

        [Fact]
        public void AWebFormOrDocumentationDifferenceIsNotComparedAndIsListed()
        {
            var expected = Snapshot(UrlAccessYes, string.Empty,
                Part("web", "WebForm", "xml", "<WebForm>a</WebForm>"), Part("doc", "Documentation", "xml", "before"));
            var actual = Snapshot(UrlAccessYes, string.Empty,
                Part("web", "WebForm", "xml", "<WebForm>b</WebForm>"), Part("doc", "Documentation", "xml", "after"));

            var result = expected.ComparePropertyVerificationWith(actual);

            Assert.True(result.Equal);
            Assert.Empty(result.ChangedParts);
            Assert.Equal(new[] { "Documentation", "WebForm" }, result.NotComparedParts.OrderBy(name => name, StringComparer.Ordinal));
        }

        [Fact]
        public void ADifferentTextPartFailsTheVerificationAndItsLinesAreShown()
        {
            var expected = Snapshot(UrlAccessYes, string.Empty, Part("events", "Events", "source", "Event 'Load'\nDo Start\n"));
            var actual = Snapshot(UrlAccessYes, string.Empty, Part("events", "Events", "source", "Event 'Load'\nDo Stop\n"));

            var result = expected.ComparePropertyVerificationWith(actual);

            Assert.False(result.Equal);
            Assert.Equal("Events", result.ChangedParts[0].ToString());
            Assert.NotEmpty(result.ChangedLines);
        }

        [Fact]
        public void ADifferentVariablesTextFailsTheVerificationAndIsShownAsLines()
        {
            var expected = Snapshot(UrlAccessYes, "&Count Numeric(4)\n");
            var actual = Snapshot(UrlAccessYes, "&Count Character(4)\n");

            var result = expected.ComparePropertyVerificationWith(actual);

            Assert.False(result.Equal);
            Assert.Contains("Variables", result.ChangedParts.Select(part => part.ToString()));
            Assert.NotEmpty(result.ChangedLines);
        }
    }
}
