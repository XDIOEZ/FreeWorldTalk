using System;
using UnityEngine;

// The game's existing loader requires this exact global type name.
public sealed class ModPluginLauncher : BaseModLauncher
{
    public override void launcher()
    {
        if (FreeWorldTalk.TalkRuntime.Instance != null) return;
        var root = new GameObject("FreeWorldTalk.Runtime");
        UnityEngine.Object.DontDestroyOnLoad(root);
        try { root.AddComponent<FreeWorldTalk.TalkRuntime>().Initialize(config.Folder); }
        catch
        {
            UnityEngine.Object.Destroy(root);
            throw;
        }
    }
}
