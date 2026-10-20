"""CAD-independent source preparation and conservative presentation policy."""
from pathlib import Path
import hashlib
import os
import re

ROOT = Path(__file__).resolve().parent
GENERATED_DIRECTORIES = frozenset({
    "exports", ".venv", "venv", "__pycache__", ".git", "node_modules",
    ".pytest_cache", ".mypy_cache", ".ruff_cache",
})
LOCAL_FILES = frozenset({"fit-debug.json", ".gitignore", ".DS_Store", ".coverage"})
# Geometry generation, effects layout, assembly instructions and verification
# remain manufacturing inputs even when their output is visible in the viewer.
PRESENTATION_SOURCES = frozenset({
    "viewer_document.py", "viewer_style.py", "refresh_viewer.py", "viewer.js", "viewer-template.html",
    "viewer_runtime.py", "viewer_ui.js", "workshop-ui-runtime.js",
    "ui_test_runtime.cjs", "preview_ui_fixture.py",
    "viewer-workshop.css", "workshop-design-system.css", "show_timeline.js",
    "twin_media.js", "twin_media.css", "twin_viewer.js", "twin-viewer.css",
    "viewer_engine_core.js", "twin_core.js", "timeline_core.js",
    "twin_media_core.js", "twin_media_controller.js",
    # Data-only bundle packaging/proof tools never create mechanical geometry.
    # Their exact output bytes and proof are independently validated at publication.
    "export_model_bundle.py", "model_verification.py", "certify_model_bundle.py",
    "test_model_bundle_export.py", "test_model_verification.py",
    "README.md", "VIEWER.md", "CAD-WORKFLOW.md", "test_viewer_document.py", "test_viewer_style.py",
    "test_viewer.cjs", "test_show_timeline.cjs", "test_twin_media.cjs",
    "test_twin_media_browser.cjs", "test_twin_viewer.cjs",
    "reference/workshop-design-system.css",
})


def presentation_only(name):
    """Only explicitly reviewed UI sources may reuse mechanical evidence."""
    return name in PRESENTATION_SOURCES


def validation_only(name):
    """Verifier/test edits require new acceptance but no geometry generation."""
    if presentation_only(name):
        return False
    return name == "verify.py" or re.fullmatch(r"test_[a-z0-9_]+\.(?:py|cjs)", name) is not None


def source_hashes():
    """Hash every real source, including unknown nested manufacturing inputs."""
    import inspect_data
    from viewer_style import snapshot_styles

    snapshot_styles()
    inspect_data.assert_snapshot_current()
    return file_hashes(ROOT)


def file_hashes(root):
    """Read the complete source inventory without generating any snapshots."""
    root = Path(root).resolve()
    sources = {}
    for directory, child_directories, filenames in os.walk(root):
        # Prune tool environments before descending; merely filtering rglob's
        # results still walks every installed CAD dependency on each cache hit.
        child_directories[:] = sorted(name for name in child_directories if name not in GENERATED_DIRECTORIES)
        for name in child_directories:
            if (Path(directory) / name).is_symlink():
                raise ValueError("Symlink source directory cannot be fingerprinted: " + name)
        for name in sorted(filenames):
            path = Path(directory) / name
            if name in LOCAL_FILES or path.suffix in {".pyc", ".pyo"}:
                continue
            relative = path.relative_to(root).as_posix()
            if not path.resolve().is_relative_to(root):
                raise ValueError("Source input escapes CAD source directory: " + relative)
            sources[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
    return sources


def prepared_sources():
    """Prepare exactly the source side of regenerate.prepared_identity()."""
    import assembly_docs
    import inspect_data

    assembly_docs.main()
    inspect_data.snapshot_sources()
    return source_hashes()
