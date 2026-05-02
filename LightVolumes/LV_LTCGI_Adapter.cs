#if VRC_LIGHT_VOLUMES

using System;
using System.Collections.Generic;
using UdonSharp;
using UnityEngine;
using UnityEditor;
using VRCLightVolumes;
using VRC.Udon;

#if UNITY_EDITOR
using UnityEditor.SceneManagement;
using UdonSharpEditor;
#endif

#if UDONSHARP
using VRC.SDKBase;
#else
using VRCShader = UnityEngine.Shader;
#endif

namespace pi.LTCGI.LVAdapter
{
    [DefaultExecutionOrder(100)] // after LightVolumeManager
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LV_LTCGI_Adapter : UdonSharpBehaviour
    {
        public LightVolumeManager LightVolumeManager;
        public CustomRenderTexture PostProcessorCRT;
        public int[] LTCGIEnabledLightVolumeIDs = new int[0];

        private int lightVolumesWithLTCGIID;
        private int lightVolumeFwdWorldMatrixID;
        private Matrix4x4[] lightVolumeFwdWorldMatrices = new Matrix4x4[0];
        private long updateCount;

        private void Start()
        {
            lightVolumeFwdWorldMatrixID = VRCShader.PropertyToID("_UdonLightVolumeFwdWorldMatrix");
            VRCShader.SetGlobalMatrixArray(lightVolumeFwdWorldMatrixID, new Matrix4x4[32]);
            lightVolumesWithLTCGIID = VRCShader.PropertyToID("_UdonLightVolumesWithLTCGI");
            VRCShader.SetGlobalFloat(lightVolumesWithLTCGIID, BitConverter.Int32BitsToSingle(0));
            Debug.Log($"LV_LTCGI_Adapter initialized with {LTCGIEnabledLightVolumeIDs.Length} LTCGI enabled light volumes.", this);
            foreach (var id in LTCGIEnabledLightVolumeIDs)
            {
                Debug.Log($" - {id}: {LightVolumeManager.LightVolumeInstances[id].name}", LightVolumeManager.LightVolumeInstances[id]);
            }
        }

        private void LateUpdate() // after Update() in LightVolumeManager for the frame
        {
            if (!LightVolumeManager.AutoUpdateVolumes && updateCount > 0) return;
            DoUpdate();
        }

        public void DoUpdate()
        {
            updateCount++;

            var enabledCount = LightVolumeManager.EnabledCount;
            var enabledIDs = LightVolumeManager.EnabledIDs;
            if (enabledCount != lightVolumeFwdWorldMatrices.Length)
                lightVolumeFwdWorldMatrices = new Matrix4x4[enabledCount];
            
            int enabledVolumes = 0;
            for (int i = 0; i < enabledCount; i++)
            {
                var id = enabledIDs[i];
                if (Array.IndexOf(LTCGIEnabledLightVolumeIDs, id) >= 0)
                {
                    var instance = LightVolumeManager.LightVolumeInstances[id];
                    var invWorldMatrix = instance.InvWorldMatrix;
                    lightVolumeFwdWorldMatrices[i] = invWorldMatrix.inverse;
                    enabledVolumes |= 1 << i;
                }
            }

            VRCShader.SetGlobalMatrixArray(lightVolumeFwdWorldMatrixID, lightVolumeFwdWorldMatrices);
            VRCShader.SetGlobalFloat(lightVolumesWithLTCGIID, BitConverter.Int32BitsToSingle(enabledVolumes));
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        private static readonly List<int> tmpLTCGIEnabledLightVolumeIDs = new();
        public static void SetupDependencies(ref LightVolumeManager refLightVolumeManager, ref CustomRenderTexture refPostProcessorCRT, ref int[] refLTCGIEnabledLightVolumeIDs, bool isUI)
        {
            if (refLightVolumeManager == null)
            {
                var foundManager = FindObjectOfType<LightVolumeManager>();
                if (foundManager != null)
                {
                    refLightVolumeManager = foundManager;
                }
                else if (isUI)
                {
                    EditorGUILayout.HelpBox("No LightVolumeManager found in the scene. Please create one.", MessageType.Warning);
                }
            }

            if (refPostProcessorCRT == null)
            {
                var thisPath = AssetDatabase.GUIDToAssetPath("b9ee507ae056a484ba791c34abfe3982"); // this script's GUID
                var thisDir = System.IO.Path.GetDirectoryName(thisPath);
                var crtPath = System.IO.Path.Combine(thisDir, "LV_CRT_LTCGI.asset");
                refPostProcessorCRT = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(crtPath);
                if (refPostProcessorCRT == null)
                {
                    Debug.LogWarning("LV_LTCGI_Adapter: Post Processor CRT not found. Please create it in the same folder as this script or reimport the package.");
                }
            }

            if (refLightVolumeManager != null && refPostProcessorCRT != null)
            {
                // find LTCGI enabled light volumes
                tmpLTCGIEnabledLightVolumeIDs.Clear();
                for (int i = 0; i < refLightVolumeManager.LightVolumeInstances.Length; i++)
                {
                    var lv = refLightVolumeManager.LightVolumeInstances[i];
                    if (lv != null && lv.TryGetComponent<LightVolumeLTCGI>(out _))
                    {
                        tmpLTCGIEnabledLightVolumeIDs.Add(i);
                    }
                }
                var difference = tmpLTCGIEnabledLightVolumeIDs.Count != refLTCGIEnabledLightVolumeIDs.Length;
                if (!difference)
                {
                    for (int i = 0; i < tmpLTCGIEnabledLightVolumeIDs.Count; i++)
                    {
                        if (tmpLTCGIEnabledLightVolumeIDs[i] != refLTCGIEnabledLightVolumeIDs[i])
                        {
                            difference = true;
                            break;
                        }
                    }
                }
                if (difference)
                {
                    refLTCGIEnabledLightVolumeIDs = tmpLTCGIEnabledLightVolumeIDs.ToArray();
                    Debug.Log($"LV_LTCGI_Adapter: Found and set {refLTCGIEnabledLightVolumeIDs.Length} LTCGI enabled light volumes.");

                    // register the CRT as a post processor
                    if (refLightVolumeManager.TryGetComponent<LightVolumeSetup>(out var lvSetup))
                    {
                        refLightVolumeManager.AtlasPostProcessors ??= new CustomRenderTexture[0]; // workaround for NRE on init

                        if (refLTCGIEnabledLightVolumeIDs.Length == 0)
                            lvSetup.UnregisterPostProcessorCRT(refPostProcessorCRT);
                        else
                            lvSetup.RegisterPostProcessorCRT(refPostProcessorCRT);
                    }
                }
            }
        }
#endif
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    [CustomEditor(typeof(LV_LTCGI_Adapter))]
    public class LV_LTCGI_AdapterEditor : Editor
    {
        [InitializeOnLoadMethod]
        private static void InitEditorUpdateLoop()
        {
            // register callbacks
            EditorApplication.update += EditorUpdate;
            LTCGI_Controller.OnLTCGIShadowmapBakeComplete += OnLTCGIShadowmapBakeComplete;
            LTCGI_Controller.OnLTCGIShadowmapClearData += OnLTCGIShadowmapClearData;
            Texture3DAtlasGenerator.OnPreAtlasCreate += OnPreAtlasCreate;
        }

        private static LightVolumeManager lightVolumeManager;
        private static CustomRenderTexture postProcessorCRT;
        private static Matrix4x4[] lightVolumeFwdWorldMatrices = new Matrix4x4[32];
        private static List<UdonBehaviour> tmpUdonBehaviours = new();
        private static int[] LTCGIEnabledLightVolumeIDs = new int[0];
        private static void EditorUpdate()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            if (LTCGI_Controller.Singleton == null)
                return;

            if (!FindAndUpdateLVAdapter())
            {
                var newUdon = LTCGI_Controller.Singleton.gameObject.AddUdonSharpComponent<LV_LTCGI_Adapter>();
                LV_LTCGI_Adapter.SetupDependencies(ref lightVolumeManager, ref postProcessorCRT, ref newUdon.LTCGIEnabledLightVolumeIDs, isUI: false);
                EditorUtility.SetDirty(newUdon);
                UdonSharpEditorUtility.CopyProxyToUdon(newUdon);
                Debug.Log("LV_LTCGI_Adapter: Added to LTCGI_Controller.");
            }

            LV_LTCGI_Adapter.SetupDependencies(ref lightVolumeManager, ref postProcessorCRT, ref LTCGIEnabledLightVolumeIDs, isUI: false);
            if (lightVolumeManager != null && postProcessorCRT != null)
            {
                var enabledCount = lightVolumeManager.EnabledCount;
                var enabledIDs = lightVolumeManager.EnabledIDs;

                Array.Clear(lightVolumeFwdWorldMatrices, 0, lightVolumeFwdWorldMatrices.Length); // fine in editor

                int enabledVolumes = 0;
                for (int i = 0; i < enabledCount; i++)
                {
                    var id = enabledIDs[i];
                    if (Array.IndexOf(LTCGIEnabledLightVolumeIDs, id) >= 0)
                    {
                        var instance = lightVolumeManager.LightVolumeInstances[id];
                        var invWorldMatrix = instance.InvWorldMatrix;
                        lightVolumeFwdWorldMatrices[i] = invWorldMatrix.inverse;
                        enabledVolumes |= 1 << i;
                    }
                }

                VRCShader.SetGlobalMatrixArray(VRCShader.PropertyToID("_UdonLightVolumeFwdWorldMatrix"), lightVolumeFwdWorldMatrices);
                VRCShader.SetGlobalFloat(VRCShader.PropertyToID("_UdonLightVolumesWithLTCGI"), BitConverter.Int32BitsToSingle(enabledVolumes));
            }
        }

        private static bool FindAndUpdateLVAdapter()
        {
            LTCGI_Controller.Singleton.GetComponents(tmpUdonBehaviours);
            if (tmpUdonBehaviours.Count == 0)
                return false;

            var foundLVAdapter = false;
            foreach (var ub in tmpUdonBehaviours)
            {
                var proxy = UdonSharpEditorUtility.GetProxyBehaviour(ub);
                if (proxy is LV_LTCGI_Adapter adapter)
                {
                    if (foundLVAdapter)
                    {
                        Debug.LogWarning("Multiple LV_LTCGI_Adapter components found on LTCGI_Controller.", adapter);
                        DestroyImmediate(adapter);
                    }
                    else
                    {
                        foundLVAdapter = true;
                        LV_LTCGI_Adapter.SetupDependencies(ref adapter.LightVolumeManager, ref adapter.PostProcessorCRT, ref adapter.LTCGIEnabledLightVolumeIDs, isUI: false);
                        UdonSharpEditorUtility.CopyProxyToUdon(adapter);
                    }
                }
            }
            return foundLVAdapter;
        }

        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target))
                return;
            
            var adapter = (LV_LTCGI_Adapter)target;
            LV_LTCGI_Adapter.SetupDependencies(ref adapter.LightVolumeManager, ref adapter.PostProcessorCRT, ref adapter.LTCGIEnabledLightVolumeIDs, isUI: true);
            UdonSharpEditorUtility.CopyProxyToUdon(adapter);

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.ObjectField("Light Volume Manager", adapter.LightVolumeManager, typeof(LightVolumeManager), true);
            EditorGUILayout.ObjectField("Post Processor CRT", adapter.PostProcessorCRT, typeof(CustomRenderTexture), false);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "LTCGI Light Volume Adapter is active.",
                MessageType.Info
            );
        }

        // Bake handlers

        private static void OnPreAtlasCreate(LightVolume[] obj)
        {
            if (LTCGI_Controller.Singleton == null || LTCGI_Controller.Singleton.bakeInProgress)
                return;

            if (!FindAndUpdateLVAdapter())
            {
                Debug.LogWarning("LV_LTCGI_Adapter: Could not find LV_LTCGI_Adapter during pre-atlas creation. Skipping LTCGI LV bake.");
                return;
            }

            var curscene = EditorSceneManager.GetActiveScene().name;
            foreach (var volume in obj)
            {
                if (volume is LightVolumeLTCGI)
                {
                    // re-apply our LTCGI shadowmap textures
                    var path = $"Assets/LTCGI-Generated/{curscene}-lvdata-{VRC.Core.ExtensionMethods.GetHierarchyPath(volume.transform).Replace('/', '_')}-";
                    var tex0 = AssetDatabase.LoadAssetAtPath<Texture3D>($"{path}tex0.asset");
                    var tex1 = AssetDatabase.LoadAssetAtPath<Texture3D>($"{path}tex1.asset");
                    var tex2 = AssetDatabase.LoadAssetAtPath<Texture3D>($"{path}tex2.asset");
                    volume.Texture0 = tex0;
                    volume.Texture1 = tex1;
                    volume.Texture2 = tex2;
                    Debug.Log($"LV_LTCGI_Adapter: Re-applied LTCGI textures for {VRC.Core.ExtensionMethods.GetHierarchyPath(volume.transform)}.", tex0);
                }
            }
        }

        private static void OnLTCGIShadowmapBakeComplete()
        {
            var curscene = EditorSceneManager.GetActiveScene().name;
            var allLTCGIVolumes = FindObjectsOfType<LightVolumeLTCGI>();
            foreach (var volume in allLTCGIVolumes)
            {
                var path = $"Assets/LTCGI-Generated/{curscene}-lvdata-{VRC.Core.ExtensionMethods.GetHierarchyPath(volume.transform).Replace('/', '_')}-";
                var copy0 = Instantiate(volume.Texture0);
                AssetDatabase.CreateAsset(copy0, $"{path}tex0.asset");
                var copy1 = Instantiate(volume.Texture1);
                AssetDatabase.CreateAsset(copy1, $"{path}tex1.asset");
                var copy2 = Instantiate(volume.Texture2);
                AssetDatabase.CreateAsset(copy2, $"{path}tex2.asset");
                Debug.Log($"LV_LTCGI_Adapter: Created LTCGI textures for {VRC.Core.ExtensionMethods.GetHierarchyPath(volume.transform)} at {path}*", copy0);
            }
        }

        private static void OnLTCGIShadowmapClearData()
        {
            var curscene = EditorSceneManager.GetActiveScene().name;
            var allLTCGIVolumes = FindObjectsOfType<LightVolumeLTCGI>();
            foreach (var volume in allLTCGIVolumes)
            {
                var path = $"Assets/LTCGI-Generated/{curscene}-lvdata-{VRC.Core.ExtensionMethods.GetHierarchyPath(volume.transform).Replace('/', '_')}-";
                AssetDatabase.DeleteAsset($"{path}tex0.asset");
                AssetDatabase.DeleteAsset($"{path}tex1.asset");
                AssetDatabase.DeleteAsset($"{path}tex2.asset");
                Debug.Log($"LV_LTCGI_Adapter: Cleared LTCGI textures for {VRC.Core.ExtensionMethods.GetHierarchyPath(volume.transform)} at {path}*");
            }
        }
    }
#endif
}

#endif