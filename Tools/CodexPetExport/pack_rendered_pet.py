"""Adapt actual Unity captures to the installed Codex v2 assembly/QA tools.

No art is invented here: the only raster operation before the bundled pipeline
is a common 2x downsample of the renderer's fixed-camera transparent frames.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys
from PIL import Image

STATES = {"idle": 6, "running-right": 8, "running-left": 8, "waving": 4,
          "jumping": 5, "failed": 8, "waiting": 6, "running": 6, "review": 6}


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--skill-dir", type=Path, required=True)
    args = parser.parse_args()
    run = args.run_dir.resolve()
    scripts = args.skill_dir.resolve() / "scripts"

    def tool(name, *values):
        subprocess.run([sys.executable, str(scripts / name), *map(str, values)], check=True)

    # Validate counts before writing any partial normalized row.
    groups = {**STATES, "look": 16}
    sources = {}
    for state, count in groups.items():
        paths = sorted((run / "raw" / state).glob("*.png"))
        if len(paths) != count:
            raise ValueError(f"{state}: expected {count} actual render captures, got {len(paths)}")
        sources[state] = paths
    for folder in ("qa", "final", "frames", "look-cells"):
        (run / folder).mkdir(parents=True, exist_ok=True)
    request = {"pet_id": "miku-nt-local", "display_name": "Miku NT",
               "description": "Miku NT rendered from your local avatar. Local personal use only.",
               "method": "existing-3d-asset-conversion", "chroma_key": {"hex": "#FF00FF"}}
    (run / "pet_request.json").write_text(json.dumps(request, indent=2), encoding="utf-8")
    for state, paths in sources.items():
        dest = run / ("look-cells" if state == "look" else f"frames/{state}")
        dest.mkdir(parents=True, exist_ok=True)
        for path in paths:
            with Image.open(path) as opened:
                if opened.mode != "RGBA" or opened.size != (384, 416):
                    raise ValueError(f"Unexpected render geometry: {path}")
                opened.resize((192, 208), Image.Resampling.LANCZOS).save(dest / path.name)
    tool("inspect_frames.py", "--frames-root", run / "frames", "--json-out", run / "qa/review.json")
    tool("compose_atlas.py", "--frames-root", run / "frames", "--output", run / "final/spritesheet.png", "--webp-output", run / "final/spritesheet.webp")
    tool("make_contact_sheet.py", run / "final/spritesheet.webp", "--output", run / "qa/contact-sheet.png")
    tool("render_animation_previews.py", "--frames-root", run / "frames", "--output-dir", run / "qa/previews")
    tool("assemble_extended_atlas.py", "--base-atlas", run / "final/spritesheet.webp", "--look-cells-dir", run / "look-cells",
         "--neutral-cell", run / "frames/idle/00.png", "--chroma-key", "#FF00FF", "--output", run / "final/spritesheet-extended.png",
         "--webp-output", run / "final/spritesheet-extended.webp", "--manifest-output", run / "final/spritesheet-extended.json")
    # The generic assembler centers a cropped neutral reference and fits gaze
    # poses around it. These inputs are already registered by one real camera:
    # retain that registration instead of recentering the rendered character.
    with Image.open(run / "final/spritesheet-extended.png") as opened:
        atlas = opened.convert("RGBA")
    atlas.paste(atlas.crop((0, 0, 192, 208)), (6 * 192, 0))
    for index, path in enumerate(sorted((run / "look-cells").glob("*.png"))):
        with Image.open(path) as cell:
            atlas.paste(cell.convert("RGBA"), ((index % 8) * 192, (9 + index // 8) * 208))
    atlas.save(run / "final/spritesheet-extended.png")
    tool("despill_chroma_edges.py", run / "final/spritesheet-extended.png", "--output", run / "final/spritesheet-extended.png",
         "--webp-output", run / "final/spritesheet-extended.webp", "--chroma-key", "#FF00FF", "--json-out", run / "qa/chroma-despill-extended.json")
    tool("validate_atlas.py", run / "final/spritesheet-extended.webp", "--require-v2", "--chroma-key", "#FF00FF", "--json-out", run / "final/validation-extended.json")
    tool("make_contact_sheet.py", run / "final/spritesheet-extended.webp", "--output", run / "qa/contact-sheet-extended.png")
    tool("make_direction_qa_sheet.py", run / "final/spritesheet-extended.webp", "--output", run / "qa/look-directions.png")
    tool("measure_direction_continuity.py", run / "final/spritesheet-extended.webp", "--json-out", run / "qa/look-continuity.json")
    tool("make_direction_blind_qa_sheet.py", run / "final/spritesheet-extended.webp", "--output", run / "qa/direction-blind-pairs.png", "--answer-key", run / "qa/direction-blind-answer-key.json")


if __name__ == "__main__":
    main()
