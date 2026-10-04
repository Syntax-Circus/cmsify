from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "scripts" / "stage_docs.py"
SPEC = importlib.util.spec_from_file_location("stage_docs", SCRIPT_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Unable to load {SCRIPT_PATH}")
STAGE_DOCS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(STAGE_DOCS)


class RewriteLinksTests(unittest.TestCase):
    def test_sqlite_readme_link_targets_repository_and_preserves_fragment(self) -> None:
        source = "[bounded predicates](../../src/Cmsify.Infrastructure.Sqlite/README.md#bounded-json-string-predicates)"

        self.assertEqual(
            "[bounded predicates](https://github.com/Syntax-Circus/cmsify/blob/main/src/Cmsify.Infrastructure.Sqlite/README.md#bounded-json-string-predicates)",
            STAGE_DOCS.rewrite_links(source),
        )

    def test_changelog_provider_portability_link_targets_repository(self) -> None:
        source = "[provider portability](docs/provider-portability.md)"

        self.assertEqual(
            "[provider portability](https://github.com/Syntax-Circus/cmsify/blob/main/docs/provider-portability.md)",
            STAGE_DOCS.rewrite_links(source),
        )

    def test_repository_links_remain_valid_after_public_docs_are_staged(self) -> None:
        source = "\n".join(
            [
                "[.NET SDK](../sdk/dotnet/src/SyntaxCircus.Cmsify.Client/README.md)",
                "[release](release-runbook.md)",
                "[rollback](rollback-runbook.md)",
                "[fixture](../tests/upgrade/fixtures/v0.1.3/manifest.json)",
                "[checksums](../tests/upgrade/fixtures/v0.1.3/SHA256SUMS)",
                "[upgrade guide](../tests/upgrade/README.md)",
                "[upgrade section](../tests/upgrade/README.md#build-and-rehearse-an-exact-candidate)",
                "[workflow](../.github/workflows/upgrade-rollback.yml)",
                "[keyring](../docker-compose.prod.keyring.env.example)",
                "[provider qualification](provider-portability.md)",
                "[engine evidence](evidence/2026-10-02-engine-packages.md)",
            ]
        )

        rewritten = STAGE_DOCS.rewrite_links(source)

        self.assertNotIn("](../", rewritten)
        self.assertNotIn("](release-runbook.md)", rewritten)
        self.assertNotIn("](rollback-runbook.md)", rewritten)
        self.assertIn("https://github.com/Syntax-Circus/cmsify/blob/main/docs/release-runbook.md", rewritten)
        self.assertIn("https://github.com/Syntax-Circus/cmsify/blob/main/tests/upgrade/fixtures/v0.1.3/manifest.json", rewritten)
        self.assertIn("https://github.com/Syntax-Circus/cmsify/tree/main/tests/upgrade", rewritten)
        self.assertIn("](https://github.com/Syntax-Circus/cmsify/blob/main/docs/provider-portability.md)", rewritten)
        self.assertIn("](https://github.com/Syntax-Circus/cmsify/blob/main/docs/evidence/2026-10-02-engine-packages.md)", rewritten)


if __name__ == "__main__":
    unittest.main()
