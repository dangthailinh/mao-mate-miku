"""Integration acceptance checks for actual, locally rendered Miku pixels."""
import hashlib
import os
from pathlib import Path
import unittest
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
RUN = Path(os.environ.get("MIKU_PET_RUN", ROOT / "Logs/CodexPet-20261029"))


class RenderAcceptance(unittest.TestCase):
    def test_original_avatar_unchanged(self):
        digest = hashlib.sha256((ROOT / "AvatarAssets/hatsune-miku.vrm").read_bytes()).hexdigest()
        self.assertEqual(digest, "f0ebcbdf5b2d4f86b123c63d4860ecf0e178e69b926c16ee01ec2f30c9d4e345")

    def test_actual_preview_has_transparent_padding_and_visible_avatar(self):
        with Image.open(RUN / "preview.png") as image:
            self.assertEqual(image.mode, "RGBA")
            self.assertEqual(image.size, (384, 416))
            alpha = image.getchannel("A")
            box = alpha.point(lambda value: 255 if value > 24 else 0).getbbox()
            self.assertIsNotNone(box)
            self.assertGreater(box[0], 3)
            self.assertGreater(box[1], 3)
            self.assertLess(box[2], image.width - 3)
            self.assertLess(box[3], image.height - 3)
            self.assertGreater(box[3] - box[1], 190)
            self.assertEqual(alpha.getpixel((0, 0)), 0)
            visible = [pixel for pixel in image.get_flattened_data() if pixel[3] > 128]
            self.assertGreater(len(visible), 3000)
            self.assertGreater(sum(max(pixel[:3]) > 80 for pixel in visible), len(visible) * 0.25)

    def test_preview_shows_the_face_not_the_back_of_the_head(self):
        with Image.open(RUN / "preview.png").convert("RGBA") as image:
            box = image.getchannel("A").point(lambda a: 255 if a > 24 else 0).getbbox()
            center = (box[0] + box[2]) // 2
            face = list(image.crop((center - 32, box[1] + 20, center + 32, box[1] + 90)).get_flattened_data())
            skin = sum(a > 128 and r > 150 and r > g * 1.04 and r > b * 1.06 for r, g, b, a in face)
            purple = sum(a > 128 and r > g * 1.25 and b > g * 1.3 and b > 70 for r, g, b, a in face)
            self.assertGreater(skin, 80)
            self.assertGreater(purple, 10)


if __name__ == "__main__":
    unittest.main()
