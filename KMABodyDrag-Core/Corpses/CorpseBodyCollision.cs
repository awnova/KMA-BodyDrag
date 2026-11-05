//====================[ Imports ]====================
using System.Collections.Generic;
using UnityEngine;

namespace KMABodyDrag.Corpses
{
    //====================[ CorpseBody: Collision ]====================
    // Limbs snagged on the world (KeepMeAlive's BodyRagdollCollision). A corpse's contacts are its own colliders: a snagged limb's
    // colliders, and its spacer (CorpseBodyRig), pass through the world for a moment, and go back to the Deadbody layer once clear.
    internal static partial class CorpseBody
    {
        //====================[ Tuning ]====================
        // A limb pulled further than this from its joint (m) is snagged on geometry and is put back on it.
        private const float MaxJointStretch = 0.14f;
        // A snagged limb passes through the world for at least this long (s), and at most this long from the first snag even if it
        // is still inside something.
        private const float SnagReleaseMin = 0.3f;
        private const float SnagReleaseMax = 2f;
        // Penetration deeper than this counts as still inside geometry (resting contact is a few mm).
        private const float SnagOverlapTolerance = 0.02f;
        // Unity's built-in "Ignore Raycast" layer; in the project's collision matrix it collides with nothing, so a collider moved
        // onto it passes through the world while keeping its shape and bounds.
        private const int ReleasedLayer = 2;

        private static readonly List<Collider> _restored = new List<Collider>();
        private static readonly Collider[] _overlaps = new Collider[16];
        private static int _worldMask = -1;

        //====================[ Snags ]====================
        // Repositions stretched limbs and briefly releases their colliders.
        private static void FixSnags(Session session)
        {
            bool moved = false;

            foreach (var (joint, limb) in session.Joints)
            {
                var parent = joint.connectedBody;
                if (parent == null || limb == null) continue;

                Vector3 limbAnchor = limb.transform.TransformPoint(joint.anchor);
                Vector3 parentAnchor = parent.transform.TransformPoint(joint.connectedAnchor);
                Vector3 gap = parentAnchor - limbAnchor;
                if (gap.sqrMagnitude < MaxJointStretch * MaxJointStretch) continue;

                // Through the transform, so the bones below follow in the same move.
                limb.transform.position += gap;
                limb.velocity = parent.velocity;
                limb.angularVelocity = parent.angularVelocity;
                ReleaseFromWorld(session, limb.transform);
                moved = true;
            }

            // The game drives physics itself with transform auto-sync off; without this the next step would still see the stretched
            // pose and write it back.
            if (moved) PhysicsExtensions.SyncTransforms();
        }

        // Moves the colliders of a limb and everything below it onto a layer that collides with nothing. They stay enabled (their
        // bounds and shape stay valid for the overlap test that brings them back).
        private static void ReleaseFromWorld(Session session, Transform limb)
        {
            float until = Time.time + SnagReleaseMin;
            foreach (var collider in session.ColliderWas.Keys)
            {
                if (collider == null || !collider.transform.IsChildOf(limb)) continue;

                collider.gameObject.layer = ReleasedLayer;
                // A limb that snags again restarts its minimum but keeps its first release time, so the hard maximum still counts
                // from the original snag.
                float since = session.Released.TryGetValue(collider, out var existing) ? existing.Since : Time.time;
                session.Released[collider] = new Release { Since = since, Until = until };
            }
        }

        // Brings released colliders back once they are no longer inside anything they would collide with.
        private static void UpdateReleased(Session session)
        {
            if (session.Released.Count == 0) return;

            _restored.Clear();
            foreach (var pair in session.Released)
            {
                var collider = pair.Key;
                if (collider == null)
                {
                    _restored.Add(collider);
                    continue;
                }
                if (Time.time < pair.Value.Until) continue;

                bool overdue = Time.time >= pair.Value.Since + SnagReleaseMax;
                if (overdue || !IsInsideWorld(collider))
                {
                    RestoreLayer(session, collider);
                    _restored.Add(collider);
                }
            }
            foreach (var collider in _restored) session.Released.Remove(collider);
        }

        private static void RestoreLayer(Session session, Collider collider)
        {
            if (collider != null && session.ColliderWas.TryGetValue(collider, out var was)) collider.gameObject.layer = was.Layer;
        }

        // Inside the static world (what a Deadbody collider collides with, less the other bodies).
        private static bool IsInsideWorld(Collider collider)
        {
            if (_worldMask < 0) _worldMask = LayerMask.GetMask("Terrain", "HighPolyCollider", "TransparentCollider");
            Bounds bounds = collider.bounds;
            int count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, _overlaps, Quaternion.identity,
                _worldMask, QueryTriggerInteraction.Ignore);

            Transform t = collider.transform;
            for (int i = 0; i < count; i++)
            {
                var other = _overlaps[i];
                if (Physics.ComputePenetration(collider, t.position, t.rotation,
                        other, other.transform.position, other.transform.rotation, out _, out float depth)
                    && depth > SnagOverlapTolerance)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
