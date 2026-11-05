//====================[ Imports ]====================
using System.Collections.Generic;
using UnityEngine;

namespace KMABodyDrag.Animation.Shared
{
    //====================[ BoneNames ]====================
    // The bones of EFT's player rig that the downed body code addresses by name, and the lookups for them.
    internal static class BoneNames
    {
        public const string Prefix = "Base Human";

        public const string Pelvis = "Base HumanPelvis";
        public const string Ribcage = "Base HumanRibcage";
        public const string Neck = "Base HumanNeck";
        public const string Head = "Base HumanHead";

        public const string LeftThigh = "Base HumanLThigh1";
        public const string RightThigh = "Base HumanRThigh1";
        public const string LeftThighTwist = "Base HumanLThigh2";
        public const string RightThighTwist = "Base HumanRThigh2";
        public const string LeftCalf = "Base HumanLCalf";
        public const string RightCalf = "Base HumanRCalf";
        public const string LeftFoot = "Base HumanLFoot";
        public const string RightFoot = "Base HumanRFoot";

        public const string LeftUpperArm = "Base HumanLUpperarm";
        public const string RightUpperArm = "Base HumanRUpperarm";
        public const string LeftForearm = "Base HumanLForearm1";
        public const string RightForearm = "Base HumanRForearm1";
        public const string LeftForearmTwist2 = "Base HumanLForearm2";
        public const string LeftForearmTwist3 = "Base HumanLForearm3";
        public const string RightForearmTwist2 = "Base HumanRForearm2";
        public const string RightForearmTwist3 = "Base HumanRForearm3";
        public const string LeftPalm = "Base HumanLPalm";
        public const string RightPalm = "Base HumanRPalm";

        // The first bone of a transform list with this name, or null.
        public static Transform Find(IEnumerable<Transform> bones, string name)
        {
            foreach (var bone in bones)
            {
                if (bone != null && bone.name == name) return bone;
            }
            return null;
        }

        // A finger bone below a hand's palm: "Base HumanLDigit21" for the left hand's digit 21 (the tens are the finger:
        // 1 the thumb, 2 the index, 3 the middle, 5 the little; the units the joint along it).
        public static Transform FindDigit(Transform palm, bool left, int digit) =>
            FindDescendant(palm, Prefix + (left ? "L" : "R") + "Digit" + digit);

        // The middle, index and little-finger roots (digits 31, 21 and 51) under a palm; false when any is missing.
        public static bool TryFindKnuckles(Transform palm, bool left, out Transform middle, out Transform index, out Transform little)
        {
            middle = FindDigit(palm, left, 31);
            index = FindDigit(palm, left, 21);
            little = FindDigit(palm, left, 51);
            return middle != null && index != null && little != null;
        }

        // The first transform below root (not root itself) with this name, checking direct children before subtrees, or null.
        public static Transform FindDescendant(Transform root, string name)
        {
            foreach (Transform child in root)
            {
                if (child.name == name) return child;
            }
            foreach (Transform child in root)
            {
                var found = FindDescendant(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
