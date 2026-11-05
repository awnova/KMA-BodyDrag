//====================[ Imports ]====================
using UnityEngine;

namespace KMABodyDrag.Animation.Hands
{
    //====================[ HandFrames ]====================
    // How the mod reads a hand's orientation off a skeleton, whatever the axes of its bones. There are two frames, for two
    // jobs; both are built from joint positions only (never from bone axes), so they mean the same on any skeleton.
    //
    //   FromKnuckles - the hand as a rigid object: for placing things in the hand (the paddles, the stim pen) and fingers
    //                  relative to it (the stim's fingers). The same frame is built in Blender by the bakes
    //                  (bake_defib_hands.py, bake_stim_jab.py), so a baked mesh or finger pose means the same here.
    //   ReadArm      - the hand at the end of the arm: for reaching the palm onto something (DraggerArms), where "neutral" is
    //                  the straight wrist, so the fingers carry on from the forearm.
    internal static class HandFrames
    {
        // The hand's frame from its joints: forward along palm -> middle knuckle, side along the index -> little knuckle
        // line, up = forward x side (Unity: Quaternion.LookRotation(forward, up)). The bakes build it the same way.
        public static Quaternion FromKnuckles(Vector3 palm, Vector3 middle, Vector3 index, Vector3 little)
        {
            Vector3 forward = (middle - palm).normalized;
            Vector3 across = index - little;
            across = (across - forward * Vector3.Dot(across, forward)).normalized;
            return Quaternion.LookRotation(forward, Vector3.Cross(forward, across));
        }

        // The hand's knuckle frame (FromKnuckles) relative to its palm bone; false without the knuckles.
        public static bool TryReadKnuckleFrame(Transform palm, bool left, out Quaternion frameLocal)
        {
            frameLocal = Quaternion.identity;
            if (!BoneNames.TryFindKnuckles(palm, left, out var middle, out var index, out var little)) return false;
            frameLocal = FromKnuckles(Vector3.zero, LocalOffset(palm, middle), LocalOffset(palm, index), LocalOffset(palm, little));
            return true;
        }

        // The offset of `bone` from `root` in `root`'s own space, walked from each bone's localPosition and localRotation
        // (unaffected by any scale or pose above `root`).
        public static Vector3 LocalOffset(Transform root, Transform bone)
        {
            if (root == null || bone == null) return Vector3.zero;
            if (!bone.IsChildOf(root)) return Quaternion.Inverse(root.rotation) * (bone.position - root.position);
            Vector3 offset = Vector3.zero;
            for (Transform t = bone; t != null && t != root; t = t.parent)
                offset = t.localPosition + t.localRotation * offset;
            return offset;
        }

        // The hand at the end of the arm, as rotation-only offsets from the palm bone (scale-free).
        public struct ArmFrame
        {
            // Which way the fingers point, which way the palm faces, and the middle of the palm's skin.
            public Vector3 FingerLocal;
            public Vector3 PalmLocal;
            public Vector3 PalmContactLocal;
            // The far end of the palm from the wrist: the knuckle line (the middle finger's root), palm skin out of the palm.
            public Vector3 KnuckleContactLocal;
        }

        // Works out the arm's hand frame in the palm bone's own space without relying on how the bone's axes are set up or
        // how the wrist is bent when the rig is built (the palm bone starts at the wrist, Digit21/31/41/51 are the index to
        // little-finger roots, Digit11 the thumb, and the palm faces the side the fingers curl to):
        //   across - index root to little-finger root (the knuckle line);
        //   along  - wrist to middle-finger root (or midpoint of index and little), in the palm's plane;
        //   palm   - out of the palm, from those two.
        // palmThickness: how far the palm's skin sits out from the bones of the palm. False when the fingers are not found
        // or the hand is degenerate.
        public static bool TryReadArm(Transform palm, bool left, float palmThickness, out ArmFrame frame)
        {
            frame = default;
            Transform index = BoneNames.FindDigit(palm, left, 21);
            Transform middle = BoneNames.FindDigit(palm, left, 31);
            Transform pinky = BoneNames.FindDigit(palm, left, 51);
            if (index == null || pinky == null) return false;

            Vector3 indexLocal = LocalOffset(palm, index);
            Vector3 pinkyLocal = LocalOffset(palm, pinky);
            Vector3 forward = middle != null ? LocalOffset(palm, middle) : (indexLocal + pinkyLocal) * 0.5f;
            Vector3 across = indexLocal - pinkyLocal;
            // Along x (index - pinky) comes out of the palm of a right hand; a left hand is the mirror image.
            Vector3 palmSide = Vector3.Cross(forward, across);
            if (left) palmSide = -palmSide;
            if (forward.sqrMagnitude < 1e-6f || palmSide.sqrMagnitude < 1e-10f) return false;
            palmSide.Normalize();
            Vector3 along = Vector3.ProjectOnPlane(forward, palmSide).normalized;

            // Wrist to the middle of the palm: half the way to the knuckles.
            float toKnuckles = Mathf.Clamp(Vector3.Dot(forward, along), 0.04f, 0.12f);
            float halfPalm = Mathf.Clamp(toKnuckles * 0.5f, 0.02f, 0.06f);

            frame.FingerLocal = along;
            frame.PalmLocal = palmSide;
            frame.PalmContactLocal = along * halfPalm + palmSide * palmThickness;
            frame.KnuckleContactLocal = along * toKnuckles + palmSide * palmThickness;
            return true;
        }
    }
}
