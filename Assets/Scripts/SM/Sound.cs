using UnityEngine;

namespace ProgramZer0.SoundSystem
{
    [System.Serializable]
    public class Sound
    {
        public string name = null;
        public AudioClip clip = null;
        public bool loop = false;
        public bool isMusic = false;
        public bool canPause = true;
        [Range(0f, 1f)]
        public float volume = .2f;
        [Range(0f, 3f)]
        public float pitch = 1;
        [Range(0, 250)]
        public int priority = 128;
        [HideInInspector]
        public AudioSource source = null;
    }
}
