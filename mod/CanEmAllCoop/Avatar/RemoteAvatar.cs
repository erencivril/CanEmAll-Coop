using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace CanEmAllCoop.Avatar;

/// <summary>
/// State and scene objects of one remote player. Network side (buffer, clock) lives as long as the peer; the
/// GameObject is built lazily once a local Player exists and rebuilt whenever Unity destroyed it (scene change).
/// </summary>
internal sealed class RemoteAvatar
{
    sealed class Part
    {
        public Renderer R;
        public Color Base;
        public bool Tinted; // greyed while down
    }

    static readonly string[] FallbackShaders =
    {
        "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Lit", "Unlit/Color", "Standard",
        "Sprites/Default", "Hidden/Internal-Colored",
    };
    static Shader _fallbackShader;
    static bool _fallbackSearched;

    public readonly Peer Peer;
    public readonly PoseBuffer Buffer = new();
    public readonly ClockSync Clock = new();
    public double LastArrival;
    public Vector3 LastPosition;
    public bool Down;
    public byte Weapon = WeaponCodes.Unknown;

    GameObject _root;
    Transform _head, _visual, _hand, _carry;
    GameObject _weaponBlock, _fist, _flashBall;
    Light _flashLight;
    Renderer _flashRenderer, _blockRenderer;
    Material _flashMat;
    Collider _collider;
    Rigidbody _rb;
    readonly List<Part> _parts = new();

    float _headY, _lowerOffset, _tagHeight;
    Vector3 _handBase;
    bool _placed;
    bool _seqInit;
    byte _lastSeq;
    byte _shownWeapon = 0xFE;
    bool _shownDown;
    float _downBlend;
    float _flashTimer;
    double _lastFxTime = -10;
    float _swing;
    float _stepTimer;

    public RemoteAvatar(Peer peer) { Peer = peer; }

    public bool IsBuilt => _root != null;
    public Collider Collider => _collider;
    public bool Visible => _root != null && _root.activeSelf;

    public void ResetNet()
    {
        Buffer.Clear();
        Clock.Reset();
        LastArrival = 0;
        _seqInit = false;
    }

    // ---------------------------------------------------------------- build

    public bool EnsureBuilt(Player lp)
    {
        if (_root != null) return true;
        if (lp == null) return false;
        try
        {
            Build(lp);
            return _root != null;
        }
        catch (Exception e)
        {
            Log.Error($"Avatar: building {Peer} failed", e);
            Destroy();
            return false;
        }
    }

    void Build(Player lp)
    {
        _parts.Clear();
        _placed = false;
        _shownWeapon = 0xFE;
        _shownDown = false;
        _downBlend = 0f;

        var cap = lp.GetComponent<CapsuleCollider>();
        if (cap == null) cap = lp.GetComponentInChildren<CapsuleCollider>();
        float h = 2f, r = 0.4f, dir = 1;
        var center = Vector3.zero;
        if (cap != null) { h = cap.height; r = cap.radius; center = cap.center; dir = cap.direction; }
        if (h < 0.5f) h = 2f;
        if (r < 0.05f) r = 0.4f;

        int layer = lp.gameObject.layer;
        int visLayer = layer;
        var cam = lp.mainCamera;
        if (cam != null && (cam.cullingMask & (1 << layer)) == 0) visLayer = 0; // player layer not rendered: use Default

        _root = new GameObject("CoopAvatar_" + Peer.Id);
        _root.layer = layer;

        _collider = null;
        var col = _root.AddComponent<CapsuleCollider>();
        col.height = h; col.radius = r; col.center = center; col.direction = (int)dir;
        col.isTrigger = false;
        _collider = col;

        _rb = _root.AddComponent<Rigidbody>();
        _rb.isKinematic = true;
        _rb.useGravity = false;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        var tint = AvatarStyle.Tint(Peer.Index);
        var tintColor = new Color(tint.R, tint.G, tint.B, 1f);

        float bottom = center.y - h * 0.5f;
        float top = center.y + h * 0.5f;
        _headY = top - 0.3f;
        if (lp.mainCameraParent != null)
        {
            float y = lp.transform.InverseTransformPoint(lp.mainCameraParent.position).y;
            _headY = Mathf.Clamp(y, bottom + h * 0.6f, top + 0.3f);
        }
        _lowerOffset = Mathf.Max(0f, h * 0.5f - r - 0.05f);

        _visual = NewChild("Visual", _root.transform, visLayer).transform;

        float headD = Mathf.Max(0.35f, r * 1.7f);
        float bodyBottom = bottom;
        float bodyTop = _headY - headD * 0.35f;
        float bodyH = Mathf.Max(r * 2f, bodyTop - bodyBottom);
        Prim(PrimitiveType.Capsule, "Body", _visual, visLayer,
            new Vector3(center.x, bodyBottom + bodyH * 0.5f, center.z), new Vector3(r * 2f, bodyH * 0.5f, r * 2f), tintColor, true);

        var headPivot = NewChild("Head", _visual, visLayer).transform;
        headPivot.localPosition = new Vector3(center.x, _headY, center.z);
        _head = headPivot;
        var skin = new Color(0.96f, 0.80f, 0.66f, 1f);
        Prim(PrimitiveType.Sphere, "Skull", headPivot, visLayer, Vector3.zero, Vector3.one * headD, skin, true);
        var eye = new Color(0.05f, 0.05f, 0.05f, 1f);
        float ey = headD * 0.05f, ez = headD * 0.46f, ex = headD * 0.2f, es = headD * 0.16f;
        Prim(PrimitiveType.Cube, "EyeL", headPivot, visLayer, new Vector3(-ex, ey, ez), new Vector3(es, es * 1.3f, es * 0.6f), eye, false);
        Prim(PrimitiveType.Cube, "EyeR", headPivot, visLayer, new Vector3(ex, ey, ez), new Vector3(es, es * 1.3f, es * 0.6f), eye, false);
        var hat = new Color(1f, 0.85f, 0.1f, 1f);
        Prim(PrimitiveType.Sphere, "HatDome", headPivot, visLayer, new Vector3(0f, headD * 0.28f, 0f), new Vector3(headD * 1.02f, headD * 0.62f, headD * 1.02f), hat, false);
        Prim(PrimitiveType.Cube, "HatBrim", headPivot, visLayer, new Vector3(0f, headD * 0.2f, headD * 0.5f), new Vector3(headD * 0.75f, headD * 0.06f, headD * 0.4f), hat, false);

        _tagHeight = _headY + headD * 0.9f;

        // right hand: weapon block / fist, muzzle flash
        _handBase = new Vector3(center.x + r * 0.9f, _headY - 0.5f, center.z + r * 0.5f);
        _hand = NewChild("Hand", _visual, visLayer).transform;
        _hand.localPosition = _handBase;
        _weaponBlock = Prim(PrimitiveType.Cube, "Weapon", _hand, visLayer, new Vector3(0f, 0f, 0.25f), new Vector3(0.09f, 0.13f, 0.5f), new Color(0.15f, 0.15f, 0.17f, 1f), false);
        _blockRenderer = _weaponBlock.GetComponent<Renderer>();
        _fist = Prim(PrimitiveType.Sphere, "Fist", _hand, visLayer, new Vector3(0f, 0f, 0.1f), Vector3.one * 0.16f, skin, false);
        _flashBall = Prim(PrimitiveType.Sphere, "Flash", _hand, visLayer, new Vector3(0f, 0f, 0.62f), Vector3.one * 0.2f, new Color(1f, 0.75f, 0.2f, 1f), false);
        _flashRenderer = _flashBall.GetComponent<Renderer>();
        _flashMat = _flashRenderer.material;
        _flashBall.SetActive(false);
        var lightGo = NewChild("FlashLight", _hand, visLayer);
        lightGo.transform.localPosition = new Vector3(0f, 0f, 0.7f);
        _flashLight = lightGo.AddComponent<Light>();
        _flashLight.type = LightType.Point;
        _flashLight.range = 6f;
        _flashLight.intensity = 3f;
        _flashLight.color = new Color(1f, 0.75f, 0.3f, 1f);
        _flashLight.shadows = LightShadows.None;
        _flashLight.enabled = false;

        _carry = Prim(PrimitiveType.Cube, "Carry", _visual, visLayer,
            new Vector3(center.x, _headY - 0.45f, center.z + r + 0.35f), new Vector3(0.4f, 0.28f, 0.28f), tintColor * 0.7f, false).transform;
        _carry.gameObject.SetActive(false);

        _root.SetActive(false); // shown by Apply once a pose placed it (avoids a frame at the origin)
    }

    static GameObject NewChild(string name, Transform parent, int layer)
    {
        var g = new GameObject(name);
        g.layer = layer;
        g.transform.SetParent(parent, false);
        return g;
    }

    GameObject Prim(PrimitiveType type, string name, Transform parent, int layer, Vector3 localPos, Vector3 localScale, Color color, bool tinted)
    {
        var g = GameObject.CreatePrimitive(type);
        g.name = name;
        var c = g.GetComponent<Collider>();
        if (c != null) UnityEngine.Object.DestroyImmediate(c); // visuals must not take part in physics
        g.layer = layer;
        var t = g.transform;
        t.SetParent(parent, false);
        t.localPosition = localPos;
        t.localScale = localScale;
        var rend = g.GetComponent<Renderer>();
        Paint(rend, color);
        _parts.Add(new Part { R = rend, Base = color, Tinted = tinted });
        return g;
    }

    static void Paint(Renderer rend, Color color)
    {
        if (rend == null) return;
        var m = rend.material;
        if (m == null || m.shader == null || m.shader.name.StartsWith("Hidden/InternalError", StringComparison.Ordinal))
        {
            var sh = FindFallbackShader();
            if (sh != null) { m = new Material(sh); rend.material = m; }
        }
        if (m != null) m.color = color;
    }

    static Shader FindFallbackShader()
    {
        if (_fallbackSearched) return _fallbackShader;
        _fallbackSearched = true;
        foreach (var n in FallbackShaders)
        {
            var s = Shader.Find(n);
            if (s != null) { _fallbackShader = s; break; }
        }
        return _fallbackShader;
    }

    public void Destroy()
    {
        try { if (_root != null) UnityEngine.Object.Destroy(_root); }
        catch (Exception) { }
        _root = null; _head = null; _visual = null; _hand = null; _carry = null;
        _weaponBlock = null; _fist = null; _flashBall = null; _flashLight = null;
        _flashRenderer = null; _blockRenderer = null; _flashMat = null;
        _collider = null; _rb = null;
        _parts.Clear();
        _placed = false;
    }

    public void SetVisible(bool visible)
    {
        if (_root != null && _root.activeSelf != visible) _root.SetActive(visible);
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>Tag anchor above the head, or false when not shown.</summary>
    public bool TryGetTagPosition(out Vector3 world)
    {
        world = default;
        if (_root == null || !_root.activeSelf) return false;
        world = _root.transform.TransformPoint(new Vector3(0f, _tagHeight, 0f));
        return true;
    }

    public void Apply(Player lp, in AvPose s, float dt)
    {
        if (_root == null) return;
        var pos = new Vector3(s.X, s.Y, s.Z);
        LastPosition = pos;
        Weapon = s.Weapon;
        Down = s.Has(PoseFlags.Down);

        var rot = Quaternion.Euler(0f, s.Yaw, 0f);
        var tr = _root.transform;
        if (!_placed || (tr.position - pos).sqrMagnitude > 100f)
        {
            tr.position = pos;
            tr.rotation = rot;
            _placed = true;
        }
        else if (_rb != null)
        {
            _rb.MovePosition(pos);
            _rb.MoveRotation(rot);
        }
        else
        {
            tr.position = pos;
            tr.rotation = rot;
        }

        if (!_root.activeSelf) _root.SetActive(true);
        if (_head != null) _head.localRotation = Quaternion.Euler(Mathf.Clamp(s.Pitch, -90f, 90f), 0f, 0f);

        UpdateDown(Down, dt);
        UpdateWeaponLook(s.Weapon);
        if (_carry != null) _carry.gameObject.SetActive(s.HeldId != 0);

        // shots / swings (fire sequence is part of the interpolated discrete state, so it fires in sync with the motion)
        if (!_seqInit) { _lastSeq = s.FireSeq; _seqInit = true; }
        else
        {
            int shots = FireSeq.Delta(_lastSeq, s.FireSeq);
            _lastSeq = s.FireSeq;
            if (shots > 0 && !Down) Shoot(s.Weapon, pos);
        }

        UpdateContinuousFx(s, dt);
        UpdateHand(s.Weapon, dt);
        UpdateFootsteps(lp, s, pos, dt);
    }

    void UpdateDown(bool down, float dt)
    {
        _downBlend = AvatarStyle.Blend(_downBlend, down, dt, 0.3f);
        if (_visual != null)
        {
            _visual.localRotation = Quaternion.Euler(80f * _downBlend, 0f, 0f);
            _visual.localPosition = new Vector3(0f, -_lowerOffset * _downBlend, 0f);
        }
        if (down == _shownDown) return;
        _shownDown = down;
        foreach (var p in _parts)
        {
            if (!p.Tinted || p.R == null) continue;
            var m = p.R.material;
            if (m != null) m.color = down ? Color.Lerp(p.Base, new Color(0.45f, 0.45f, 0.45f, 1f), 0.75f) : p.Base;
        }
    }

    void UpdateWeaponLook(byte code)
    {
        if (code == _shownWeapon || _weaponBlock == null) return;
        _shownWeapon = code;
        bool fists = code == WeaponCodes.Fists || code == WeaponCodes.Unknown;
        _weaponBlock.SetActive(!fists);
        if (_fist != null) _fist.SetActive(fists);
        if (fists) return;

        var t = _weaponBlock.transform;
        var color = new Color(0.15f, 0.15f, 0.17f, 1f);
        var scale = new Vector3(0.09f, 0.13f, 0.5f);
        switch (code)
        {
            case WeaponCodes.Smg: case WeaponCodes.Ak47: scale = new Vector3(0.09f, 0.14f, 0.7f); break;
            case WeaponCodes.Shotgun: scale = new Vector3(0.1f, 0.12f, 0.85f); break;
            case WeaponCodes.Rocket: scale = new Vector3(0.2f, 0.2f, 0.9f); color = new Color(0.25f, 0.3f, 0.2f, 1f); break;
            case WeaponCodes.Flame: scale = new Vector3(0.12f, 0.16f, 0.6f); color = new Color(0.7f, 0.15f, 0.1f, 1f); break;
            case WeaponCodes.Chainsaw: scale = new Vector3(0.1f, 0.16f, 0.7f); color = new Color(0.95f, 0.5f, 0.1f, 1f); break;
            case WeaponCodes.Bat: scale = new Vector3(0.08f, 0.08f, 0.9f); color = new Color(0.55f, 0.38f, 0.2f, 1f); break;
            case WeaponCodes.Knife: scale = new Vector3(0.03f, 0.1f, 0.4f); color = new Color(0.75f, 0.78f, 0.8f, 1f); break;
            case WeaponCodes.Vacuum: scale = new Vector3(0.22f, 0.22f, 0.55f); color = new Color(0.2f, 0.7f, 0.85f, 1f); break;
        }
        t.localScale = scale;
        t.localPosition = new Vector3(0f, 0f, scale.z * 0.5f);
        if (_flashBall != null) _flashBall.transform.localPosition = new Vector3(0f, 0f, scale.z + 0.1f);
        if (_flashLight != null) _flashLight.transform.localPosition = new Vector3(0f, 0f, scale.z + 0.2f);
        if (_blockRenderer != null && _blockRenderer.material != null) _blockRenderer.material.color = color;
    }

    void Shoot(byte code, Vector3 pos)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now - _lastFxTime < 0.04) return; // never machine-gun the cosmetics
        _lastFxTime = now;

        if (WeaponCodes.IsMelee(code)) _swing = 1f;
        else
        {
            _swing = 0.35f;
            _flashTimer = 0.06f;
            SetFlash(true, new Color(1f, 0.75f, 0.2f, 1f), 3f);
        }
        AvatarWeapons.PlayShot(code, pos + Vector3.up * _headY);
    }

    void UpdateContinuousFx(in AvPose s, float dt)
    {
        if (_flashTimer > 0f)
        {
            _flashTimer -= dt;
            if (_flashTimer <= 0f) SetFlash(false, default, 0f);
            return;
        }
        bool vac = s.Has(PoseFlags.Vacuum) && s.Weapon == WeaponCodes.Vacuum;
        bool flame = s.Has(PoseFlags.Firing) && s.Weapon == WeaponCodes.Flame;
        if (vac) SetFlash(true, new Color(0.3f, 0.85f, 1f, 1f), 1.2f);
        else if (flame) SetFlash(true, new Color(1f, UnityEngine.Random.Range(0.35f, 0.7f), 0.1f, 1f), UnityEngine.Random.Range(1.5f, 3.5f));
        else SetFlash(false, default, 0f);
    }

    void SetFlash(bool on, Color color, float intensity)
    {
        if (_flashBall != null && _flashBall.activeSelf != on) _flashBall.SetActive(on);
        if (_flashLight != null)
        {
            if (_flashLight.enabled != on) _flashLight.enabled = on;
            if (on) { _flashLight.color = color; _flashLight.intensity = intensity; }
        }
        if (on && _flashMat != null) _flashMat.color = color;
    }

    void UpdateHand(byte code, float dt)
    {
        if (_hand == null) return;
        if (_swing > 0f) _swing = Mathf.Max(0f, _swing - dt / 0.2f);
        bool melee = WeaponCodes.IsMelee(code);
        _hand.localPosition = _handBase + (melee ? new Vector3(0f, -0.15f * _swing, 0.3f * _swing) : new Vector3(0f, 0.04f * _swing, -0.12f * _swing));
        _hand.localRotation = Quaternion.Euler(melee ? -70f * _swing : -8f * _swing, 0f, 0f);
    }

    void UpdateFootsteps(Player lp, in AvPose s, Vector3 pos, float dt)
    {
        float speed = Mathf.Sqrt(s.Vx * s.Vx + s.Vz * s.Vz);
        if (lp == null || Down || !s.Has(PoseFlags.Grounded) || speed < 1.5f) { _stepTimer = 0f; return; }
        float interval = lp.footStepInterval;
        if (interval < 0.05f) interval = 0.5f;
        if (s.Has(PoseFlags.Running)) interval /= 1.5f;
        _stepTimer += dt;
        if (_stepTimer < interval) return;
        _stepTimer = 0f;
        AvatarWeapons.PlayFootstep(lp, pos);
    }
}
