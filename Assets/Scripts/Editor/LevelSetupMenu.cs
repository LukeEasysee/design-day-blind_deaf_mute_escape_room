using System.Collections.Generic;
using EscapeRoom.Networking;
using EscapeRoom.Player;
using EscapeRoom.Senses;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeRoom.EditorTools
{
    /// <summary>
    /// Escape Room > Setup Level: wires the designers' Level scene into the multiplayer game.
    /// Safe to run again after the level changes (e.g. to resize the death zone).
    /// </summary>
    public static class LevelSetupMenu
    {
        const string BootstrapPath = "Assets/Scenes/NetworkingTest.unity";
        const string LevelPath = "Assets/Scenes/Level.unity";
        const string LevelName = "Level";
        const string GoalHumPrefabPath = "Assets/Prefabs/GoalHum.prefab";

        [MenuItem("Escape Room/Setup Level")]
        public static void Setup()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("Stop Play mode first."); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            AddToBuild(BootstrapPath, LevelPath);

            var boot = EditorSceneManager.OpenScene(BootstrapPath, OpenSceneMode.Single);
            var level = EditorSceneManager.OpenScene(LevelPath, OpenSceneMode.Additive);

            SetupBootstrap(boot);
            SetupLevel(level);

            EditorSceneManager.SaveScene(boot);
            EditorSceneManager.SaveScene(level);
            EditorSceneManager.CloseScene(level, true);
            Debug.Log("[Escape Room] Level setup complete. Play NetworkingTest, host, press START GAME.");
        }

        static void AddToBuild(params string[] paths)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            for (int i = paths.Length - 1; i >= 0; i--)
            {
                scenes.RemoveAll(s => s.path == paths[i]);
                scenes.Insert(0, new EditorBuildSettingsScene(paths[i], true));
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        static T Find<T>(Scene scene) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var c = root.GetComponentInChildren<T>(true);
                if (c != null) return c;
            }
            return null;
        }

        static Transform FindByName(Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t;
            return null;
        }

        static T Ensure<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        // ---------- Bootstrap (NetworkingTest) ----------
        static void SetupBootstrap(Scene boot)
        {
            var flow = Find<GameFlow>(boot);
            if (flow == null)
            {
                var go = new GameObject("GameFlow");
                SceneManager.MoveGameObjectToScene(go, boot);
                go.AddComponent<NetworkObject>();
                flow = go.AddComponent<GameFlow>();
            }
            flow.levelSceneName = LevelName;
            flow.roundSeconds = 120f;
            EditorUtility.SetDirty(flow);

            // The real level replaces the test floor / test beacon.
            foreach (var root in boot.GetRootGameObjects())
                if (root.name == "TestFloor" || root.GetComponent<GoalHum>() != null)
                    Object.DestroyImmediate(root);
            EditorSceneManager.MarkSceneDirty(boot);
        }

        // ---------- Level ----------
        static void SetupLevel(Scene level)
        {
            // Spawn points per role ("Spawners/Blind Spawn" etc.)
            foreach (var (name, role) in new[] { ("Blind Spawn", PlayerRole.Blind), ("Deaf Spawn", PlayerRole.Deaf), ("Mute Spawn", PlayerRole.Mute) })
            {
                var t = FindByName(level, name);
                if (t == null) { Debug.LogWarning($"[Escape Room] '{name}' not found in Level"); continue; }
                var sp = Ensure<PlayerSpawnPoint>(t.gameObject);
                sp.role = role;
                EditorUtility.SetDirty(sp);
            }

            // Paper: synced drawing + synced send/return.
            foreach (var surface in FindAll<DrawingSurface>(level))
            {
                Ensure<NetworkObject>(surface.gameObject);
                Ensure<PaperNetworkSync>(surface.gameObject);
            }

            // Pens / eraser: holder owns them, NetworkTransform syncs the movement.
            foreach (var pen in FindAll<PenMouseController>(level))
            {
                Ensure<NetworkObject>(pen.gameObject);
                var nt = Ensure<NetworkTransform>(pen.gameObject);
                nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
                nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
                Ensure<PenNetworkSync>(pen.gameObject);
            }

            // Drawing station on the table: view pose = the level's original drawing camera.
            var table = FindByName(level, "Table");
            if (table != null)
            {
                var station = Ensure<DrawingStation>(table.gameObject);
                var view = FindByName(level, "DrawingView");
                if (view == null)
                {
                    view = new GameObject("DrawingView").transform;
                    SceneManager.MoveGameObjectToScene(view.gameObject, level);
                    var levelCam = Find<Camera>(level);
                    if (levelCam != null)
                    {
                        view.SetPositionAndRotation(levelCam.transform.position, levelCam.transform.rotation);
                        station.fieldOfView = levelCam.fieldOfView;
                    }
                    else view.SetPositionAndRotation(table.position + Vector3.up * 1.5f, Quaternion.Euler(80f, 0f, 0f));
                }
                station.viewPoint = view;
                var targets = new List<Collider>();
                foreach (var c in table.GetComponentsInChildren<Collider>()) targets.Add(c);
                foreach (var surface in FindAll<DrawingSurface>(level))
                    foreach (var c in surface.GetComponents<Collider>()) targets.Add(c);
                station.targets = targets.ToArray();
                EditorUtility.SetDirty(station);
            }
            else Debug.LogWarning("[Escape Room] No 'Table' in Level: drawing station not created");

            // Level bounds (everything that renders) -> death zone underneath, covering the whole footprint.
            var bounds = new Bounds();
            bool init = false;
            foreach (var root in level.GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<Renderer>())
                {
                    if (r is ParticleSystemRenderer) continue;
                    if (!init) { bounds = r.bounds; init = true; } else bounds.Encapsulate(r.bounds);
                }

            var zoneT = FindByName(level, "DeathZone");
            GameObject zone = zoneT != null ? zoneT.gameObject : new GameObject("DeathZone");
            if (zoneT == null) SceneManager.MoveGameObjectToScene(zone, level);
            const float margin = 12f, depthBelow = 4f, thickness = 3f;
            zone.transform.SetPositionAndRotation(new Vector3(bounds.center.x, bounds.min.y - depthBelow, bounds.center.z), Quaternion.identity);
            zone.transform.localScale = Vector3.one;
            var box = Ensure<BoxCollider>(zone);
            box.isTrigger = true;
            box.center = Vector3.zero;
            box.size = new Vector3(bounds.size.x + margin * 2f, thickness, bounds.size.z + margin * 2f);
            Ensure<DeathZone>(zone);

            // Silent goal beacon on the finish platform, for the Blind player.
            var finish = FindByName(level, "Finish-Platform");
            if (finish != null && Find<GoalHum>(level) == null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GoalHumPrefabPath);
                if (prefab != null)
                {
                    var hum = (GameObject)PrefabUtility.InstantiatePrefab(prefab, level);
                    hum.transform.position = finish.position + Vector3.up * 1f;
                }
            }

            EditorSceneManager.MarkSceneDirty(level);
            Debug.Log($"[Escape Room] Death zone at y={zone.transform.position.y:F1}, size {box.size} (level bounds {bounds.size})");
        }

        static List<T> FindAll<T>(Scene scene) where T : Component
        {
            var list = new List<T>();
            foreach (var root in scene.GetRootGameObjects()) list.AddRange(root.GetComponentsInChildren<T>(true));
            return list;
        }
    }
}
