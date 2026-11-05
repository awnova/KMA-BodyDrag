//====================[ Imports ]====================
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KMABodyDrag.Animation.Hands
{
    //====================[ HoldGrip ]====================
    // The dragger's fingers closed round the dragged body's shoulders (KeepMeAlive's Build/Tools/DragPose/bake_hold_grip.py from
    // Build/Assets/Downed/Drag_Hold_Pose.blend, the fingers fitted against the body's mesh by fit_hold_grip.py): for each hand its finger
    // bones (parents first) and each one's rotation relative to the hand's knuckle frame, from its rest pose, the encoding HandFingers
    // puts onto the game's own skeleton. A hand on the body holds this pose (DraggerArms).
    internal sealed class HoldGrip
    {
        private const string Resource = "KMABodyDrag.Resources.drag_hold_grip.json";
        private const string Schema = "keepmealive.holdgrip.v1";

        public sealed class Hand
        {
            public string[] Bones;
            public Quaternion[] Pose;
        }

        public Hand Left, Right;

        public static HoldGrip Instance => EmbeddedData.Get(Resource, nameof(HoldGrip), Load);

        public Hand For(bool left) => left ? Left : Right;

        private static HoldGrip Load()
        {
            var doc = EmbeddedData.ReadJson(Resource, Schema);
            return new HoldGrip { Left = ReadHand((JObject)doc["left"]), Right = ReadHand((JObject)doc["right"]) };
        }

        private static Hand ReadHand(JObject hand)
        {
            var bones = ((JArray)hand["bones"]).ToObject<string[]>();
            var pose = (JArray)hand["pose"];
            if (bones.Length == 0 || pose.Count != bones.Length) throw new InvalidDataException("finger track length");
            var rotations = new Quaternion[bones.Length];
            for (int i = 0; i < rotations.Length; i++)
            {
                rotations[i] = EmbeddedData.ReadQuaternion(pose[i]);
            }
            return new Hand { Bones = bones, Pose = rotations };
        }
    }
}
