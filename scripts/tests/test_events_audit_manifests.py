import hashlib
import json
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / "config"
MANIFESTS = sorted(CONFIG.glob("events-*-audit.json"))
U11_MANIFEST = CONFIG / "events-u11-wwp16.1-audit.json"
# The U16 proof (2026-10-14) predates the sanitized result kept in the repository; later proofs ship theirs.
U16_MANIFEST = CONFIG / "events-u16-wwp16.2-audit.json"
SHA256 = re.compile(r"^[0-9a-f]{64}$")
CRITICAL = (
    "Artech.Architecture.Common",
    "Artech.Genexus.Common",
    "Artech.Udm.Framework",
    "Artech.Common.Properties",
    "Artech.Packages.Patterns",
    "DVelop.Patterns.WorkWithPlus",
    "DVelop.Extensions.WWPPackagesCommon",
    "DVelop.Extensions.UserControls",
    "DVelop.Extensions.WWPPackages",
)


def _load(path):
    return json.loads(path.read_text(encoding="utf-8"))


def _proof_path(manifest):
    # events-<bundle>-audit.json cites docs/evidence/events-<bundle>-native-proof.json.
    return ROOT / "docs" / "evidence" / manifest.name.replace("-audit.json", "-native-proof.json")


def _verified_manifests():
    return [
        manifest
        for manifest in MANIFESTS
        if manifest != U16_MANIFEST and _load(manifest)["nativeVerification"]["status"] == "verified"
    ]


def _includes_workwithplus(data):
    return "DVelop.Patterns.WorkWithPlus" in data["criticalAssemblies"]


def _expected_critical(data):
    # A bundle audited without WorkWithPlus has the five SDK assemblies only.
    return CRITICAL if _includes_workwithplus(data) else tuple(name for name in CRITICAL if name.startswith("Artech."))


class EventsAuditManifestTests(unittest.TestCase):
    """The Events save audit manifests are read by the Worker (EventsBundleAudits).

    EventsBundleAuditTests checks them against a Worker built with a GeneXus SDK; these checks need neither, so the
    lane that always runs keeps the manifests and the native proof they cite well formed.
    """

    def test_the_repository_ships_the_three_audited_bundles(self):
        self.assertEqual(
            ["events-u10-audit.json", "events-u11-wwp16.1-audit.json", "events-u16-wwp16.2-audit.json"],
            [manifest.name for manifest in MANIFESTS],
        )

    def test_bundle_ids_are_distinct_and_name_the_workwithplus_release_when_the_bundle_has_it(self):
        ids = [_load(manifest)["bundleId"] for manifest in MANIFESTS]
        self.assertEqual(len(ids), len(set(ids)))
        for manifest in MANIFESTS:
            data = _load(manifest)
            with self.subTest(manifest=manifest.name):
                if _includes_workwithplus(data):
                    self.assertRegex(data["bundleId"], r"^18\.0\.\d+\.\d+\+wwp\d+(\.\d+)+$")
                else:
                    self.assertRegex(data["bundleId"], r"^18\.0\.\d+\.\d+$")
                    self.assertFalse(any(entry["name"].startswith("DVelop.") for entry in data["assemblies"]))

    def test_each_manifest_lists_its_files_once_with_a_hash_and_a_path_inside_the_installation(self):
        for manifest in MANIFESTS:
            data = _load(manifest)
            with self.subTest(manifest=manifest.name):
                paths = [entry["relativePath"].lower() for entry in data["assemblies"]]
                self.assertEqual(len(paths), len(set(paths)), "a relative path is listed twice")
                for entry in data["assemblies"]:
                    self.assertRegex(entry["sha256"], SHA256, entry["name"])
                    path = entry["relativePath"]
                    self.assertFalse(
                        path.startswith(("/", "\\")) or ":" in path or ".." in path.replace("\\", "/").split("/"),
                        path,
                    )
                expected = _expected_critical(data)
                self.assertCountEqual(expected, data["criticalAssemblies"])
                names = [entry["name"] for entry in data["assemblies"]]
                for critical in expected:
                    self.assertEqual(1, names.count(critical), critical)

    def test_the_u10_and_u11_manifests_are_verified_by_a_sanitized_result(self):
        self.assertEqual([CONFIG / "events-u10-audit.json", U11_MANIFEST], _verified_manifests())

    def test_each_verified_proof_digest_is_the_sha256_of_the_sanitized_result_kept_in_the_repository(self):
        verified = _verified_manifests()
        self.assertIn(U11_MANIFEST, verified)
        for manifest in verified:
            with self.subTest(manifest=manifest.name):
                raw = _proof_path(manifest).read_bytes()
                # The repository checks text files out with LF line endings (.gitattributes), so that is the form the digest covers.
                self.assertNotIn(b"\r", raw)
                self.assertEqual(hashlib.sha256(raw).hexdigest(), _load(manifest)["nativeVerification"]["proofEvidenceSha256"])

    def test_each_sanitized_verified_result_has_no_failed_check(self):
        for manifest in _verified_manifests():
            result = json.loads(_proof_path(manifest).read_text(encoding="utf-8"))
            self.assertTrue(result["sets"], manifest.name)
            for name, group in result["sets"].items():
                with self.subTest(manifest=manifest.name, set=name):
                    self.assertEqual(len(group["checks"]), group["total"])
                    self.assertEqual(0, group["failed"])
                    self.assertTrue(all(check["ok"] is True for check in group["checks"]))


if __name__ == "__main__":
    unittest.main()
