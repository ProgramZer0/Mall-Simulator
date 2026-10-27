using UnityEngine;

namespace ProgramZer0.SoundSystem
{
    /// <summary>
    /// Ongoing 3D sound for something that moves: footsteps on a player, wheels on a cart.
    ///
    /// Drop it on the player prefab (Footsteps) or a cart prefab (Loop). It works out speed
    /// from how far the object moved each frame, which every client already knows because
    /// positions are synced by NetworkTransform. So each machine plays these sounds for every
    /// player/cart on its own - no RPCs, nothing extra sent over the network.
    /// </summary>
    public class MovementAudio : MonoBehaviour
    {
        public enum Mode { Footsteps, Loop }

        [SerializeField] private Mode mode = Mode.Footsteps;

        [Header("Speed")]
        [Tooltip("Below this horizontal speed (m/s) the object counts as standing still.")]
        [SerializeField] private float minSpeed = 0.4f;
        [Tooltip("How quickly the measured speed follows real movement. Higher = snappier.")]
        [SerializeField] private float speedSmoothing = 10f;
        [Tooltip("Faster than this (m/s) is treated as a teleport/respawn snap, not movement.")]
        [SerializeField] private float maxPlausibleSpeed = 20f;

        [Header("Footsteps")]
        [Tooltip("One is picked at random per step (never the same twice in a row).")]
        [SerializeField] private AudioClip[] stepClips;
        [Tooltip("Meters travelled per step. Faster movement = more frequent steps automatically.")]
        [SerializeField] private float stepDistance = 1.8f;
        [Range(0f, 1f)] [SerializeField] private float stepVolume = 0.6f;
        [Range(0f, 0.5f)] [SerializeField] private float stepPitchVariance = 0.08f;
        [Tooltip("Only step when there's ground below. Leave empty (Nothing) to skip the check. " +
                 "Don't include the player's own layer.")]
        [SerializeField] private LayerMask groundMask;
        [Tooltip("Ray length down from just above the pivot. Covers pivots at the feet or mid-body.")]
        [SerializeField] private float groundRayLength = 1.3f;

        [Header("Loop (carts, machines)")]
        [SerializeField] private AudioClip loopClip;
        [Tooltip("Speed (m/s) at which the loop reaches full volume and max pitch.")]
        [SerializeField] private float speedForFullVolume = 3f;
        [Range(0f, 1f)] [SerializeField] private float loopMaxVolume = 0.6f;
        [SerializeField] private float loopMinPitch = 0.85f;
        [SerializeField] private float loopMaxPitch = 1.15f;
        [Tooltip("How fast the loop fades in/out when starting/stopping.")]
        [SerializeField] private float loopFadeSpeed = 6f;

        private AudioSource source;
        private Vector3 lastPos;
        private float speed;
        private float distanceSinceStep;
        private int lastStepIndex = -1;

        private void Start()
        {
            // Own child object so it never clashes with other AudioSources on the prefab.
            var go = new GameObject("MovementAudio");
            go.transform.SetParent(transform, false);
            source = go.AddComponent<AudioSource>();
            WorldAudio.ConfigureSource(source);

            if (mode == Mode.Loop && loopClip != null)
            {
                source.clip = loopClip;
                source.loop = true;
                source.volume = 0f;
                // Random start point so several carts don't play in perfect unison.
                source.time = Random.Range(0f, loopClip.length);
                source.Play();
            }

            lastPos = transform.position;
        }

        private void OnEnable() => lastPos = transform.position;

        private void OnDisable()
        {
            if (source != null && mode == Mode.Loop) source.volume = 0f;
        }

        private void LateUpdate()
        {
            if (source == null) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 pos = transform.position;
            Vector3 delta = pos - lastPos;
            delta.y = 0f;
            lastPos = pos;

            float rawSpeed = delta.magnitude / dt;
            if (rawSpeed > maxPlausibleSpeed)
            {
                // Spawn/teleport snap: don't play a burst of steps or a wheel screech.
                rawSpeed = 0f;
                speed = 0f;
                distanceSinceStep = 0f;
                delta = Vector3.zero;
            }

            speed = Mathf.Lerp(speed, rawSpeed, 1f - Mathf.Exp(-speedSmoothing * dt));

            if (mode == Mode.Footsteps) UpdateFootsteps(delta.magnitude);
            else UpdateLoop(dt);
        }

        private void UpdateFootsteps(float moved)
        {
            if (speed < minSpeed || !IsGrounded())
            {
                // Start the next walk most of the way to a step, so the first one comes quickly.
                distanceSinceStep = stepDistance * 0.7f;
                return;
            }

            distanceSinceStep += moved;
            if (distanceSinceStep < stepDistance) return;
            distanceSinceStep = 0f;

            if (stepClips == null || stepClips.Length == 0) return;

            int i = Random.Range(0, stepClips.Length);
            if (stepClips.Length > 1 && i == lastStepIndex)
                i = (i + 1) % stepClips.Length;
            lastStepIndex = i;

            float volume = stepVolume * WorldAudio.VolumeScale;
            if (volume <= 0f || stepClips[i] == null) return;

            source.pitch = 1f + Random.Range(-stepPitchVariance, stepPitchVariance);
            source.PlayOneShot(stepClips[i], volume);
        }

        private void UpdateLoop(float dt)
        {
            if (loopClip == null) return;

            float t = speed < minSpeed ? 0f : Mathf.Clamp01(speed / Mathf.Max(0.01f, speedForFullVolume));
            float targetVolume = t * loopMaxVolume * WorldAudio.VolumeScale;

            source.volume = Mathf.MoveTowards(source.volume, targetVolume, loopFadeSpeed * dt);
            source.pitch = Mathf.Lerp(loopMinPitch, loopMaxPitch, t);
        }

        private bool IsGrounded()
        {
            if (groundMask.value == 0) return true;

            Vector3 origin = transform.position + Vector3.up * 0.1f;
            return Physics.Raycast(origin, Vector3.down, groundRayLength, groundMask,
                                   QueryTriggerInteraction.Ignore);
        }
    }
}
