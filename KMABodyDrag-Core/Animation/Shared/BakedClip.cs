//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KMABodyDrag.Animation.Shared
{
    //====================[ BakedClip ]====================
    // A clip baked by Build/Tools/DownedFall/bake_blender_fall.py, skeleton independent: per bone, the world rotation change
    // since the reference frame, in the character frame (+Z facing, +Y up), and the direction the bone points in at
    // the reference frame; plus the pelvis travel. ClipRig puts it onto a player's skeleton. Clips baked against the
    // same reference (the ragdoll loop against the fall's first frame) play on the same rig, and one carries on from
    // the pose the other leaves, twist and all.
    internal sealed class BakedClip
    {
        private const string Schema = "keepmealive.downedfall.v1";

        public sealed class Track
        {
            public string Bone;
            public Quaternion[] Delta;
            // The child the bone points at, and which way it points at the reference frame; null when it has none.
            public string Child;
            public Vector3 Direction;
        }

        // "<file>:<action>" of the frame the clip was baked against.
        public string Reference;
        public float Fps;
        public int Frames;
        public float Duration => (Frames - 1) / Fps;
        // Pelvis height above the ankle joints (for the skeleton's scale) and above the floor, at the reference frame.
        public float HipsHeight;
        public float FloorHeight;
        public Vector3[] Pelvis;
        public Track[] Tracks;

        //====================[ Clips ]====================
        // KeepMeAlive's settle clip (Build/Tools/DownedFall/make_settle_clip.py in KeepMeAlive). Only its first frame is used here: the
        // muscle pose (lying on the back, legs and spine straight, arms at the sides), where a corpse's joints are zeroed (MusclePose).
        public static BakedClip Settle => Get("KMABodyDrag.Resources.downed_settle.json");
        // Loaded on first use; null (logged once) when it cannot be.
        private static BakedClip Get(string resource) => EmbeddedData.Get(resource, nameof(BakedClip), () => Load(resource));

        // Whether this clip plays on a rig set up for the other: baked against the same frame, the same bones.
        public bool SharesRig(BakedClip other)
        {
            if (other == null || Reference != other.Reference || Tracks.Length != other.Tracks.Length) return false;
            for (int i = 0; i < Tracks.Length; i++)
            {
                if (Tracks[i].Bone != other.Tracks[i].Bone) return false;
            }
            return true;
        }

        // The two frames either side of time t (clamped to the clip) and how far between them it is.
        public void FrameAt(float t, out int a, out int b, out float f)
        {
            f = MathUtil.SplitKey(t * Fps, Frames, out a, out b);
        }

        // The way the spine points at a frame, from the pelvis toward the neck, as a yaw (degrees) in the character
        // frame; NaN when the clip has no spine or it stands straight up.
        public float SpineYaw(int frame)
        {
            Vector3 spine = Vector3.zero;
            foreach (var track in Tracks)
            {
                if (track.Child == null) continue;
                if (track.Bone == BoneNames.Pelvis || track.Bone.StartsWith(BoneNames.Prefix + "Spine", StringComparison.Ordinal))
                    spine += track.Delta[Mathf.Clamp(frame, 0, Frames - 1)] * track.Direction;
            }
            spine.y = 0f;
            return spine.sqrMagnitude > 1e-6f ? Mathf.Atan2(spine.x, spine.z) * Mathf.Rad2Deg : float.NaN;
        }

        //====================[ Loading ]====================
        private static BakedClip Load(string resource)
        {
            var doc = EmbeddedData.ReadJson(resource, Schema);
            var clip = new BakedClip
            {
                // Clips baked before the reference was recorded were baked against their own first frame.
                Reference = (string)doc["reference"] ?? (string)doc["source"],
                Fps = (float)doc["fps"],
                Frames = (int)doc["frames"],
                HipsHeight = (float)doc["hipsHeight"]
            };
            if (clip.Fps <= 0f || clip.Frames < 2 || clip.HipsHeight <= 0f) throw new InvalidDataException("bad timing");
            clip.FloorHeight = doc["floorHeight"] != null ? (float)doc["floorHeight"] : clip.HipsHeight;

            var pelvis = (JArray)doc["pelvis"];
            if (pelvis.Count != clip.Frames) throw new InvalidDataException("pelvis track length");
            clip.Pelvis = new Vector3[clip.Frames];
            for (int i = 0; i < clip.Frames; i++)
                clip.Pelvis[i] = new Vector3((float)pelvis[i][0], (float)pelvis[i][1], (float)pelvis[i][2]);

            var tracks = new List<Track>();
            foreach (JObject entry in (JArray)doc["bones"])
            {
                var delta = (JArray)entry["delta"];
                if (delta.Count != clip.Frames) throw new InvalidDataException("track length " + entry["bone"]);
                var track = new Track { Bone = (string)entry["bone"], Delta = new Quaternion[clip.Frames], Child = (string)entry["child"] };
                for (int i = 0; i < clip.Frames; i++)
                    track.Delta[i] = EmbeddedData.ReadQuaternion(delta[i]);
                if (entry["direction"] is JArray d) track.Direction = new Vector3((float)d[0], (float)d[1], (float)d[2]).normalized;
                else track.Child = null;
                tracks.Add(track);
            }
            clip.Tracks = tracks.ToArray();
            return clip;
        }
    }
}
