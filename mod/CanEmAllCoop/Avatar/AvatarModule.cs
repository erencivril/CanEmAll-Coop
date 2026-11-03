using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using CanEmAllCoop.UI;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace CanEmAllCoop.Avatar;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Remote players: one primitive-built avatar per peer, driven by a 20 Hz unreliable pose stream that is
/// interpolated ~100 ms behind. Offline the module only counts a byte in one postfix and otherwise does nothing.
/// </summary>
public sealed class AvatarModule : ICoopModule
{
    public string Name => "Avatar";

    /// <summary>Render delay of remote poses in seconds.</summary>
    public const double InterpDelay = 0.1;
    const double StaleSeconds = 3.0;

    /// <summary>
    /// Integration hook for flow: return true while the LOCAL player is down (waiting for respawn). When null the
    /// avatar uses GameManager.IsSuiciding.
    /// </summary>
    public static Func<bool> LocalDownProvider;

    static readonly Dictionary<ulong, RemoteAvatar> ById = new();
    static readonly List<RemoteAvatar> All = new();
    static readonly PoseSendGate Gate = new();

    static byte _fireSeq;
    static byte _localWeapon = WeaponCodes.Unknown;
    static float _lastPunchTime;
    static AvPose _lastSent;
    static bool _hasLastSent;
    static long _sent, _recv;
    static AvatarBehaviour _tags;
    static bool _tagsFailed;
    static IntPtr _lastPlayerPtr;
    static bool _ignoresDirty;
    static int _tagErrors;

    public void Init(Harmony harmony)
    {
        try { Net.Register(PoseCodec.MsgPose, OnPose); }
        catch (Exception e) { Log.Error("Avatar: could not register the pose message", e); return; }

        try { PlayerPositions.Register(RemotePositions); }
        catch (Exception e) { Log.Error("Avatar: PlayerPositions.Register failed", e); }

        try { ClassInjector.RegisterTypeInIl2Cpp<AvatarBehaviour>(); }
        catch (Exception e)
        {
            _tagsFailed = true;
            Log.Error("Avatar: AvatarBehaviour could not be registered (no name tags)", e);
        }

        // Firearms (pistol, SMG, AK, shotgun, rocket, chainsaw) call Player.RecoilCamera once per shot.
        try
        {
            var m = AccessTools.Method(typeof(Player), "RecoilCamera");
            if (m == null) Log.Error("Avatar: Player.RecoilCamera not found, remote muzzle flashes disabled");
            else harmony.Patch(m, postfix: new HarmonyMethod(typeof(AvatarModule), nameof(RecoilPostfix)));
        }
        catch (Exception e) { Log.Error("Avatar: patching Player.RecoilCamera failed", e); }
    }

    // pure counter; offline it only costs an IsOnline check
    static void RecoilPostfix()
    {
        if (!Net.IsOnline || _localWeapon == WeaponCodes.Chainsaw) return; // chainsaw recoils every frame: Firing flag instead
        unchecked { _fireSeq++; }
    }

    // ---------------------------------------------------------------- lifecycle

    public void OnSessionStart()
    {
        DestroyAll();
        Gate.Reset();
        _hasLastSent = false;
        _sent = _recv = 0;
    }

    public void OnSessionEnd()
    {
        DestroyAll();
        _hasLastSent = false;
    }

    public void OnPeerJoined(Peer peer) { /* avatars appear with the first pose; nothing to push */ }

    public void OnPeerLeft(Peer peer)
    {
        if (peer == null || !ById.TryGetValue(peer.Id, out var a)) return;
        a.Destroy();
        ById.Remove(peer.Id);
        All.Remove(a);
    }

    static void DestroyAll()
    {
        foreach (var a in All) a.Destroy();
        All.Clear();
        ById.Clear();
    }

    // ---------------------------------------------------------------- receive

    static void OnPose(Peer from, NetReader r)
    {
        if (from == null || !Net.IsOnline || from.Id == Net.LocalId) return;
        var pose = PoseCodec.Read(r);
        _recv++;

        if (!ById.TryGetValue(from.Id, out var a))
        {
            a = new RemoteAvatar(from);
            ById[from.Id] = a;
            All.Add(a);
        }
        double now = Time.realtimeSinceStartupAsDouble;
        a.Clock.Observe(now, pose.Time);
        a.Buffer.Add(pose);
        a.LastArrival = now;
    }

    // ---------------------------------------------------------------- update

    public void OnUpdate()
    {
        if (!Net.IsOnline)
        {
            if (All.Count > 0) DestroyAll(); // defensive: SessionEnded should have done it
            return;
        }

        EnsureTagBehaviour();

        var lp = Player.Instance;
        bool inWorld = lp != null;
        double now = Time.realtimeSinceStartupAsDouble;

        if (inWorld)
        {
            TrackLocal(lp);
            SendPose(lp, now);
        }
        UpdateRemotes(inWorld ? lp : null, now);
    }

    static void TrackLocal(Player lp)
    {
        var inv = Inventory.Instance;
        _localWeapon = AvatarWeapons.LocalCode(inv);
        if (inv == null) return;

        // punches: Inventory.Punch stamps lastPunchTime on every swing; melee weapons have no common hook
        float lpt = inv.lastPunchTime;
        if (_localWeapon == WeaponCodes.Fists && lpt > _lastPunchTime + 0.0001f) unchecked { _fireSeq++; }
        _lastPunchTime = lpt;

        if ((_localWeapon == WeaponCodes.Bat || _localWeapon == WeaponCodes.Knife) && CanInput() && Input.GetMouseButtonDown(0))
            unchecked { _fireSeq++; }
    }

    static bool CanInput()
    {
        var gm = GameManager.Instance;
        return gm != null && gm.CanPlayerInput;
    }

    static void SendPose(Player lp, double now)
    {
        if (Net.Peers.Count <= 1) return; // nobody to send to

        var p = ReadLocalPose(lp, now);
        bool urgent = _hasLastSent &&
            (p.FireSeq != _lastSent.FireSeq || p.Weapon != _lastSent.Weapon || p.Flags != _lastSent.Flags);
        if (!Gate.Due(now, CoopConfig.PoseRateHz, urgent)) return;

        _lastSent = p;
        _hasLastSent = true;
        _sent++;
        // a client's Broadcast is relayed by the host to every other client, so no explicit relay is needed
        Net.Broadcast(PoseCodec.MsgPose, w => PoseCodec.Write(w, p), reliable: false);
    }

    static AvPose ReadLocalPose(Player lp, double now)
    {
        var p = new AvPose { Time = now };
        var pos = lp.transform.position;
        p.X = pos.x; p.Y = pos.y; p.Z = pos.z;
        p.Yaw = lp.rotY;
        p.Pitch = lp.rotX;
        var rb = lp.rb;
        if (rb != null)
        {
            var v = rb.linearVelocity;
            p.Vx = v.x; p.Vy = v.y; p.Vz = v.z;
        }

        byte flags = 0;
        if (lp.IsGrounded) flags |= PoseFlags.Grounded;
        if (SafeCanRun(lp)) flags |= PoseFlags.Running;

        var vac = Vacuum.Instance;
        if (_localWeapon == WeaponCodes.Vacuum && vac != null && vac.state == Vacuum.VacuumState.Sucking) flags |= PoseFlags.Vacuum;
        if (WeaponCodes.IsContinuous(_localWeapon) && CanInput() && Input.GetMouseButton(0)) flags |= PoseFlags.Firing;
        if (IsLocalDown()) flags |= PoseFlags.Down;

        p.Flags = flags;
        p.Weapon = _localWeapon;
        p.FireSeq = _fireSeq;

        var held = lp.heldEnemy;
        p.HeldId = held != null ? NetIds.IdOf(held) : 0u;
        return p;
    }

    static bool SafeCanRun(Player lp)
    {
        try { return lp.CanRun(); }
        catch (Exception) { return false; }
    }

    static bool IsLocalDown()
    {
        try
        {
            if (LocalDownProvider != null) return LocalDownProvider();
            var gm = GameManager.Instance;
            return gm != null && gm.IsSuiciding;
        }
        catch (Exception) { return false; }
    }

    static void UpdateRemotes(Player lp, double now)
    {
        float dt = Time.deltaTime;

        if (lp == null)
        {
            // menu / loading: no world to show avatars in
            foreach (var a in All) if (a.IsBuilt) a.Destroy();
            _lastPlayerPtr = IntPtr.Zero;
            return;
        }

        if (lp.Pointer != _lastPlayerPtr)
        {
            _lastPlayerPtr = lp.Pointer;
            _ignoresDirty = true;
        }

        foreach (var a in All)
        {
            if (a.Buffer.IsEmpty) continue;

            bool stale = now - a.LastArrival > StaleSeconds;
            if (stale)
            {
                a.SetVisible(false);
                continue;
            }

            bool wasBuilt = a.IsBuilt;
            if (!a.EnsureBuilt(lp)) continue;
            if (!wasBuilt) _ignoresDirty = true;

            double renderTime = a.Clock.ToSender(now) - InterpDelay;
            if (a.Buffer.Sample(renderTime, out var s)) a.Apply(lp, s, dt);
        }

        if (_ignoresDirty) { ApplyIgnores(lp); _ignoresDirty = false; }
    }

    // avatars never block the local player or each other (elevator, doors)
    static void ApplyIgnores(Player lp)
    {
        try
        {
            var lc = lp.GetComponent<Collider>();
            if (lc == null) lc = lp.GetComponentInChildren<Collider>();
            for (int i = 0; i < All.Count; i++)
            {
                var ci = All[i].Collider;
                if (ci == null) continue;
                if (lc != null) Physics.IgnoreCollision(ci, lc, true);
                for (int j = i + 1; j < All.Count; j++)
                {
                    var cj = All[j].Collider;
                    if (cj != null) Physics.IgnoreCollision(ci, cj, true);
                }
            }
        }
        catch (Exception e) { Log.Error("Avatar: IgnoreCollision failed", e); }
    }

    static void EnsureTagBehaviour()
    {
        if (_tagsFailed || _tags != null) return;
        try
        {
            var go = new GameObject("CoopAvatarTags");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _tags = go.AddComponent<AvatarBehaviour>();
            if (_tags == null) { _tagsFailed = true; Log.Error("Avatar: AddComponent<AvatarBehaviour> returned null (no name tags)"); }
        }
        catch (Exception e)
        {
            _tagsFailed = true;
            Log.Error("Avatar: could not create the name tag behaviour", e);
        }
    }

    // ---------------------------------------------------------------- name tags (called from AvatarBehaviour.OnGUI)

    internal static void DrawTags()
    {
        if (!Net.IsOnline || All.Count == 0) return;
        var lp = Player.Instance;
        if (lp == null) return;
        var cam = lp.mainCamera;
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        // Pixel sizes, no GUI.matrix: the font is chosen per tag. Base size follows the screen height (36 px at 1440p),
        // the distance factor shrinks far tags but never below a readable minimum.
        float baseFont = Screen.height / 40f;
        float minFont = Mathf.Max(16f, Screen.height / 80f);
        try
        {
            foreach (var a in All)
            {
                if (!a.TryGetTagPosition(out var world)) continue;
                var sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0.1f || sp.z > 90f) continue;

                int font = Mathf.RoundToInt(Mathf.Max(minFont, baseFont * Mathf.Clamp(12f / sp.z, 0.55f, 1.25f)));
                string text = AvatarStyle.TagText(a.Peer.Name, a.Weapon, a.Down);
                int lines = 0, longest = 0;
                foreach (var line in text.Split('\n')) { lines++; longest = Math.Max(longest, line.Length); }
                float w = longest * font * 0.62f + font * 1.2f, h = lines * font * 1.3f + font * 0.6f;
                // keep the box on screen: a player standing at the edge of the view still gets a readable tag
                float x = Mathf.Clamp(sp.x - w * 0.5f, 4f, Mathf.Max(4f, Screen.width - w - 4f));
                float y = Mathf.Clamp(Screen.height - sp.y - h, 4f, Mathf.Max(4f, Screen.height - h - 4f));

                var t = AvatarStyle.Tint(a.Peer.Index);
                var tagColor = new Color(Mathf.Lerp(t.R, 1f, 0.85f), Mathf.Lerp(t.G, 1f, 0.85f), Mathf.Lerp(t.B, 1f, 0.85f), 1f);
                OpaqueBox.Draw(new Rect(x, y, w, h), text, font, new Color(0f, 0f, 0f, 1f), tagColor, bold: true, wrap: false);
            }
        }
        catch (Exception e)
        {
            if (++_tagErrors <= 3) Log.Error("Avatar: DrawTags failed", e);
        }
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Positions of the remote avatars that sent a pose recently (registered with <see cref="PlayerPositions"/>).</summary>
    static IEnumerable<Vector3> RemotePositions()
    {
        var list = new List<Vector3>(3);
        if (!Net.IsOnline) return list;
        double now = Time.realtimeSinceStartupAsDouble;
        foreach (var a in All)
            if (!a.Buffer.IsEmpty && now - a.LastArrival <= StaleSeconds) list.Add(a.LastPosition);
        return list;
    }

    public string DebugState()
    {
        int built = 0;
        foreach (var a in All) if (a.IsBuilt) built++;
        return $"remote={All.Count} built={built} sent={_sent} recv={_recv} weapon={WeaponCodes.Label(_localWeapon)} fireSeq={_fireSeq}";
    }
}
