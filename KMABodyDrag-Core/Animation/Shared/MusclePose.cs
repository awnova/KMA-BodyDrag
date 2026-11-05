//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KMABodyDrag.Animation.Shared
{
    //====================[ MusclePose ]====================
    // KeepMeAlive's muscle pose (BodyRagdollMusclePose), built on a copy of the player's skeleton without moving the live
    // transforms: the first frame of BakedClip.Settle (lying on the back, legs and spine straight, arms at the sides), with
    // RefineSettleArms. A corpse's joints are zeroed in it (CorpseBody.ZeroJoints), so the anatomical limits are centred on a
    // human pose, not on however the body lay when it was picked up.
    internal static class MusclePose
    {
        // Throws when the clip did not load or does not fit this skeleton (CorpseBody.CanRig checks first).
        public static SkeletonCopy Build(Player player)
        {
            var clip = BakedClip.Settle ?? throw new InvalidOperationException("the settle clip did not load");
            var rig = ClipRig.TryBuild(player, clip) ?? throw new InvalidOperationException("the settle clip does not fit this skeleton");

            Transform[] all = PlayerUtils.GetPelvis(player).GetComponentsInChildren<Transform>(true);
            var bind = SkeletonBind.Read(player, new HashSet<Transform>(all));
            var rotations = new Quaternion[rig.Count];
            rig.Evaluate(clip, 0f, rotations);

            var given = new Dictionary<Transform, Quaternion>();
            for (int k = 0; k < rig.Count; k++) given[rig.Bones[k]] = rig.BindFrame * rotations[k];

            var settle = new SkeletonCopy(all, bind);
            settle.Pose(given);
            settle.RefineSettleArms();
            return settle;
        }

        // A copy of a player's skeleton posed without moving the real one: each transform's rotation in the bind pose's own
        // space (only the rotations relative to each other are used).
        internal sealed class SkeletonCopy
        {
            private readonly Transform[] _all;
            private readonly int[] _parent;
            private readonly bool[] _bound;
            private readonly Quaternion[] _bind;
            private readonly Quaternion[] _rotation;
            private readonly Dictionary<Transform, int> _index = new Dictionary<Transform, int>();

            public SkeletonCopy(Transform[] all, Dictionary<Transform, Matrix4x4> bind)
            {
                _all = all;
                int n = all.Length;
                _parent = new int[n];
                _bound = new bool[n];
                _bind = new Quaternion[n];
                _rotation = new Quaternion[n];
                for (int i = 0; i < n; i++) _index[all[i]] = i;
                for (int i = 0; i < n; i++)
                {
                    _parent[i] = all[i].parent != null && _index.TryGetValue(all[i].parent, out int p) ? p : -1;
                    if (bind != null && bind.TryGetValue(all[i], out var m))
                    {
                        _bound[i] = true;
                        _bind[i] = m.rotation;
                    }
                }
            }

            // given: the bones whose rotation is given outright (bind-space world rotations).
            public void Pose(Dictionary<Transform, Quaternion> given)
            {
                for (int i = 0; i < _all.Length; i++)
                {
                    var t = _all[i];
                    int p = _parent[i];
                    if (given.TryGetValue(t, out var own)) _rotation[i] = own;
                    else if (_bound[i]) _rotation[i] = _bind[i];
                    else _rotation[i] = p >= 0 ? _rotation[p] * t.localRotation : _bind[i];
                }
            }

            // BakedClip.Settle frame 0 has an artificial ~84 deg palms-up forearm twist (which rotated the elbow flexion
            // hinge axis by ~84 deg relative to the upper arm) and zero hip abduction (leaving the thick thigh colliders
            // slightly overlapping at the midline).
            // I refine SettlePose here so:
            //   1. Upper arms and forearms lie cleanly outside the torso with a pure 1-DOF elbow hinge (-14 deg flexion,
            //      zero axial twist discrepancy);
            //   2. Thighs have a subtle 3.5 deg outward abduction so left and right leg colliders have positive clearance.
            public void RefineSettleArms()
            {
                var origRot = (Quaternion[])_rotation.Clone();
                for (int i = 0; i < _all.Length; i++)
                {
                    var t = _all[i];
                    int p = _parent[i];
                    if (p < 0) continue;

                    switch (t.name)
                    {
                        case BoneNames.LeftUpperArm:
                            _rotation[i] = _rotation[i] * Quaternion.Euler(4f, -9f, 0f);
                            break;
                        case BoneNames.RightUpperArm:
                            _rotation[i] = _rotation[i] * Quaternion.Euler(-4f, 9f, 0f);
                            break;
                        case BoneNames.LeftForearm:
                        case BoneNames.RightForearm:
                            _rotation[i] = _rotation[p] * Quaternion.Euler(0f, 0f, -14f);
                            break;
                        case BoneNames.LeftForearmTwist2:
                        case BoneNames.LeftForearmTwist3:
                        case BoneNames.RightForearmTwist2:
                        case BoneNames.RightForearmTwist3:
                            if (_bound[i] && _bound[p])
                                _rotation[i] = _rotation[p] * (Quaternion.Inverse(_bind[p]) * _bind[i]);
                            break;
                        case BoneNames.LeftPalm:
                        case BoneNames.RightPalm:
                            if (_bound[i] && _bound[p])
                                _rotation[i] = _rotation[p] * (Quaternion.Inverse(_bind[p]) * _bind[i]) * Quaternion.Euler(0f, 0f, -8f);
                            break;
                        case BoneNames.LeftThigh:
                            _rotation[i] = _rotation[i] * Quaternion.Euler(0f, -3.5f, 0f);
                            break;
                        case BoneNames.RightThigh:
                            _rotation[i] = _rotation[i] * Quaternion.Euler(0f, 3.5f, 0f);
                            break;
                        case BoneNames.LeftThighTwist:
                        case BoneNames.RightThighTwist:
                        case BoneNames.LeftCalf:
                        case BoneNames.RightCalf:
                        case BoneNames.LeftFoot:
                        case BoneNames.RightFoot:
                            Quaternion origLocal = Quaternion.Inverse(origRot[p]) * origRot[i];
                            _rotation[i] = _rotation[p] * origLocal;
                            break;
                    }
                }
            }

            public Transform[] AllBones => _all;
            public bool Has(Transform t) => t != null && _index.ContainsKey(t);
            public Quaternion Rotation(Transform t) => _rotation[_index[t]];

            public Quaternion LocalRotation(Transform t)
            {
                int i = _index[t];
                int p = _parent[i];
                return p >= 0 ? Quaternion.Inverse(_rotation[p]) * _rotation[i] : _rotation[i];
            }
        }
    }
}
