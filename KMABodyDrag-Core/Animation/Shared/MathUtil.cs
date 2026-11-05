//====================[ Imports ]====================
using UnityEngine;

namespace KMABodyDrag.Animation.Shared
{
    //====================[ MathUtil ]====================
    // The small pieces of math the body, drag and hands code all share: angles, headings on the level, yaw, smoothing
    // and swing-twist.
    internal static class MathUtil
    {
        //====================[ Angles ]====================
        // The turn as an angle in degrees (-180..180, signed about the returned axis) rather than Unity's 0..360.
        public static float ToSignedAngleAxis(this Quaternion q, out Vector3 axis)
        {
            q.ToAngleAxis(out float angle, out axis);
            return angle > 180f ? angle - 360f : angle;
        }

        public static bool IsFinite(this Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        //====================[ Heading ]====================
        // A direction laid flat and made unit; false when it points (almost) straight up or down.
        public static bool TryLevel(Vector3 direction, out Vector3 level)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude > 1e-6f)
            {
                level = direction.normalized;
                return true;
            }
            level = Vector3.zero;
            return false;
        }

        // A forward direction on the level (unit), or fallback when it points straight up or down.
        public static Vector3 Level(Vector3 forward, Vector3 fallback) =>
            TryLevel(forward, out Vector3 level) ? level : fallback;

        // A forward direction as a yaw-only rotation, or identity when it points straight up or down.
        public static Quaternion Heading(Vector3 forward) =>
            TryLevel(forward, out Vector3 level) ? Quaternion.LookRotation(level) : Quaternion.identity;

        // The turn by a yaw (degrees) about the world up.
        public static Quaternion Yaw(float degrees) => Quaternion.Euler(0f, degrees, 0f);

        // The level direction a yaw (degrees) points in.
        public static Vector3 YawForward(float degrees) => Yaw(degrees) * Vector3.forward;

        // The yaw (degrees) of a direction; its height is ignored.
        public static float YawOf(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        //====================[ Smoothing ]====================
        // Share of the way to close this frame when following at a rate (1/s), frame-rate independent.
        public static float Follow(float rate) => 1f - Mathf.Exp(-rate * Time.deltaTime);

        // The same for a time constant (s).
        public static float FollowOver(float seconds) => Follow(1f / seconds);

        // 0..1 along a human point-to-point movement at time fraction x (0..1, clamped): the minimum-jerk profile, which starts
        // and stops at rest with the bell-shaped speed of a real reach (Flash & Hogan 1985).
        public static float MinJerk(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * x * (10f + x * (6f * x - 15f));
        }

        //====================[ Sampling ]====================
        // The two keys a fractional position along `count` baked keys lies between (clamped to the ends) and how far past the
        // first it is (0..1).
        public static float SplitKey(float position, int count, out int a, out int b)
        {
            position = Mathf.Clamp(position, 0f, count - 1);
            a = Mathf.Min(Mathf.FloorToInt(position), count - 1);
            b = Mathf.Min(a + 1, count - 1);
            return position - a;
        }

        //====================[ Swing-Twist ]====================
        // The part of q that turns about axis (swing-twist decomposition).
        public static Quaternion TwistAbout(Quaternion q, Vector3 axis)
        {
            Vector3 along = Vector3.Project(new Vector3(q.x, q.y, q.z), axis);
            float length = Mathf.Sqrt(along.sqrMagnitude + q.w * q.w);
            if (length < 1e-6f) return Quaternion.identity;
            return new Quaternion(along.x / length, along.y / length, along.z / length, q.w / length);
        }

        // The signed angle (degrees, -180..180) q turns about axis.
        public static float SignedTwist(Quaternion q, Vector3 axis)
        {
            float angle = TwistAbout(q, axis).ToSignedAngleAxis(out Vector3 twistAxis);
            return Vector3.Dot(twistAxis, axis) < 0f ? -angle : angle;
        }
    }
}
