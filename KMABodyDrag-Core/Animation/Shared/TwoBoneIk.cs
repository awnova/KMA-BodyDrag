//====================[ Imports ]====================
using UnityEngine;

namespace KMABodyDrag.Animation.Shared
{
    //====================[ TwoBoneIk ]====================
    // Two-bone limb solve (shoulder-elbow-wrist, hip-knee-foot) on real bones, rotations only.
    internal static class TwoBoneIk
    {
        // Where the middle joint lands when the end of a limb of lengths upper and lower reaches toward `target` from
        // `root`, bent toward `bend` (a direction across the root-target line; its part along the line is dropped), and the
        // centre of the circle it can lie on. Out of reach, the limb is straight toward the target.
        public static Vector3 MiddlePoint(Vector3 root, Vector3 target, float upper, float lower, Vector3 bend, out Vector3 centre)
        {
            Vector3 toTarget = target - root;
            float d = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upper - lower) + 1e-3f, upper + lower - 1e-4f);
            Vector3 dir = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : Vector3.down;

            // The bend laid square to the line to the target, whatever it was worked out against: a callers' bend that the target has
            // since moved away from (DraggerArms solves the arm again as the hand is laid) put the elbow off its circle, so the hand
            // missed by up to ~3 cm at a close target (logged 2-5 cm below 35 cm from the shoulder; exact when square).
            bend = Vector3.ProjectOnPlane(bend, dir);
            bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized;

            // Law of cosines at the root.
            float cos = (upper * upper + d * d - lower * lower) / (2f * upper * d);
            float sin = Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos));
            centre = root + dir * (upper * cos);
            return centre + bend * (upper * sin);
        }

        // Puts the end of the limb on target by turning its upper and lower bone, the middle joint toward `bend` (a
        // unit direction perpendicular to the root-target line). Out of reach, the limb goes straight toward the
        // target and the end stops short of it. Nothing moves for a target on the root or a zero bend.
        public static void Solve(Transform root, Transform mid, Transform end, Vector3 target, float upper, float lower, Vector3 bend)
        {
            Vector3 a = root.position;
            Vector3 toTarget = target - a;
            if (toTarget.sqrMagnitude < 1e-8f || bend.sqrMagnitude < 1e-6f) return;

            Vector3 joint = MiddlePoint(a, target, upper, lower, bend, out _);
            float d = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upper - lower) + 1e-3f, upper + lower - 1e-4f);
            Vector3 reached = a + toTarget.normalized * d;

            Vector3 midBefore = mid.position;
            root.rotation = Quaternion.FromToRotation(midBefore - a, joint - a) * root.rotation;
            mid.rotation = Quaternion.FromToRotation(end.position - mid.position, reached - mid.position) * mid.rotation;
        }
    }
}
