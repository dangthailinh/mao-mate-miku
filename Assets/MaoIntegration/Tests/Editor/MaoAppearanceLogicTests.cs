using NUnit.Framework;
using UnityEngine;

namespace MaoIntegration.Tests
{
    public sealed class MaoAppearanceLogicTests
    {
        [Test] public void UnknownPresetIsRejectedWithoutChangingOtherChoices()
        {
            var value = new MaoAppearanceProfile { outfit = 2, eyeColor = 1 };
            Assert.IsFalse(value.TrySet(MaoAppearancePart.Hair, 99));
            Assert.AreEqual(2, value.outfit);
            Assert.AreEqual(1, value.eyeColor);
            Assert.AreEqual(0, value.hair);
        }

        [Test] public void HairSelectionDoesNotResetOutfitOrFace()
        {
            var value = new MaoAppearanceProfile { outfit = 1, face = 2 };
            Assert.IsTrue(value.TrySet(MaoAppearancePart.Hair, 1));
            Assert.AreEqual(1, value.hair);
            Assert.AreEqual(1, value.outfit);
            Assert.AreEqual(2, value.face);
        }

        [Test] public void InvalidSavedProfileFallsBackToOriginalForEachInvalidField()
        {
            var value = new MaoAppearanceProfile { hair = -1, outfit = 22, face = 2, eyeColor = 99 };
            value.Normalize();
            Assert.AreEqual(0, value.hair);
            Assert.AreEqual(0, value.outfit);
            Assert.AreEqual(2, value.face);
            Assert.AreEqual(0, value.eyeColor);
        }

        [Test] public void ResetRestoresEveryAppearanceCategory()
        {
            var value = new MaoAppearanceProfile { hair = 2, hairColor = 2, outfit = 3, face = 1, eyeColor = 2 };
            value.Reset();
            Assert.IsTrue(value.IsOriginal);
        }

        [Test] public void SavedChoicesRoundTripWithoutLosingSelections()
        {
            var value = new MaoAppearanceProfile { hair = 1, hairColor = 2, outfit = 3, face = 2, eyeColor = 1 };
            var restored = JsonUtility.FromJson<MaoAppearanceProfile>(JsonUtility.ToJson(value));
            restored.Normalize();
            Assert.AreEqual(JsonUtility.ToJson(value), JsonUtility.ToJson(restored));
        }

        [Test] public void ShortHairKeepsAnchorFixedAndBoundsLength()
        {
            var anchor = new Vector3(1, 2, 3);
            Assert.AreEqual(anchor, MaoAppearanceGeometry.ShortenTail(anchor, anchor, 0.42f));
            var point = MaoAppearanceGeometry.ShortenTail(anchor + Vector3.down, anchor, 0.42f);
            Assert.That(Vector3.Distance(anchor, point), Is.EqualTo(0.42f).Within(0.00001f));
        }

        [Test] public void NonFiniteHairParameterCannotProduceCorruptVertices()
        {
            var point = new Vector3(0, -1, 0);
            Assert.AreEqual(point, MaoAppearanceGeometry.ShortenTail(point, Vector3.zero, float.NaN));
        }

        [Test] public void FaceChangesCannotAffectBodyVertices()
        {
            var point = new Vector3(0.2f, 0.5f, 0);
            Assert.AreEqual(point, MaoAppearanceGeometry.ShapeFace(point, new Vector3(0, 1.2f, 0), 1));
            Assert.AreEqual(point, MaoAppearanceGeometry.ShapeFace(point, Vector3.zero, 0));
        }

        // Numeric IDs let the tests exercise the additive contract before its implementation.
        private const MaoAppearancePart CatEars = (MaoAppearancePart)5;
        private const MaoAppearancePart CatTail = (MaoAppearancePart)6;

        [Test] public void AccessoryCategoriesAppendWithoutRenumberingExistingPresets()
        {
            Assert.AreEqual(0, (int)MaoAppearancePart.Hair);
            Assert.AreEqual(4, (int)MaoAppearancePart.EyeColor);
            Assert.AreEqual("CatEars", System.Enum.GetName(typeof(MaoAppearancePart), CatEars));
            Assert.AreEqual("CatTail", System.Enum.GetName(typeof(MaoAppearancePart), CatTail));
            Assert.AreEqual(2, MaoAppearanceProfile.Count(CatEars));
            Assert.AreEqual(2, MaoAppearanceProfile.Count(CatTail));
        }

        [Test] public void LegacySavedProfileKeepsExistingChoicesAndAccessoriesOff()
        {
            var value = JsonUtility.FromJson<MaoAppearanceProfile>(
                "{\"version\":1,\"hair\":2,\"hairColor\":1,\"outfit\":3,\"face\":2,\"eyeColor\":1}");
            value.Normalize();
            Assert.AreEqual(2, value.hair);
            Assert.AreEqual(1, value.hairColor);
            Assert.AreEqual(3, value.outfit);
            Assert.AreEqual(2, value.face);
            Assert.AreEqual(1, value.eyeColor);
            Assert.AreEqual(0, value.Get(CatEars));
            Assert.AreEqual(0, value.Get(CatTail));
        }

        [Test] public void EarsAndTailToggleIndependentlyWithoutChangingClothing()
        {
            var value = new MaoAppearanceProfile { hairColor = 2, outfit = 3, face = 1 };
            Assert.IsTrue(value.TrySet(CatEars, 1));
            Assert.IsTrue(value.TrySet(CatTail, 1));
            Assert.IsTrue(value.TrySet(CatEars, 0));
            Assert.AreEqual(0, value.Get(CatEars));
            Assert.AreEqual(1, value.Get(CatTail));
            Assert.AreEqual(2, value.hairColor);
            Assert.AreEqual(3, value.outfit);
            Assert.AreEqual(1, value.face);
        }

        [Test] public void SavedAccessoriesSurviveJsonRoundTripAndCopy()
        {
            var value = JsonUtility.FromJson<MaoAppearanceProfile>(
                "{\"hair\":1,\"outfit\":2,\"catEars\":1,\"catTail\":1}");
            var copy = value.Copy();
            Assert.AreEqual(1, copy.Get(CatEars));
            Assert.AreEqual(1, copy.Get(CatTail));
            Assert.AreEqual(1, copy.hair);
            Assert.AreEqual(2, copy.outfit);
        }

        [Test] public void InvalidSavedAccessoriesNormalizeToOffWithoutLosingOutfit()
        {
            var value = JsonUtility.FromJson<MaoAppearanceProfile>(
                "{\"outfit\":3,\"catEars\":-1,\"catTail\":99}");
            value.Normalize();
            Assert.AreEqual(0, value.Get(CatEars));
            Assert.AreEqual(0, value.Get(CatTail));
            Assert.AreEqual(3, value.outfit);
        }

        [Test] public void ResetClearsAccessoriesAndRestoresOriginalStatus()
        {
            var value = new MaoAppearanceProfile();
            Assert.IsTrue(value.TrySet(CatTail, 1));
            Assert.IsFalse(value.IsOriginal);
            value.Reset();
            Assert.IsTrue(value.IsOriginal);
            Assert.AreEqual(0, value.Get(CatTail));
            Assert.AreEqual(0, value.Get(CatEars));
        }

        [Test] public void InvalidAccessorySelectionsCannotOverwriteEnabledChoice()
        {
            var value = new MaoAppearanceProfile();
            Assert.IsTrue(value.TrySet(CatEars, 1));
            Assert.IsFalse(value.TrySet(CatEars, -1));
            Assert.IsFalse(value.TrySet(CatEars, 2));
            Assert.IsFalse(value.TrySet((MaoAppearancePart)100, 0));
            Assert.AreEqual(1, value.Get(CatEars));
        }

        [Test] public void VisualVerificationRecognizesOnlyItsTwoFixedFlags()
        {
            Assert.IsTrue(VerificationRequested("--mao-smoke-cat-accessories"));
            Assert.IsTrue(VerificationRequested("--mao-smoke-cat-accessories-reload", "--mao-smoke-exit"));
            Assert.IsFalse(VerificationRequested("--mao-smoke-cat-accessories-extra"));
        }

        [Test] public void OrdinaryLaunchArgumentsNeverEnableVisualVerification()
        {
            Assert.IsFalse(VerificationRequested());
            Assert.IsFalse(VerificationRequested((string[])null));
            Assert.IsFalse(VerificationRequested("--mao-smoke-chat", "-logFile", "player.log"));
        }

        private static bool VerificationRequested(params string[] arguments)
        {
            var type = typeof(MaoAppearanceProfile).Assembly.GetType("MaoIntegration.MaoAppearanceVerification");
            Assert.IsNotNull(type, "Visual verification needs a fixed, shared command-line guard.");
            var method = type.GetMethod("IsRequested");
            Assert.IsNotNull(method);
            return (bool)method.Invoke(null, new object[] { arguments });
        }
    }
}
