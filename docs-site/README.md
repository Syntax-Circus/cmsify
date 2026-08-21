# Cmsify documentation site

The public documentation site is built with MkDocs Material. Its published pages are staged at build time so the task-oriented guides, root configuration inventory, SDK READMEs, and changelog keep their existing source files.

## Local validation

Install the pinned tooling and build from the repository root:

```powershell
python -m pip install --requirement docs-site/requirements.txt
python docs-site/scripts/stage_docs.py
mkdocs build --strict --config-file docs-site/mkdocs.yml
```

The generated input and output directories are ignored by Git.

## GitHub Pages setup

The workflow deploys only successful `main` builds. In the repository's Pages settings, choose **GitHub Actions** as the build source, set the custom domain to `docs.cmsify.dev`, and enforce HTTPS. At the DNS provider, create a `CNAME` record for `docs` that points to `syntax-circus.github.io`.
