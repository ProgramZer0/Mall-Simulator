using UnityEngine;

/// <summary>
/// A loop that plays while this moves (a cart's wheels, a hand truck, a rolling rack): louder and a little
/// higher the faster it goes, silent when it stops. Works on every machine from how the object moves, so
/// nothing's sent. Put it on the cart prefab and give it the loop.
/// </summary>
public class RollingSound : MonoBehaviour
{
    [SerializeField] private AudioClip loop;
    [SerializeField, Range(0f, 1f)] private float volume = 0.6f;
    [Tooltip("At this speed (m/s) or faster: full volume.")]
    [SerializeField, Min(0.1f)] private float fullSpeed = 3f;
    [Tooltip("Slower than this (m/s): silent.")]
    [SerializeField, Min(0f)] private float minSpeed = 0.15f;
    [Tooltip("Pitch at a crawl and at full speed.")]
    [SerializeField] private Vector2 pitch = new(0.85f, 1.15f);

    private AudioSource src;
    private Vector3 last;
    private float speed;

    private void Awake()
    {
        if (loop == null) { enabled = false; return; }
        src = gameObject.AddComponent<AudioSource>();
        ProgramZer0.SoundSystem.WorldAudio.ConfigureSource(src);
        src.clip = loop;
        src.loop = true;
        src.playOnAwake = false;
        src.volume = 0f;
        last = transform.position;
    }

    private void Update()
    {
        Vector3 d = transform.position - last;
        last = transform.position;
        d.y = 0f;
        float now = Time.deltaTime > 0f ? d.magnitude / Time.deltaTime : 0f;
        speed = Mathf.Lerp(speed, now, 1f - Mathf.Exp(-Time.deltaTime * 10f)); // smoothed (network jitter)
        float t = Mathf.InverseLerp(minSpeed, fullSpeed, speed);
        if (t <= 0f)
        {
            if (src.isPlaying) src.Stop();
            return;
        }
        if (!src.isPlaying) src.Play();
        src.volume = volume * t;
        src.pitch = Mathf.Lerp(pitch.x, pitch.y, t);
    }
}
