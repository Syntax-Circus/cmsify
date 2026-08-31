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
            ]
        )

        rewritten = STAGE_DOCS.rewrite_links(source)

        self.assertNotIn("](../", rewritten)
        self.assertNotIn("](release-runbook.md)", rewritten)
        self.assertNotIn("](rollback-runbook.md)", rewritten)
        self.assertIn("https://github.com/Syntax-Circus/cmsify/blob/main/docs/release-runbook.md", rewritten)
        self.assertIn("https://github.com/Syntax-Circus/cmsify/blob/main/tests/upgrade/fixtures/v0.1.3/manifest.json", rewritten)
        self.assertIn("https://github.com/Syntax-Circus/cmsify/tree/main/tests/upgrade", rewritten)


if __name__ == "__main__":
    unittest.main()
