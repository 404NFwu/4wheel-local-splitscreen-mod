using System;
using System.Collections.Generic;
using System.Reflection;
using GameManager.OnlineBase;
using GameManager.OnlineTenLevels;
using GameManager.OnlyUp;
using HarmonyLib;
using Mirror;
using NewUIManager.Base;
using UnityEngine;
using UnityEngine.InputSystem;

namespace WheelchairLocalCoop;

/// <summary>Additional host-owned actors, with one real Mirror/Steam connection.</summary>
internal static class LocalPlayers
{
    private static readonly List<OnlineBasePlayerRef> players = new();
    private static readonly HashSet<IntPtr> extras = new();
    private static readonly Dictionary<IntPtr, float> respawnAt = new();
    private static readonly HashSet<IntPtr> controlsReleased = new();
    private static readonly Dictionary<IntPtr, float> restoreControlsAt = new();
    private static readonly Dictionary<IntPtr, float> manualHoldStarted = new();
    private static readonly HashSet<IntPtr> manualHoldTriggered = new();
    private static readonly Dictionary<IntPtr, int> resetCounts = new();
    private const float ManualRespawnHoldSeconds = 1.2f;
    private static Action<string> log = _ => { };
    private static float nextSpawnAttempt;
    private static bool clearing;

    public static IReadOnlyList<OnlineBasePlayerRef> Players => players;
    public static bool Active => players.Count > 1 && NetworkServer.active && NetworkClient.active;

    public static void Install(Harmony harmony, Action<string> logger)
    {
        log = logger;
        Prefix(harmony, typeof(NetworkIdentity), "OnStartClient", nameof(BeforeIdentityStartClient));
        Prefix(harmony, typeof(OnBaseSingleSceneGameMode), "RegisterLocalGamePlayer", nameof(BeforeRegister));
        Prefix(harmony, typeof(OnBaseSingleSceneGameMode), "UnregisterGamePlayer", nameof(BeforeUnregister));
        Prefix(harmony, typeof(OnlineBasePlayerRef), "SetupOnlineCamera", nameof(BeforeSetupCamera));
        Prefix(harmony, typeof(OnlineBaseCameraController), "OnStartClient", nameof(BeforeCameraStart));
        Prefix(harmony, typeof(OnBaseSingleSceneGameMode), "PlayerDeath", nameof(BeforeDeathScreen));
        Prefix(harmony, typeof(OnBaseSingleSceneGameMode), "LocalPlayerFinishLineTrigger", nameof(BeforeLocalFinishScreen));
        Prefix(harmony, typeof(HoldToRestartGame), "Update", nameof(BeforeGlobalRespawnHold));
        Prefix(harmony, typeof(TLOnlineGameMode), "CheckRespawnInputInUI", nameof(BeforeGlobalRespawnInput));
        Prefix(harmony, typeof(OnlyUpGameMode), "CheckRespawnInputInUI", nameof(BeforeGlobalRespawnInput));
        harmony.Patch(AccessTools.Method(typeof(OnlineBasePlayerRef), "ResetPlayer"),
            postfix: new HarmonyMethod(typeof(LocalPlayers), nameof(AfterResetPlayer)));
        log("Local player lifecycle patches installed.");
    }

    private static void Prefix(Harmony harmony, Type target, string method, string callback)
    {
        var original = AccessTools.Method(target, method)
            ?? throw new MissingMethodException(target.FullName, method);
        var prefix = typeof(LocalPlayers).GetMethod(callback, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(LocalPlayers), callback);
        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
    }

    public static void Tick(int desiredPlayers)
    {
        desiredPlayers = Math.Clamp(desiredPlayers, 1, 4);
        if (!NetworkServer.active || !NetworkClient.active || NetworkClient.localPlayer == null)
        {
            if (players.Count != 0) Clear();
            return;
        }

        var leader = NetworkClient.localPlayer.GetComponent<OnlineBasePlayerRef>();
        if (leader == null || leader.GameMode == null || !leader.GameMode.IsGameplayScene())
        {
            if (players.Count != 0) Clear();
            return;
        }

        if (players.Count == 0 || players[0] == null || players[0].Pointer != leader.Pointer)
        {
            Clear();
            players.Add(leader);
            log("Host gameplay actor detected. Local multiplayer session initialized.");
        }

        for (int i = players.Count - 1; i >= 1; --i)
        {
            if (players[i] == null || i >= desiredPlayers)
                RemoveAt(i);
        }

        if (players.Count < desiredPlayers && Time.unscaledTime >= nextSpawnAttempt)
        {
            nextSpawnAttempt = Time.unscaledTime + 2f;
            try
            {
                SpawnExtra(leader, players.Count);
                nextSpawnAttempt = 0f;
            }
            catch (Exception ex)
            {
                log("Local actor creation failed; retrying in 2 seconds: " + ex);
            }
        }

        for (int i = 0; i < players.Count; ++i)
        {
            var actor = players[i];
            if (actor == null) continue;
            var pointer = actor.Pointer;
            if (i != 0 && actor.netIdentity.clientStarted)
            {
                // Initial countdown releases only the game's single InstancedGamePlayer.
                // Do the equivalent once for each additional actor, preserving later finish locks.
                var mode = actor.GameMode;
                if (!controlsReleased.Contains(pointer) && CanControl(actor))
                {
                    actor.SetControlsEnabled(true);
                    controlsReleased.Add(pointer);
                }
            }

            if (!Active) continue;
            // ResetPlayer restores controls only when its initial gameInProgress
            // check succeeds. Recheck once after its delayed restore has completed,
            // preserving pause/countdown/finish locks rather than enabling every frame.
            if (restoreControlsAt.TryGetValue(pointer, out var restoreAt) &&
                Time.unscaledTime >= restoreAt && !actor.IsDead)
            {
                if (HasFinished(actor)) restoreControlsAt.Remove(pointer);
                else if (CanControl(actor))
                {
                    actor.SetControlsEnabled(true);
                    controlsReleased.Add(pointer);
                    restoreControlsAt.Remove(pointer);
                    log("Post-respawn controls restored for " + actor.PlayerName + ".");
                }
            }
            TickManualRespawn(actor);
            if (!actor.IsDead)
            {
                respawnAt.Remove(pointer);
                continue;
            }
            if (!respawnAt.TryGetValue(pointer, out var due))
                respawnAt[pointer] = Time.unscaledTime + 2.5f;
            else if (Time.unscaledTime >= due && CanRespawn(actor))
                Respawn(actor);
        }
    }

    private static void SpawnExtra(OnlineBasePlayerRef leader, int index)
    {
        var prefab = leader.GameMode.PrefabGamePlay;
        if (prefab == null) throw new InvalidOperationException("The current mode has no gameplay player prefab.");
        if (NetworkServer.localConnection == null) throw new InvalidOperationException("No host connection.");
        var position = leader.transform.position;
        // Keep newcomers close to P1, with distinct collider space.
        var offset = index switch
        {
            1 => new Vector3(-1.4f, 0.35f, -1.4f),
            2 => new Vector3(1.4f, 0.35f, -1.4f),
            _ => new Vector3(0f, 0.35f, 1.4f)
        };
        var gameObject = UnityEngine.Object.Instantiate(prefab, position + leader.transform.rotation * offset,
            leader.transform.rotation);
        OnlineBasePlayerRef actor = null;
        try
        {
            actor = gameObject.GetComponent<OnlineBasePlayerRef>();
            if (actor == null) throw new InvalidOperationException("Gameplay prefab is not an OnlineBasePlayerRef.");
            gameObject.name = "LocalCoop_Player" + (index + 1);
            extras.Add(actor.netIdentity.Pointer);
            players.Add(actor);
            actor.NetworkplayerName = "Local Player " + (index + 1);
            actor.NetworksyncSkinData = leader.NetworksyncSkinData;
            actor.NetworkisHost = false;
            DisableActorCameras(actor);
            // Spawn supports several owned identities; AddPlayerForConnection does not.
            NetworkServer.Spawn(gameObject, NetworkServer.localConnection);
            log("Spawned local player " + (index + 1) + ", netId=" + actor.netId + ".");
        }
        catch
        {
            if (actor != null)
            {
                players.Remove(actor);
                extras.Remove(actor.netIdentity.Pointer);
            }
            if (gameObject != null) UnityEngine.Object.Destroy(gameObject);
            throw;
        }
    }

    public static bool IsExtra(NetworkIdentity identity) => identity != null && extras.Contains(identity.Pointer);

    private static void BeforeIdentityStartClient(NetworkIdentity __instance)
    {
        if (!IsExtra(__instance) || !NetworkServer.active) return;
        // Host spawn normally overwrites this immediately before OnStartClient.
        // Do not change NetworkClient.localPlayer or connection.identity.
        __instance.isLocalPlayer = true;
        __instance.isOwned = true;
        __instance.connectionToServer = NetworkClient.connection;
        log("Initializing host-owned local actor, netId=" + __instance.netId + ".");
    }

    private static bool BeforeRegister(OnBaseSingleSceneGameMode __instance, OnlineBasePlayerRef gamePlayer)
    {
        if (gamePlayer == null || !IsExtra(gamePlayer.netIdentity)) return true;
        if (!__instance.AllGamePlayers.Contains(gamePlayer))
            __instance.AllGamePlayers.Add(gamePlayer);
        __instance.OnGamePlayersUpdated?.Invoke();
        return false;
    }

    private static bool BeforeUnregister(OnBaseSingleSceneGameMode __instance, OnlineBasePlayerRef gamePlayer)
    {
        if (gamePlayer == null || !IsExtra(gamePlayer.netIdentity)) return true;
        __instance.AllGamePlayers.Remove(gamePlayer);
        __instance.OnGamePlayersUpdated?.Invoke();
        return false;
    }

    private static bool BeforeSetupCamera(OnlineBasePlayerRef __instance)
    {
        if (!IsExtra(__instance.netIdentity)) return true;
        DisableActorCameras(__instance);
        return false;
    }

    private static bool BeforeCameraStart(OnlineBaseCameraController __instance)
    {
        if (!IsExtra(__instance.netIdentity)) return true;
        __instance.enabled = false;
        return false;
    }

    private static void DisableActorCameras(OnlineBasePlayerRef actor)
    {
        if (actor.CamController != null) actor.CamController.enabled = false;
        // Only descendants of this actor: never disable a shared scene Camera.main.
        foreach (var camera in actor.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        foreach (var listener in actor.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
    }

    private static bool BeforeDeathScreen(OnBaseSingleSceneGameMode __instance)
    {
        // A shared screen cannot open a blocking death menu for one participant.
        return !Active || players[0] == null || players[0].GameMode.Pointer != __instance.Pointer;
    }

    private static bool BeforeLocalFinishScreen(OnBaseSingleSceneGameMode __instance, OnlineBasePlayerRef gamePlayer)
    {
        if (!Active || players[0] == null || players[0].GameMode.Pointer != __instance.Pointer)
            return true;
        // PlayerFinishLineTrigger sends CmdNotifyFinishLineTrigger after this call.
        // Keep that server-side result and the mode's AllPlayersFinished completion;
        // suppress the single-player local overlay/event until the whole group finishes.
        log((gamePlayer != null ? gamePlayer.PlayerName : "Local player") + " reached the finish line.");
        return false;
    }

    private static bool BeforeGlobalRespawnHold(HoldToRestartGame __instance)
    {
        if (!Active) return true;
        // The stock widget pools Y from all pads and always resets the single P1
        // reference. Local hold timers below replace both this input and overlay.
        __instance.ResetComponent(true);
        return false;
    }

    private static bool BeforeGlobalRespawnInput(OnBaseSingleSceneGameMode __instance)
    {
        return !Active || players[0] == null || players[0].GameMode.Pointer != __instance.Pointer;
    }

    private static bool HasFinished(OnlineBasePlayerRef actor)
    {
        var finishActor = actor.TryCast<OnDefaultFinishLinePlayerRef>();
        return finishActor != null && finishActor.LevelCompleted;
    }

    private static bool CanRespawn(OnlineBasePlayerRef actor)
    {
        if (actor == null || actor.GameMode == null || HasFinished(actor)) return false;
        var mode = actor.GameMode;
        return mode.IsGameplayScene() && !mode.IsPaused && mode.GameInProgress &&
            (!mode.EnableInitialCountdown || mode.RaceStarted);
    }

    private static bool CanControl(OnlineBasePlayerRef actor) => !actor.IsDead && CanRespawn(actor);

    private static bool IsLocalActor(OnlineBasePlayerRef actor)
    {
        if (actor == null) return false;
        foreach (var candidate in players)
            if (candidate != null && candidate.Pointer == actor.Pointer) return true;
        return false;
    }

    private static void AfterResetPlayer(OnlineBasePlayerRef __instance)
    {
        if (!Active || !IsLocalActor(__instance)) return;
        var pointer = __instance.Pointer;
        restoreControlsAt[pointer] = Time.unscaledTime + 0.25f;
        resetCounts.TryGetValue(pointer, out int count);
        resetCounts[pointer] = count + 1;
        log("Reset completed for " + __instance.PlayerName + ", count=" + (count + 1) +
            ", gameInProgress=" + __instance.GameMode.GameInProgress +
            ", raceStarted=" + __instance.GameMode.RaceStarted +
            ", paused=" + __instance.GameMode.IsPaused + ".");
    }

    private static void TickManualRespawn(OnlineBasePlayerRef actor)
    {
        var pointer = actor.Pointer;
        var pad = InputRouting.GetPad(actor);
        bool held = pad != null && pad.added && pad.enabled && pad.buttonNorth.isPressed;
        if (InputRouting.IsKeyboard(actor) && Keyboard.current != null)
            held |= Keyboard.current.rKey.isPressed;
        if (!held || !CanRespawn(actor))
        {
            manualHoldStarted.Remove(pointer);
            if (!held) manualHoldTriggered.Remove(pointer);
            return;
        }
        if (manualHoldTriggered.Contains(pointer)) return;
        if (!manualHoldStarted.TryGetValue(pointer, out var started))
            manualHoldStarted[pointer] = Time.unscaledTime;
        else if (Time.unscaledTime - started >= ManualRespawnHoldSeconds)
        {
            manualHoldTriggered.Add(pointer);
            manualHoldStarted.Remove(pointer);
            ResetActor(actor);
        }
    }

    public static float GetRespawnHoldProgress(OnlineBasePlayerRef actor)
    {
        if (actor == null || !manualHoldStarted.TryGetValue(actor.Pointer, out var started)) return 0f;
        return Mathf.Clamp01((Time.unscaledTime - started) / ManualRespawnHoldSeconds);
    }

    public static int GetResetCount(OnlineBasePlayerRef actor) =>
        actor != null && resetCounts.TryGetValue(actor.Pointer, out var count) ? count : 0;

    /// <summary>May also be called by the input router for a per-player respawn button.</summary>
    public static void Respawn(OnlineBasePlayerRef actor)
    {
        if (!Active || actor == null || !actor.IsDead || !CanRespawn(actor)) return;
        ResetActor(actor);
    }

    private static void ResetActor(OnlineBasePlayerRef actor)
    {
        try
        {
            respawnAt[actor.Pointer] = Time.unscaledTime + 5f;
            actor.ResetPlayer();
            log("Respawning " + actor.PlayerName + ".");
        }
        catch (Exception ex)
        {
            log("Individual respawn failed: " + ex);
        }
    }

    private static void RemoveAt(int index)
    {
        var actor = players[index];
        players.RemoveAt(index);
        if (actor == null) return;
        var identityPointer = actor.netIdentity.Pointer;
        var pointer = actor.Pointer;
        try
        {
            InputRouting.Unregister(actor.transform);
            if (NetworkServer.active && actor.netId != 0)
                NetworkServer.Destroy(actor.gameObject);
            else
                UnityEngine.Object.Destroy(actor.gameObject);
        }
        catch (Exception ex)
        {
            log("Local actor cleanup: " + ex.Message);
        }
        finally
        {
            extras.Remove(identityPointer);
            controlsReleased.Remove(pointer);
            respawnAt.Remove(pointer);
            restoreControlsAt.Remove(pointer);
            manualHoldStarted.Remove(pointer);
            manualHoldTriggered.Remove(pointer);
            resetCounts.Remove(pointer);
        }
    }

    public static void Clear()
    {
        if (clearing) return;
        clearing = true;
        try
        {
            for (int i = players.Count - 1; i >= 1; --i) RemoveAt(i);
            if (players.Count > 0 && players[0] != null)
                InputRouting.Unregister(players[0].transform);
            players.Clear();
            extras.Clear();
            respawnAt.Clear();
            controlsReleased.Clear();
            restoreControlsAt.Clear();
            manualHoldStarted.Clear();
            manualHoldTriggered.Clear();
            resetCounts.Clear();
            nextSpawnAttempt = 0;
        }
        finally { clearing = false; }
    }
}
