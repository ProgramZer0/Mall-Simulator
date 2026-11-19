using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Keeps one NetworkManager. The NetworkManager lives on from scene to scene, so coming back to a scene
/// that has one of its own (the main menu after a game, or the game scene loaded from the menu) would make
/// a second: this removes the new one before it starts. Put it on the NetworkManager's object (a prefab
/// placed in both the menu and the game scene, so pressing Play in either works).
/// </summary>
[DefaultExecutionOrder(-10000)]
public class NetworkManagerGuard : MonoBehaviour
{
    private void Awake()
    {
        var mine = GetComponent<NetworkManager>();
        foreach (var other in FindObjectsByType<NetworkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (other == mine || other == null) continue;
            if (other.gameObject.scene.name == "DontDestroyOnLoad")
            {
                DestroyImmediate(gameObject); // the one already running wins
                return;
            }
        }
    }
}
