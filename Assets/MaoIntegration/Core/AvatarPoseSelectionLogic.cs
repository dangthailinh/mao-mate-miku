namespace MaoIntegration
{
    public enum PosePlaybackBlockReason
    {
        None,
        IndexOutOfRange,
        AnimatorMissing,
        AnimatorNotHumanoid,
        PoseHandlerMissing,
        InvalidMuscles,
        AnimatorBusy
    }

    public static class AvatarPoseSelectionLogic
    {
        public const int PoseMenuPageCapacity = 9;

        public static int NextIndex(int currentIndex, int poseCount)
        {
            if (poseCount <= 0) return -1;
            return currentIndex < 0 ? 0 : (currentIndex + 1) % poseCount;
        }

        public static int PreviousIndex(int currentIndex, int poseCount)
        {
            if (poseCount <= 0) return -1;
            if (currentIndex <= 0) return poseCount - 1;
            return (currentIndex - 1) % poseCount;
        }

        public static int ConsoleCycleIndex(int currentIndex, int poseCount)
        {
            if (poseCount <= 0) return -1;
            if (currentIndex < 0) return 0;
            return currentIndex >= poseCount - 1 ? -1 : currentIndex + 1;
        }

        public static string[] ConsoleEntryHiddenAnimatorBools()
        {
            return System.Array.Empty<string>();
        }

        public static string[] ConsoleEntryHiddenAnimatorStates()
        {
            return System.Array.Empty<string>();
        }

        public static string[] PoseResourceNames()
        {
            return new[]
            {
                "Cool_01", "Cool_02", "Cool_03",
                "Kawaii_01", "Kawaii_02", "Kawaii_03",
                "Female Standing 1", "Female Standing 2", "Female Crouch",
                "Sit_01", "Sit_02", "Sit_03",
                "Sleep_01", "Sieep_02", "Sieep_03"
            };
        }

        public static string[] RuntimeFbxPoseResourcePaths()
        {
            return new[]
            {
                "MikuPoses/Fbx/Female Standing 1",
                "MikuPoses/Fbx/Female Standing 2",
                "MikuPoses/Fbx/Female Crouch"
            };
        }

        public static int PosePageCount(int poseCount)
        {
            return poseCount <= 0 ? 0 : (poseCount + PoseMenuPageCapacity - 1) / PoseMenuPageCapacity;
        }

        public static int[] PoseIndicesForPage(int pageIndex, int poseCount)
        {
            int pageCount = PosePageCount(poseCount);
            if (pageIndex < 0 || pageIndex >= pageCount) return System.Array.Empty<int>();
            int start = pageIndex * PoseMenuPageCapacity;
            int count = System.Math.Min(PoseMenuPageCapacity, poseCount - start);
            var result = new int[count];
            for (int index = 0; index < count; index++) result[index] = start + index;
            return result;
        }

        public static string PoseMenuLabel(string poseName)
        {
            switch (poseName ?? string.Empty)
            {
                case "Female Standing 1": return "STAND 1";
                case "Female Standing 2": return "STAND 2";
                case "Female Crouch": return "CROUCH";
                case "Cool_01": return "COOL 1";
                case "Cool_02": return "COOL 2";
                case "Cool_03": return "COOL 3";
                case "Kawaii_01": return "CUTE 1";
                case "Kawaii_02": return "CUTE 2";
                case "Kawaii_03": return "CUTE 3";
                case "Sit_01": return "SIT 1";
                case "Sit_02": return "SIT 2";
                case "Sit_03": return "SIT 3";
                case "Sleep_01": return "SLEEP 1";
                case "Sieep_02": return "SLEEP 2";
                case "Sieep_03": return "SLEEP 3";
                default: return string.IsNullOrWhiteSpace(poseName) ? "POSE" : poseName.ToUpperInvariant();
            }
        }

        public static bool HasExpectedMuscleCount(float[] muscles, int expectedCount)
        {
            return muscles != null && muscles.Length == expectedCount;
        }

        public static PosePlaybackBlockReason EvaluatePlayback(
            int poseIndex,
            int poseCount,
            bool hasAnimator,
            bool animatorIsHumanoid,
            bool hasPoseHandler,
            bool hasValidMuscles,
            bool animatorIsBusy)
        {
            if (poseIndex < 0 || poseIndex >= poseCount) return PosePlaybackBlockReason.IndexOutOfRange;
            if (!hasAnimator) return PosePlaybackBlockReason.AnimatorMissing;
            if (!animatorIsHumanoid) return PosePlaybackBlockReason.AnimatorNotHumanoid;
            if (!hasPoseHandler) return PosePlaybackBlockReason.PoseHandlerMissing;
            if (!hasValidMuscles) return PosePlaybackBlockReason.InvalidMuscles;
            return animatorIsBusy ? PosePlaybackBlockReason.AnimatorBusy : PosePlaybackBlockReason.None;
        }

        public static bool ShouldExpirePose(bool isPoseActive, float stopAt, float now)
        {
            return isPoseActive && stopAt > 0f && now >= stopAt;
        }
    }
}
