using System;
using EscapeRoom.Networking;
using UnityEngine;

namespace EscapeRoom.Player
{
    /// <summary>
    /// Procedural animation for the humanoid CharacterToon (the asset pack has no walk/idle clips).
    /// Runs on every client from synced state (speed, pitch, raised hands):
    ///  - idle: arms relaxed at the sides
    ///  - walking: legs and arms swing with speed, small body bob, footstep events
    ///  - head nods with the player's look pitch
    ///  - raised hand: arm straightens and points exactly at what the player is looking at,
    ///    with the index finger out
    /// The local player's head is collapsed so the first-person camera sees their own arms/body
    /// but not the inside of their head.
    /// </summary>
    public class CharacterPose : MonoBehaviour
    {
        [SerializeField] Animator model;
        [SerializeField] PlayerController controller;

        [Header("Idle")]
        [Range(-1f, 1f)] public float armDown = -0.62f;
        [Range(-1f, 1f)] public float armForward = 0.15f;
        [Range(-1f, 1f)] public float elbowStretch = 0.75f;

        [Header("Walk")]
        public float strideLength = 1.3f;
        [Range(0f, 1f)] public float legSwing = 0.4f;
        [Range(0f, 1f)] public float armSwing = 0.3f;
        [Range(0f, 1f)] public float kneeBend = 0.5f;
        public float bobHeight = 0.02f;

        [Header("Pointing")]
        public float raiseSpeed = 8f;
        public float pointDistance = 10f;

        /// <summary>Raised on each footstep (foot position). Used for the Blind player's sound pulses.</summary>
        public event Action<Vector3> Footstep;

        public Transform Head => head;

        HumanPoseHandler poseHandler;
        HumanPose pose;
        Vector3 restBodyPosition;
        Quaternion restBodyRotation;
        float[] restMuscles;
        float phase;
        float leftWeight, rightWeight;
        int lastStepSign;
        bool headHidden;
        Vector3 modelBaseLocalPosition;
        float restAnkleHeight;

        Transform head, leftUpper, leftLower, leftHand, rightUpper, rightLower, rightHand, leftFoot, rightFoot;

        // Muscle indices, resolved by name so they survive Unity version changes.
        int mSpine, mChest, mNeckNod, mHeadNod;
        int mLUpLeg, mRUpLeg, mLLowLeg, mRLowLeg;
        int mLArmDU, mRArmDU, mLArmFB, mRArmFB, mLFore, mRFore, mLShoulder, mRShoulder;
        int[] mLFingers, mRFingers, mLIndex, mRIndex;

        void Awake()
        {
            if (model == null) model = GetComponentInChildren<Animator>();
            if (controller == null) controller = GetComponentInParent<PlayerController>();

            // We drive the skeleton ourselves; the Animator must not reset it every frame.
            model.enabled = false;

            poseHandler = new HumanPoseHandler(model.avatar, model.transform);
            poseHandler.GetHumanPose(ref pose);
            restBodyPosition = pose.bodyPosition;
            restBodyRotation = pose.bodyRotation;
            restMuscles = (float[])pose.muscles.Clone();
            for (int i = 0; i < restMuscles.Length; i++) restMuscles[i] = 0f;

            head = model.GetBoneTransform(HumanBodyBones.Head);
            leftUpper = model.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            leftLower = model.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            leftHand = model.GetBoneTransform(HumanBodyBones.LeftHand);
            rightUpper = model.GetBoneTransform(HumanBodyBones.RightUpperArm);
            rightLower = model.GetBoneTransform(HumanBodyBones.RightLowerArm);
            rightHand = model.GetBoneTransform(HumanBodyBones.RightHand);
            leftFoot = model.GetBoneTransform(HumanBodyBones.LeftFoot);
            rightFoot = model.GetBoneTransform(HumanBodyBones.RightFoot);

            // Ground reference: how high the ankles sit above the feet's soles in the bind pose.
            modelBaseLocalPosition = model.transform.localPosition;
            restAnkleHeight = Mathf.Min(leftFoot.position.y, rightFoot.position.y) - model.transform.position.y;

            mSpine = M("Spine Front-Back"); mChest = M("Chest Front-Back");
            mNeckNod = M("Neck Nod Down-Up"); mHeadNod = M("Head Nod Down-Up");
            mLUpLeg = M("Left Upper Leg Front-Back"); mRUpLeg = M("Right Upper Leg Front-Back");
            mLLowLeg = M("Left Lower Leg Stretch"); mRLowLeg = M("Right Lower Leg Stretch");
            mLArmDU = M("Left Arm Down-Up"); mRArmDU = M("Right Arm Down-Up");
            mLArmFB = M("Left Arm Front-Back"); mRArmFB = M("Right Arm Front-Back");
            mLFore = M("Left Forearm Stretch"); mRFore = M("Right Forearm Stretch");
            mLShoulder = M("Left Shoulder Down-Up"); mRShoulder = M("Right Shoulder Down-Up");
            mLFingers = Fingers("Left", false); mRFingers = Fingers("Right", false);
            mLIndex = Fingers("Left", true); mRIndex = Fingers("Right", true);
        }

        static int M(string name) => Array.IndexOf(HumanTrait.MuscleName, name);

        static int[] Fingers(string side, bool indexOnly)
        {
            var list = new System.Collections.Generic.List<int>();
            foreach (var f in indexOnly ? new[] { "Index" } : new[] { "Thumb", "Middle", "Ring", "Little" })
                for (int j = 1; j <= 3; j++)
                {
                    int i = M($"{side} {f} {j} Stretched");
                    if (i >= 0) list.Add(i);
                }
            return list.ToArray();
        }

        void LateUpdate()
        {
            if (poseHandler == null) return;

            float dt = Time.deltaTime;
            float speed = controller != null ? controller.GroundSpeed : 0f;
            float walk = controller != null ? Mathf.Clamp01(speed / Mathf.Max(0.1f, controller.walkSpeed)) : 0f;
            if (speed < 0.05f) walk = 0f;

            // Gait phase: one full cycle (two steps) per stride.
            phase += speed / Mathf.Max(0.1f, strideLength) * Mathf.PI * dt;
            if (phase > Mathf.PI * 2f) phase -= Mathf.PI * 2f;
            float s = Mathf.Sin(phase);

            // Footsteps happen when the swing changes direction.
            int sign = s >= 0f ? 1 : -1;
            if (walk > 0.1f && sign != lastStepSign)
                Footstep?.Invoke((sign > 0 ? leftFoot : rightFoot).position);
            lastStepSign = sign;

            bool lRaise = controller != null && controller.LeftHandRaised;
            bool rRaise = controller != null && controller.RightHandRaised;
            leftWeight = Mathf.MoveTowards(leftWeight, lRaise ? 1f : 0f, raiseSpeed * dt);
            rightWeight = Mathf.MoveTowards(rightWeight, rRaise ? 1f : 0f, raiseSpeed * dt);

            Array.Copy(restMuscles, pose.muscles, restMuscles.Length);
            var m = pose.muscles;

            // Legs
            Set(m, mLUpLeg, s * legSwing * walk);
            Set(m, mRUpLeg, -s * legSwing * walk);
            Set(m, mLLowLeg, 1f - kneeBend * walk * Mathf.Max(0f, -s));
            Set(m, mRLowLeg, 1f - kneeBend * walk * Mathf.Max(0f, s));

            // Arms (idle + swing), blended towards a forward reach when raising
            Set(m, mLArmDU, Mathf.Lerp(armDown, 0.3f, leftWeight));
            Set(m, mRArmDU, Mathf.Lerp(armDown, 0.3f, rightWeight));
            Set(m, mLArmFB, Mathf.Lerp(armForward - s * armSwing * walk, 1f, leftWeight));
            Set(m, mRArmFB, Mathf.Lerp(armForward + s * armSwing * walk, 1f, rightWeight));
            Set(m, mLFore, Mathf.Lerp(elbowStretch, 1f, leftWeight));
            Set(m, mRFore, Mathf.Lerp(elbowStretch, 1f, rightWeight));
            Set(m, mLShoulder, 0.3f * leftWeight);
            Set(m, mRShoulder, 0.3f * rightWeight);

            // Pointing hand: index out, other fingers curled. Relaxed hand otherwise.
            foreach (int i in mLFingers) Set(m, i, Mathf.Lerp(0.2f, -1f, leftWeight));
            foreach (int i in mRFingers) Set(m, i, Mathf.Lerp(0.2f, -1f, rightWeight));
            foreach (int i in mLIndex) Set(m, i, Mathf.Lerp(0.2f, 1f, leftWeight));
            foreach (int i in mRIndex) Set(m, i, Mathf.Lerp(0.2f, 1f, rightWeight));

            // Look pitch -> neck/head nod (positive pitch = looking down)
            float pitch = controller != null ? controller.CurrentPitch : 0f;
            float nod = Mathf.Clamp(-pitch / 70f, -1f, 1f);
            Set(m, mNeckNod, nod * 0.5f);
            Set(m, mHeadNod, nod * 0.8f);
            Set(m, mSpine, 0.05f * walk);
            Set(m, mChest, -0.1f * nod);

            pose.bodyPosition = restBodyPosition + Vector3.up * (bobHeight / Mathf.Max(0.0001f, model.transform.lossyScale.y) * walk * Mathf.Abs(Mathf.Cos(phase)));
            pose.bodyRotation = restBodyRotation;
            model.transform.localPosition = modelBaseLocalPosition;
            poseHandler.SetHumanPose(ref pose);
            GroundFeet();

            // Exact aim for raised arms: point at what the player is looking at.
            if (controller != null && (leftWeight > 0f || rightWeight > 0f))
            {
                Vector3 eye = head != null ? head.position : transform.position + Vector3.up * 1.5f;
                Vector3 target = eye + controller.LookDirection * pointDistance;
                if (leftWeight > 0f) AimArm(leftUpper, leftLower, leftHand, target, leftWeight);
                if (rightWeight > 0f) AimArm(rightUpper, rightLower, rightHand, target, rightWeight);
            }

            if (head != null && headHidden) head.localScale = Vector3.one * 0.0001f;
        }

        // The muscle pose doesn't keep the feet where the bind pose had them, so shift the
        // whole model until the lowest (standing) foot is back on the ground.
        void GroundFeet()
        {
            if (leftFoot == null || rightFoot == null) return;
            float groundY = model.transform.position.y;
            float lowest = Mathf.Min(leftFoot.position.y, rightFoot.position.y) - groundY;
            float delta = lowest - restAnkleHeight;
            model.transform.position -= Vector3.up * delta;
        }

        static void Set(float[] m, int i, float v)
        {
            if (i >= 0) m[i] = Mathf.Clamp(v, -1f, 1f);
        }

        static void AimArm(Transform upper, Transform lower, Transform hand, Vector3 target, float w)
        {
            if (upper == null || lower == null || hand == null) return;
            Vector3 aim = (target - upper.position).normalized;
            Vector3 cur = (hand.position - upper.position).normalized;
            upper.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(cur, aim), w) * upper.rotation;
            // Straighten the forearm along the same line.
            Vector3 curLower = (hand.position - lower.position).normalized;
            lower.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(curLower, aim), w) * lower.rotation;
        }

        /// <summary>First person: collapse the head so the camera doesn't see the inside of it.</summary>
        public void SetHeadHidden(bool hidden)
        {
            headHidden = hidden;
            if (head != null) head.localScale = hidden ? Vector3.one * 0.0001f : Vector3.one;
        }
    }
}
