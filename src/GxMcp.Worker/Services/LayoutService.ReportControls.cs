using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Typed report-band control mutations.  Report layouts are represented as a
    /// deliberately small XML projection of the native ReportPart; keeping the
    /// projection here makes add/move/remove share the same version check,
    /// geometry validation, persistence and independent read-back contract.
    /// </summary>
    public partial class LayoutService
    {
        private const double DefaultReportControlWidth = 100;
        private const double DefaultReportControlHeight = 20;

        public string AddReportControl(string target, JObject args)
        {
            if (args == null) args = new JObject();
            string blockName = Text(args, "printBlockName");
            string kind = NormalizeReportControlKind(Text(args, "kind"));
            string controlName = Text(args, "controlName");
            if (string.IsNullOrWhiteSpace(controlName)) controlName = Text(args, "name");
            string binding = Text(args, "binding");
            string caption = Text(args, "caption");
            string requestedType = Text(args, "controlType");
            bool dryRun = args["dryRun"]?.ToObject<bool?>() == true;
            bool rollback = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;

            if (Text(args, "after") != null && Text(args, "below") != null)
                return Models.McpResponse.Err(code: "ReportPlacementConflict", message: "Use either after or below, not both.", target: target);
            var validation = ValidateReportControlRequest(target, blockName, kind, controlName, binding, caption, requestedType);
            if (validation != null) return validation;

            var obj = _objectService.FindObject(target);
            if (obj == null) return ReportObjectNotFound(target);
            var context = LoadVisualContext(obj, target, VisualSurface.Report);
            if (context.Error != null) return context.Error;
            if (context.Surface != VisualSurface.Report)
                return ReportSurfaceRequired(target);

            string baseline = context.Document.ToString(SaveOptions.DisableFormatting);
            string version = WriteService.ComputeContentVersionToken(obj, baseline);
            string expected = Text(args, "baseVersion") ?? Text(args, "expectedVersion");
            if (string.IsNullOrWhiteSpace(expected))
                return ReportBaseVersionRequired(target, version);
            string stale = CheckExpectedVersion(expected, version);
            if (stale != null) return stale;

            XElement block = FindPrintBlock(context.Document, blockName);
            if (block == null) return ReportBlockNotFound(target, blockName);
            if (FindReportControls(block, controlName).Count > 0)
                return Models.McpResponse.Err(code: "ReportControlAlreadyExists", message: "A report control with that name already exists in this print block.", target: target,
                    extra: new JObject { ["controlName"] = controlName, ["printBlockName"] = blockName });

            string typeName = ResolveReportControlType(kind, requestedType);
            XElement control = CreateReportControl(typeName, controlName, kind, binding, caption, args);
            string afterName = Text(args, "after");
            string belowName = Text(args, "below");
            string anchorName = !string.IsNullOrWhiteSpace(afterName) ? afterName : belowName;
            var anchorCandidates = FindReportControls(block, anchorName);
            if (!string.IsNullOrWhiteSpace(anchorName) && anchorCandidates.Count == 0)
                return Models.McpResponse.Err(code: "ReportControlAnchorNotFound", message: "Placement anchor '" + anchorName + "' was not found in the print block.", target: target);
            if (anchorCandidates.Count > 1)
                return Models.McpResponse.Err(code: "AmbiguousReportAnchor", message: "Placement anchor is not unique in the print block.", target: target);
            ApplyReportGeometry(control, args, anchorCandidates.FirstOrDefault(), out _);
            ApplyOptionalReportAttributes(control, args);

            string overlap = FindReportOverlap(block, control, controlName);
            if (overlap != null)
                return Models.McpResponse.Err(code: "ReportControlOverlap", message: overlap, target: target,
                    hint: "Choose non-overlapping geometry or use after/below relative placement.", extra: new JObject { ["controlName"] = controlName });

            InsertReportControl(block, control, Text(args, "after"), Text(args, "below"));
            string requested = context.Document.ToString(SaveOptions.DisableFormatting);
            string diff = BoundedDiff(baseline, requested);
            CaptureExpectedReportOrder(context.Document, blockName, args);
            if (dryRun) return ReportMutationPreview(target, "add_report_control", controlName, blockName, diff, requested, version);

            string persistError = PersistVisualXml(obj, context, target, requested, baseline,
                compositionRepairToken: null, baseVersion: version);
            if (persistError != null) return persistError;

            // Capture the version of the candidate before any fresh read. A later
            // read is evidence of the current state, not proof that this write owns it.
            string attemptedVersion = CaptureReportAttemptVersion(obj, requested);
            string postVersion = ReadReportVersionAfterSave(target, obj);
            if (string.IsNullOrWhiteSpace(postVersion))
            {
                return ReportMutationFailure(target, "add_report_control", controlName, blockName, diff,
                    "The report layout was saved but its post-save version could not be read.",
                    persisted: true, rolledBack: false, rollbackRequested: rollback);
            }
            var verification = VerifyReportControl(target, obj, context, blockName, controlName, typeName, binding, caption, args);
            if (verification != null)
            {
                bool rolledBack = rollback && TryRestoreReportBaseline(
                    obj, target, baseline, attemptedVersion, requested);
                return ReportMutationFailure(target, "add_report_control", controlName, blockName, diff, verification,
                    persisted: true, rolledBack: rolledBack, rollbackRequested: rollback);
            }

            return ReportMutationSuccess(target, "add_report_control", controlName, blockName, diff,
                postVersion ?? version, true);
        }

        public string MoveReportControl(string target, JObject args)
        {
            if (args == null) args = new JObject();
            string controlName = Text(args, "controlName") ?? Text(args, "name");
            string blockName = Text(args, "printBlockName");
            bool dryRun = args["dryRun"]?.ToObject<bool?>() == true;
            bool rollback = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            if (string.IsNullOrWhiteSpace(controlName))
                return MissingReportControlName(target);
            if (Text(args, "after") != null && Text(args, "below") != null)
                return Models.McpResponse.Err(code: "ReportPlacementConflict", message: "Use either after or below, not both.", target: target);

            var obj = _objectService.FindObject(target);
            if (obj == null) return ReportObjectNotFound(target);
            var context = LoadVisualContext(obj, target, VisualSurface.Report);
            if (context.Error != null) return context.Error;
            if (context.Surface != VisualSurface.Report) return ReportSurfaceRequired(target);

            string baseline = context.Document.ToString(SaveOptions.DisableFormatting);
            string version = WriteService.ComputeContentVersionToken(obj, baseline);
            string expected = Text(args, "baseVersion") ?? Text(args, "expectedVersion");
            if (string.IsNullOrWhiteSpace(expected))
                return ReportBaseVersionRequired(target, version);
            string stale = CheckExpectedVersion(expected, version);
            if (stale != null) return stale;

            XElement requestedBlock = string.IsNullOrWhiteSpace(blockName)
                ? null : FindPrintBlock(context.Document, blockName);
            if (!string.IsNullOrWhiteSpace(blockName) && requestedBlock == null)
                return ReportBlockNotFound(target, blockName);
            var matchingControls = requestedBlock == null
                ? FindReportControls(context.Document, controlName)
                : FindReportControls(requestedBlock, controlName);
            if (matchingControls.Count == 0)
                return Models.McpResponse.Err(code: "ReportControlNotFound", message: "Report control not found: " + controlName + ".", target: target,
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "get_tree", ["name"] = target }, "Lists report controls and their geometry.")));
            if (matchingControls.Count != 1)
                return Models.McpResponse.Err(code: "AmbiguousReportControl", message: "Report control name is not unique; provide printBlockName.", target: target);
            XElement control = matchingControls[0];
            XElement block = control.Parent;
            if (requestedBlock != null && !SameName(block, blockName))
                return Models.McpResponse.Err(code: "ReportControlWrongBlock", message: "Control '" + controlName + "' is not in print block '" + blockName + "'.", target: target);

            string afterName = Text(args, "after");
            string belowName = Text(args, "below");
            XElement anchor = !string.IsNullOrWhiteSpace(afterName)
                ? FindReportControl(block, afterName)
                : FindReportControl(block, belowName);
            if (!string.IsNullOrWhiteSpace(afterName) && FindReportControls(block, afterName).Count == 0)
                return Models.McpResponse.Err(code: "ReportControlAnchorNotFound", message: "after anchor was not found in the control's print block.", target: target);
            if (!string.IsNullOrWhiteSpace(afterName) && FindReportControls(block, afterName).Count > 1)
                return Models.McpResponse.Err(code: "AmbiguousReportAnchor", message: "after anchor is not unique in the control's print block.", target: target);
            if (!string.IsNullOrWhiteSpace(belowName) && FindReportControls(block, belowName).Count == 0)
                return Models.McpResponse.Err(code: "ReportControlAnchorNotFound", message: "below anchor was not found in the control's print block.", target: target);
            if (!string.IsNullOrWhiteSpace(belowName) && FindReportControls(block, belowName).Count > 1)
                return Models.McpResponse.Err(code: "AmbiguousReportAnchor", message: "below anchor is not unique in the control's print block.", target: target);
            if (HasRelativePlacement(args) && !ApplyReportControlOrder(block, control, args))
                return Models.McpResponse.Err(code: "ReportControlAnchorNotFound", message: "The relative placement anchor cannot be used to reorder the control in its print block.", target: target);
            ApplyReportGeometry(control, args, anchor, out bool geometryChanged);
            ApplyOptionalReportAttributes(control, args);
            if (!geometryChanged && !HasRelativePlacement(args))
                return Models.McpResponse.Err(code: "ReportControlGeometryRequired", message: "Provide left/top/width/height or after/below for move_report_control.", target: target);
            string overlap = FindReportOverlap(block, control, controlName);
            if (overlap != null)
                return Models.McpResponse.Err(code: "ReportControlOverlap", message: overlap, target: target);

            string requested = context.Document.ToString(SaveOptions.DisableFormatting);
            string diff = BoundedDiff(baseline, requested);
            string expectedBlockName = Text(Attr(block, "Name")) ?? Text(Attr(block, "ControlName"));
            CaptureExpectedReportOrder(context.Document, expectedBlockName, args);
            if (dryRun) return ReportMutationPreview(target, "move_report_control", controlName, expectedBlockName, diff, requested, version);

            string persistError = PersistVisualXml(obj, context, target, requested, baseline,
                compositionRepairToken: null, baseVersion: version);
            if (persistError != null) return persistError;
            // Capture the candidate version before a fresh read can observe another writer.
            string attemptedVersion = CaptureReportAttemptVersion(obj, requested);
            string postVersion = ReadReportVersionAfterSave(target, obj);
            if (string.IsNullOrWhiteSpace(postVersion))
            {
                return ReportMutationFailure(target, "move_report_control", controlName, Text(Attr(block, "Name")), diff,
                    "The report layout was saved but its post-save version could not be read.",
                    persisted: true, rolledBack: false, rollbackRequested: rollback);
            }
            string originalType = Text(Attr(control, "TypeName"));
            string originalBinding = string.Equals(originalType, "ReportAttribute", StringComparison.OrdinalIgnoreCase)
                ? Attr(control, "AttributeReference")
                : Attr(control, "ControlSource");
            var verification = VerifyReportControl(target, obj, context, Text(Attr(block, "Name")) ?? Text(Attr(block, "ControlName")), controlName,
                originalType, originalBinding, Text(Attr(control, "Text")), args);
            if (verification != null)
            {
                bool rolledBack = rollback && TryRestoreReportBaseline(
                    obj, target, baseline, attemptedVersion, requested);
                return ReportMutationFailure(target, "move_report_control", controlName, Text(Attr(block, "Name")), diff, verification,
                    persisted: true, rolledBack: rolledBack, rollbackRequested: rollback);
            }
            return ReportMutationSuccess(target, "move_report_control", controlName, Text(Attr(block, "Name")), diff,
                postVersion ?? version, true);
        }

        public string RemoveReportControl(string target, JObject args)
        {
            if (args == null) args = new JObject();
            string controlName = Text(args, "controlName") ?? Text(args, "name");
            string blockName = Text(args, "printBlockName");
            bool dryRun = args["dryRun"]?.ToObject<bool?>() == true;
            bool rollback = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            if (string.IsNullOrWhiteSpace(controlName)) return MissingReportControlName(target);

            var obj = _objectService.FindObject(target);
            if (obj == null) return ReportObjectNotFound(target);
            var context = LoadVisualContext(obj, target, VisualSurface.Report);
            if (context.Error != null) return context.Error;
            if (context.Surface != VisualSurface.Report) return ReportSurfaceRequired(target);
            string baseline = context.Document.ToString(SaveOptions.DisableFormatting);
            string version = WriteService.ComputeContentVersionToken(obj, baseline);
            string expected = Text(args, "baseVersion") ?? Text(args, "expectedVersion");
            if (string.IsNullOrWhiteSpace(expected))
                return ReportBaseVersionRequired(target, version);
            string stale = CheckExpectedVersion(expected, version);
            if (stale != null) return stale;

            XElement requestedBlock = string.IsNullOrWhiteSpace(blockName)
                ? null : FindPrintBlock(context.Document, blockName);
            if (!string.IsNullOrWhiteSpace(blockName) && requestedBlock == null)
                return ReportBlockNotFound(target, blockName);
            var matchingControls = requestedBlock == null
                ? FindReportControls(context.Document, controlName)
                : FindReportControls(requestedBlock, controlName);
            if (matchingControls.Count == 0) return Models.McpResponse.Err(code: "ReportControlNotFound", message: "Report control not found: " + controlName + ".", target: target);
            if (matchingControls.Count != 1) return Models.McpResponse.Err(code: "AmbiguousReportControl", message: "Report control name is not unique; provide printBlockName.", target: target);
            XElement control = matchingControls[0];
            XElement block = control.Parent;
            string actualBlock = Text(Attr(block, "Name")) ?? Text(Attr(block, "ControlName"));
            if (requestedBlock != null && !SameName(block, blockName))
                return Models.McpResponse.Err(code: "ReportControlWrongBlock", message: "Control '" + controlName + "' is not in print block '" + blockName + "'.", target: target);
            control.Remove();
            string requested = context.Document.ToString(SaveOptions.DisableFormatting);
            string diff = BoundedDiff(baseline, requested);
            CaptureExpectedReportOrder(context.Document, actualBlock, args);
            if (dryRun) return ReportMutationPreview(target, "remove_report_control", controlName, actualBlock, diff, requested, version);

            string persistError = PersistVisualXml(obj, context, target, requested, baseline,
                compositionRepairToken: null, baseVersion: version);
            if (persistError != null) return persistError;
            // Keep the candidate's version as the rollback fence; the independent
            // read below may instead observe a newer layout.
            string attemptedVersion = CaptureReportAttemptVersion(obj, requested);
            var rereadObject = _objectService.FindObjectFreshByIdentity(obj);
            var reread = rereadObject == null
                ? LayoutContextResult.FromError("Independent report object read returned no fresh object.")
                : LoadVisualContext(rereadObject, target, VisualSurface.Report);
            if (reread.Error != null)
            {
                return ReportMutationFailure(target, "remove_report_control", controlName, actualBlock, diff, reread.Error,
                    persisted: true, rolledBack: false, rollbackRequested: rollback);
            }
            string removalVerification = VerifyReportControlRemoved(reread.Document, actualBlock, controlName);
            if (removalVerification != null)
            {
                bool rolledBack = rollback && TryRestoreReportBaseline(
                    obj, target, baseline, attemptedVersion, requested);
                return ReportMutationFailure(target, "remove_report_control", controlName, actualBlock, diff, removalVerification,
                    persisted: true, rolledBack: rolledBack, rollbackRequested: rollback);
            }
            if (!VerifyExpectedReportOrder(FindPrintBlock(reread.Document, actualBlock), args))
            {
                bool rolledBack = rollback && TryRestoreReportBaseline(
                    obj, target, baseline, attemptedVersion, requested);
                return ReportMutationFailure(target, "remove_report_control", controlName, actualBlock, diff,
                    "The SDK changed the requested report control order during save.",
                    persisted: true, rolledBack: rolledBack, rollbackRequested: rollback);
            }
            string persistedVersion = ComputeReportVersion(obj, reread);
            if (string.IsNullOrWhiteSpace(persistedVersion))
            {
                return ReportMutationFailure(target, "remove_report_control", controlName, actualBlock, diff,
                    "The report layout was saved but its post-save version could not be read.",
                    persisted: true, rolledBack: false, rollbackRequested: rollback);
            }
            return ReportMutationSuccess(target, "remove_report_control", controlName, actualBlock, diff,
                persistedVersion, true);
        }

        private static string ReportBaseVersionRequired(string target, string currentVersion)
        {
            return Models.McpResponse.Err(
                code: "ReportBaseVersionRequired",
                message: "baseVersion is required for report-control mutations, including dryRun previews.",
                target: target,
                hint: "Read the report with action=get_tree and pass its versionToken.",
                extra: new JObject { ["currentVersion"] = currentVersion });
        }

        private static string ValidateReportControlRequest(string target, string block, string kind, string name, string binding, string caption, string requestedType)
        {
            if (string.IsNullOrWhiteSpace(block)) return Models.McpResponse.Err(code: "ReportPrintBlockRequired", message: "printBlockName is required.", target: target);
            if (string.IsNullOrWhiteSpace(name)) return Models.McpResponse.Err(code: "ReportControlNameRequired", message: "controlName is required for a report control.", target: target);
            if (string.IsNullOrWhiteSpace(kind) && string.IsNullOrWhiteSpace(requestedType))
                return Models.McpResponse.Err(code: "ReportControlKindRequired", message: "kind is required (variable, attribute, label, line, box, or image).", target: target);
            bool requiresBinding = kind == "variable" || kind == "attribute"
                || string.Equals(requestedType, "ReportVariable", StringComparison.OrdinalIgnoreCase)
                || string.Equals(requestedType, "ReportAttribute", StringComparison.OrdinalIgnoreCase);
            if (requiresBinding && string.IsNullOrWhiteSpace(binding))
                return Models.McpResponse.Err(code: "ReportControlBindingRequired", message: "binding is required for variable and attribute controls.", target: target);
            if (kind == "label" && string.IsNullOrWhiteSpace(caption))
                return Models.McpResponse.Err(code: "ReportControlCaptionRequired", message: "caption is required for label controls.", target: target);
            return null;
        }

        private static string NormalizeReportControlKind(string kind)
            => (kind ?? string.Empty).Trim().ToLowerInvariant();

        private static string ResolveReportControlType(string kind, string requestedType)
        {
            if (!string.IsNullOrWhiteSpace(requestedType)) return requestedType.Trim();
            switch (kind)
            {
                case "variable": return "ReportVariable";
                case "attribute": return "ReportAttribute";
                case "label": return "ReportLabel";
                case "line": return "ReportLine";
                case "box": return "ReportRectangle";
                case "image": return "ReportImage";
                default: return "ReportLabel";
            }
        }

        private static XElement CreateReportControl(string typeName, string name, string kind, string binding, string caption, JObject args)
        {
            var control = new XElement("Control",
                new XAttribute("TypeName", typeName),
                new XAttribute("Name", name),
                new XAttribute("ControlName", name));
            if (!string.IsNullOrWhiteSpace(binding))
                control.SetAttributeValue(
                    string.Equals(typeName, "ReportAttribute", StringComparison.OrdinalIgnoreCase)
                        ? "AttributeReference"
                        : "ControlSource",
                    binding.Trim());
            if (!string.IsNullOrWhiteSpace(caption)) control.SetAttributeValue("Text", caption);
            else if (kind == "variable" || kind == "attribute") control.SetAttributeValue("Text", string.Empty);
            return control;
        }

        private static void ApplyOptionalReportAttributes(XElement control, JObject args)
        {
            foreach (string name in new[] { "font", "fontName", "fontSize", "alignment", "picture", "visible" })
            {
                string value = Text(args, name);
                if (value != null) control.SetAttributeValue(char.ToUpperInvariant(name[0]) + name.Substring(1), value);
            }
        }

        private static void ApplyReportGeometry(XElement control, JObject args, XElement anchor, out bool changed)
        {
            changed = false;
            double x = ReadNumber(args, "left", "x");
            double y = ReadNumber(args, "top", "y");
            double width = ReadNumber(args, "width", null);
            double height = ReadNumber(args, "heightControl", "height");
            if (anchor != null)
            {
                double ax = ReadNumber(anchor, "x", "left");
                double ay = ReadNumber(anchor, "y", "top");
                double aw = ReadNumber(anchor, "width", null);
                double ah = ReadNumber(anchor, "height", null);
                if (Text(args, "after") != null) { if (!HasValue(args, "left", "x")) x = ax + aw + 1; if (!HasValue(args, "top", "y")) y = ay; }
                if (Text(args, "below") != null) { if (!HasValue(args, "left", "x")) x = ax; if (!HasValue(args, "top", "y")) y = ay + ah + 1; }
            }
            if (!HasValue(args, "width") && awIsMissing(control)) width = DefaultReportControlWidth;
            if (!HasValue(args, "heightControl", "height") && ahIsMissing(control)) height = DefaultReportControlHeight;
            if (HasValue(args, "left", "x") || anchor != null && Text(args, "after") != null) { control.SetAttributeValue("X", Number(x)); changed = true; }
            if (HasValue(args, "top", "y") || anchor != null && Text(args, "below") != null) { control.SetAttributeValue("Y", Number(y)); changed = true; }
            if (HasValue(args, "width") || awIsMissing(control)) { control.SetAttributeValue("Width", Number(width)); changed = true; }
            if (HasValue(args, "heightControl", "height") || ahIsMissing(control)) { control.SetAttributeValue("Height", Number(height)); changed = true; }
        }

        private static bool awIsMissing(XElement e) => e?.Attribute("Width") == null && e?.Attribute("Left") == null;
        private static bool ahIsMissing(XElement e) => e?.Attribute("Height") == null && e?.Attribute("Top") == null;

        private static bool ApplyReportControlOrder(XElement block, XElement control, JObject args)
        {
            string anchorName = !string.IsNullOrWhiteSpace(Text(args, "after"))
                ? Text(args, "after")
                : Text(args, "below");
            if (string.IsNullOrWhiteSpace(anchorName)) return false;

            XElement anchor = FindReportControl(block, anchorName);
            if (anchor == null || anchor == control
                || control.Parent == null || !ReferenceEquals(control.Parent, anchor.Parent))
                return false;

            if (!ReferenceEquals(anchor.ElementsAfterSelf().FirstOrDefault(), control))
            {
                control.Remove();
                anchor.AddAfterSelf(control);
            }
            return true;
        }

        private static void InsertReportControl(XElement block, XElement control, string after, string below)
        {
            XElement anchor = !string.IsNullOrWhiteSpace(after) ? FindReportControl(block, after) : FindReportControl(block, below);
            if (anchor != null) anchor.AddAfterSelf(control);
            else block.Add(control);
        }

        private static string FindReportOverlap(XElement block, XElement candidate, string candidateName)
        {
            if (candidate == null) return null;
            var rect = ReadRect(candidate);
            if (rect == null) return null;
            foreach (var other in block.Elements().Where(e => e.Name.LocalName.Equals("Control", StringComparison.OrdinalIgnoreCase)))
            {
                if (SameName(other, candidateName)) continue;
                var otherRect = ReadRect(other);
                if (otherRect == null) continue;
                if (rect.Left < otherRect.Right && rect.Right > otherRect.Left && rect.Top < otherRect.Bottom && rect.Bottom > otherRect.Top)
                    return "Report control geometry overlaps '" + (Text(Attr(other, "ControlName")) ?? Text(Attr(other, "Name"))) + "'.";
            }
            return null;
        }

        private static System.Drawing.RectangleF ReadRect(XElement e)
        {
            if (e == null || !double.TryParse(Text(Attr(e, "X")) ?? Text(Attr(e, "Left")), NumberStyles.Any, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(Text(Attr(e, "Y")) ?? Text(Attr(e, "Top")), NumberStyles.Any, CultureInfo.InvariantCulture, out double y) ||
                !double.TryParse(Text(Attr(e, "Width")), NumberStyles.Any, CultureInfo.InvariantCulture, out double w) ||
                !double.TryParse(Text(Attr(e, "Height")), NumberStyles.Any, CultureInfo.InvariantCulture, out double h)) return System.Drawing.RectangleF.Empty;
            if (w <= 0 || h <= 0) return System.Drawing.RectangleF.Empty;
            return new System.Drawing.RectangleF((float)x, (float)y, (float)w, (float)h);
        }

        private static XElement FindPrintBlock(XDocument doc, string name)
            => doc?.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("PrintBlock", StringComparison.OrdinalIgnoreCase) && SameName(e, name));

        private static List<XElement> FindReportControls(XDocument doc, string name)
            => doc == null || string.IsNullOrWhiteSpace(name)
                ? new List<XElement>()
                : doc.Descendants().Where(e => e.Name.LocalName.Equals("Control", StringComparison.OrdinalIgnoreCase) && SameName(e, name)).ToList();

        private static XElement FindReportControl(XDocument doc, string name)
            => FindReportControls(doc, name).FirstOrDefault();

        private static string VerifyReportControlRemoved(XDocument document, string blockName, string controlName)
        {
            XElement block = FindPrintBlock(document, blockName);
            if (block == null)
                return "The SDK read-back did not contain print block '" + blockName + "'.";
            return FindReportControls(block, controlName).Count == 0
                ? null
                : "The report control is still present in print block '" + blockName + "' after the SDK save.";
        }

        private static List<XElement> FindReportControls(XElement parent, string name)
            => parent == null || string.IsNullOrWhiteSpace(name)
                ? new List<XElement>()
                : parent.Descendants().Where(e => e.Name.LocalName.Equals("Control", StringComparison.OrdinalIgnoreCase) && SameName(e, name)).ToList();

        private static XElement FindReportControl(XElement parent, string name)
            => FindReportControls(parent, name).FirstOrDefault();

        private static bool SameName(XElement e, string name)
            => e != null && !string.IsNullOrWhiteSpace(name) &&
               (string.Equals(Attr(e, "ControlName"), name, StringComparison.OrdinalIgnoreCase) || string.Equals(Attr(e, "Name"), name, StringComparison.OrdinalIgnoreCase));

        private static bool HasRelativePlacement(JObject args) => Text(args, "after") != null || Text(args, "below") != null;

        private static void CaptureExpectedReportOrder(XDocument document, string blockName, JObject args)
        {
            XElement block = FindPrintBlock(document, blockName);
            if (block == null || args == null) return;
            args["_requestedReportOrder"] = new JArray(
                block.Elements("Control")
                    .Select(control => Attr(control, "ControlName") ?? Attr(control, "Name"))
                    .Where(name => !string.IsNullOrWhiteSpace(name)));
        }

        private static bool VerifyExpectedReportOrder(XElement block, JObject args)
        {
            var expected = args?["_requestedReportOrder"] as JArray;
            if (expected == null || block == null) return true;
            var actual = block.Elements("Control")
                .Select(control => Attr(control, "ControlName") ?? Attr(control, "Name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            return expected.Select(token => token.ToString())
                .SequenceEqual(actual, StringComparer.OrdinalIgnoreCase);
        }

        private static string CheckExpectedVersion(string expected, string actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.Ordinal)) return null;
            return Models.McpResponse.Err(code: "StaleObject", message: "The report layout changed after baseVersion was read.", hint: "Read genexus_layout action=get_tree again and retry with the new versionToken.", extra: new JObject { ["expectedVersion"] = expected, ["actualVersion"] = actual });
        }

        private static string ReportSurfaceRequired(string target)
            => Models.McpResponse.Err(code: "ReportSurfaceRequired", message: "This action requires a Procedure report Layout surface.", target: target);

        private static string ReportObjectNotFound(string target)
            => Models.McpResponse.Err(
                code: "ObjectNotFound",
                message: "Object not found.",
                target: target,
                nextSteps: new JArray(Models.McpResponse.NextStep(
                    "genexus_list_objects",
                    new JObject { ["query"] = target },
                    "Lists report-capable objects so the exact name and type can be confirmed.")));

        private static string ReportBlockNotFound(string target, string block)
            => Models.McpResponse.Err(code: "ReportPrintBlockNotFound", message: "Print block not found: " + block + ".", target: target);

        private static string MissingReportControlName(string target)
            => Models.McpResponse.Err(code: "ReportControlNameRequired", message: "controlName is required.", target: target);

        private string VerifyReportControl(string target, KBObject original, LayoutContextResult originalContext, string block, string name, string type, string binding, string caption, JObject args)
        {
            KBObject fresh = _objectService.FindObjectFreshByIdentity(original);
            if (fresh == null) return "Independent report object read returned no fresh object.";
            var reread = LoadVisualContext(fresh, target, VisualSurface.Report);
            if (reread.Error != null) return reread.Error;
            var printBlock = FindPrintBlock(reread.Document, block);
            if (printBlock == null) return "The SDK read-back did not contain the requested print block.";
            var control = FindReportControl(printBlock, name);
            if (control == null || !SameName(control, name)) return "The SDK read-back did not contain the requested report control in its print block.";
            if (!VerifyExpectedReportOrder(printBlock, args)) return "The SDK changed the requested report control order during save.";
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(Attr(control, "TypeName"), type, StringComparison.OrdinalIgnoreCase)) return "The SDK changed the report control type during save.";
            if (!string.IsNullOrWhiteSpace(binding))
            {
                string actualBinding = string.Equals(type, "ReportAttribute", StringComparison.OrdinalIgnoreCase)
                    ? Attr(control, "AttributeReference")
                    : Attr(control, "ControlSource");
                if (string.IsNullOrWhiteSpace(actualBinding))
                    actualBinding = Attr(control, "ControlSource");
                if (!string.Equals(actualBinding, binding, StringComparison.OrdinalIgnoreCase))
                    return "The SDK changed the report control binding during save.";
            }
            if (!string.IsNullOrWhiteSpace(caption) && !string.Equals(Attr(control, "Text"), caption, StringComparison.OrdinalIgnoreCase)) return "The SDK changed the report control caption during save.";

            if (!VerifyReportNumber(control, args, "left", "X")
                || !VerifyReportNumber(control, args, "x", "X")
                || !VerifyReportNumber(control, args, "top", "Y")
                || !VerifyReportNumber(control, args, "y", "Y")
                || !VerifyReportNumber(control, args, "width", "Width")
                || !VerifyReportNumber(control, args, "height", "Height")
                || !VerifyReportNumber(control, args, "heightControl", "Height"))
                return "The SDK changed the requested report control geometry during save.";

            foreach (string attributeName in new[] { "font", "fontName", "fontSize", "alignment", "picture", "visible" })
            {
                string requested = Text(args, attributeName);
                if (requested == null) continue;
                string actual = Attr(control, char.ToUpperInvariant(attributeName[0]) + attributeName.Substring(1));
                if (string.Equals(attributeName, "picture", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(actual))
                    actual = Attr(control, "ImageReference");
                if (!string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase))
                    return "The SDK changed the requested report control " + attributeName + " during save.";
            }
            return null;
        }

        private static bool VerifyReportNumber(XElement control, JObject args, string requestName, string xmlName)
        {
            string requested = Text(args, requestName);
            if (requested == null) return true;
            if (!double.TryParse(requested, NumberStyles.Any, CultureInfo.InvariantCulture, out double expected)) return false;
            string actualText = Attr(control, xmlName);
            return double.TryParse(actualText, NumberStyles.Any, CultureInfo.InvariantCulture, out double actual)
                && Math.Abs(expected - actual) < 0.0005;
        }

        private static string CaptureReportAttemptVersion(KBObject obj, string requested)
        {
            // A missing candidate token intentionally leaves automatic rollback
            // unavailable; a later fresh token cannot prove ownership of this write.
            if (obj == null || string.IsNullOrWhiteSpace(requested)) return null;
            try
            {
                return WriteService.ComputeContentVersionToken(obj, requested);
            }
            catch
            {
                return null;
            }
        }

        private string ComputeReportVersion(KBObject obj, LayoutContextResult context)
        {
            if (obj == null || context?.Document == null) return null;
            return WriteService.ComputeContentVersionToken(
                obj, context.Document.ToString(SaveOptions.DisableFormatting));
        }

        private string ReadReportVersionAfterSave(string target, KBObject fallback)
        {
            // This is an observation for the success/readback contract. Rollback must
            // use the version captured from the attempted candidate, not this fresh read.
            try
            {
                KBObject current = fallback == null ? null : _objectService.FindObjectFreshByIdentity(fallback);
                if (current == null) return null;
                var currentContext = LoadVisualContext(current, target, VisualSurface.Report);
                return currentContext.Error == null ? ComputeReportVersion(current, currentContext) : null;
            }
            catch { return null; }
        }

        internal static bool IsReportRollbackFenceCurrent(
            string attemptedVersion,
            string currentVersion,
            string attemptedXml,
            string currentXml)
        {
            if (string.IsNullOrWhiteSpace(attemptedVersion)
                || string.IsNullOrWhiteSpace(currentVersion)
                || string.IsNullOrWhiteSpace(attemptedXml)
                || string.IsNullOrWhiteSpace(currentXml))
                return false;
            if (!string.Equals(attemptedVersion, currentVersion, StringComparison.Ordinal))
                return false;
            try
            {
                return XmlEquivalence.AreEquivalent(attemptedXml, currentXml, out _);
            }
            catch
            {
                return false;
            }
        }

        private bool TryRestoreReportBaseline(
            KBObject obj,
            string target,
            string baseline,
            string attemptedVersion,
            string attemptedXml)
        {
            if (obj == null
                || string.IsNullOrWhiteSpace(baseline)
                || string.IsNullOrWhiteSpace(attemptedVersion)
                || string.IsNullOrWhiteSpace(attemptedXml))
                return false;
            try
            {
                KBObject current = _objectService.FindObjectFreshByIdentity(obj);
                if (current == null) return false;
                var currentContext = LoadVisualContext(current, target, VisualSurface.Report);
                if (currentContext.Error != null) return false;
                string currentXml = currentContext.Document.ToString(SaveOptions.DisableFormatting);
                if (string.Equals(currentXml, baseline, StringComparison.Ordinal)) return true;
                string currentVersion = ComputeReportVersion(current, currentContext);
                if (!IsReportRollbackFenceCurrent(
                    attemptedVersion, currentVersion, attemptedXml, currentXml))
                {
                    Logger.Warn("[ReportControl] rollback refused: current report state is not the failed write's version.");
                    return false;
                }
                if (PersistVisualXml(current, currentContext, target, baseline,
                    null, null, attemptedVersion) != null)
                    return false;
                KBObject restoredObject = _objectService.FindObjectFreshByIdentity(obj);
                var restored = restoredObject == null
                    ? LayoutContextResult.FromError("Independent report restore read returned no fresh object.")
                    : LoadVisualContext(restoredObject, target, VisualSurface.Report);
                return restored.Error == null
                    && XmlEquivalence.AreEquivalent(baseline,
                        restored.Document.ToString(SaveOptions.DisableFormatting), out _);
            }
            catch (Exception ex)
            {
                Logger.Warn("[ReportControl] baseline restore failed: " + ex.Message);
                return false;
            }
        }

        private static string BoundedDiff(string before, string after)
        {
            try
            {
                string diff = DiffBuilder.UnifiedDiff(before, after, 1);
                return diff != null && diff.Length > 6000 ? diff.Substring(0, 6000) + "\n…diff truncated…" : diff;
            }
            catch { return string.Empty; }
        }

        private static string ReportMutationPreview(string target, string action, string control, string block, string diff, string xml, string version)
        {
            string source = xml ?? string.Empty;
            if (source.Length > 12000) source = source.Substring(0, 12000) + "\n…source truncated…";
            return new JObject { ["status"] = "ok", ["code"] = "ReportControlDryRun", ["target"] = target, ["result"] = new JObject {
                ["action"] = action, ["controlName"] = control, ["printBlockName"] = block, ["dryRun"] = true,
                ["persisted"] = false, ["transformationsSimulated"] = true, ["versionToken"] = version,
                ["diff"] = diff, ["source"] = source } }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string ReportMutationSuccess(string target, string action, string control, string block, string diff, string version, bool persisted)
        {
            return new JObject { ["status"] = "ok", ["code"] = "ReportControlWritten", ["target"] = target, ["result"] = new JObject {
                ["action"] = action, ["controlName"] = control, ["printBlockName"] = block, ["persisted"] = persisted,
                ["verified"] = true, ["rereadConfirmed"] = true, ["versionToken"] = version, ["diff"] = diff } }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string ReportMutationFailure(string target, string action, string control, string block, string diff, object detail, bool persisted, bool rolledBack, bool rollbackRequested)
        {
            string message = detail?.ToString() ?? "Report control verification failed.";
            return Models.McpResponse.Err(code: "ReportControlWriteVerificationFailed", message: message, target: target,
                hint: "The persisted layout was re-read; retry from a fresh baseVersion.", extra: new JObject {
                    ["action"] = action, ["controlName"] = control, ["printBlockName"] = block, ["persisted"] = persisted,
                    ["rolledBack"] = rolledBack, ["rollbackRequested"] = rollbackRequested,
                    ["rollbackDeferred"] = rollbackRequested && !rolledBack,
                    ["recoveryRequired"] = persisted && !rolledBack,
                    ["recoveryHint"] = persisted && !rolledBack
                        ? "The write may have committed but could not be independently verified or safely restored; do not retry until a fresh read and recovery decision are recorded."
                        : null,
                    ["diff"] = diff });
        }

        private static string Text(JObject args, string name) => args?[name] == null ? null : Text(args[name]);
        private static string Text(JToken token) => token == null || token.Type == JTokenType.Null ? null : token.ToString();
        private static string Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
        private static bool HasValue(JObject args, params string[] names) => names.Any(n => args[n] != null && args[n].Type != JTokenType.Null && !string.IsNullOrWhiteSpace(args[n].ToString()));
        private static double ReadNumber(JObject args, string a, string b) { string s = Text(args, a) ?? (b == null ? null : Text(args, b)); return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double n) ? n : 0; }
        private static double ReadNumber(XElement e, string a, string b) { string s = Text(Attr(e, a)) ?? (b == null ? null : Text(Attr(e, b))); return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double n) ? n : 0; }
        private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
