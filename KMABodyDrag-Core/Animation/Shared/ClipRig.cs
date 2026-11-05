//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KMABodyDrag.Animation.Shared
{
    //====================[ ClipRig ]====================
    // A player's skeleton set up to play BakedClips. Each bone a clip drives is first turned to point the way it
    // points at the clip's reference frame, then given the clip's rotation change. Bones it does not drive that sit
    // between ones it does (twist bones, the ribcage) are held at their bind pose relative to their parent. Rotations
    // come out in the character frame; the caller turns them to face the way the body faces.
    internal sealed class ClipRig
    {
        // Bones the clips do not drive that sit between ones they do.
        private static readonly string[] HeldBones =
        {
            BoneNames.Ribcage, BoneNames.LeftThighTwist, BoneNames.RightThighTwist,
            BoneNames.LeftForearmTwist2, BoneNames.LeftForearmTwist3, BoneNames.RightForearmTwist2, BoneNames.RightForearmTwist3
        };

        // What has been reported for whom, so a body that cannot be rigged says why once, not every tick.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        public static void ClearReports() => Reported.Clear();

        private static void Report(Player player, string what)
        {
            if (!Reported.Add(player.ProfileId + what)) return;
            Plugin.LogSource.LogWarning($"[ClipRig] {player.ProfileId} (local={player.IsYourPlayer}): {what}");
        }

        // Parent-first, so a bone's rotation is worked out after its parent's.
        public readonly Transform[] Bones;
        // Clip track of each bone, or -1 for a held bone.
        private readonly int[] _track;
        // Rig index of each bone's parent, or -1 when it is not in the rig (only ever a driven bone).
        private readonly int[] _parent;
        // Driven: the bind pose turned to the reference frame's direction, in the character frame.
        // Held: the rotation relative to the parent.
        private readonly Quaternion[] _rest;
        private readonly Dictionary<Transform, int> _index = new Dictionary<Transform, int>();

        public readonly Transform Pelvis;
        // Clip metres to this skeleton's metres.
        public readonly float Scale;
        // The bind pose's character frame: bind-space rotation = BindFrame * character-frame rotation.
        public readonly Quaternion BindFrame;
        private readonly BakedClip _clip;

        private ClipRig(BakedClip clip, List<Transform> bones, List<int> tracks, List<Quaternion> rest, Transform pelvis, float scale, Quaternion bindFrame)
        {
            BindFrame = bindFrame;
            _clip = clip;
            int n = bones.Count;
            Bones = bones.ToArray();
            _track = tracks.ToArray();
            _rest = rest.ToArray();
            _parent = new int[n];
            for (int k = 0; k < n; k++) _index[Bones[k]] = k;
            for (int k = 0; k < n; k++) _parent[k] = IndexOf(Bones[k].parent);
            Pelvis = pelvis;
            Scale = scale;
        }

        public int Count => Bones.Length;
        public int IndexOf(Transform t) => t != null && _index.TryGetValue(t, out int k) ? k : -1;
        public int ParentOf(int k) => _parent[k];
        // Driven by the clip, rather than held relative to its parent.
        public bool IsDriven(int k) => _track[k] >= 0;

        // Whether a clip plays on this rig.
        public bool Plays(BakedClip clip) => clip != null && clip.SharesRig(_clip);

        // The character-frame rotation of every bone of the rig at time t of the clip (one this rig Plays).
        public void Evaluate(BakedClip clip, float t, Quaternion[] rotations)
        {
            clip.FrameAt(t, out int a, out int b, out float f);
            for (int k = 0; k < Bones.Length; k++)
            {
                int track = _track[k];
                rotations[k] = track >= 0
                    ? Quaternion.Slerp(clip.Tracks[track].Delta[a], clip.Tracks[track].Delta[b], f) * _rest[k]
                    : rotations[_parent[k]] * _rest[k];
            }
        }

        //====================[ Build ]====================
        // Sets up the rig for a clip (and every clip that shares its reference), or returns null when a bone it
        // drives is missing.
        public static ClipRig TryBuild(Player player, BakedClip clip)
        {
            if (clip == null) return null;
            if (!TryMeasure(player, clip, out var pelvis, out var byName, out var bind, out var bindFrame, out float scale)) return null;

            var bones = new List<Transform>();
            var tracks = new Dictionary<Transform, int>();
            for (int i = 0; i < clip.Tracks.Length; i++)
            {
                if (!byName.TryGetValue(clip.Tracks[i].Bone, out var t) || !bind.ContainsKey(t))
                {
                    Report(player, byName.ContainsKey(clip.Tracks[i].Bone)
                        ? $"no bind pose for {clip.Tracks[i].Bone}"
                        : $"no bone named {clip.Tracks[i].Bone} under the skeleton");
                    return null;
                }
                bones.Add(t);
                tracks[t] = i;
            }

            var included = new HashSet<Transform>(bones);
            foreach (var name in HeldBones)
            {
                if (byName.TryGetValue(name, out var t) && included.Add(t)) bones.Add(t);
            }

            // Parent-first. A held bone needs its parent in the rig; one whose parent was dropped is dropped too.
            bones.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
            Quaternion toCharacter = Quaternion.Inverse(bindFrame);
            var kept = new List<Transform>();
            var keptTracks = new List<int>();
            var rest = new List<Quaternion>();
            var keptSet = new HashSet<Transform>();
            foreach (var bone in bones)
            {
                if (tracks.TryGetValue(bone, out int track))
                {
                    rest.Add(DrivenRest(bone, clip.Tracks[track], bind, toCharacter));
                    keptTracks.Add(track);
                }
                else
                {
                    if (bone.parent == null || !keptSet.Contains(bone.parent)) continue;
                    // Bind pose relative to the parent; a bone no mesh is bound to keeps the pose it has now.
                    rest.Add(bind.TryGetValue(bone, out var own) && bind.TryGetValue(bone.parent, out var above)
                        ? Quaternion.Inverse(above.rotation) * own.rotation
                        : bone.localRotation);
                    keptTracks.Add(-1);
                }
                kept.Add(bone);
                keptSet.Add(bone);
            }

            return new ClipRig(clip, kept, keptTracks, rest, pelvis, scale, bindFrame);
        }

        // The bind pose in the character frame, turned so the bone points at its child the way it does in the
        // clip's reference frame.
        private static Quaternion DrivenRest(Transform bone, BakedClip.Track track, Dictionary<Transform, Matrix4x4> bind, Quaternion toCharacter)
        {
            Quaternion rest = toCharacter * bind[bone].rotation;
            if (track.Child == null || bone.parent == null) return rest;

            var child = BoneNames.FindDescendant(bone, track.Child);
            if (child == null || !bind.TryGetValue(child, out var childBind)) return rest;

            Vector3 pointing = toCharacter * ((Vector3)childBind.GetColumn(3) - (Vector3)bind[bone].GetColumn(3));
            return pointing.sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(pointing.normalized, track.Direction) * rest : rest;
        }

        //====================[ Skeleton ]====================
        // Clip metres to this skeleton's metres, from the pelvis height.
        public static bool TryGetScale(Player player, BakedClip clip, out float scale) =>
            TryMeasure(player, clip, out _, out _, out _, out _, out scale);

        // Finds the bones and bind poses, and works out the bind frame and the clip-to-skeleton scale.
        private static bool TryMeasure(Player player, BakedClip clip, out Transform pelvis, out Dictionary<string, Transform> byName,
            out Dictionary<Transform, Matrix4x4> bind, out Quaternion bindFrame, out float scale)
        {
            byName = null;
            bind = null;
            bindFrame = Quaternion.identity;
            scale = 0f;

            var bones = player.PlayerBones;
            pelvis = PlayerUtils.GetPelvis(player);
            if (clip == null || pelvis == null || pelvis.parent == null) return false;

            byName = NameTable(player, pelvis.parent);

            bind = SkeletonBind.Read(player, WantedBones(byName));
            if (!TryBindFrame(bind, byName, out bindFrame, out float bindToWorld, out float bindHips))
            {
                var missing = new List<string>();
                foreach (var name in new[] { BoneNames.Pelvis, BoneNames.Head, BoneNames.LeftThigh, BoneNames.RightThigh, BoneNames.LeftCalf, BoneNames.LeftFoot, BoneNames.RightFoot })
                    if (!byName.TryGetValue(name, out var t) || !bind.ContainsKey(t)) missing.Add(name);
                Report(player, $"skeleton frame not found ({bind.Count} bind poses from {player.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length} meshes; none for: {string.Join(", ", missing)})");
                return false;
            }
            scale = bindHips * bindToWorld / clip.HipsHeight;
            return scale > 0f && !float.IsInfinity(scale);
        }

        // The player's bones by name. The skeleton's own table lists the real ones (keyed by path from its root); a first
        // match by name does not: whatever the player holds carries a full copy of the arm bones under the same names, and
        // for the local player (whose hands container hangs under its camera, ahead of the ribcage) the copy comes first,
        // has no mesh bound to it, and the rig cannot be built. Anything the table lacks is found by name as before.
        private static Dictionary<string, Transform> NameTable(Player player, Transform root)
        {
            var byName = new Dictionary<string, Transform>();
            var skeleton = player.PlayerBody != null ? player.PlayerBody.SkeletonHands : null;
            if (skeleton != null && skeleton.Bones != null)
            {
                foreach (var pair in skeleton.Bones)
                {
                    var bone = pair.Value;
                    if (bone == null || !bone.IsChildOf(root)) continue;
                    int slash = pair.Key.LastIndexOf('/');
                    byName[slash >= 0 ? pair.Key.Substring(slash + 1) : pair.Key] = bone;
                }
            }

            int copies = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!byName.TryGetValue(t.name, out var known)) byName[t.name] = t;
                else if (known != t && t.name.StartsWith(BoneNames.Prefix, StringComparison.Ordinal)) copies++;
            }
            if (copies > 0) Report(player, $"{copies} copies of rig bones under the skeleton (held items); the skeleton's own table is used");
            return byName;
        }

        // Every bone of the rig proper (not the weapon, gear or camera transforms that hang off it).
        private static HashSet<Transform> WantedBones(Dictionary<string, Transform> byName)
        {
            var wanted = new HashSet<Transform>();
            foreach (var t in byName.Values)
                if (t.name.StartsWith(BoneNames.Prefix, StringComparison.Ordinal)) wanted.Add(t);
            return wanted;
        }

        // The character frame of the bind pose (up from the feet to the head, right from the left hip to the
        // right), how many world metres one bind unit is, and the bind pelvis height above the feet.
        private static bool TryBindFrame(Dictionary<Transform, Matrix4x4> bind, Dictionary<string, Transform> byName,
            out Quaternion frame, out float bindToWorld, out float hipsHeight)
        {
            frame = Quaternion.identity;
            bindToWorld = 1f;
            hipsHeight = 0f;
            Transform Bone(string name) => byName.TryGetValue(name, out var t) ? t : null;
            if (!SkeletonBind.TryFrame(bind, Bone, out Vector3 up, out Vector3 right, out Vector3 feet)
                || !SkeletonBind.TryPosition(bind, Bone(BoneNames.Pelvis), out var pelvis)
                || !SkeletonBind.TryPosition(bind, Bone(BoneNames.LeftThigh), out var leftHip)
                || !SkeletonBind.TryPosition(bind, Bone(BoneNames.LeftCalf), out var leftKnee))
                return false;

            frame = Quaternion.LookRotation(Vector3.Cross(right, up), up);

            // Bind space may not be in metres; the thigh's length in both tells the scale.
            float bindThigh = Vector3.Distance(leftHip, leftKnee);
            float worldThigh = Vector3.Distance(byName[BoneNames.LeftThigh].position, byName[BoneNames.LeftCalf].position);
            if (bindThigh < 1e-5f) return false;
            bindToWorld = worldThigh / bindThigh;
            hipsHeight = Vector3.Dot(pelvis - feet, up);
            return hipsHeight > 0f;
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (var p = t.parent; p != null; p = p.parent) depth++;
            return depth;
        }
    }
}
