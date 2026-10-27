using System;
using System.Collections;
using UnityEngine;

namespace ProgramZer0.SoundSystem
{
    public class SoundManager : MonoBehaviour
    {
        public Sound[] sounds;

        [SerializeField] private bool enableDebugLogs = false;

        [SerializeField] private float modSound = .9f;
        [SerializeField] private float modMusicSound = 0.7f;

        private Sound currentMusic = null;
        private Coroutine musicFadeCoroutine;
        private bool musicMuted = false;
        private bool allSoundsMuted = false;

        private void DLog(string message)
        {
            if (enableDebugLogs)
                Debug.Log(message);
        }

        private void Awake()
        {
            foreach (Sound s in sounds)
            {
                s.source = gameObject.AddComponent<AudioSource>();
                s.source.clip = s.clip;
                s.source.loop = s.loop;
                s.source.playOnAwake = false;

                s.source.volume = s.volume;
                s.source.pitch = s.pitch;
                s.source.priority = s.priority;
            }

            ApplyMuteStates();
            DLog("finished SM init");
        }

        private Sound FindSound(string name)
        {
            Sound s = Array.Find(sounds, sound => sound.name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s == null)
                Debug.LogWarning($"Sound '{name}' not found!");
            return s;
        }

        public float GetSoundMod() { return modSound; }
        public float getSoundMusicMod() { return modMusicSound; }

        public void PlayIfAlreadyNotPlaying(string name)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            if (s.source.isPlaying)
                return;

            s.source.volume = s.volume * (s.isMusic ? modMusicSound : modSound);
            s.source.Play();
        }

        public void PlayRandomSound(string[] soundNames)
        {
            if (soundNames == null || soundNames.Length == 0)
            {
                Debug.LogWarning("No sound names provided for random selection!");
                return;
            }

            Play(soundNames[UnityEngine.Random.Range(0, soundNames.Length)]);
        }

        public void Play(string name)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            s.source.volume = s.volume * (s.isMusic ? modMusicSound : modSound);
            DLog($"playing sound {name}");
            s.source.Play();
        }

        public void PlayRandomPitch(string name, float plus_minusP)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            s.source.volume = s.volume * (s.isMusic ? modMusicSound : modSound);
            s.source.pitch = s.pitch + UnityEngine.Random.Range(-plus_minusP, plus_minusP);
            DLog($"playing sound {name}");
            s.source.Play();
        }

        public void PlayEvenIfPlaying(string name)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            if (s.source.isPlaying)
                Stop(name);

            s.source.volume = s.volume * (s.isMusic ? modMusicSound : modSound);
            s.source.Play();
        }

        public void FadeInSoundIfNotPlaying(string name, float fadeTime = 1f)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            if (s.source.isPlaying)
                return;

            StartCoroutine(FadeInCoroutine(s, fadeTime));
        }

        public void FadeInSound(string name, float fadeTime = 1f)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            StartCoroutine(FadeInCoroutine(s, fadeTime));
        }

        public void FadeOutSound(string name, float fadeTime = 1f)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            StartCoroutine(FadeOutCoroutine(s, fadeTime));
        }

        private IEnumerator FadeOutCoroutine(Sound s, float fadeTime)
        {
            float startVolume = s.source.volume;

            float timer = 0f;
            while (timer < fadeTime)
            {
                timer += Time.deltaTime;
                float t = timer / fadeTime;
                s.source.volume = Mathf.Lerp(startVolume, 0f, t);
                yield return null;
            }

            s.source.Stop();
            s.source.volume = s.volume * (s.isMusic ? modMusicSound : modSound);
            if (s == currentMusic)
                currentMusic = null;
        }

        private IEnumerator FadeInCoroutine(Sound s, float fadeTime)
        {
            float targetVolume = s.volume * (s.isMusic ? modMusicSound : modSound);

            s.source.volume = 0f;
            s.source.Play();

            float timer = 0f;
            while (timer < fadeTime)
            {
                timer += Time.deltaTime;
                float t = timer / fadeTime;
                s.source.volume = Mathf.Lerp(0f, targetVolume, t);
                yield return null;
            }

            s.source.volume = targetVolume;
        }

        public void PlayMusic(string name, float fadeTime = 1f)
        {
            Sound newMusic = Array.Find(sounds, sound => sound.name.Equals(name, StringComparison.OrdinalIgnoreCase) && sound.isMusic);

            if (newMusic == null)
            {
                Debug.LogWarning($"Music '{name}' not found!");
                return;
            }

            if (newMusic == currentMusic) return;

            if (musicFadeCoroutine != null)
                StopCoroutine(musicFadeCoroutine);

            musicFadeCoroutine = StartCoroutine(CrossfadeMusic(newMusic, fadeTime));
        }

        public void PlayRandomMusic(string[] musicNames, float fadeTime = 1f)
        {
            if (musicNames == null || musicNames.Length == 0)
            {
                Debug.LogWarning("No music names provided for random selection!");
                return;
            }

            PlayMusic(musicNames[UnityEngine.Random.Range(0, musicNames.Length)], fadeTime);
        }

        private IEnumerator CrossfadeMusic(Sound newMusic, float fadeTime)
        {
            Sound oldMusic = currentMusic;
            currentMusic = newMusic;

            float oldStartVolume = (oldMusic != null && oldMusic.source != null) ? oldMusic.source.volume : 0f;
            float newTargetVolume = newMusic.volume * modMusicSound;

            if (newMusic != null)
            {
                newMusic.source.volume = 0f;
                newMusic.source.Play();
            }

            float timer = 0f;
            while (timer < fadeTime)
            {
                timer += Time.deltaTime;
                float t = timer / fadeTime;

                if (oldMusic != null && oldMusic.source != null)
                    oldMusic.source.volume = Mathf.Lerp(oldStartVolume, 0f, t);

                if (newMusic != null && newMusic.source != null)
                    newMusic.source.volume = Mathf.Lerp(0f, newTargetVolume, t);

                yield return null;
            }

            if (oldMusic != null && oldMusic.source != null)
            {
                oldMusic.source.Stop();
                oldMusic.source.volume = oldMusic.volume * modMusicSound;
            }

            if (newMusic != null && newMusic.source != null)
                newMusic.source.volume = newTargetVolume;

            musicFadeCoroutine = null;
        }

        public void Stop(string name)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            s.source.Stop();

            if (s == currentMusic)
                currentMusic = null;
        }

        public void StopAll()
        {
            foreach (Sound s in sounds)
            {
                if (s != currentMusic)
                    s.source.Stop();
            }
        }

        public void FadeOutCurrentMusic()
        {
            if (currentMusic != null)
            {
                StartCoroutine(FadeOutCoroutine(currentMusic, 1));
                currentMusic = null;
            }
        }

        public string GetCurrentMusicName()
        {
            return currentMusic != null ? currentMusic.name : null;
        }

        public void SetSoundMod(float vol)
        {
            DLog("setting sound");
            modSound = vol;
            foreach (Sound s in sounds)
            {
                if (s != currentMusic)
                    s.source.volume = s.volume * modSound;
            }
        }

        public void SetSoundMusicMod(float vol)
        {
            DLog("setting music");
            modMusicSound = vol;
            if (currentMusic != null && currentMusic.source != null)
                currentMusic.source.volume = currentMusic.volume * modMusicSound;
        }

        public void RefreshSingleMod(string name)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            if (s != currentMusic)
                s.source.volume = s.volume * modSound;
            else if (currentMusic != null && currentMusic.source != null)
                currentMusic.source.volume = currentMusic.volume * modMusicSound;
        }

        public void RefreshSoundMods()
        {
            SetSoundMod(modSound);
            SetSoundMusicMod(modMusicSound);
        }

        public void SetSoundVolume(string name, float _vol, bool refreshSound = false)
        {
            Sound s = FindSound(name);
            if (s == null) return;

            s.volume = _vol;
            if (refreshSound)
                SetSoundMod(modSound);
        }

        // --- Mute ---

        public void SetMuteMusic(bool val)
        {
            musicMuted = val;
            DLog($"music muted: {val}");
            ApplyMuteStates();
        }

        public void SetMuteAllSounds(bool val)
        {
            allSoundsMuted = val;
            DLog($"all sounds muted: {val}");
            ApplyMuteStates();
        }

        public bool GetMuteMusic() { return musicMuted; }
        public bool GetMuteAllSounds() { return allSoundsMuted; }

        // Applies the current mute flags to every AudioSource's native `mute` property.
        // Using AudioSource.mute (instead of zeroing out volume) means a toggle takes
        // effect immediately on whatever's currently playing - including a source that's
        // mid fade-in/out or mid music-crossfade - without clashing with the volume values
        // those coroutines are actively animating frame to frame.
        private void ApplyMuteStates()
        {
            foreach (Sound s in sounds)
            {
                if (s.source == null) continue;
                s.source.mute = allSoundsMuted || (s.isMusic && musicMuted);
            }
        }
    }
}