//====================[ Imports ]====================
using EFT;
using Fika.Core.Networking.LiteNetLib.Utils;
using JsonType;
using UnityEngine;

namespace KMABodyDrag.Networking
{
    //====================[ Drag Packets ]====================
    // Dragger -> everyone: a claim on a corpse (active) or a release. A release names whoever is to say where the body comes to
    // rest (declarerId; empty when nobody will, e.g. before the lift). The host settles claims that crossed (CorpsePacketHandlers).
    public struct CorpseDragStatePacket : INetSerializable
    {
        public string corpseId;
        public string draggerId;
        public bool active;
        public string declarerId;

        public void Deserialize(NetDataReader reader)
        {
            corpseId = reader.GetString();
            draggerId = reader.GetString();
            active = reader.GetBool();
            declarerId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(corpseId ?? "");
            writer.Put(draggerId ?? "");
            writer.Put(active);
            writer.Put(declarerId ?? "");
        }
    }

    // Dragger -> everyone, several times a second: which way their body faces (Fika sends only the look). Only the latest matters.
    public struct CorpseDragHeadingPacket : INetSerializable
    {
        public string draggerId;
        public float yaw;

        public void Deserialize(NetDataReader reader)
        {
            draggerId = reader.GetString();
            yaw = reader.GetFloat();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(draggerId ?? "");
            writer.Put(yaw);
        }
    }

    // Declarer -> everyone, once the body it let go of has settled on its own screen: the corpse root and every ragdoll bone's world
    // pose (Corpse.GetTransformSync order), so every peer ends on the same body. From the same sender as the release, so it always
    // comes after it. The count is sent: Fika's own corpse packet assumes 12 bones, a simplified (zombie) skeleton may have others.
    public struct CorpseRestPacket : INetSerializable
    {
        public string corpseId;
        public string declarerId;
        public Vector3 root;
        public TransformSync[] bones;

        // More than any skeleton has: a corrupt count must not allocate without bound.
        private const int MaxBones = 64;

        public void Deserialize(NetDataReader reader)
        {
            corpseId = reader.GetString();
            declarerId = reader.GetString();
            root = ReadVector(reader);
            int count = Mathf.Clamp(reader.GetInt(), 0, MaxBones);
            bones = new TransformSync[count];
            for (int i = 0; i < count; i++)
            {
                bones[i] = new TransformSync
                {
                    Position = ReadVector(reader),
                    Rotation = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat())
                };
            }
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(corpseId ?? "");
            writer.Put(declarerId ?? "");
            WriteVector(writer, root);
            int count = bones?.Length ?? 0;
            writer.Put(count);
            for (int i = 0; i < count; i++)
            {
                WriteVector(writer, bones[i].Position);
                Quaternion q = bones[i].Rotation;
                writer.Put(q.x);
                writer.Put(q.y);
                writer.Put(q.z);
                writer.Put(q.w);
            }
        }

        private static Vector3 ReadVector(NetDataReader reader) => new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());

        private static void WriteVector(NetDataWriter writer, Vector3 v)
        {
            writer.Put(v.x);
            writer.Put(v.y);
            writer.Put(v.z);
        }
    }
}
