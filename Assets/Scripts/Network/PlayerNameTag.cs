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

    private TextMeshPro label;

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
        if (IsOwner) Local = this;
        if (!IsOwner) Build();
    }

    /// <summary>The local player's tag (null when not spawned).</summary>
    public static PlayerNameTag Local { get; private set; }

    /// <summary>Change your name (your own tag): everyone sees it straight away.</summary>
    public void Rename(string name)
    {
        if (IsSpawned && IsOwner) RenameRpc(name ?? "");
    }

    [Rpc(SendTo.Server)]
    private void RenameRpc(string name, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
        string n = MallConnection.CleanName(name);
        if (n.Length == 0) return;
        if (MallConnection.Instance != null) MallConnection.Instance.SetName(OwnerClientId, n);
        playerName.Value = new FixedString64Bytes(n.Length > 30 ? n.Substring(0, 30) : n);
    }

    public override void OnNetworkDespawn()
    {
        if (Local == this) Local = null;
        playerName.OnValueChanged -= OnNameChanged;
        if (label != null) Destroy(label.gameObject);
    }

    private void OnNameChanged(FixedString64Bytes previous, FixedString64Bytes current)
    {
        if (label != null) label.text = current.ToString();
    }

    private void Build()
    {
        var go = new GameObject("Name Tag");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.up * height;
        label = go.AddComponent<TextMeshPro>();
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = fontSize;
        label.color = color;
        label.outlineWidth = 0.2f;
        label.rectTransform.sizeDelta = new Vector2(4f, 0.6f);
        label.text = PlayerName;
    }

    private void LateUpdate()
    {
        if (label == null) return;
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 d = label.transform.position - cam.transform.position;
        bool show = showWithin <= 0f || d.sqrMagnitude <= showWithin * showWithin;
        if (label.gameObject.activeSelf != show) label.gameObject.SetActive(show);
        if (show && d.sqrMagnitude > 1e-4f) label.transform.rotation = Quaternion.LookRotation(d);
    }
}
