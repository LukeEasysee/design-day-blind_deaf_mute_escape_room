using EscapeRoom.Networking;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EscapeRoom.EditorTools
{
    /// <summary>
    /// One-click setup so nobody has to hand-wire the NetworkManager / player prefab.
    /// Menu: Escape Room > Setup Network In Current Scene
    /// </summary>
    public static class NetworkSetupMenu
    {
        const string PrefabPath = "Assets/Prefabs/NetworkPlayer.prefab";

        [MenuItem("Escape Room/Setup Network In Current Scene")]
        public static void Setup()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) ?? CreatePlayerPrefab();

            var nm = Object.FindAnyObjectByType<NetworkManager>();
            if (nm == null)
            {
                var go = new GameObject("NetworkManager");
                Undo.RegisterCreatedObjectUndo(go, "Create NetworkManager");
                nm = go.AddComponent<NetworkManager>();
            }

            // Explicit null check: Unity's fake-null objects make ?? unreliable on components.
            var transport = nm.GetComponent<UnityTransport>();
            if (transport == null) transport = nm.gameObject.AddComponent<UnityTransport>();
            nm.NetworkConfig ??= new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = transport;
            nm.NetworkConfig.PlayerPrefab = prefab;

            if (!nm.GetComponent<SessionManager>()) nm.gameObject.AddComponent<SessionManager>();
            if (!nm.GetComponent<LobbyUI>()) nm.gameObject.AddComponent<LobbyUI>();
            if (!nm.GetComponent<RoleEffects>()) nm.gameObject.AddComponent<RoleEffects>();
            if (!nm.GetComponent<VoiceChatManager>()) nm.gameObject.AddComponent<VoiceChatManager>();

            // The scene camera would fight the per-player cameras; players bring their own.
            var mainCam = Camera.main;
            if (mainCam != null && mainCam.GetComponentInParent<NetworkPlayer>() == null)
            {
                var listener = mainCam.GetComponent<AudioListener>();
                if (listener) listener.enabled = false;
            }

            EditorUtility.SetDirty(nm);
            EditorSceneManager.MarkSceneDirty(nm.gameObject.scene);
            Selection.activeGameObject = nm.gameObject;
            Debug.Log("[Escape Room] Network setup complete. Save the scene, then link the project to Unity Cloud (see Assets/Scripts/Networking/README.md).");
        }

        static GameObject CreatePlayerPrefab()
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            root.name = "NetworkPlayer";
            root.transform.position = new Vector3(0, 1, 0);

            root.AddComponent<NetworkObject>();
            var nt = root.AddComponent<NetworkTransform>();
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;

            var camGo = new GameObject("PlayerCamera");
            camGo.transform.SetParent(root.transform, false);
            camGo.transform.localPosition = new Vector3(0, 0.6f, 0);
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false; // enabled only for the owner
            var listener = camGo.AddComponent<AudioListener>();
            listener.enabled = false;

            var player = root.AddComponent<NetworkPlayer>();
            var so = new SerializedObject(player);
            so.FindProperty("playerCamera").objectReferenceValue = cam;
            so.ApplyModifiedPropertiesWithoutUndo();

            System.IO.Directory.CreateDirectory("Assets/Prefabs");
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }
    }
}
