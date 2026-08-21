"""Build the MkDocs input from documentation that remains authoritative elsewhere."""

from __future__ import annotations

import re
import shutil
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SITE_ROOT = ROOT / "docs-site"
CONTENT = SITE_ROOT / "content"
STAGED = SITE_ROOT / ".generated"
GUIDES = (
    "getting-started.md",
    "authentication-and-authorization.md",
    "integrating.md",
    "content-modeling.md",
    "content-components-and-choice-sets.md",
    "packages.md",
    "operations.md",
    "roadmap.md",
)
REPOSITORY_URL = "https://github.com/Syntax-Circus/cmsify"


def extract_configuration(readme: str) -> str:
    match = re.search(
        r"(?ms)^## Configuration\s*$\n(.*?)(?=^## Run in production with Docker\s*$)",
        readme,
    )
    if match is None:
        raise RuntimeError("Could not find the Configuration section in README.md.")

    return "# Configuration\n\n" + match.group(1).strip() + "\n"


def rewrite_links(text: str) -> str:
    replacements = {
        "../README.md#configuration": "configuration.md",
        "../sdk/typescript/README.md": "sdk/typescript.md",
        "../sdk/dotnet/README.md": "sdk/dotnet.md",
        "../../docs/integrating.md": "../integrating.md",
        "../../examples": f"{REPOSITORY_URL}/tree/main/examples",
        "../examples/nextjs-app-router/cmsify.ts": f"{REPOSITORY_URL}/blob/main/examples/nextjs-app-router/cmsify.ts",
        "../examples/dotnet/CmsifyClientSample.cs": f"{REPOSITORY_URL}/blob/main/examples/dotnet/CmsifyClientSample.cs",
    }
    for source, replacement in replacements.items():
        text = text.replace(source, replacement)
    return text


def write_markdown(destination: Path, source: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(rewrite_links(source.read_text(encoding="utf-8")), encoding="utf-8")


def main() -> None:
    if STAGED.exists():
        shutil.rmtree(STAGED)
    STAGED.mkdir(parents=True)

    shutil.copytree(CONTENT, STAGED, dirs_exist_ok=True)

    for guide in GUIDES:
        write_markdown(STAGED / guide, ROOT / "docs" / guide)

    write_markdown(STAGED / "sdk" / "typescript.md", ROOT / "sdk" / "typescript" / "README.md")
    write_markdown(STAGED / "sdk" / "dotnet.md", ROOT / "sdk" / "dotnet" / "README.md")
    write_markdown(STAGED / "changelog.md", ROOT / "CHANGELOG.md")

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    (STAGED / "configuration.md").write_text(
        rewrite_links(extract_configuration(readme)), encoding="utf-8"
    )


if __name__ == "__main__":
    main()
