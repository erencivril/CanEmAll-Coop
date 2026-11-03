using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using CanEmAllCoop.UI;
using UnityEngine;

namespace CanEmAllCoop.Flow;
using Net = CanEmAllCoop.Net.Net;

/// <summary>Small IMGUI status block (barriers, downtime, join progress) under the foundation's HUD line.</summary>
internal static class FlowHud
{
    static readonly List<string> Lines = new();

    public static void Draw()
    {
        bool toast = FlowCore.HasToast;
        if (!Net.IsOnline && !Net.Connecting && !FlowCore.HasNotice && !toast) return;
        Lines.Clear();
        FlowCore.CollectHudLines(Lines);
        if (Lines.Count == 0 && !toast) return;

        float scale = Mathf.Max(0.75f, Screen.height / 1080f);
        var saved = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        try
        {
            float virtualWidth = Screen.width / scale;
            float w = 420f, rowH = 22f;
            float x = (virtualWidth - w) / 2f, y = 34f;
            for (int i = 0; i < Lines.Count; i++)
            {
                var r = new Rect(x, y + i * (rowH + 2f), w, rowH);
                GUI.Box(r, "");
                GUI.Label(new Rect(r.x + 8f, r.y + 2f, r.width - 16f, rowH - 2f), Lines[i]);
            }
            if (toast) DrawToast(virtualWidth, y + Lines.Count * (rowH + 2f) + 10f);
        }
        finally { GUI.matrix = saved; }
    }

    /// <summary>Red box with the wrapped message. Drawn with OpaqueBox (GUI.Box only: other IMGUI calls are stripped in this build).</summary>
    static void DrawToast(float virtualWidth, float y)
    {
        const int fontSize = 18;
        float w = Mathf.Min(680f, virtualWidth - 40f);
        string text = FlowCore.ToastText;
        int lines = 0;
        foreach (var part in text.Split('\n')) lines += Mathf.Max(1, Mathf.CeilToInt(part.Length * fontSize * 0.52f / (w - 24f)));
        float h = lines * (fontSize + 5f) + 18f;
        var rect = new Rect((virtualWidth - w) / 2f, y, w, h);

        OpaqueBox.Draw(rect, text, fontSize, new Color(0.55f, 0.06f, 0.06f, 1f), Color.white, bold: true, wrap: true);
    }
}

/// <summary>Injected MonoBehaviour that only draws FlowHud. A failure to create it just means no on-screen hints.</summary>
public class FlowOverlayBehaviour : MonoBehaviour
{
    public FlowOverlayBehaviour(IntPtr ptr) : base(ptr) { }

    void OnGUI()
    {
        try { FlowHud.Draw(); }
        catch (Exception e) { FlowOverlay.DrawFailed(e); }
    }
}

internal static class FlowOverlay
{
    static bool _tried;
    static GameObject _go;
    static int _failures;

    public static void Ensure()
    {
        if (_go != null) return;
        if (_tried && _failures > 3) return;
        _tried = true;
        try
        {
            if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<FlowOverlayBehaviour>())
                Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<FlowOverlayBehaviour>();
            _go = new GameObject("CanEmAllCoop.FlowOverlay");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.AddComponent<FlowOverlayBehaviour>();
        }
        catch (Exception e)
        {
            _failures++;
            _go = null;
            Log.Error("Flow: could not create the on-screen status overlay (the flow still works, only the hints are missing)", e);
        }
    }

    public static void DrawFailed(Exception e)
    {
        _failures++;
        if (_failures <= 3) Log.Error("Flow: overlay draw failed", e);
    }
}
