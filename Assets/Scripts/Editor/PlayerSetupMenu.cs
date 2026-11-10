using EscapeRoom.Networking;
using EscapeRoom.Player;
using EscapeRoom.Senses;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EscapeRoom.EditorTools
{
    /// <summary>
    /// Escape Room > Setup Player: builds the networked first-person character from
    /// CharacterToon and the Blind-vision assets. Safe to run again (idempotent).
    /// </summary>
    public static class PlayerSetupMenu
    {
        const string PlayerPrefabPath = "Assets/Prefabs/NetworkPlayer.prefab";
        const string GoalHumPrefabPath = "Assets/Prefabs/GoalHum.prefab";
        const string CharacterPath = "Assets/Graphics/Adrift Team/Toon Faces and Characters Lite/Prefabs/CharacterToon.prefab";
        const string RingTexturePath = "Assets/Graphics/Textures/SoundPulseRing.png";
        const string PulseMaterialPath = "Assets/Graphics/Materials/SoundPulse.mat";
        const float CharacterHeight = 1.7f;

        [MenuItem("Escape Room/Setup Player")]
        public static void Setup()
        {
            int layer = EnsureLayer(SoundVisualizer.LayerName);
            var material = EnsurePulseMaterial();
            BuildPlayerPrefab();
            EnsureGoalHumPrefab();
            SetupScene(material);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Escape Room] Player setup complete (BlindVision layer = {layer}). Save the scene.");
        }

        // ---------- Layer ----------
        static int EnsureLayer(string name)
        {
            int existing = LayerMask.NameToLayer(name);
            if (existing >= 0) return existing;

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            for (int i = 8; i < layers.arraySize; i++)
            {
                var p = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(p.stringValue))
                {
                    p.stringValue = name;
                    tagManager.ApplyModifiedProperties();
                    return i;
                }
            }
            Debug.LogError("[Escape Room] No free layer for " + name);
            return -1;
        }

        // ---------- Pulse ring material ----------
        static Material EnsurePulseMaterial()
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(RingTexturePath);
            if (tex == null)
            {
                const int size = 128;
                var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ring = Mathf.Exp(-Mathf.Pow((r - 0.82f) / 0.07f, 2f));   // bright ring
                    float glow = Mathf.Clamp01(1f - r) * 0.15f;                       // faint inner glow
                    float a = Mathf.Clamp01(ring + glow) * (r < 1f ? 1f : 0f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
                t.Apply();
                System.IO.Directory.CreateDirectory("Assets/Graphics/Textures");
                System.IO.File.WriteAllBytes(RingTexturePath, t.EncodeToPNG());
                Object.DestroyImmediate(t);
                AssetDatabase.ImportAsset(RingTexturePath);
                var imp = (TextureImporter)AssetImporter.GetAtPath(RingTexturePath);
                imp.alphaIsTransparency = true;
                imp.mipmapEnabled = true;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.SaveAndReimport();
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(RingTexturePath);
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(PulseMaterialPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                mat = new Material(shader);
                System.IO.Directory.CreateDirectory("Assets/Graphics/Materials");
                AssetDatabase.CreateAsset(mat, PulseMaterialPath);
            }
            // Transparent + additive, unlit, no depth write.
            mat.SetTexture("_BaseMap", tex);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 2f); // additive
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_BLENDMODE_ADD");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------- Player prefab ----------
        static void BuildPlayerPrefab()
        {
            var characterPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
            if (characterPrefab == null)
            {
                Debug.LogError("[Escape Room] CharacterToon prefab not found at " + CharacterPath);
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) == null)
            {
                System.IO.Directory.CreateDirectory("Assets/Prefabs");
                var tmp = new GameObject("NetworkPlayer");
                tmp.AddComponent<NetworkObject>();
                PrefabUtility.SaveAsPrefabAsset(tmp, PlayerPrefabPath);
                Object.DestroyImmediate(tmp);
            }

            // Edit in place so the prefab GUID (referenced by the NetworkManager) stays the same.
            var root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                // Remove the old capsule placeholder.
                Object.DestroyImmediate(root.GetComponent<CapsuleCollider>());
                Object.DestroyImmediate(root.GetComponent<MeshRenderer>());
                Object.DestroyImmediate(root.GetComponent<MeshFilter>());

                if (!root.GetComponent<NetworkObject>()) root.AddComponent<NetworkObject>();
                var nt = root.GetComponent<NetworkTransform>();
                if (nt == null) nt = root.AddComponent<NetworkTransform>();
                nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
                nt.SyncRotAngleX = false;
                nt.SyncRotAngleZ = false;
                nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;

                var cc = root.GetComponent<CharacterController>();
                if (cc == null) cc = root.AddComponent<CharacterController>();
                cc.height = CharacterHeight;
                cc.radius = 0.3f;
                cc.skinWidth = 0.03f;
                // The capsule rests skinWidth above the floor; offset it so the root (feet) sits on the floor.
                cc.center = new Vector3(0f, CharacterHeight / 2f + cc.skinWidth, 0f);
                cc.stepOffset = 0.3f;

                var netPlayer = root.GetComponent<NetworkPlayer>();
                if (netPlayer == null) netPlayer = root.AddComponent<NetworkPlayer>();
                var controller = root.GetComponent<PlayerController>();
                if (controller == null) controller = root.AddComponent<PlayerController>();
                var body = root.GetComponent<CharacterPose>();
                if (body == null) body = root.AddComponent<CharacterPose>();

                // Model
                var oldModel = root.transform.Find("Model");
                if (oldModel != null) Object.DestroyImmediate(oldModel.gameObject);
                var model = (GameObject)PrefabUtility.InstantiatePrefab(characterPrefab, root.transform);
                model.name = "Model";
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                model.transform.localScale = Vector3.one;
                var smr = model.GetComponentInChildren<SkinnedMeshRenderer>();
                smr.updateWhenOffscreen = true; // bones are posed by code; keep bounds correct
                float top = smr.bounds.max.y - root.transform.position.y;
                float scale = CharacterHeight / Mathf.Max(0.01f, top);
                model.transform.localScale = Vector3.one * scale;
                var animator = model.GetComponent<Animator>();
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
                var head = animator.GetBoneTransform(HumanBodyBones.Head);

                // Camera rig. The toon proportions (big head, short arms) mean a camera at the
                // real eyes can't see raised hands, so the first-person camera sits just above the
                // shoulders, at the neck. Raised hands then appear in the lower part of the view.
                float headY = head.position.y - root.transform.position.y;
                float shoulderY = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm).position.y - root.transform.position.y;
                float eyeY = shoulderY + (headY - shoulderY) * 0.8f;
                var pivot = root.transform.Find("CameraPivot");
                if (pivot == null)
                {
                    pivot = new GameObject("CameraPivot").transform;
                    pivot.SetParent(root.transform, false);
                }
                pivot.localPosition = new Vector3(0f, eyeY, 0.02f);
                pivot.localRotation = Quaternion.identity;

                var cam = root.GetComponentInChildren<Camera>(true);
                if (cam == null)
                {
                    var camGo = new GameObject("PlayerCamera");
                    cam = camGo.AddComponent<Camera>();
                    camGo.AddComponent<AudioListener>();
                }
                cam.transform.SetParent(pivot, false);
                cam.transform.localPosition = Vector3.zero;
                cam.transform.localRotation = Quaternion.identity;
                cam.nearClipPlane = 0.03f;
                cam.fieldOfView = 75f;
                cam.enabled = false; // enabled only for the owner
                var listener = cam.GetComponent<AudioListener>();
                if (listener == null) listener = cam.gameObject.AddComponent<AudioListener>();
                listener.enabled = false;

                // Wire serialized references
                var so = new SerializedObject(netPlayer);
                so.FindProperty("playerCamera").objectReferenceValue = cam;
                so.FindProperty("voiceAnchor").objectReferenceValue = head;
                so.ApplyModifiedPropertiesWithoutUndo();

                so = new SerializedObject(controller);
                so.FindProperty("cameraPivot").objectReferenceValue = pivot;
                so.ApplyModifiedPropertiesWithoutUndo();

                so = new SerializedObject(body);
                so.FindProperty("model").objectReferenceValue = animator;
                so.FindProperty("controller").objectReferenceValue = controller;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
                Debug.Log($"[Escape Room] NetworkPlayer prefab rebuilt: model scale {scale:F3}, eye height {eyeY:F2} m");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ---------- Goal hum ----------
        static void EnsureGoalHumPrefab()
        {
            // Silent beacon: only the Blind player's sound-pulse visual, no audio.
            var go = new GameObject("GoalHum");
            go.AddComponent<GoalHum>();
            PrefabUtility.SaveAsPrefabAsset(go, GoalHumPrefabPath);
            Object.DestroyImmediate(go);
        }

        // ---------- Scene ----------
        static void SetupScene(Material pulseMaterial)
        {
            var nm = Object.FindAnyObjectByType<NetworkManager>();
            if (nm != null)
            {
                var vis = nm.GetComponent<SoundVisualizer>();
                if (vis == null) vis = nm.gameObject.AddComponent<SoundVisualizer>();
                var so = new SerializedObject(vis);
                so.FindProperty("pulseMaterial").objectReferenceValue = pulseMaterial;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(nm.gameObject);
            }
            else Debug.LogWarning("[Escape Room] No NetworkManager in scene: run Setup Network In Current Scene first.");

            // Lobby/game state (in-scene NetworkObject).
            if (Object.FindAnyObjectByType<GameFlow>() == null)
            {
                var flow = new GameObject("GameFlow");
                flow.AddComponent<NetworkObject>();
                flow.AddComponent<GameFlow>();
                Undo.RegisterCreatedObjectUndo(flow, "Create GameFlow");
            }

            // Test floor so players have something to walk on (real levels are built separately).
            if (GameObject.Find("TestFloor") == null)
            {
                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.name = "TestFloor";
                floor.transform.localScale = new Vector3(4f, 1f, 4f); // 40 x 40 m
                Undo.RegisterCreatedObjectUndo(floor, "Create TestFloor");
            }

            // A test goal hum to walk towards.
            if (Object.FindAnyObjectByType<GoalHum>() == null)
            {
                var humPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GoalHumPrefabPath);
                var hum = (GameObject)PrefabUtility.InstantiatePrefab(humPrefab);
                hum.transform.position = new Vector3(0f, 1f, 12f);
                Undo.RegisterCreatedObjectUndo(hum, "Create GoalHum");
            }

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
