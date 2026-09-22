using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Artech.Architecture.Common;
using Artech.Architecture.Common.Parts;
using Artech.Architecture.Common.Services;
using Artech.Genexus.Common;

namespace GxMcp.Worker.Services
{
    internal sealed class ModuleInstallPlanException : Exception
    {
        internal string Code { get; }
        internal ModuleInstallPlanException(string code, string message) : base(message) { Code = code; }
    }

    internal sealed class ModuleInstallObject
    {
        internal Guid Guid { get; set; }
        internal string Name { get; set; }
        internal string SimpleName { get; set; }
        internal Guid TypeGuid { get; set; }
    }

    // Read-only package inspection. Never extracts files or invokes import/build callbacks.
    internal sealed class ModuleInstallPackage
    {
        internal string FilePath { get; private set; }
        internal string Sha256 { get; private set; }
        internal ModulePackage Metadata { get; private set; }
        internal IReadOnlyList<ModuleInstallObject> Objects { get; private set; }
        internal IReadOnlyList<string> ResolutionWarnings { get; private set; } = Array.Empty<string>();
        private const long MaxXmlCharacters = 32 * 1024 * 1024;

        internal static ModuleInstallPackage Read(string path)
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string hash;
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                    file.Position = 0;
                    using (var package = Package.Open(file, FileMode.Open, FileAccess.Read))
                    {
                        XElement manifest = ReadXml(package, "ModuleManifest.mf");
                        XElement definition = ReadXml(package, "ModuleDefinition.xml");
                        if (manifest.Name != "ModulePackage" || definition.Name != "ExportFile") Fail("ModulePackageInvalid", "Unexpected package document root.");
                        string name = Text(manifest, "Name"), version = Text(manifest, "Version");
                        ValidateName(name);
                        ParseVersion(version);
                        Guid id = ParseGuid(Text(manifest, "ID"));
                        if (!bool.TryParse(Text(manifest, "HasDatabase"), out bool database)) Fail("ModulePackageInvalid", "The database capability is missing or invalid.");
                        if (database) Fail("ModulePackageUnsupported", "Packages containing database changes require a separate explicit workflow.");
                        var dependencies = new List<PackagedModuleDependency>();
                        var platforms = new List<ModulePlatform>();
                        foreach (XElement platform in manifest.Element("Platforms")?.Elements() ?? Enumerable.Empty<XElement>())
                        {
                            if (platform.Name != "ModulePlatform" || !int.TryParse(Text(platform, "Generator"), out int generator) || !int.TryParse(Text(platform, "Dbms"), out int dbms))
                                Fail("ModulePackageInvalid", "A module platform is invalid.");
                            platforms.Add(new ModulePlatform { Generator = int.Parse(Text(platform, "Generator")), Dbms = int.Parse(Text(platform, "Dbms")) });
                        }
                        foreach (XElement dep in manifest.Element("Dependencies")?.Elements() ?? Enumerable.Empty<XElement>())
                        {
                            if (dep.Name != "PackagedModuleDependency") Fail("ModulePackageInvalid", "Unknown dependency metadata.");
                            var item = new PackagedModuleDependency { Name = Text(dep, "Name"), Guid = ParseGuid(Text(dep, "Guid")), Version = Text(dep, "Version"), MinimumVersion = Text(dep, "MinimumVersion"), MaximumVersion = Text(dep, "MaximumVersion") };
                            ValidateName(item.Name);
                            foreach (string v in new[] { item.Version, item.MinimumVersion, item.MaximumVersion }.Where(v => !string.IsNullOrEmpty(v))) ParseVersion(v);
                            if (!string.IsNullOrEmpty(item.MinimumVersion) && !string.IsNullOrEmpty(item.MaximumVersion) && Compare(item.MinimumVersion, item.MaximumVersion) > 0) Fail("ModuleDependencyConflict", "A dependency version range is inverted.");
                            dependencies.Add(item);
                        }
                        // Export actions can run code during import. Allow only the structural export envelope.
                        foreach (XElement export in definition.DescendantsAndSelf("ExportFile"))
                            if (export.Elements().Any(e => e.Name != "KMW" && e.Name != "Source" && e.Name != "Objects" && e.Name != "Dependencies"))
                                Fail("ModulePackageUnsupported", "Import actions or unknown export sections are not supported.");
                        if (manifest.Elements().Any(e => !new[] { "ID", "Name", "Version", "Description", "Owner", "Author", "HasDatabase", "Platforms", "Dependencies", "ServerUrl", "Tags", "ProjectUrl", "LicenseUrl" }.Contains(e.Name.LocalName)))
                            Fail("ModulePackageUnsupported", "Package hooks are not supported.");
                        var objects = new List<ModuleInstallObject>();
                        foreach (XElement group in definition.Descendants("Objects"))
                        {
                            foreach (XElement obj in group.Elements())
                            {
                                if (obj.Name != "Object") Fail("ModulePackageUnsupported", "Non-object import operations are not supported.");
                                var item = new ModuleInstallObject { Guid = ParseGuid((string)obj.Attribute("guid")), TypeGuid = ParseGuid((string)obj.Attribute("type")), SimpleName = (string)obj.Attribute("name"), Name = (string)obj.Attribute("fullyQualifiedName") ?? (string)obj.Attribute("name") };
                                if (item.TypeGuid == ObjClass.Transaction || item.TypeGuid == ObjClass.Table) Fail("ModulePackageUnsupported", "Database object imports require a separate explicit workflow.");
                                if (string.IsNullOrWhiteSpace(item.Name) || objects.Any(o => o.Guid == item.Guid)) Fail("ModulePackageInvalid", "Missing or duplicate object identity.");
                                objects.Add(item);
                            }
                        }
                        XElement rootObject = definition.Element("Objects")?.Elements("Object").SingleOrDefault();
                        if (rootObject == null || ParseGuid((string)rootObject.Attribute("guid")) != id || (string)rootObject.Attribute("name") != name || (string)rootObject.Attribute("moduleVersion") != version)
                            Fail("ModulePackageInvalid", "Manifest and module definition identities or versions disagree.");
                        if (ParseGuid((string)rootObject.Attribute("type")) != KBObjectClasses.Module) Fail("ModulePackageInvalid", "The package root is not a Module object.");
                        foreach (XElement reference in definition.Descendants("Reference").Where(r => (string)r.Attribute("Type") == "Module"))
                        {
                            Guid dependencyId = ParseGuid((string)reference.Attribute("Id"));
                            var dependency = dependencies.SingleOrDefault(d => d.Guid == dependencyId);
                            XElement properties = reference.Element("Properties");
                            if (dependency == null || (string)properties?.Attribute("Name") != dependency.Name || !Same((string)properties?.Attribute("Version"), dependency.Version) || !Same((string)properties?.Attribute("MinimumVersion"), dependency.MinimumVersion) || !Same((string)properties?.Attribute("MaximumVersion"), dependency.MaximumVersion))
                                Fail("ModulePackageInvalid", "Definition dependencies disagree with the manifest.");
                        }
                        return new ModuleInstallPackage { FilePath = Path.GetFullPath(path), Sha256 = hash, Metadata = new ModulePackage { ID = id.ToString(), Name = name, Version = version, HasDatabase = false, Dependencies = dependencies, Platforms = platforms }, Objects = objects.AsReadOnly() };
                    }
                }
            }
            catch (ModuleInstallPlanException) { throw; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException || ex is ArgumentException || ex is InvalidOperationException || ex is System.IO.FileFormatException)
            {
                throw new ModuleInstallPlanException("ModulePackageInvalid", "The local OPC package could not be read or validated (" + ex.GetType().Name + ").");
            }
        }

        internal static IReadOnlyList<ModuleInstallPackage> Plan(string opcFile, string name, string version, string gxPath)
        {
            string sdk = gxPath ?? Environment.GetEnvironmentVariable("GX_PATH");
            string modules = string.IsNullOrWhiteSpace(sdk) ? null : Path.Combine(sdk, "Modules");
            var warnings = new List<string>();
            var root = string.IsNullOrWhiteSpace(opcFile) ? Resolve(new[] { modules }, name, version, null, null, warnings) : Read(opcFile);
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(root.Metadata.Name, name, StringComparison.OrdinalIgnoreCase)) Fail("ModulePackageIdentityMismatch", "The requested module name differs from the package.");
            if (!string.IsNullOrWhiteSpace(version) && Compare(root.Metadata.Version, version) != 0) Fail("ModulePackageIdentityMismatch", "The requested version differs from the package.");
            var result = new List<ModuleInstallPackage>();
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected = new Dictionary<string, ModuleInstallPackage>(StringComparer.OrdinalIgnoreCase);
            Action<ModuleInstallPackage> visit = null;
            visit = package =>
            {
                string key = package.Metadata.Name;
                if (visiting.Contains(key)) Fail("ModuleDependencyCycle", "The dependency graph contains a cycle.");
                if (selected.TryGetValue(key, out var previous))
                {
                    if (previous.Metadata.ID != package.Metadata.ID || Compare(previous.Metadata.Version, package.Metadata.Version) != 0 || previous.Sha256 != package.Sha256) Fail("ModuleDependencyConflict", "The dependency graph requests incompatible packages.");
                    return;
                }
                visiting.Add(key);
                foreach (var dep in package.Metadata.Dependencies)
                {
                    var child = Resolve(new[] { Path.GetDirectoryName(package.FilePath), modules }, dep.Name, dep.Version, dep.MinimumVersion, dep.MaximumVersion, warnings);
                    if (ParseGuid(child.Metadata.ID) != dep.Guid) Fail("ModuleDependencyConflict", "A dependency package has a different identity.");
                    visit(child);
                }
                visiting.Remove(key);
                selected.Add(key, package);
                result.Add(package);
            };
            visit(root);
            root.ResolutionWarnings = warnings.AsReadOnly();
            return result.AsReadOnly();
        }

        internal static bool Satisfies(string actual, string exact, string minimum, string maximum)
        {
            ParseVersion(actual);
            return (string.IsNullOrEmpty(exact) || Compare(actual, exact) == 0) && (string.IsNullOrEmpty(minimum) || Compare(actual, minimum) >= 0) && (string.IsNullOrEmpty(maximum) || Compare(actual, maximum) <= 0);
        }

        private static ModuleInstallPackage Resolve(IEnumerable<string> directories, string name, string exact, string minimum, string maximum, List<string> warnings)
        {
            ValidateName(name);
            foreach (string requested in new[] { exact, minimum, maximum }.Where(v => !string.IsNullOrEmpty(v))) ParseVersion(requested);
            ModuleInstallPackage chosen = null;
            foreach (string directory in directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory)) continue;
                var candidates = new List<Tuple<string, Version>>();
                try
                {
                    foreach (string path in Directory.EnumerateFiles(directory, name + "_*.opc", SearchOption.TopDirectoryOnly))
                    {
                        string suffix = Path.GetFileNameWithoutExtension(path).Substring(name.Length + 1);
                        if (!Version.TryParse(suffix, out Version parsed))
                        { warnings.Add("ModuleCandidateSkipped: invalid filename version."); continue; }
                        if (Satisfies(suffix, exact, minimum, maximum)) candidates.Add(Tuple.Create(path, parsed));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { Fail("ModulePackageUnreadable", "A local package directory could not be inspected."); }
                foreach (var entry in candidates.OrderByDescending(c => c.Item2).ThenBy(c => c.Item1, StringComparer.OrdinalIgnoreCase))
                {
                    // Adjacent packages win over SDK packages. Still compare copies of the
                    // selected version so a duplicate with different bytes is never hidden.
                    if (chosen != null && Compare(entry.Item2.ToString(), chosen.Metadata.Version) != 0)
                    {
                        try { Read(entry.Item1); }
                        catch (ModuleInstallPlanException error) { warnings.Add("ModuleCandidateSkipped: version " + entry.Item2 + "; " + error.Code + "."); }
                        continue;
                    }
                    var candidate = Read(entry.Item1);
                    if (!string.Equals(candidate.Metadata.Name, name, StringComparison.OrdinalIgnoreCase) || Compare(candidate.Metadata.Version, entry.Item2.ToString()) != 0)
                        Fail("ModulePackageIdentityMismatch", "A candidate filename disagrees with its manifest identity or version.");
                    if (chosen != null && (chosen.Sha256 != candidate.Sha256 || chosen.Metadata.ID != candidate.Metadata.ID))
                        Fail("ModuleDependencyConflict", "Multiple packages have the same version and different contents or identities.");
                    if (chosen == null) chosen = candidate;
                }
            }
            if (chosen == null) Fail("ModulePackageNotFound", "No local package satisfies the requested module version.");
            return chosen;
        }

        private static XElement ReadXml(Package package, string name)
        {
            Uri uri = PackUriHelper.CreatePartUri(new Uri(name, UriKind.Relative));
            if (!package.PartExists(uri)) Fail("ModulePackageInvalid", "Required OPC metadata is missing.");
            using (Stream stream = package.GetPart(uri).GetStream(FileMode.Open, FileAccess.Read))
            using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlCharacters }))
                return XElement.Load(reader);
        }
        private static string Text(XElement element, string name) => ((string)element.Element(name))?.Trim();
        private static bool Same(string a, string b) => (a ?? "") == (b ?? "");
        private static Guid ParseGuid(string text) { if (!Guid.TryParse(text, out Guid value) || value == Guid.Empty) Fail("ModulePackageInvalid", "A package identity is invalid."); return value; }
        private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) Fail("ModulePackageInvalid", "The module name is invalid."); }
        private static Version ParseVersion(string text) { if (!Version.TryParse(text, out Version value)) Fail("ModulePackageInvalid", "A package version is invalid."); return value; }
        private static int Compare(string a, string b) => ParseVersion(a).CompareTo(ParseVersion(b));
        private static void Fail(string code, string message) => throw new ModuleInstallPlanException(code, message);
    }
}
