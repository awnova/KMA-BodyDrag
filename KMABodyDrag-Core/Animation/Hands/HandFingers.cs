//====================[ Imports ]====================
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KMABodyDrag.Animation.Hands
{
    //====================[ HandFingers ]====================
    // One hand's finger bones, set from rotations baked in Blender (the stim jab's, the defibrillator's grip). The bakes give
    // each bone's rotation relative to the hand's knuckle frame (HandFrames.FromKnuckles), from its rest pose:
    //     R_bone(now) = H(now) * pose * H(bind)^-1 * R_bone(bind)
    // so the bone's own axes and the bake's rig do not matter: pose goes onto this skeleton's bind pose of the bone, turned
    // with the hand. Rotations only, in world space, parents before children.
    internal sealed class HandFingers
    {
        private readonly Transform[] _bones;
        // Each bone's rotation at the bind pose, relative to the hand's knuckle frame at the bind pose.
        private readonly Quaternion[] _rest;

        private HandFingers(Transform[] bones, Quaternion[] rest)
        {
            _bones = bones;
            _rest = rest;
        }

        public int Count => _bones.Length;

        // The hand's fingers named by `bones` (parents first), or null (logged) when a bone is missing or has no bind pose.
        // Needs the knuckles too (digits 21, 31 and 51), which `bones` must include.
        public static HandFingers TryCreate(Player player, Transform palm, bool left, IReadOnlyList<string> bones, string tag)
        {
            if (!BoneNames.TryFindKnuckles(palm, left, out var middle, out var index, out var little)) return null;

            var transforms = new Transform[bones.Count];
            var wanted = new HashSet<Transform> { palm };
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i] = BoneNames.FindDescendant(palm, bones[i]);
                if (transforms[i] == null)
                {
                    Plugin.LogSource.LogWarning($"[{tag}] {player.ProfileId}: no finger bone {bones[i]}; the hand keeps its own fingers");
                    return null;
                }
                wanted.Add(transforms[i]);
            }

            var bind = SkeletonBind.Read(player, wanted);
            if (bind.Count != wanted.Count)
            {
                Plugin.LogSource.LogWarning($"[{tag}] {player.ProfileId}: bind pose found for {bind.Count} of {wanted.Count} hand bones; the hand keeps its own fingers");
                return null;
            }

            Quaternion bindHand = HandFrames.FromKnuckles(Position(bind[palm]), Position(bind[middle]), Position(bind[index]), Position(bind[little]));
            var rest = new Quaternion[transforms.Length];
            for (int i = 0; i < rest.Length; i++) rest[i] = Quaternion.Inverse(bindHand) * bind[transforms[i]].rotation;
            return new HandFingers(transforms, rest);
        }

        // hand: the hand's knuckle frame in the world now (palm bone rotation * its frame relative to the palm). weight: how
        // much of the pose against the fingers as the game has them.
        public void Apply(Quaternion hand, IReadOnlyList<Quaternion> pose, float weight)
        {
            if (weight <= 0f) return;
            for (int i = 0; i < _bones.Length && i < pose.Count; i++)
            {
                Quaternion wanted = hand * pose[i] * _rest[i];
                _bones[i].rotation = weight >= 1f ? wanted : Quaternion.Slerp(_bones[i].rotation, wanted, weight);
            }
        }

        private static Vector3 Position(Matrix4x4 m) => m.GetColumn(3);
    }
}
