using System;
using System.Collections.Generic;
using System.Globalization;
using CanEmAllCoop.Cans;
using CanEmAllCoop.Core;
using CanEmAllCoop.Economy;
using CanEmAllCoop.Enemies;
using CanEmAllCoop.Flow;
using UnityEngine;

namespace CanEmAllCoop.Debug;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Scripted scenarios for the two-instance runs (switches are parsed and documented in <see cref="DebugModule"/>). Each driver calls
/// the same game methods the input ends in (Player.GrabDeadBody / ThrowHeldBody, Inventory.PunchDelayed, Vacuum.Suck / Throw) and logs
/// numbers that say whether the result is right. Inactive unless its launch switch is given.
/// </summary>
internal sealed class DebugDrivers
{
    public static float GrabBodySecs, PunchEvery, VacuumEvery, FacingEvery, ColorsEvery, HoldWatchEvery;
    public static bool NoCarry;

    public bool Active => GrabBodySecs > 0f || PunchEvery > 0f || VacuumEvery > 0f || FacingEvery > 0f || ColorsEvery > 0f || HoldWatchEvery > 0f;

    readonly LogGate _fail = new(3, 30f);

    public static bool Parse(string name, string value)
    {
        switch (name)
        {
            case "--coop-debug-grabbody": GrabBodySecs = Secs(value, 6f); return true;
            case "--coop-debug-punch": PunchEvery = Secs(value, 3f); return true;
            case "--coop-debug-vacuum": VacuumEvery = Secs(value, 6f); return true;
            case "--coop-debug-facing": FacingEvery = Secs(value, 5f); return true;
            case "--coop-debug-skillcolors": ColorsEvery = Secs(value, 1f); return true;
            case "--coop-debug-holdwatch": HoldWatchEvery = Secs(value, 0.5f); return true;
            case "--coop-debug-nocarry": NoCarry = true; return true;
        }
        return false;
    }

    static float Secs(string v, float def) =>
        float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0f ? s : def;

    public void Tick(float now)
    {
        if (GrabBodySecs > 0f) Run("grabbody", () => GrabBody(now));
        if (PunchEvery > 0f) Run("punch", () => Punch(now));
        if (VacuumEvery > 0f) Run("vacuum", () => VacuumDrive(now));
        if (FacingEvery > 0f) Run("facing", () => Facing(now));
        if (ColorsEvery > 0f) Run("skillcolors", () => SkillColors(now));
        if (HoldWatchEvery > 0f) Run("holdwatch", () => HoldWatch(now));
    }

    void Run(string what, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            if (_fail.Allow(what, Time.unscaledTime, out var n)) Log.Error($"[DEBUG] {what} driver failed{(n > 0 ? $" (+{n} more)" : "")}", e);
        }
    }

    static bool InDay => FlowCore.GameStarted && FlowCore.Phase == FlowPhase.Day && Player.Instance != null && Net.IsOnline;

    // ------------------------------------------------------------------------------------------------ helpers

    static string V(Vector3 v) => $"({v.x:0.00},{v.y:0.00},{v.z:0.00})";
    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // Player.RotateCamera sets mainCameraParent.localRotation = Euler(rotX, rotY, 0): yaw and pitch both live on the camera pivot, the body does not turn.
    static void SetAim(Player pl, float yaw, float pitch)
    {
        pl.rotY = yaw;
        pl.rotX = pitch;
        pl.transform.rotation = Quaternion.identity;
        var cp = pl.mainCameraParent;
        if (cp != null) cp.localRotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    /// <summary>Turns the camera pivot at <paramref name="target"/>. Returns the angle between the camera forward and the wanted direction afterwards.</summary>
    static float AimAt(Player pl, Vector3 target)
    {
        var cp = pl.mainCameraParent;
        var d = target - cp.position;
        float flat = Mathf.Sqrt(d.x * d.x + d.z * d.z);
        float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        float pitch = -Mathf.Atan2(d.y, flat) * Mathf.Rad2Deg;
        SetAim(pl, yaw, pitch);
        return Vector3.Angle(cp.forward, d);
    }

    static void PlaceAt(Player pl, Vector3 pos)
    {
        var rb = pl.rb;
        if (rb != null) { rb.position = pos; rb.linearVelocity = Vector3.zero; }
        pl.transform.position = pos;
    }

    static float _playerY = float.NaN;
    /// <summary>Height of the player's pivot while standing on the hall floor (read once while the player is grounded).</summary>
    static float PlayerY(Player pl)
    {
        if (float.IsNaN(_playerY) && pl.IsGrounded) _playerY = pl.transform.position.y;
        return float.IsNaN(_playerY) ? 0.6f : _playerY;
    }

    /// <summary>Longest free run from <paramref name="from"/> (chest height) along <paramref name="dir"/>, capped at <paramref name="max"/>.</summary>
    static float FreeRun(Vector3 from, Vector3 dir, float max)
    {
        return Physics.Raycast(from, dir, out var hit, max) ? hit.distance : max;
    }

    static readonly Vector3[] Dirs8 =
    {
        new Vector3(1, 0, 0), new Vector3(0.7071f, 0, 0.7071f), new Vector3(0, 0, 1), new Vector3(-0.7071f, 0, 0.7071f),
        new Vector3(-1, 0, 0), new Vector3(-0.7071f, 0, -0.7071f), new Vector3(0, 0, -1), new Vector3(0.7071f, 0, -0.7071f),
    };

    static Vector3 BestDirection(Vector3 around, float max, out float run)
    {
        var from = around + Vector3.up * 1.0f;
        Vector3 best = Dirs8[0];
        run = -1f;
        foreach (var d in Dirs8)
        {
            float r = FreeRun(from, d, max);
            if (r > run) { run = r; best = d; }
        }
        return best;
    }

    // ------------------------------------------------------------------------------------------------ grab / carry / throw a dead body

    enum GB { Seek, Aim, Carry, Flight }
    GB _gb = GB.Seek;
    Enemy _body; uint _bodyId; Transform _bodyTr;
    Vector3 _gbWalkDir, _gbStartPlayer, _gbStartBody, _gbThrowPos;
    float _gbT0, _gbNextLog, _gbMaxDist, _gbWalkLimit, _gbWalked, _gbNextSeek, _gbThrowT, _gbBackDist;
    int _gbNo, _gbSamples;
    float _gbSumDist;

    void GrabBody(float now)
    {
        if (!InDay) return;
        var pl = Player.Instance;
        switch (_gb)
        {
            case GB.Seek:
            {
                if (now < _gbNextSeek) return;
                _gbNextSeek = now + 1.5f;
                if (pl.HeldEnemy != null) return;
                var body = NearestBody(pl.transform.position);
                if (body == null) { NoBody(now); return; }
                uint id = CanEmAllCoop.Core.NetIds.IdOf(body);
                if (id == 0 && Net.IsClient) { NoBody(now); return; }
                _body = body; _bodyId = id; _bodyTr = body.transform;
                var bp = _bodyTr.position;
                // stand behind the body (seen from the walking direction) with free space ahead to walk through
                _gbWalkDir = BestDirection(bp, 9f, out float run);
                _gbWalkLimit = Mathf.Clamp(run - 2.0f, 0.5f, 6f);
                _gbBackDist = FreeRun(bp + Vector3.up, -_gbWalkDir, 2.0f);
                var stand = bp - _gbWalkDir * Mathf.Clamp(_gbBackDist - 0.3f, 0.8f, 1.6f);
                stand.y = PlayerY(pl);
                PlaceAt(pl, stand);
                float err = AimAt(pl, bp + Vector3.up * 0.3f);
                _gbStartPlayer = stand; _gbStartBody = bp;
                _gbNo++;
                Log.Info($"[DEBUG] grabbody #{_gbNo}: body #{id} at {V(bp)} kinematic={_body.rb?.isKinematic} -> player placed at {V(stand)} walkDir={V(_gbWalkDir)} freeAhead={run:0.0} aimErr={err:0.0}deg role={Net.Role}");
                _gb = GB.Aim; _gbT0 = now;
                break;
            }
            case GB.Aim:
            {
                if (_body == null || _bodyTr == null) { _gb = GB.Seek; return; }
                if (now - _gbT0 < 0.15f) { AimAt(pl, _bodyTr.position + Vector3.up * 0.3f); return; }   // let the camera / physics settle one or two frames
                var ig = _body.TryCast<IGrabbable>();
                if (ig == null) { Log.Info("[DEBUG] grabbody: Enemy is not castable to IGrabbable"); _gb = GB.Seek; return; }
                pl.GrabDeadBody(ig);
                bool held = pl.HeldEnemy != null;
                Log.Info($"[DEBUG] grabbody #{_gbNo}: GrabDeadBody -> held={held} bodyKinematic={_body.rb?.isKinematic} holdDistance={pl.bodyHoldDistance:0.0} maxHeld={pl.maxHeldBodyDistance:0.0}");
                if (!held) { _gb = GB.Seek; _gbNextSeek = now + 3f; return; }
                _gb = GB.Carry; _gbT0 = now; _gbNextLog = now; _gbMaxDist = 0f; _gbWalked = 0f; _gbSamples = 0; _gbSumDist = 0f;
                break;
            }
            case GB.Carry:
            {
                if (_body == null || _bodyTr == null) { _gb = GB.Seek; return; }
                float dt = Time.unscaledDeltaTime;
                // walk (teleport a few cm per frame: the game's own movement needs input) then stand still
                if (_gbWalked < _gbWalkLimit && now - _gbT0 < GrabBodySecs - 1.5f)
                {
                    float step = 2.5f * dt;
                    _gbWalked += step;
                    var np = pl.transform.position + _gbWalkDir * step;
                    PlaceAt(pl, np);
                }
                float yaw = Mathf.Atan2(_gbWalkDir.x, _gbWalkDir.z) * Mathf.Rad2Deg;
                SetAim(pl, yaw, 5f);
                var pp = pl.transform.position;
                var bp = _bodyTr.position;
                float dist = Vector3.Distance(Flat(bp), Flat(pp));
                _gbMaxDist = Mathf.Max(_gbMaxDist, dist);
                _gbSumDist += dist; _gbSamples++;
                bool stillHeld = pl.HeldEnemy != null;
                if (now >= _gbNextLog || !stillHeld)
                {
                    _gbNextLog = now + 0.5f;
                    Log.Info($"[DEBUG] grabbody #{_gbNo}: t={now - _gbT0:0.0}s player={V(pp)} body={V(bp)} dist={dist:0.00} playerWalked={_gbWalked:0.0} bodyMoved={Vector3.Distance(bp, _gbStartBody):0.0} held={stillHeld} bodyKinematic={_body.rb?.isKinematic}");
                }
                if (!stillHeld)
                {
                    Log.Info($"[DEBUG] grabbody #{_gbNo}: RESULT DROPPED after {now - _gbT0:0.0}s (player walked {_gbWalked:0.0} m, body moved {Vector3.Distance(bp, _gbStartBody):0.0} m, maxDist {_gbMaxDist:0.00}, maxHeldBodyDistance {pl.maxHeldBodyDistance:0.0})");
                    _gb = GB.Seek; _gbNextSeek = now + 4f; return;
                }
                if (now - _gbT0 >= GrabBodySecs)
                {
                    float moved = Vector3.Distance(bp, _gbStartBody);
                    Log.Info($"[DEBUG] grabbody #{_gbNo}: RESULT CARRIED for {now - _gbT0:0.0}s: player walked {_gbWalked:0.0} m, body moved {moved:0.0} m, mean dist {_gbSumDist / Math.Max(1, _gbSamples):0.00} max dist {_gbMaxDist:0.00}");
                    // throw straight ahead, slightly up
                    SetAim(pl, yaw, -10f);
                    _gbThrowPos = bp;
                    pl.ThrowHeldBody();
                    var rb = _body.rb;
                    Log.Info($"[DEBUG] grabbody #{_gbNo}: ThrowHeldBody from {V(bp)} dir {V(_gbWalkDir)} -> held={pl.HeldEnemy != null} bodyVel={(rb != null ? V(rb.linearVelocity) : "?")} kinematic={rb?.isKinematic} id #{_bodyId}");
                    _gb = GB.Flight; _gbThrowT = now; _gbNextLog = now + 0.5f;
                }
                break;
            }
            case GB.Flight:
            {
                if (_body == null || _bodyTr == null) { _gb = GB.Seek; return; }
                if (now < _gbNextLog) return;
                float since = now - _gbThrowT;
                _gbNextLog = now + (since < 3f ? 0.5f : 1f);
                var bp = _bodyTr.position;
                var rb = _body.rb;
                Log.Info($"[DEBUG] grabbody #{_gbNo}: flight t={since:0.0}s body #{_bodyId} pos={V(bp)} fromThrow={Vector3.Distance(bp, _gbThrowPos):0.00} speed={(rb != null ? rb.linearVelocity.magnitude : -1f):0.00} kinematic={rb?.isKinematic} role={Net.Role}");
                if (since >= 8f) { _gb = GB.Seek; _gbNextSeek = now + 2f; }
                break;
            }
        }
    }

    float _nextNoBody;
    void NoBody(float now)
    {
        if (now < _nextNoBody) return;
        _nextNoBody = now + 10f;
        Log.Info($"[DEBUG] grabbody: no dead body to pick up (role {Net.Role})");
    }

    static Enemy NearestBody(Vector3 from)
    {
        Enemy best = null; float bd = float.MaxValue;
        if (Net.IsClient)
        {
            foreach (var p in ClientPuppets.All)
            {
                if (p.E == null || !p.Dead || !p.E.gameObject.activeInHierarchy) continue;
                if (GrabSync.Table.IsHeld(p.Id)) continue;
                float d = (p.E.transform.position - from).sqrMagnitude;
                if (d < bd) { bd = d; best = p.E; }
            }
        }
        else
        {
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>())
            {
                if (e == null || !e.IsDead || !e.gameObject.activeInHierarchy) continue;
                float d = (e.transform.position - from).sqrMagnitude;
                if (d < bd) { bd = d; best = e; }
            }
        }
        return best;
    }

    // ------------------------------------------------------------------------------------------------ punch

    enum PS { Idle, Aimed, Wait }
    PS _ps = PS.Idle;
    Enemy _pTarget; uint _pId; Transform _pTr;
    float _pNext, _pT;
    Vector3 _pDir;
    bool _pControl;
    long _pSentBefore, _pResultsBefore;
    int _pNo, _pFired, _pSentOk;
    string _pPredicted;

    void Punch(float now)
    {
        if (!InDay) return;
        var pl = Player.Instance;
        var inv = Inventory.Instance;
        if (inv == null) return;
        switch (_ps)
        {
            case PS.Idle:
            {
                if (now < _pNext) return;
                _pNext = now + PunchEvery;
                var t = NearestLive(pl.transform.position);
                if (t == null) { NoTarget("punch", now); return; }
                _pTarget = t; _pTr = t.transform; _pId = CanEmAllCoop.Core.NetIds.IdOf(t);
                var tp = _pTr.position;
                // approach from the side the player is on
                var dir = Flat(pl.transform.position - tp);
                if (dir.sqrMagnitude < 0.01f) dir = Vector3.right;
                dir.Normalize();
                // free side: use the longest free run among the 8 directions if the straight one is blocked
                if (FreeRun(tp + Vector3.up, dir, 1.6f) < 1.5f) dir = BestDirection(tp, 3f, out _);
                _pDir = dir;
                _ps = PS.Aimed; _pT = now;
                break;
            }
            case PS.Aimed:
            {
                if (_pTarget == null || _pTr == null || _pTarget.IsDead) { _ps = PS.Idle; return; }
                // the enemy keeps walking: stay 1.2 m from where it is now
                var stand = _pTr.position + _pDir * 1.2f;
                stand.y = PlayerY(pl);
                PlaceAt(pl, stand);
                var chest = _pTr.position + Vector3.up * 0.6f;
                float err = AimAt(pl, chest);
                if (now - _pT < 0.1f) return;   // a few frames for the camera pose to be what the ray will use
                var cam = pl.mainCamera != null ? pl.mainCamera.transform : pl.mainCameraParent;
                string ray = "none";
                float distanceToPunch = inv.distanceToPunch;
                int mask = ~(1 << pl.gameObject.layer);   // the game's ray does not start inside its own player either
                if (Physics.Raycast(cam.position, cam.forward, out var hit, distanceToPunch, mask))
                {
                    var col = hit.collider;
                    var en = col != null ? col.GetComponentInParent<Enemy>() : null;
                    ray = $"{(col != null ? col.gameObject.name : "?")}@{hit.distance:0.00}{(en != null ? $" enemy#{CanEmAllCoop.Core.NetIds.IdOf(en)}" : "")}";
                }
                // negative control on every 4th trial: give the target the truck-shaped capsule it had before the fix (r 0.2, h 0.2) for this punch only
                var cap = _pTarget.mainCollider?.TryCast<CapsuleCollider>();
                _pControl = cap != null && (_pNo + 1) % 4 == 0;
                if (_pControl)
                {
                    cap.radius = 0.2f; cap.height = 0.2f; cap.center = new Vector3(0f, 0.2f, 0f);
                    ray = "(re-aimed below)";
                    if (Physics.Raycast(cam.position, cam.forward, out var hit2, distanceToPunch, mask))
                    {
                        var col2 = hit2.collider;
                        var en2 = col2 != null ? col2.GetComponentInParent<Enemy>() : null;
                        ray = $"{(col2 != null ? col2.gameObject.name : "?")}@{hit2.distance:0.00}{(en2 != null ? $" enemy#{CanEmAllCoop.Core.NetIds.IdOf(en2)}" : "")}";
                    }
                    else ray = "none";
                }
                _pPredicted = ray;
                _pSentBefore = Combat.HitsSent; _pResultsBefore = Combat.HitResults;
                _pNo++;
                Log.Info($"[DEBUG] punch #{_pNo}{(_pControl ? " (CONTROL: truck-shaped capsule)" : "")}: target #{_pId} {V(_pTr.position)} dist={Vector3.Distance(cam.position, chest):0.00} aimErr={err:0.0}deg rayHit={ray} distanceToPunch={distanceToPunch:0.0} " +
                         $"capsule r={cap?.radius:0.00} h={cap?.height:0.00} center={(cap != null ? V(cap.center) : "?")} role={Net.Role}");
                inv.StartCoroutine(inv.PunchDelayed(0.02f));   // the coroutine Inventory.Punch starts; ends in Enemy.Damage
                if (!_pControl) _pFired++;
                _ps = PS.Wait; _pT = now;
                break;
            }
            case PS.Wait:
            {
                if (_pTr != null && _pTarget != null && now - _pT < 0.3f)   // keep looking while the delayed hit fires
                {
                    var stand = _pTr.position + _pDir * 1.2f;
                    stand.y = PlayerY(pl);
                    PlaceAt(pl, stand);
                    AimAt(pl, _pTr.position + Vector3.up * 0.6f);
                }
                if (now - _pT < 0.8f) return;   // Combat.FlushClient batches hits
                long sent = Combat.HitsSent - _pSentBefore;
                if (_pControl)
                {
                    var cap = _pTarget?.mainCollider?.TryCast<CapsuleCollider>();
                    if (cap != null) { cap.radius = 0.3f; cap.height = 1.2f; cap.center = new Vector3(0f, 0.6f, 0f); }
                }
                if (sent > 0 && !_pControl) _pSentOk++;
                Log.Info($"[DEBUG] punch #{_pNo}{(_pControl ? " (CONTROL)" : "")}: {(sent > 0 ? "HIT SENT" : "NO HIT")} (hitsSent +{sent}, hitResults +{Combat.HitResults - _pResultsBefore}, hitTarget=#{(sent > 0 ? Combat.LastClientHitId : 0)} aimedAt=#{_pId}) rayHit={_pPredicted}; totals fired={_pFired} sent={_pSentOk} hitsSent={Combat.HitsSent} hitsApplied={Combat.HitsApplied} hitResults={Combat.HitResults}");
                _ps = PS.Idle;
                break;
            }
        }
    }

    float _nextNoTarget;
    void NoTarget(string what, float now)
    {
        if (now < _nextNoTarget) return;
        _nextNoTarget = now + 10f;
        Log.Info($"[DEBUG] {what}: no target (role {Net.Role})");
    }

    static Enemy NearestLive(Vector3 from)
    {
        Enemy best = null; float bd = float.MaxValue;
        if (Net.IsClient)
        {
            foreach (var p in ClientPuppets.All)
            {
                if (p.E == null || p.Dead || p.E.IsDead || !p.E.gameObject.activeInHierarchy) continue;
                float d = (p.E.transform.position - from).sqrMagnitude;
                if (d < bd) { bd = d; best = p.E; }
            }
        }
        else
        {
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>())
            {
                if (e == null || e.IsDead || !e.gameObject.activeInHierarchy) continue;
                float d = (e.transform.position - from).sqrMagnitude;
                if (d < bd) { bd = d; best = e; }
            }
        }
        return best;
    }

    // ------------------------------------------------------------------------------------------------ vacuum a can and throw it into the reward pipe

    enum VS { Seek, Suck, Hold, Flight }
    VS _vs = VS.Seek;
    Can _vCan; uint _vId; Transform _vTr;
    float _vT, _vNext, _vNextLog, _vSettleT, _vStandD;
    bool _vSettled, _vDiag, _vNudged;
    Vector3 _vStandDir;
    int _vNo, _vGrabbedBefore;
    Vector3 _vPipe;
    readonly HashSet<uint> _vBad = new();   // cans the vacuum would not take (e.g. next to a crusher)

    void VacuumDrive(float now)
    {
        if (!InDay) return;
        var pl = Player.Instance;
        var vac = Vacuum.Instance;
        var rm = RewardMachine.Instance;
        if (vac == null || rm == null || rm.point == null) return;
        switch (_vs)
        {
            case VS.Seek:
            {
                if (now < _vNext) return;
                _vNext = now + VacuumEvery;
                var can = NearestCan(pl.transform.position, _vBad);
                if (can == null) { NoTarget("vacuum (no can)", now); return; }
                _vCan = can; _vTr = can.transform; _vId = CanEmAllCoop.Core.NetIds.IdOf(can);
                var cp = _vTr.position;
                var dir = BestDirection(cp, 2.5f, out float run);
                var stand = cp + dir * Mathf.Clamp(run - 0.4f, 0.8f, 1.3f);
                stand.y = PlayerY(pl);
                PlaceAt(pl, stand);
                float err = AimAt(pl, cp);
                _vNo++;
                _vGrabbedBefore = vac.grabbables != null ? vac.grabbables.Count : 0;
                bool wasActive = vac.gameObject.activeSelf;
                if (!wasActive) vac.gameObject.SetActive(true);
                Log.Info($"[DEBUG] vacuum #{_vNo}: can #{_vId} qty={can.quantity} at {V(cp)} -> player at {V(stand)} aimErr={err:0.0}deg vacuumActive(before)={wasActive} range={vac.suckRange:0.0} capacity={vac.maxCapacity * Math.Max(1, vac.maxCapacityMultiplier)} inVacuum={_vGrabbedBefore} role={Net.Role}");
                _vs = VS.Suck; _vT = now; _vNextLog = now; _vDiag = false; _vNudged = false;
                break;
            }
            case VS.Suck:
            {
                if (_vCan == null || _vTr == null) { _vs = VS.Seek; return; }
                // what Update does while the right mouse button is held
                AimAt(pl, _vTr.position);
                vac.SetState(Vacuum.VacuumState.Sucking);
                vac.UpdateCrusherSuckState();
                vac.Suck();
                int n = vac.grabbables != null ? vac.grabbables.Count : 0;
                if (!_vDiag && now - _vT > 0.6f)
                {
                    _vDiag = true;
                    var pt = vac.point != null ? vac.point.position : _vTr.position;
                    var near = Physics.OverlapSphere(pt, vac.suckRange, ~0);
                    var names = new System.Text.StringBuilder();
                    int shown = 0;
                    foreach (var col in near) { if (col == null || shown++ >= 8) continue; names.Append(col.gameObject.name).Append('/').Append(col.gameObject.layer).Append(col.isTrigger ? "T" : "").Append(' '); }
                    var cc = _vCan.mainCollider;
                    Log.Info($"[DEBUG] vacuum #{_vNo}: diag enemyLayer={vac.enemyLayer.value} wallLayer={vac.wallLayer.value} canLayer={_vCan.gameObject.layer} canCollider={(cc != null ? cc.GetType().Name + (cc.enabled ? "" : " off") : "none")} protectedByCrusher={vac.IsProtectedByCrusher(_vTr.position)} " +
                             $"overlap({near.Length})={names} suckRange={vac.suckRange:0.0} force={vac.suckForce:0.0} strength={vac.vacuumStrength:0.00}/{vac.targetVacuumStrength:0.00} ignored={vac.ignoredGrabbables?.Count} weapon={Inventory.Instance?.currentWeaponIndex} " +
                             $"angleFromPointForward={(vac.point != null ? Vector3.Angle(vac.point.forward, _vTr.position - pt) : -1f):0} linecastToWall={Physics.Linecast(pt, _vTr.position, vac.wallLayer.value)} inRange={vac.grabbablesInRange?.Count}");
                }
                if (now >= _vNextLog)
                {
                    _vNextLog = now + 0.5f;
                    Log.Info($"[DEBUG] vacuum #{_vNo}: sucking t={now - _vT:0.0}s can #{_vId} pos={V(_vTr.position)} active={_vCan.gameObject.activeInHierarchy} inVacuum={n} inRange={vac.grabbablesInRange?.Count} state={vac.state} throwEnemy={vac.throwEnemy} pointDist={(vac.point != null ? Vector3.Distance(vac.point.position, _vTr.position) : -1f):0.00}");
                }
                if (n > _vGrabbedBefore)
                {
                    vac.SetState(Vacuum.VacuumState.Idle);
                    // Throw() sends the first can in the list, which is not necessarily the one aimed at (several can be in range)
                    var first = vac.grabbables[0];
                    uint firstId = first != null ? CanEmAllCoop.Core.NetIds.IdOf(first) : 0;
                    var fc = first != null ? first.TryCast<Can>() : null;
                    if (fc != null && firstId != 0) { _vCan = fc; _vId = firstId; _vTr = fc.transform; }
                    Log.Info($"[DEBUG] vacuum #{_vNo}: can #{_vId} is in the vacuum after {now - _vT:0.00}s (inVacuum={n}) hold={(GrabSync.Table.TryGetHold(_vId, out var h) ? h.Holder + "/" + h.Kind : "none")}");
                    _vs = VS.Hold; _vT = now; _vSettled = false;
                }
                else if (now - _vT > 1.2f && !_vNudged && vac.point != null && _vTr != null)
                {
                    // nothing in range: put the can where the vacuum is looking, as if the player had walked right up to it
                    _vNudged = true;
                    var target = vac.point.position + vac.point.forward * 0.8f;
                    var rb = _vCan.rb;
                    if (rb != null) { rb.position = target; rb.linearVelocity = Vector3.zero; }
                    _vTr.position = target;
                    Log.Info($"[DEBUG] vacuum #{_vNo}: no pull after 1.2 s, moved can #{_vId} in front of the vacuum point to {V(target)}");
                }
                else if (now - _vT > 3.5f)
                {
                    vac.SetState(Vacuum.VacuumState.Idle);
                    _vBad.Add(_vId);
                    Log.Info($"[DEBUG] vacuum #{_vNo}: RESULT could not suck can #{_vId} in 3.5 s (pos {V(_vTr.position)}, dist {Vector3.Distance(_vTr.position, pl.mainCameraParent.position):0.00}); skipping it");
                    _vs = VS.Seek; _vNext = now + 0.5f;
                }
                break;
            }
            case VS.Hold:
            {
                if (now - _vT < 0.7f) return;   // the grab reaches the host (GrabReq) before the throw
                _vPipe = rm.point.position;
                if (!_vSettled)
                {
                    // the vacuum's throw point trails the camera (smoothed); after a teleport it needs a moment to catch up
                    float floorY0 = PlayerY(pl);
                    var above0 = new Vector3(_vPipe.x, floorY0, _vPipe.z);
                    // the reward pipe is a hole in the floor (a player over it falls and is "down"): stand on solid ground
                    var dir0 = GroundedStand(above0, 1.5f, 6f, out float d0);
                    _vStandDir = dir0; _vStandD = d0;
                    PlaceAt(pl, above0 + dir0 * _vStandD);
                    SetAim(pl, Mathf.Atan2(-dir0.x, -dir0.z) * Mathf.Rad2Deg, 0f);
                    _vSettled = true; _vSettleT = now;
                    Log.Info($"[DEBUG] vacuum #{_vNo}: moved player to {V(above0 + dir0 * _vStandD)} (pipe {V(_vPipe)}), now at {V(pl.transform.position)}, rb {(pl.rb != null ? V(pl.rb.position) : "?")}");
                    return;
                }
                if (now - _vSettleT < 0.4f) return;
                if (now - _vSettleT < 1.6f && vac.point != null && Vector3.Distance(vac.point.position, pl.mainCameraParent.position) > 1.6f) return;
                // stand 2-3 m from the pipe on the freest side and pick the pitch whose arc passes the pipe point
                // the pipe point itself is below the floor (the sphere of RewardMachine.radius around it reaches up through the floor):
                // aim at the spot on the floor straight above it and stand a few metres away on the freest side
                float floorY = PlayerY(pl);
                var above = new Vector3(_vPipe.x, floorY, _vPipe.z);
                var dir = _vStandDir;
                float d = _vStandD;
                var stand = above + dir * d;
                float yaw = Mathf.Atan2(-dir.x, -dir.z) * Mathf.Rad2Deg;
                float pitch = BestPitch(pl, yaw, new Vector3(_vPipe.x, floorY - 0.7f, _vPipe.z), out float miss);
                SetAim(pl, yaw, pitch);
                Log.Info($"[DEBUG] vacuum #{_vNo}: throw point {(vac.point != null ? V(vac.point.position) : "?")} camera {V(pl.mainCameraParent.position)} player {V(pl.transform.position)}");
                bool can = vac.CanThrow();
                Log.Info($"[DEBUG] vacuum #{_vNo}: throwing can #{_vId} at the pipe {V(_vPipe)} (radius {rm.radius:0.0}, sphere-to-stand {Vector3.Distance(stand, _vPipe):0.0}) from {V(stand)} horizDist={d:0.0} pitch={pitch:0.0} predictedMiss={miss:0.00} canThrow={can} force={pl.objectThrowForce:0.0}/{pl.objectThrowUpForce:0.0} throwEnemy={vac.throwEnemy}");
                vac.throwEnemy = false;   // the scroll wheel picks what Throw() sends: cans, not enemies
                vac.SetState(Vacuum.VacuumState.Throwing);
                vac.Throw();
                vac.SetState(Vacuum.VacuumState.Idle);
                var rb = _vCan.rb;
                Log.Info($"[DEBUG] vacuum #{_vNo}: Throw() done: can #{_vId} active={_vCan.gameObject.activeInHierarchy} pos={V(_vTr.position)} vel={(rb != null ? V(rb.linearVelocity) : "?")} gravity={rb?.useGravity} kinematic={rb?.isKinematic} inVacuum={vac.grabbables?.Count}");
                _vs = VS.Flight; _vT = now; _vNextLog = now + 0.5f;
                break;
            }
            case VS.Flight:
            {
                if (now < _vNextLog) return;
                float since = now - _vT;
                _vNextLog = now + (since < 4f ? 0.5f : 1f);
                bool gone = _vCan == null || _vTr == null || !_vCan.gameObject.activeInHierarchy || _vCan.IsInPool;
                Log.Info($"[DEBUG] vacuum #{_vNo}: flight t={since:0.0}s can #{_vId} {(gone ? "GONE (paid / pooled)" : "pos=" + V(_vTr.position) + " pipeDist=" + Vector3.Distance(_vTr.position, _vPipe).ToString("0.00"))} role={Net.Role}");
                if (gone || since >= 8f) { _vs = VS.Seek; _vNext = now + 1f; }
                break;
            }
        }
    }

    static Can NearestCan(Vector3 from, HashSet<uint> skip)
    {
        Can best = null; float bd = float.MaxValue;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Can>())
        {
            if (c == null || c.IsInPool || c.IsProcessed || !c.gameObject.activeInHierarchy) continue;
            uint id = CanEmAllCoop.Core.NetIds.IdOf(c);
            if (id == 0 || skip.Contains(id) || GrabSync.Table.IsHeld(id)) continue;
            float d = (c.transform.position - from).sqrMagnitude;
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    /// <summary>The farthest spot (6 m down to 1.5 m, 8 directions) around <paramref name="center"/> that has floor under it and a clear line to the centre.</summary>
    static Vector3 GroundedStand(Vector3 center, float minD, float maxD, out float dist)
    {
        for (float d = maxD; d >= minD; d -= 0.5f)
        {
            foreach (var dir in Dirs8)
            {
                var spot = center + dir * d;
                if (!Physics.Raycast(spot + Vector3.up * 0.5f, Vector3.down, out var down, 3f)) continue;
                if (down.point.y < center.y - 1.5f) continue;
                if (FreeRun(spot + Vector3.up * 1.0f, -dir, d) < d - 0.1f) continue;
                dist = d;
                return dir;
            }
        }
        dist = minD;
        return Dirs8[0];
    }

    /// <summary>Ballistic search: the pitch (degrees, positive = looking down) whose arc comes closest to <paramref name="target"/>.</summary>
    static float BestPitch(Player pl, float yaw, Vector3 target, out float bestMiss)
    {
        var vac = Vacuum.Instance;
        float g = Physics.gravity.y;
        float bestPitch = -20f; bestMiss = float.MaxValue;
        for (float pitch = -70f; pitch <= 40f; pitch += 2f)
        {
            SetAim(pl, yaw, pitch);
            var cp = pl.mainCameraParent;
            var start = vac.point != null ? vac.point.position : cp.position;
            var v = cp.forward * pl.objectThrowForce + Vector3.up * pl.objectThrowUpForce;
            var p = start;
            float min = float.MaxValue;
            for (float t = 0f; t < 3f; t += 0.02f)
            {
                v.y += g * 0.02f;
                p += v * 0.02f;
                float d = (p - target).magnitude;
                if (d < min) min = d;
                if (p.y < target.y - 2f) break;
            }
            if (min < bestMiss) { bestMiss = min; bestPitch = pitch; }
        }
        return bestPitch;
    }

    // ------------------------------------------------------------------------------------------------ enemy facing

    sealed class FaceTrack { public Vector3 Pos; public float T; public bool Has; }
    readonly Dictionary<IntPtr, FaceTrack> _face = new();
    readonly List<float> _angVisual = new(), _angRoot = new();
    float _nextFaceSample, _nextFaceLog, _nextFaceRefresh;
    Enemy[] _hostEnemies = Array.Empty<Enemy>();
    int _faceSamples;

    void Facing(float now)
    {
        if (!FlowCore.GameStarted || FlowCore.Phase != FlowPhase.Day) return;
        if (now < _nextFaceSample) return;
        _nextFaceSample = now + 0.5f;

        IEnumerable<Enemy> live;
        if (Net.IsClient)
        {
            var l = new List<Enemy>();
            foreach (var p in ClientPuppets.All) if (p.E != null && !p.Dead && p.E.gameObject.activeInHierarchy) l.Add(p.E);
            live = l;
        }
        else
        {
            if (now >= _nextFaceRefresh)
            {
                _nextFaceRefresh = now + 2f;
                var arr = UnityEngine.Object.FindObjectsOfType<Enemy>();
                var l = new List<Enemy>(arr.Length);
                foreach (var e in arr) if (e != null && !e.IsDead) l.Add(e);
                _hostEnemies = l.ToArray();
            }
            live = _hostEnemies;
        }

        foreach (var e in live)
        {
            if (e == null || e.IsDead) continue;
            var tr = e.transform;
            var vis = e.visual;
            if (vis == null) continue;
            if (!_face.TryGetValue(e.Pointer, out var ft)) _face[e.Pointer] = ft = new FaceTrack();
            var pos = tr.position;
            if (ft.Has)
            {
                float dt = now - ft.T;
                var mv = Flat(pos - ft.Pos);
                if (dt > 0.2f && dt < 1.5f && mv.magnitude / dt >= 0.5f)
                {
                    _angVisual.Add(Vector3.Angle(Flat(vis.forward), mv));
                    _angRoot.Add(Vector3.Angle(Flat(tr.forward), mv));
                }
            }
            ft.Pos = pos; ft.T = now; ft.Has = true;
        }
        _faceSamples++;

        if (now >= _nextFaceLog)
        {
            _nextFaceLog = now + FacingEvery;
            Log.Info($"[DEBUG] facing role={Net.Role}: {Summarize("visual", _angVisual)} | {Summarize("root", _angRoot)}");
            _angVisual.Clear(); _angRoot.Clear();
            if (_face.Count > 2000) _face.Clear();
        }
    }

    static string Summarize(string label, List<float> a)
    {
        if (a.Count == 0) return $"{label}: no moving enemies";
        var s = new List<float>(a);
        s.Sort();
        float sum = 0f; int w45 = 0, back = 0;
        foreach (var x in s) { sum += x; if (x <= 45f) w45++; if (x > 135f) back++; }
        return $"{label}: n={s.Count} mean={sum / s.Count:0.0}deg median={s[s.Count / 2]:0.0}deg within45={100f * w45 / s.Count:0}% behind(>135)={100f * back / s.Count:0}%";
    }

    // ------------------------------------------------------------------------------------------------ skill node colours

    readonly HashSet<int> _seenPurchased = new();
    readonly Dictionary<int, (float t, bool ok)> _pendingColors = new();
    bool _colorsFirstScan = true;
    float _nextColors, _nextColorsSummary;

    static bool Near(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f && Mathf.Abs(a.a - b.a) < 0.01f;

    static string C(Color c) => $"({c.r:0.00},{c.g:0.00},{c.b:0.00},{c.a:0.00})";

    void SkillColors(float now)
    {
        if (!FlowCore.GameStarted || now < _nextColors) return;
        _nextColors = now + ColorsEvery;
        var nodes = GameAccess.Tree?.nodes;
        if (nodes == null) return;
        int purchased = 0, bgOk = 0, iconOk = 0, newOnes = 0;
        string firstBad = null;
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            if (n == null || n.skill == null || !n.skill.isPurchased) continue;
            purchased++;
            bool bg = n.background != null && Near(n.background.color, n.purchasedColor);
            bool ic = n.icon != null && Near(n.icon.color, n.purchasedIconColor);
            if (bg) bgOk++;
            if (ic) iconOk++;
            if (!bg || !ic) firstBad ??= $"skill {(int)n.skill.skillId} bg={(n.background != null ? C(n.background.color) : "null")} want {C(n.purchasedColor)}, icon={(n.icon != null ? C(n.icon.color) : "null")} want {C(n.purchasedIconColor)}";
            int sid = (int)n.skill.skillId;
            if (_seenPurchased.Add(sid) && !_colorsFirstScan) _pendingColors[sid] = (now, bg && ic);
            if (_pendingColors.TryGetValue(sid, out var first) && now - first.t >= 1.5f)
            {
                _pendingColors.Remove(sid);
                newOnes++;
                Log.Info($"[DEBUG] skillcolors role={Net.Role}: skill {sid} became purchased: on first sight {(first.ok ? "tinted" : "NOT tinted yet")}; 1.5 s later background {C(n.background.color)} (want {C(n.purchasedColor)}) {(bg ? "OK" : "WRONG")}, icon {C(n.icon.color)} (want {C(n.purchasedIconColor)}) {(ic ? "OK" : "WRONG")}");
            }
        }
        _colorsFirstScan = false;
        if (now >= _nextColorsSummary)
        {
            _nextColorsSummary = now + 10f;
            Log.Info($"[DEBUG] skillcolors role={Net.Role}: purchased={purchased} backgroundOk={bgOk} iconOk={iconOk}{(firstBad != null ? " first mismatch: " + firstBad : "")}");
        }
    }

    // ------------------------------------------------------------------------------------------------ host: what a remote holder does to a held object

    readonly Dictionary<uint, float> _watch = new();
    readonly List<uint> _watchScratch = new();
    float _nextWatch;
    int _lastPaid;

    void HoldWatch(float now)
    {
        if (!Net.IsHost) return;
        if (CanHost.Paid != _lastPaid)
        {
            _lastPaid = CanHost.Paid;
            Log.Info($"[DEBUG] holdwatch: host paid can #{CanHost.LastPaidId} qty={CanHost.LastPaidQty} (paid total {CanHost.Paid}) money={MoneyManager.Instance?.Money}");
        }
        if (now < _nextWatch) return;
        _nextWatch = now + HoldWatchEvery;

        _watchScratch.Clear();
        foreach (var kv in GrabSync.Table.All)
            if (kv.Value.Holder != Items.LocalIndex) _watchScratch.Add(kv.Key);
        foreach (var id in _watchScratch)
        {
            if (!_watch.ContainsKey(id)) Log.Info($"[DEBUG] holdwatch: #{id} is held by player {GrabSync.Table.HolderOf(id)}");
            _watch[id] = now + 10f;
        }

        _watchScratch.Clear();
        foreach (var kv in _watch) _watchScratch.Add(kv.Key);
        foreach (var id in _watchScratch)
        {
            if (now > _watch[id]) { _watch.Remove(id); continue; }
            if (!Items.TryResolve(id, out var comp))
            {
                Log.Info($"[DEBUG] holdwatch: #{id} no longer exists on the host (paid / pooled / gone)");
                _watch.Remove(id);
                continue;
            }
            var rb = Items.RigidbodyOf(comp);
            string kind = comp.TryCast<Enemy>() != null ? "body" : comp.TryCast<Can>() != null ? "can" : "crate";
            var hold = GrabSync.Table.HolderOf(id);
            Log.Info($"[DEBUG] holdwatch: {kind} #{id} holder={(hold == GrabTable.None ? "none" : hold.ToString())} pos={V(comp.transform.position)} speed={(rb != null ? rb.linearVelocity.magnitude : -1f):0.00} kinematic={rb?.isKinematic} gravity={rb?.useGravity} active={comp.gameObject.activeInHierarchy}");
        }
    }
}
