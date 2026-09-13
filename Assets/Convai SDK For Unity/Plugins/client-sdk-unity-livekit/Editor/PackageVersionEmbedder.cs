#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class PackageVersionEmbedder
{
    const string PackageName = "io.livekit.livekit-sdk";
    const string EmbeddedPackageJsonPath = "Assets/Convai SDK For Unity/Plugins/client-sdk-unity-livekit/package.json";
    const string ResourcePath = "Assets/Resources";
    const string AssetName = "LiveKitSdkVersionInfo";

    [InitializeOnLoadMethod]
    static void Embed()
    {
        // FindForAssetPath works with any asset inside the package
        var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PackageName}");
        // The LiveKit package is embedded inside the copied Convai SDK under Assets,
        // so PackageInfo cannot find it after the registry package is removed.
        string version = info != null ? info.version : ReadEmbeddedVersion();
        if (string.IsNullOrEmpty(version)) return;

        if (!System.IO.Directory.Exists(ResourcePath))
            System.IO.Directory.CreateDirectory(ResourcePath);

        string path = $"{ResourcePath}/{AssetName}.txt";
        System.IO.File.WriteAllText(path, version);
        AssetDatabase.ImportAsset(path);
    }

    static string ReadEmbeddedVersion()
    {
        if (!System.IO.File.Exists(EmbeddedPackageJsonPath)) return null;
        var manifest = JsonUtility.FromJson<EmbeddedPackageManifest>(
            System.IO.File.ReadAllText(EmbeddedPackageJsonPath));
        return manifest != null ? manifest.version : null;
    }

    [System.Serializable]
    sealed class EmbeddedPackageManifest
    {
        public string version;
    }
}
#endif
