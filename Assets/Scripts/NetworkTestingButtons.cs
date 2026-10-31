using UnityEngine;
using Unity.Netcode;

public class NetworkTestingButtons : MonoBehaviour
{
    private void OnGUI()
    {
        // The NetworkManager can be gone (e.g. while Play mode is stopping); draw nothing then.
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (!nm.IsClient && !nm.IsServer)
        {
            GUILayout.BeginArea(new Rect(10, 10, 300, 300));
            if (GUILayout.Button("Host")) nm.StartHost();
            if (GUILayout.Button("Server")) nm.StartServer();
            if (GUILayout.Button("Client")) nm.StartClient();
            GUILayout.EndArea();
        }
        else
        {
            // Top-right, out of the way of the store edit panel in the top-left.
            GUILayout.BeginArea(new Rect(Screen.width - 160, 10, 150, 80));
            string mode = nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client";
            GUILayout.Label($"Running as {mode}");
            if (GUILayout.Button("Shutdown"))
            {
                UIState.CloseAll();
                nm.Shutdown();
            }
            GUILayout.EndArea();
        }
    }
}
