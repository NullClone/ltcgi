#if UNITY_EDITOR

// based on similar script from Bakery

using System;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using System.IO;

[InitializeOnLoad]
public class LTCGI_Define : IActiveBuildTargetChanged
{
    public int callbackOrder => 0;

    public enum VRCLVDetected
    {
        NotDetected,
        DetectedOldVersion,
        DetectedCompatibleVersion,
    }
    public static VRCLVDetected vrcLightVolumesDetected = VRCLVDetected.NotDetected;

    [Serializable]
    private struct PackageVersion
    {
        public string version;
    }

    static void AddDefine()
    {
        var platform = EditorUserBuildSettings.selectedBuildTargetGroup;
        var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(platform);
        bool changed = false;
        if (!defines.Contains("LTCGI_INCLUDED"))
        {
            if (defines.Length > 0)
            {
                defines += ";";
            }
            defines += "LTCGI_INCLUDED";
            changed = true;
        }

#if LTCGI_VRC_LIGHT_VOLUMES
    #if VRC_LIGHT_VOLUMES
        vrcLightVolumesDetected = VRCLVDetected.DetectedCompatibleVersion;
    #else
        // uninstalled?
        vrcLightVolumesDetected = VRCLVDetected.NotDetected;
        defines = defines.Replace("LTCGI_VRC_LIGHT_VOLUMES", "");
        if (defines.EndsWith(";"))
            defines = defines.Substring(0, defines.Length - 1);
        changed = true;
        Debug.LogWarning("LTCGI: LTCGI_VRC_LIGHT_VOLUMES define removed because VRC_LIGHT_VOLUMES became unset.");
    #endif
#elif VRC_LIGHT_VOLUMES
        var lvmanifestpath = Path.Combine("Packages", "red.sim.lightvolumes", "package.json");
        if (File.Exists(lvmanifestpath))
        {
            var jsonraw = File.ReadAllText(lvmanifestpath);
            try
            {
                var jsonParsed = JsonUtility.FromJson<PackageVersion>(jsonraw);
                if (int.Parse(jsonParsed.version.Split('.')[0]) >= 3)
                {
                    if (!defines.Contains("LTCGI_VRC_LIGHT_VOLUMES"))
                    {
                        if (defines.Length > 0)
                        {
                            defines += ";";
                        }
                        defines += "LTCGI_VRC_LIGHT_VOLUMES";
                        changed = true;
                        vrcLightVolumesDetected = VRCLVDetected.DetectedCompatibleVersion;

                        Debug.Log($"LTCGI: VRC Light Volumes version {jsonParsed.version} detected (>= 3.x.x), enabling LTCGI_VRC_LIGHT_VOLUMES define.");
                    }
                }
                else
                {
                    vrcLightVolumesDetected = VRCLVDetected.DetectedOldVersion;
                    Debug.LogWarning($"LTCGI: VRC Light Volumes version {jsonParsed.version} detected (< 3.x.x), LTCGI_VRC_LIGHT_VOLUMES define will not be enabled.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to parse {lvmanifestpath}: {e}");
                vrcLightVolumesDetected = VRCLVDetected.NotDetected;
            }
        }
#else
        vrcLightVolumesDetected = VRCLVDetected.NotDetected;
#endif

        if (changed)
        {
            PlayerSettings.SetScriptingDefineSymbolsForGroup(platform, defines);
        }
    }

    static LTCGI_Define()
    {
        AddDefine();
        pi.LTCGI.LTCGI_Controller.MigratoryBirdsDontMigrateAsMuchAsWeDoButThisFunctionWillTakeCareOfItNonetheless();
    }

    public void OnActiveBuildTargetChanged(BuildTarget prev, BuildTarget cur)
    {
        AddDefine();
    }
}

#endif
