//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KMABodyDrag.Animation.Shared
{
    //====================[ SkeletonBind ]====================
    // The bind pose of a player's skeleton, read from the meshes bound to it.
    internal static class SkeletonBind
    {
        // Each wanted bone's pose in the bind pose (the inverse of its bind matrix). Every character mesh is bound in
        // the same space, so poses from different meshes can be compared. The player carries many meshes (every LOD,
        // every piece of gear); the search stops once every wanted bone has been found. Bones no mesh is bound to are
        // left out (they keep their pose).
        public static Dictionary<Transform, Matrix4x4> Read(Player player, HashSet<Transform> wanted)
        {
            var bind = new Dictionary<Transform, Matrix4x4>();
            foreach (var renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                Transform[] rendererBones = renderer.bones;
                Matrix4x4[] poses = null;
                for (int i = 0; i < rendererBones.Length; i++)
                {
                    var bone = rendererBones[i];
                    if (bone == null || !wanted.Contains(bone) || bind.ContainsKey(bone)) continue;
                    // Copied out of the mesh only once something in it is needed.
                    poses ??= mesh.bindposes;
                    if (i < poses.Length) bind[bone] = poses[i].inverse;
                }
                if (bind.Count == wanted.Count) break;
            }

            // Every character is built from the same prefab, so a bone's bind pose is the same on all of them. The local
            // player's own meshes may not cover every bone (its first person arms are a separate set), where another
            // player's do: remembered by bone name, and used only to fill in what the local body lacks.
            foreach (var pair in bind) Known[pair.Key.name] = pair.Value;
            if (player.IsYourPlayer)
            {
                foreach (var bone in wanted)
                    if (bone != null && !bind.ContainsKey(bone) && Known.TryGetValue(bone.name, out var known)) bind[bone] = known;
            }
            return bind;
        }

        private static readonly Dictionary<string, Matrix4x4> Known = new Dictionary<string, Matrix4x4>();

        // Raid boundary: another raid may bring another prefab.
        public static void ClearKnown() => Known.Clear();

        // Where a bone is in the bind pose; false when it is missing or no mesh is bound to it.
        public static bool TryPosition(Dictionary<Transform, Matrix4x4> bind, Transform bone, out Vector3 position)
        {
            position = Vector3.zero;
            if (bone == null || !bind.TryGetValue(bone, out var m)) return false;
            position = m.GetColumn(3);
            return true;
        }

        // The bind pose's own frame, from the skeleton's bones by name (bone): up from the feet to the head, right from
        // the left hip to the right, and the point between the feet. False when one of those bones has no bind pose
        // or the hips are on the up axis.
        public static bool TryFrame(Dictionary<Transform, Matrix4x4> bind, Func<string, Transform> bone,
            out Vector3 up, out Vector3 right, out Vector3 feet)
        {
            up = right = feet = Vector3.zero;
            if (!TryPosition(bind, bone(BoneNames.Head), out var head)
                || !TryPosition(bind, bone(BoneNames.LeftFoot), out var leftFoot)
                || !TryPosition(bind, bone(BoneNames.RightFoot), out var rightFoot)
                || !TryPosition(bind, bone(BoneNames.LeftThigh), out var leftHip)
                || !TryPosition(bind, bone(BoneNames.RightThigh), out var rightHip))
                return false;

            feet = (leftFoot + rightFoot) * 0.5f;
            up = (head - feet).normalized;
            right = Vector3.ProjectOnPlane(rightHip - leftHip, up);
            if (right.sqrMagnitude < 1e-8f) return false;
            right.Normalize();
            return true;
        }
    }
}
