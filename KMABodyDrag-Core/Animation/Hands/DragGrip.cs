//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KMABodyDrag.Animation.Hands
{
    //====================[ DragGrip ]====================
    // Where a downed body is held when it is dragged: its two shoulders and its own directions there, as points in the
    // frame of its upper chest bone (Base HumanSpine3, the RibcageUp collider's bone).
    //
    // From the game's player prefab (PlayerSuperior): the ragdoll's bodies are Spine3 and each upper arm, joined by a
    // CharacterJoint on the upper arm whose anchor is its own origin and whose connected anchor is a fixed point on
    // Spine3 ((-0.139, 0.020, +-0.163) in the prefab). The collarbones in between have no body, so as a ragdoll each
    // shoulder is held at that point, whatever the collarbones were doing: it is the shoulder you see, and the same
    // point every time. A body that is not a ragdoll (the revivee's own) is held by its shoulder bones as they are.
    //
    // Spine3's axes (prefab): its children lie along -x (up the spine) and its front colliders on +y (NeckForward
    // +y, NeckBackward -y), which the front worked out here agrees with.
    // Offsets are in metres along the chest bone's rotation (scale-free): world = chest.position + chest.rotation * o.
    internal sealed class DragGrip
    {
        // How far up the spine from each shoulder joint the body is held and swings from (m): the top of the shoulder, 8.2 cm above the
        // joint on a standing skeleton (measured on the USEC shirt mesh: the highest the shoulder rises over the joint). The dragger's
        // palms go to these points and the hands' joints hold them, so the body hangs from the very spot the hands are on.
        public const float SwingHeight = 0.082f;
        // The swing point is that far from the joint turned this many degrees about the shoulder line toward the chest's front (the
        // side the dragger's hands come from), so the palms sit forward of straight above the shoulders.
        public const float SwingTilt = 0f;

        // The shoulders (where each upper arm hangs).
        public Vector3 Left;
        public Vector3 Right;
        // The points the hands hold: each shoulder SwingHeight up the spine, turned SwingTilt toward the chest's front.
        public Vector3 SwingLeft;
        public Vector3 SwingRight;
        // Unit directions: up the spine and out of the chest.
        public Vector3 Spine;
        public Vector3 Front;
        // Unit direction from the right shoulder to the left.
        public Vector3 Across;
        // The hold's own frame on the chest: centred between the swing points, x toward the left shoulder, z up the
        // spine, y out of the chest. A body lying on its back with its head toward the dragger has this frame
        // facing the dragger square (DragSession.HoldRotation).
        public Vector3 HoldPosition;
        public Quaternion HoldRotation;
        public float HalfWidth;

        //====================[ Public API ]====================
        // The player's grip as their body is set up now. Null when the skeleton is not there.
        public static DragGrip For(Player player)
        {
            if (player == null || player.PlayerBones == null) return null;
            Transform chest = Chest(player);
            Transform leftArm = Part(player, EBodyPartColliderType.LeftUpperArm);
            Transform rightArm = Part(player, EBodyPartColliderType.RightUpperArm);
            if (chest == null || leftArm == null || rightArm == null) return null;

            Vector3 left = Shoulder(chest, leftArm);
            Vector3 right = Shoulder(chest, rightArm);
            Vector3 across = left - right;
            if (across.sqrMagnitude < 1e-6f) return null;

            // Up the spine: the chest bone's -x, square to the shoulders.
            Vector3 x = across.normalized;
            Vector3 spine = Vector3.ProjectOnPlane(Vector3.left, x).normalized;
            // Right x up-the-spine faces forward, as for a person standing facing +z.
            Vector3 front = Vector3.Cross(-across, spine).normalized;

            // Up the spine, turned toward the chest's front.
            float tilt = SwingTilt * Mathf.Deg2Rad;
            Vector3 swing = (spine * Mathf.Cos(tilt) + front * Mathf.Sin(tilt)) * SwingHeight;
            Vector3 swingLeft = left + swing;
            Vector3 swingRight = right + swing;
            return new DragGrip
            {
                Left = left,
                Right = right,
                SwingLeft = swingLeft,
                SwingRight = swingRight,
                Spine = spine,
                Front = front,
                Across = x,
                HoldPosition = (swingLeft + swingRight) * 0.5f,
                HoldRotation = Quaternion.LookRotation(spine, Vector3.Cross(spine, x)),
                HalfWidth = (swingLeft - swingRight).magnitude * 0.5f
            };
        }

        // The upper chest bone, the frame the grip is in. It carries the RibcageUp hit collider (and, as a ragdoll,
        // the chest rigidbody).
        public static Transform Chest(Player player) => Part(player, EBodyPartColliderType.RibcageUp);

        public static Vector3 World(Transform chest, Vector3 offset) => chest.position + chest.rotation * offset;

        // Whether the chest (its bone given) faces up rather than down, from this grip's front: the side the hands
        // hold it from, and whether a pickup turns it over. Any tilt counts; IsOnBack is the same split for a whole body, and IsFaceUp the stricter "lying on its back".
        public bool ChestFacesUp(Transform chest) => Vector3.Dot(chest.rotation * Front, Vector3.up) >= 0f;

        // The way a body faces from its shoulders and pelvis alone: front (out of the chest) and spine (pelvis to
        // shoulders), both unit. Right x up-the-spine faces forward, as for a person standing facing +z. False while
        // the body is too bunched up to tell.
        public static bool TryFront(Vector3 left, Vector3 right, Vector3 pelvis, out Vector3 front, out Vector3 spine)
        {
            front = Vector3.zero;
            spine = (left + right) * 0.5f - pelvis;
            if (spine.sqrMagnitude < 1e-4f) return false;
            spine.Normalize();

            front = Vector3.Cross(right - left, spine);
            if (front.sqrMagnitude < 1e-6f) return false;
            front.Normalize();
            return true;
        }

        // Whether the body lies on its back (its chest facing up), as the breathing loop has it: the chest's front at
        // least FaceUpMin up (a body on its side is not).
        private const float FaceUpMin = 0.3f;

        public static bool IsFaceUp(Player player)
        {
            Transform left = Part(player, EBodyPartColliderType.LeftUpperArm);
            Transform right = Part(player, EBodyPartColliderType.RightUpperArm);
            Transform hips = Part(player, EBodyPartColliderType.Pelvis);
            if (left == null || right == null || hips == null) return false;
            return TryFront(left.position, right.position, hips.position, out Vector3 front, out _)
                   && Vector3.Dot(front, Vector3.up) > FaceUpMin;
        }

        // How far the chest is rolled from facing straight up (degrees: 0 on the back, 90 on the side, 180 on the front), from
        // the shoulders and pelvis. False while the body is too bunched up to tell.
        public static bool TryBackTilt(Player player, out float degrees)
        {
            degrees = 0f;
            Transform left = Part(player, EBodyPartColliderType.LeftUpperArm);
            Transform right = Part(player, EBodyPartColliderType.RightUpperArm);
            Transform hips = Part(player, EBodyPartColliderType.Pelvis);
            if (left == null || right == null || hips == null) return false;
            if (!TryFront(left.position, right.position, hips.position, out Vector3 front, out _)) return false;
            degrees = Mathf.Acos(Mathf.Clamp(Vector3.Dot(front, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg;
            return true;
        }

        // Whether the chest is rolled more toward the back than toward the front: a body to settle into the breathing pose,
        // as opposed to one to hand to EFT's prone. Exactly on its side counts as the back.
        public static bool IsOnBack(Player player) => TryBackTilt(player, out float degrees) && degrees <= 90f;

        // The same, or throws when the player has none.
        public static Transform RequirePart(Player player, EBodyPartColliderType part) =>
            Part(player, part) ?? throw new System.InvalidOperationException($"{player?.ProfileId} has no {part} bone");

        // The bone carrying a body part's hit collider, or null.
        public static Transform Part(Player player, EBodyPartColliderType part) =>
            player?.PlayerBones?.BodyPartCollidersDictionary != null
            && player.PlayerBones.BodyPartCollidersDictionary.TryGetValue(part, out var collider) && collider != null && collider.Collider != null
                ? collider.Collider.transform
                : null;

        //====================[ Build ]====================
        // A shoulder in the chest's frame: where the upper arm's joint holds it on the chest, or the bone itself.
        private static Vector3 Shoulder(Transform chest, Transform upperArm)
        {
            Vector3 world = upperArm.position;
            var joint = upperArm.GetComponent<CharacterJoint>();
            bool fromJoint = joint != null && joint.connectedBody != null && joint.connectedBody.transform == chest;
            // The joint's anchor on the arm is the arm's own origin; its connected anchor is where that is held.
            if (fromJoint) world = chest.TransformPoint(joint.connectedAnchor) + (upperArm.position - upperArm.TransformPoint(joint.anchor));
            return Quaternion.Inverse(chest.rotation) * (world - chest.position);
        }
    }
}
