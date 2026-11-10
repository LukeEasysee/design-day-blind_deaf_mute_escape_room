using EscapeRoom.Networking;
using UnityEngine;

namespace EscapeRoom.Senses
{
    /// <summary>
    /// "Blind vision" sound pulses: expanding rings where noise happens (voices, footsteps,
    /// SoundEmitters such as the goal hum). Only the Blind player's camera renders the
    /// BlindVision layer, and pulses are only emitted while the local player is Blind.
    /// Rings ignore walls (the Blind camera renders nothing else), which gives a sense of direction.
    /// </summary>
    public class SoundVisualizer : MonoBehaviour
    {
        public const string LayerName = "BlindVision";

        [SerializeField] Material pulseMaterial;
        public Color color = new(0.55f, 0.85f, 1f, 1f);
        public float baseSize = 1.4f;
        public float lifetime = 1.3f;
        public float maxDistance = 40f;
        [Tooltip("Pulses closer than this are dimmed so your own surroundings don't flood the screen.")]
        public float nearFadeDistance = 0.8f;
        [Tooltip("Extra size per metre of distance, so far-away sounds stay readable (direction cue).")]
        public float growPerMetre = 0.12f;

        static SoundVisualizer instance;
        ParticleSystem pulses;

        public static int Layer => LayerMask.NameToLayer(LayerName);
        public static bool Active => instance != null && GameFlow.IsPlaying && RoleEffects.CurrentRole == PlayerRole.Blind;

        void Awake()
        {
            instance = this;
            CreateParticleSystem();
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        void CreateParticleSystem()
        {
            var go = new GameObject("SoundPulses");
            go.transform.SetParent(transform, false);
            int layer = Layer;
            if (layer >= 0) go.layer = layer;
            else Debug.LogWarning($"[SoundVisualizer] Layer '{LayerName}' missing. Run Escape Room > Setup Player.");

            pulses = go.AddComponent<ParticleSystem>();
            pulses.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = pulses.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.startLifetime = lifetime;
            main.maxParticles = 400;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var emission = pulses.emission;
            emission.enabled = false;
            var shape = pulses.shape;
            shape.enabled = false;

            var size = pulses.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.25f, 1f, 1f));

            var col = pulses.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.08f), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = pulseMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.maxParticleSize = 5f;

            pulses.Play();
        }

        /// <summary>Show a sound at a world position. Strength ~0.3 (footstep) to ~1.5 (loud).</summary>
        public static void Pulse(Vector3 position, float strength)
        {
            if (!Active || instance.pulses == null) return;
            instance.Emit(position, strength);
        }

        void Emit(Vector3 position, float strength)
        {
            var cam = NetworkPlayer.Local != null ? NetworkPlayer.Local.PlayerCamera : null;
            float alpha = 1f, distance = 0f;
            if (cam != null)
            {
                distance = Vector3.Distance(cam.transform.position, position);
                if (distance > maxDistance) return;
                alpha = Mathf.Clamp01(distance / Mathf.Max(0.01f, nearFadeDistance));
            }

            var c = color;
            c.a *= Mathf.Clamp01(0.6f + 0.4f * strength) * alpha;
            pulses.Emit(new ParticleSystem.EmitParams
            {
                position = position,
                startSize = baseSize * Mathf.Clamp(strength, 0.1f, 2.5f) * (1f + distance * growPerMetre),
                startLifetime = lifetime,
                startColor = c,
                applyShapeToPosition = false,
            }, 1);
        }
    }
}
