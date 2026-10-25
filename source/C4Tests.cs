using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using GameManager.OnlineBase;
using GameManager.OnlineDefault;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace WheelchairLocalCoop;

// Explicit test mode only. Uses existing, actually thrown server projectiles.
// Calls their native trigger callbacks; never changes arming/owner fields,
// teleports objects, calls Explode directly, or synthesizes projectiles.
internal static class C4Tests
{
    private sealed class Events
    {
        internal int Countdowns;
        internal int Explosions;
    }

    private sealed class Case
    {
        internal StickyBombProjectile Bomb;
        internal IntPtr Pointer;
        internal uint NetId;
        internal uint Owner;
        internal uint TriggerActor;
        internal int InitialCountdowns;
        internal int InitialExplosions;
        internal int CountdownBeforeTrigger;
        internal bool ProtectionObserved;
        internal bool OwnerAlreadyInside;
        internal bool OwnerExitedAtReady;
        internal float TriggerTime;
        internal object Before;
    }

    private static readonly Dictionary<IntPtr, Events> observations = new();
    private static readonly List<Case> cases = new();
    private static readonly List<object> results = new();
    private static uint[] expectedOwners = Array.Empty<uint>();
    private static Action<string> log = _ => { };
    private static string route;
    private static int index;
    private static int phase;
    private static float settleDeadline;
    private static float deadline;
    private static bool allPassed;
    private static int failures;
    private static int notTested;
    private static bool installed;
    private static object initialInspection;

    internal static bool Running { get; private set; }
    internal static string Status { get; private set; } = "idle";
    internal static object Result { get; private set; }
    internal static string ResultPath => Path.Combine(Paths.BepInExRootPath, "localcoop-c4-trigger-results.json");

    internal static void Install(Harmony harmony, Action<string> logger)
    {
        if (!Plugin.TestMode || installed) return;
        log = logger;
        // The native coroutine inlines the RPC wrapper, so observe the distinct
        // DelayedExplode factory actually called by OnTriggerEnter instead.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(StickyBombProjectile), "DelayedExplode"),
            prefix: new HarmonyMethod(typeof(C4Tests), nameof(CountdownStarted)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(StickyBombProjectile), "Explode"),
            prefix: new HarmonyMethod(typeof(C4Tests), nameof(BeforeExplode)),
            postfix: new HarmonyMethod(typeof(C4Tests), nameof(AfterExplode)));
        installed = true;
    }

    private static Events For(IntPtr pointer)
    {
        if (!observations.TryGetValue(pointer, out var value))
            observations[pointer] = value = new Events();
        return value;
    }

    private static void CountdownStarted(StickyBombProjectile __instance)
    {
        if (Plugin.TestMode && __instance.isServer) For(__instance.Pointer).Countdowns++;
    }

    private static void BeforeExplode(StickyBombProjectile __instance, out bool __state)
    {
        __state = Plugin.TestMode && __instance.isServer && !__instance.hasExploded;
    }

    private static void AfterExplode(StickyBombProjectile __instance, bool __state)
    {
        if (__state && __instance.hasExploded) For(__instance.Pointer).Explosions++;
    }

    internal static object Inspect()
    {
        var bombs = new List<object>();
        foreach (var bomb in UnityEngine.Object.FindObjectsOfType<StickyBombProjectile>())
            bombs.Add(Snapshot(bomb));
        return new { count = bombs.Count, bombs };
    }

    private static object Snapshot(StickyBombProjectile bomb)
    {
        if (!bomb) return new { destroyed = true };
        var owner = bomb.NetworkownerNetIdentity;
        var actor = bomb.playerRef;
        var sphere = bomb.sphereCollider;
        var ev = For(bomb.Pointer);
        Vector3 position = bomb.transform.position;
        return new
        {
            netId = bomb.netId, server = bomb.isServer,
            ownerNetId = owner ? owner.netId : 0,
            playerRefNetId = actor ? actor.netId : 0,
            isStuck = bomb.IsStuck, ownerExited = bomb.ownerExited, exploded = bomb.hasExploded,
            delaySeconds = bomb.delayedExplosionTime, triggerLayers = bomb.triggerLayers.value,
            sensorEnabled = sphere && sphere.enabled, sensorRadius = sphere ? sphere.radius : 0,
            playersInside = bomb._playersInside == null ? 0 : bomb._playersInside.Count,
            position = new { x = position.x, y = position.y, z = position.z },
            observedCountdowns = ev.Countdowns, observedExplosions = ev.Explosions
        };
    }

    internal static void Start(string mode = "other")
    {
        if (!Plugin.TestMode || !installed) throw new InvalidOperationException("C4 diagnostics require installed test mode hooks.");
        if (Running) throw new InvalidOperationException("A C4 trigger test is already running.");
        if (!LocalPlayers.Active || !NetworkServer.active) throw new InvalidOperationException("Start a local host match first.");
        if (mode != "owner" && mode != "other") throw new ArgumentException("C4 mode must be owner or other.");
        route = mode;
        expectedOwners = LocalPlayers.Players.Where(p => p != null).Select(p => p.netId).ToArray();
        cases.Clear();
        results.Clear();
        initialInspection = Inspect();
        foreach (var bomb in UnityEngine.Object.FindObjectsOfType<StickyBombProjectile>().OrderBy(p => p.netId))
        {
            if (!bomb.isServer || bomb.hasExploded || !bomb.NetworkownerNetIdentity) continue;
            uint owner = bomb.NetworkownerNetIdentity.netId;
            if (!expectedOwners.Contains(owner)) continue;
            var ev = For(bomb.Pointer);
            cases.Add(new Case { Bomb = bomb, Pointer = bomb.Pointer, NetId = bomb.netId, Owner = owner,
                InitialCountdowns = ev.Countdowns, InitialExplosions = ev.Explosions });
        }
        index = 0;
        phase = 0;
        allPassed = true;
        failures = 0;
        notTested = 0;
        settleDeadline = Time.unscaledTime + 15f;
        Running = true;
        Status = "waiting for " + cases.Count + " existing C4 projectile(s) to stick";
        Publish();
        log("C4 native trigger test started: " + route + ", bombs=" + cases.Count + ".");
        if (cases.Count == 0) Finish();
    }

    internal static void Tick()
    {
        if (!Running) return;
        if (!Plugin.TestMode || !LocalPlayers.Active)
        {
            allPassed = false;
            Status = "host session ended during C4 test";
            Finish();
            return;
        }
        if (index >= cases.Count) { Finish(); return; }
        try { TickCase(cases[index]); }
        catch (Exception error) { CompleteCase(false, error.GetType().Name + ": " + error.Message); }
    }

    private static void TickCase(Case item)
    {
        var ev = For(item.Pointer);
        if (phase == 2 && ev.Explosions > item.InitialExplosions)
        {
            bool oneCountdown = ev.Countdowns - item.CountdownBeforeTrigger == 1;
            CompleteCase(oneCountdown && ev.Explosions - item.InitialExplosions == 1,
                oneCountdown ? "native countdown and explosion completed" : "unexpected additional countdown(s)");
            return;
        }
        var bomb = item.Bomb;
        if (!bomb) { CompleteCase(false, "projectile disappeared before expected native explosion", phase < 2); return; }
        var owner = LocalPlayers.Players.FirstOrDefault(p => p && p.netId == item.Owner);
        if (owner == null || bomb.NetworkownerNetIdentity == null || bomb.NetworkownerNetIdentity.netId != item.Owner ||
            bomb.playerRef == null || bomb.playerRef.netId != item.Owner)
        {
            CompleteCase(false, "network owner/playerRef does not match its local actor");
            return;
        }
        if (owner.GameMode.IsPaused) { CompleteCase(false, "match is paused; native countdown cannot be tested", true); return; }
        if (phase == 0)
        {
            if (ev.Countdowns != 0 || ev.Explosions != item.InitialExplosions || bomb.hasExploded)
            {
                CompleteCase(false, "projectile was already naturally counting down/exploding before the controlled trigger", true);
                return;
            }
            if (!bomb.IsStuck)
            {
                if (Time.unscaledTime >= settleDeadline) CompleteCase(false, "C4 did not stick within the 15 second settling window", true);
                return;
            }
            item.Before = Snapshot(bomb);
            item.OwnerExitedAtReady = bomb.ownerExited;
            var trigger = route == "owner" ? owner : LocalPlayers.Players.FirstOrDefault(p => p && p.netId != item.Owner);
            if (trigger == null) { CompleteCase(false, "no different local actor available to trigger this C4", true); return; }
            var collider = EligibleCollider(bomb, trigger);
            if (collider == null) { CompleteCase(false, "actor has no Rigidbody collider accepted by C4 triggerLayers", true); return; }
            item.TriggerActor = trigger.netId;
            if (route == "owner" && !bomb.ownerExited)
            {
                item.OwnerAlreadyInside = bomb._playersInside != null && bomb._playersInside.Contains(collider.attachedRigidbody);
                bomb.OnTriggerEnter(collider);
                if (ev.Countdowns != item.InitialCountdowns)
                {
                    CompleteCase(false, "owner triggered C4 before first leaving its sensor");
                    return;
                }
                item.ProtectionObserved = true;
                phase = 1;
                deadline = Time.unscaledTime + 0.2f;
                Status = "C4 " + item.NetId + " owner protection observed; preparing exit/re-entry";
                return;
            }
            Trigger(item, collider);
            return;
        }
        if (phase == 1)
        {
            if (ev.Countdowns != item.InitialCountdowns) { CompleteCase(false, "countdown started during owner protection observation"); return; }
            if (Time.unscaledTime < deadline) return;
            var collider = EligibleCollider(bomb, owner);
            if (collider == null) { CompleteCase(false, "owner collider is no longer available"); return; }
            Trigger(item, collider);
            return;
        }
        if (Time.unscaledTime >= deadline) CompleteCase(false, "native explosion did not complete after trigger/countdown");
    }

    private static Collider EligibleCollider(StickyBombProjectile bomb, OnlineBasePlayerRef actor)
    {
        bool Accepted(Collider collider) => collider && collider.attachedRigidbody &&
            (bomb.triggerLayers.value & (1 << collider.gameObject.layer)) != 0;
        if (Accepted(actor.PCollider)) return actor.PCollider;
        foreach (var collider in actor.GetComponentsInChildren<Collider>(true))
            if (Accepted(collider)) return collider;
        return null;
    }

    private static void Trigger(Case item, Collider collider)
    {
        var bomb = item.Bomb;
        var ev = For(item.Pointer);
        item.CountdownBeforeTrigger = ev.Countdowns;
        // Native Exit removes the Rigidbody from the sensor set; for the owner
        // it also sets ownerExited. Enter must then schedule the original timer.
        bomb.OnTriggerExit(collider);
        if (route == "owner" && !bomb.ownerExited)
        {
            CompleteCase(false, "native owner exit did not enable owner re-entry");
            return;
        }
        bomb.OnTriggerEnter(collider);
        item.TriggerTime = Time.unscaledTime;
        deadline = item.TriggerTime + Math.Max(5f, bomb.delayedExplosionTime + 5f);
        phase = 2;
        Status = "C4 " + item.NetId + " owner=" + item.Owner + " triggered by=" + item.TriggerActor + "; awaiting native explosion";
        log(Status);
    }

    private static void CompleteCase(bool pass, string reason, bool skipped = false)
    {
        var item = cases[index];
        var ev = For(item.Pointer);
        allPassed &= pass;
        if (!pass) { if (skipped) notTested++; else failures++; }
        string outcome = pass ? "pass" : skipped ? "not-tested" : "fail";
        results.Add(new
        {
            projectileNetId = item.NetId, ownerNetId = item.Owner, triggerActorNetId = item.TriggerActor,
            pass, outcome, reason, route, ownerExitedAtReady = item.OwnerExitedAtReady,
            ownerProtectionObserved = item.ProtectionObserved,
            ownerAlreadyInsideBeforeObservation = item.OwnerAlreadyInside,
            ownerGateBranchCovered = item.ProtectionObserved && !item.OwnerAlreadyInside,
            countdowns = ev.Countdowns - item.InitialCountdowns,
            explosions = ev.Explosions - item.InitialExplosions,
            secondsAfterTrigger = item.TriggerTime == 0 ? 0 : Time.unscaledTime - item.TriggerTime,
            before = item.Before, after = Snapshot(item.Bomb)
        });
        log("C4 " + item.NetId + ": " + outcome.ToUpperInvariant() + " " + reason);
        index++;
        phase = 0;
        Publish();
        if (index >= cases.Count) Finish();
    }

    private static void Finish()
    {
        Running = false;
        var owners = cases.Select(c => c.Owner).Distinct().ToArray();
        bool coverage = expectedOwners.All(owners.Contains) && cases.Count > 0 && owners.Length > 1;
        allPassed &= coverage;
        if (Status != "host session ended during C4 test")
            Status = "C4 native trigger test " + (allPassed ? "PASS" : failures > 0 ? "FAIL" : "NOT TESTED / INCOMPLETE") +
                "; cases=" + results.Count + ", owners=" + owners.Length;
        Publish();
        log(Status + "; " + ResultPath);
    }

    private static void Publish()
    {
        var owners = cases.Select(c => c.Owner).Distinct().ToArray();
        Result = new
        {
            running = Running, pass = !Running && allPassed, status = Status, route, failures, notTested,
            mechanism = "existing thrown C4; native OnTriggerExit/OnTriggerEnter callbacks and original delayed Explode; no physics contact simulation",
            expectedOwnerNetIds = expectedOwners, observedOwnerNetIds = owners,
            missingOwnerNetIds = expectedOwners.Where(o => !owners.Contains(o)).ToArray(),
            initialInspection, results
        };
        File.WriteAllText(ResultPath, JsonSerializer.Serialize(Result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
