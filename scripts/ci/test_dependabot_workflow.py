"""Guard the trust boundary between dependency generators and publication."""

from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]


class DependencyWorkflowTests(unittest.TestCase):
    def test_generation_has_no_write_token_secrets_or_shared_cache(self):
        source = (ROOT / ".github/workflows/dependabot-generate.yml").read_text(encoding="utf-8")
        self.assertIn("  pull_request:\n", source)
        self.assertNotIn("pull_request_target:", source)
        self.assertNotRegex(source, r": write\b|secrets\.|(?m:^\s+cache:)|cache-dependency-path:")
        self.assertIn("package-manager-cache: false", source)
        self.assertEqual(source.count("uses: actions/checkout@"), source.count("persist-credentials: false"))
        for kind in ("backend", "native"):
            self.assertIn(f"needs.plan.outputs.{kind} == 'true'", source)
            self.assertIn(f"name: dependabot-{kind}", source)

    def test_publication_uses_trusted_source_and_exact_producer_run(self):
        source = (ROOT / ".github/workflows/dependabot-publish.yml").read_text(encoding="utf-8")
        self.assertIn("  workflow_run:\n", source)
        self.assertIn("github.event.workflow_run.event == 'pull_request'", source)
        self.assertIn("github.event.workflow_run.head_repository.full_name == github.repository", source)
        self.assertEqual(source.count("uses: actions/checkout@"), 1)
        self.assertIn("ref: ${{ github.event.repository.default_branch }}", source)
        self.assertNotIn("path: candidate", source)
        self.assertNotRegex(source, r"(?m)^\s+run:")
        self.assertEqual(source.count("uses: actions/download-artifact@"), 2)
        self.assertEqual(source.count("run-id: ${{ github.event.workflow_run.id }}"), 2)
        self.assertIn("sync.producerPlan({ github, context, core })", source)

    def test_generators_run_trusted_helper_at_pinned_candidate(self):
        source = (ROOT / ".github/workflows/dependabot-generate.yml").read_text(encoding="utf-8")
        self.assertEqual(source.count("ref: ${{ needs.plan.outputs.head }}"), 2)
        self.assertEqual(source.count("ref: ${{ needs.plan.outputs.base }}"), 2)
        for kind in ("backend", "native"):
            self.assertIn(f"python3 automation/scripts/ci/dependabot_generate.py {kind}", source)

    def test_nuget_group_covers_backend_and_test_manifest_directories(self):
        source = (ROOT / ".github/dependabot.yml").read_text(encoding="utf-8")
        nuget = source.split("  - package-ecosystem: nuget\n", 1)[1].split("  - package-ecosystem:", 1)[0]
        self.assertIn("directories:", nuget)
        self.assertIn("- /backend\n", nuget)
        self.assertIn("- /backend-tests\n", nuget)
        self.assertIn("      backend:\n", nuget)


if __name__ == "__main__":
    unittest.main()
