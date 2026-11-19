using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The player's name, floating over their head for everyone else (not over your own). The host fills it
/// in from the name they joined with (MallConnection). Put it on the player prefab.
/// </summary>
public class PlayerNameTag : NetworkBehaviour
{
    [SerializeField] private float height = 2.15f;
    [SerializeField] private float fontSize = 2.4f;
    [SerializeField] private Color color = Color.white;
    [Tooltip("Not shown further away than this (meters). 0: always.")]
    [SerializeField, Min(0f)] private float showWithin = 30f;

    private readonly NetworkVariable<FixedString64Bytes> playerName =
        new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private TextMeshPro tag;

    /// <summary>This player's name (every machine).</summary>
    public string PlayerName => playerName.Value.ToString();

    /// <summary>A player's name by client id, on any machine ("Player 2" if unknown).</summary>
    public static string NameOf(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.SpawnManager != null)
        {
            var po = nm.SpawnManager.GetPlayerNetworkObject(clientId);
            if (po != null && po.TryGetComponent<PlayerNameTag>(out var t) && t.PlayerName.Length > 0) return t.PlayerName;
        }
        return $"Player {clientId + 1}";
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            var c = MallConnection.Instance;
            string n = c != null ? c.NameOf(OwnerClientId) : $"Player {OwnerClientId + 1}";
            playerName.Value = new FixedString64Bytes(n.Length > 30 ? n.Substring(0, 30) : n);
        }
        playerName.OnValueChanged += OnNameChanged;
        if (!IsOwner) Build();
    }

    public override void OnNetworkDespawn()
    {
        playerName.OnValueChanged -= OnNameChanged;
        if (tag != null) Destroy(tag.gameObject);
    }

    private void OnNameChanged(FixedString64Bytes previous, FixedString64Bytes current)
    {
        if (tag != null) tag.text = current.ToString();
    }

    private void Build()
    {
        var go = new GameObject("Name Tag");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.up * height;
        tag = go.AddComponent<TextMeshPro>();
        tag.alignment = TextAlignmentOptions.Center;
        tag.fontSize = fontSize;
        tag.color = color;
        tag.outlineWidth = 0.2f;
        tag.rectTransform.sizeDelta = new Vector2(4f, 0.6f);
        tag.text = PlayerName;
    }

    private void LateUpdate()
    {
        if (tag == null) return;
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 d = tag.transform.position - cam.transform.position;
        bool show = showWithin <= 0f || d.sqrMagnitude <= showWithin * showWithin;
        if (tag.gameObject.activeSelf != show) tag.gameObject.SetActive(show);
        if (show && d.sqrMagnitude > 1e-4f) tag.transform.rotation = Quaternion.LookRotation(d);
    }
}
