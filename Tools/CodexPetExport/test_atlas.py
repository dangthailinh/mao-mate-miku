"""Acceptance tests for the real final pixels, not generated placeholder art."""
import hashlib
import json
import os
from pathlib import Path
import unittest
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
RUN = Path(os.environ.get("MIKU_PET_RUN", ROOT / "Logs/CodexPet-20261029"))
COUNTS = (6, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8)


class AtlasAcceptance(unittest.TestCase):
    def test_v2_geometry_and_all_used_and_unused_cells(self):
        with Image.open(RUN / "final/spritesheet-extended.webp").convert("RGBA") as atlas:
            self.assertEqual(atlas.size, (1536, 2288))
            for row, used in enumerate(COUNTS):
                for col in range(8):
                    alpha = atlas.crop((192 * col, 208 * row, 192 * (col + 1), 208 * (row + 1))).getchannel("A")
                    occupied = sum(alpha.histogram()[1:])
                    # The installed v2 assembler reserves r0c6 for neutral/front.
                    if col < used or (row == 0 and col == 6):
                        self.assertGreater(occupied, 400, (row, col))
                    else:
                        self.assertEqual(occupied, 0, (row, col))

    def test_each_standard_state_contains_motion(self):
        with Image.open(RUN / "final/spritesheet-extended.webp").convert("RGBA") as atlas:
            for row, count in enumerate(COUNTS[:9]):
                frames = [atlas.crop((192 * col, 208 * row, 192 * (col + 1), 208 * (row + 1))) for col in range(count)]
                self.assertGreaterEqual(len({hashlib.sha256(frame.tobytes()).hexdigest() for frame in frames}), 3, row)

    def test_jump_really_leaves_the_baseline(self):
        with Image.open(RUN / "final/spritesheet-extended.webp").convert("RGBA") as atlas:
            baseline = atlas.crop((0, 4 * 208, 192, 5 * 208)).getchannel("A").getbbox()[3]
            peak = atlas.crop((2 * 192, 4 * 208, 3 * 192, 5 * 208)).getchannel("A").getbbox()[3]
            self.assertGreater(baseline - peak, 8)

    def test_drag_lower_legs_stay_still_instead_of_stepping(self):
        with Image.open(RUN / "final/spritesheet-extended.webp").convert("RGBA") as atlas:
            for row in (1, 2):
                legs = []
                for col in range(8):
                    legs.append(atlas.crop((192 * col, 208 * row + 140, 192 * (col + 1), 208 * (row + 1))).tobytes())
                self.assertEqual(len(set(legs)), 1, row)

    def test_drag_feet_are_lifted_above_idle_baseline(self):
        with Image.open(RUN / "final/spritesheet-extended.webp").convert("RGBA") as atlas:
            idle = atlas.crop((0, 0, 192, 208)).getchannel("A").point(lambda a: 255 if a > 32 else 0)
            baseline = idle.getbbox()[3]
            for row in (1, 2):
                for col in range(8):
                    alpha = atlas.crop((192 * col, 208 * row, 192 * (col + 1), 208 * (row + 1))).getchannel("A")
                    bottom = alpha.point(lambda value: 255 if value > 32 else 0).getbbox()[3]
                    self.assertGreaterEqual(baseline - bottom, 8, (row, col))

    def test_all_sixteen_gaze_poses_are_distinct(self):
        with Image.open(RUN / "final/spritesheet-extended.webp").convert("RGBA") as atlas:
            poses = [atlas.crop((192 * col, 208 * row, 192 * (col + 1), 208 * (row + 1))).tobytes() for row in (9, 10) for col in range(8)]
            self.assertEqual(len(set(poses)), 16)

    def test_reserved_neutral_matches_first_idle(self):
        with Image.open(RUN / "final/spritesheet-extended.webp").convert("RGBA") as atlas:
            idle = atlas.crop((0, 0, 192, 208))
            neutral = atlas.crop((6 * 192, 0, 7 * 192, 208))
            # Extra clear-background composition and edge cleanup can round the
            # RGB of translucent pixels. The neutral silhouette must be exact.
            self.assertEqual(idle.getchannel("A").tobytes(), neutral.getchannel("A").tobytes())
            opaque_differences = [max(abs(a[i] - b[i]) for i in range(3)) for a, b in zip(idle.get_flattened_data(), neutral.get_flattened_data()) if a[3] == 255]
            self.assertLessEqual(max(opaque_differences), 3)

    def test_gaze_keeps_the_same_lower_body_registration(self):
        with Image.open(RUN / "final/spritesheet-extended.webp").convert("RGBA") as atlas:
            neutral = atlas.crop((6 * 192, 160, 7 * 192, 208)).getchannel("A").tobytes()
            for row in (9, 10):
                for col in range(8):
                    lower = atlas.crop((192 * col, 208 * row + 160, 192 * (col + 1), 208 * (row + 1))).getchannel("A").tobytes()
                    self.assertEqual(neutral, lower, (row, col))

    def test_original_appearance_settings_not_changed(self):
        path = Path.home() / "AppData/LocalLow/Linh/Mao Mate Engine/mao-appearance.json"
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), "7390cf528a755fdb0998ee45fd9ad1a4ab88b73eda3ead2cf5dce78f13f557d6")

    def test_actual_renderer_provenance(self):
        source = json.loads((RUN / "render-manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(source["method"], "local-unity-vrm-render")
        self.assertEqual(sum(row["frames"] for row in source["states"]) + len(source["lookDegrees"]), 73)
        self.assertFalse(source["rootMotion"])


if __name__ == "__main__":
    unittest.main()
