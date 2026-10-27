using UnityEngine;
using UnityEngine.Audio;

namespace ProgramZer0.SoundSystem
{
    /// <summary>
    /// WORLD sounds: things that happen at a place in the mall and should be heard by
    /// anyone nearby (items picked up/placed, doors, registers). Every sound here is 3D.
    ///
    /// Put one on the same scene object as the SoundManager to tune distances and mixer
    /// routing. Without one, the static helpers still work using the defaults below.
    ///
    /// Volume and mute follow the SoundManager's sound-effects setting (GetSoundMod /
    /// GetMuteAllSounds), so your existing settings sliders cover world sounds too.
    /// </summary>
    public class WorldAudio : MonoBehaviour
    {
        public static WorldAudio Instance { get; private set; }

        [Header("Mixer Routing (optional)")]
        [SerializeField] private AudioMixerGroup worldGroup;

        [Header("Distance")]
        [Tooltip("Within this distance a world sound is full volume.")]
        [SerializeField] private float minDistance = 2f;
        [Tooltip("Beyond this distance a world sound is silent.")]
        [SerializeField] private float maxDistance = 25f;

        [Header("Variation")]
        [Tooltip("Random +/- pitch on one-shots so repeats don't sound identical.")]
        [Range(0f, 0.5f)]
        [SerializeField] private float pitchVariance = 0.08f;

        private const float DefaultMin = 2f;
        private const float DefaultMax = 25f;
        private const float DefaultPitchVariance = 0.08f;

        public static AudioMixerGroup Group => Instance != null ? Instance.worldGroup : null;
        public static float MinDistance => Instance != null ? Instance.minDistance : DefaultMin;
        public static float MaxDistance => Instance != null ? Instance.maxDistance : DefaultMax;

        /// <summary>Volume multiplier from the player's sound settings (0 when muted).</summary>
        public static float VolumeScale
        {
            get
            {
                var sm = SoundManager.Instance;
                if (sm == null) return 1f;
                return sm.GetMuteAllSounds() ? 0f : sm.GetSoundMod();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[WorldAudio] Duplicate on '{name}' removed. Keep one on the scene audio object.", this);
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Sets an AudioSource up as a world (3D) source: distance falloff and mixer routing.
        /// Used by PlayOneShot and by MovementAudio for sources that live on moving objects.
        /// </summary>
        public static void ConfigureSource(AudioSource src)
        {
            src.playOnAwake = false;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = MinDistance;
            src.maxDistance = MaxDistance;
            src.dopplerLevel = 0f; // no pitch warble from fast-moving carts/players
            src.outputAudioMixerGroup = Group;
        }

        /// <summary>
        /// Plays a clip once at a world position. Each call gets its own short-lived source,
        /// so overlapping sounds never cut each other off.
        /// </summary>
        public static void PlayOneShot(AudioClip clip, Vector3 position, float volume = 1f)
        {
            if (clip == null) return;

            float scaled = volume * VolumeScale;
            if (scaled <= 0f) return;

            var go = new GameObject($"WorldSFX_{clip.name}");
            go.transform.position = position;

            var src = go.AddComponent<AudioSource>();
            ConfigureSource(src);

            float variance = Instance != null ? Instance.pitchVariance : DefaultPitchVariance;
            src.pitch = 1f + Random.Range(-variance, variance);
            src.clip = clip;
            src.volume = scaled;
            src.Play();

            Object.Destroy(go, clip.length / Mathf.Max(0.01f, src.pitch) + 0.1f);
        }
    }
}
