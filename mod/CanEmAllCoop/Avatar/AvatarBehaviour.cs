using System;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Avatar;

/// <summary>
/// Injected MonoBehaviour whose only job is OnGUI: name tags are IMGUI labels projected with
/// Camera.WorldToScreenPoint (no TextMesh / TMP font assets needed).
/// </summary>
public class AvatarBehaviour : MonoBehaviour
{
    public AvatarBehaviour(IntPtr ptr) : base(ptr) { }

    int _errors;

    void OnGUI()
    {
        try
        {
            var e = Event.current;
            if (e != null && e.type != EventType.Repaint) return;
            AvatarModule.DrawTags();
        }
        catch (Exception ex)
        {
            if (++_errors <= 3) Log.Error("Avatar tag drawing failed", ex);
        }
    }
}
