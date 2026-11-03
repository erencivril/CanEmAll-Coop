using System;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.UI;
using Net = CanEmAllCoop.Net.Net;

/// <summary>Injected MonoBehaviour that drives everything: network pump, module updates, IMGUI menu.</summary>
public class CoopBehaviour : MonoBehaviour
{
    public CoopBehaviour(IntPtr ptr) : base(ptr) { }

    bool _loggedUpdate, _loggedGui, _loggedGuiFail;

    void Update()
    {
        try
        {
            if (!_loggedUpdate) { _loggedUpdate = true; Log.Info("CoopBehaviour: first Update"); }
            CoopMenu.HandleHotkey();
            CoopMenu.TickLaunchJoin();
            ModuleHost.Update();
        }
        catch (Exception e) { Log.Error("CoopBehaviour.Update failed", e); }
    }

    void LateUpdate()
    {
        try { CoopMenu.KeepCursorFree(); }
        catch (Exception) { /* cursor handling must never break a frame */ }
    }

    void OnGUI()
    {
        try
        {
            if (!_loggedGui) { _loggedGui = true; Log.Info("CoopBehaviour: first OnGUI"); }
            CoopMenu.Draw();
        }
        catch (Exception e)
        {
            // OnGUI runs several times per frame: log the first failure only, or one broken control floods the log
            if (_loggedGuiFail) return;
            _loggedGuiFail = true;
            Log.Error("CoopMenu.Draw failed (further failures are not logged)", e);
        }
    }

    void OnApplicationQuit()
    {
        try { Net.Quit(); }
        catch (Exception) { }
    }
}
