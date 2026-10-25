using System;
using System.Collections.Generic;
using GameManager.OnlineDefault;
using HarmonyLib;
using NewUIManager.OnlineBase;
using UnityEngine;

namespace WheelchairLocalCoop;

/// <summary>
/// The original shared WeaponsHUD reads every controller and changes only its
/// P1 manager. Move those two input decisions into each manager's input scope,
/// while retaining the native inventory, equip, ammo and projectile code.
/// </summary>
internal static class WeaponOwnership
{
    private static bool installed;
    private static Action<string> log = _ => { };
    private static float nextErrorLog;
    [ThreadStatic] private static int hudUpdateDepth;
    private static readonly Dictionary<int, HudVisualState> hiddenHuds = new();
    private static readonly Dictionary<WeaponsCollectable, Sprite> weaponIcons = new();

    private sealed class HudVisualState
    {
        internal CanvasGroup Group;
        internal float Alpha;
        internal bool Interactable;
        internal bool BlocksRaycasts;
    }

    internal static void Install(Harmony harmony, Action<string> logger)
    {
        log = logger;
        if (installed) return;
        harmony.Patch(AccessTools.DeclaredMethod(typeof(WeaponsHUD), "Update"),
            prefix: new HarmonyMethod(typeof(WeaponOwnership), nameof(BeforeHudUpdate)),
            finalizer: new HarmonyMethod(typeof(WeaponOwnership), nameof(AfterHudUpdate)));
        var refreshHud = new HarmonyMethod(typeof(WeaponOwnership), nameof(AfterHudRefresh));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(WeaponsHUD), "Initialize"), postfix: refreshHud);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(WeaponsHUD), "RefreshSelected"), postfix: refreshHud);
        var restoreHud = new HarmonyMethod(typeof(WeaponOwnership), nameof(AfterHudDisabled));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(WeaponsHUD), "OnDisable"), postfix: restoreHud);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(WeaponsHUD), "OnDestroy"), postfix: restoreHud);
        var suppressSharedInput = new HarmonyMethod(typeof(WeaponOwnership), nameof(BeforeWeaponCycle));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(OnDefaultWeaponsManager), "NextWeapon"),
            prefix: suppressSharedInput);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(OnDefaultWeaponsManager), "PreviousWeapon"),
            prefix: suppressSharedInput);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(OnDefaultWeaponsManager), "Update"),
            postfix: new HarmonyMethod(typeof(WeaponOwnership), nameof(AfterManagerUpdate)));
        installed = true;
        log("Per-player weapon selection installed; native equip/ammo/projectiles retained.");
    }

    private static bool IsLocalManager(OnDefaultWeaponsManager manager)
    {
        if (!LocalPlayers.Active || manager == null || manager.PlayerRef == null ||
            !InputRouting.IsRegistered(manager)) return false;
        foreach (var actor in LocalPlayers.Players)
            if (actor != null && actor.Pointer == manager.PlayerRef.Pointer) return true;
        return false;
    }

    private static void BeforeHudUpdate(WeaponsHUD __instance, out bool __state)
    {
        // Restore before the native update when returning to ordinary play, so
        // its current animation/input decisions can then proceed normally.
        if (!LocalPlayers.Active) RestoreHud(__instance);
        __state = IsLocalManager(__instance.weaponsManager);
        if (__state) hudUpdateDepth++;
    }

    private static Exception AfterHudUpdate(WeaponsHUD __instance, Exception __exception, bool __state)
    {
        if (__state) hudUpdateDepth--;
        UpdateHudPresentation(__instance);
        return __exception;
    }

    private static void AfterHudRefresh(WeaponsHUD __instance) => UpdateHudPresentation(__instance);

    private static void AfterHudDisabled(WeaponsHUD __instance) => RestoreHud(__instance);

    internal static Sprite GetIcon(WeaponsCollectable type)
    {
        return weaponIcons.TryGetValue(type, out var icon) && icon ? icon : null;
    }

    private static void UpdateHudPresentation(WeaponsHUD hud)
    {
        try
        {
            if (!hud) return;
            // Reuse the game's sprites without changing the inventory or its
            // element components. Unity may replace them after a scene change.
            var elements = hud.weaponsList;
            if (elements != null)
                foreach (var element in elements)
                    if (element && element.icon && element.icon.sprite)
                        weaponIcons[element.WeaponType] = element.icon.sprite;

            if (!LocalPlayers.Active)
            {
                RestoreHud(hud);
                return;
            }
            var group = hud.canvasGroup;
            if (!group) return;
            int key = hud.GetInstanceID();
            if (hiddenHuds.TryGetValue(key, out var saved) &&
                (!saved.Group || saved.Group.Pointer != group.Pointer))
            {
                RestoreHud(hud);
                saved = null;
            }
            if (saved == null)
            {
                hiddenHuds[key] = new HudVisualState
                {
                    Group = group,
                    Alpha = group.alpha,
                    Interactable = group.interactable,
                    BlocksRaycasts = group.blocksRaycasts
                };
            }

            // RefreshSelected and HideAfterDelay own a DOTween fade. Cancel
            // only that visual tween so it cannot reveal the shared HUD after
            // our Update; do not disable the HUD or stop its inventory logic.
            if (hud.fadeTween != null) hud.KillFadeTween(false);
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }
        catch (Exception error)
        {
            ReportHudError("hide", error);
        }
    }

    private static void RestoreHud(WeaponsHUD hud)
    {
        try
        {
            if (hud == null || !hiddenHuds.Remove(hud.GetInstanceID(), out var saved)) return;
            // OnDisable also restores state: a disabled HUD will not receive
            // Update when the local session ends, and may later be reused.
            if (!saved.Group) return;
            saved.Group.alpha = saved.Alpha;
            saved.Group.interactable = saved.Interactable;
            saved.Group.blocksRaycasts = saved.BlocksRaycasts;
        }
        catch (Exception error)
        {
            ReportHudError("restore", error);
        }
    }

    private static void ReportHudError(string action, Exception error)
    {
        if (Time.unscaledTime < nextErrorLog) return;
        nextErrorLog = Time.unscaledTime + 5f;
        log("Shared weapon HUD " + action + " failed: " + error.Message);
    }

    private static bool BeforeWeaponCycle(OnDefaultWeaponsManager __instance)
    {
        // Keep the HUD's ammunition refresh/animation; suppress only its pooled
        // input calling the P1 manager. Calls from gameplay/reset still run.
        return hudUpdateDepth == 0 || !IsLocalManager(__instance);
    }

    private static void AfterManagerUpdate(OnDefaultWeaponsManager __instance)
    {
        if (!IsLocalManager(__instance)) return;
        try
        {
            // InputRouting scopes this same Update using a prefix/finalizer.
            // Harmony postfixes run before that finalizer restores global maps.
            // These original actions retain Q/E and d-pad left/right bindings,
            // edge-trigger semantics, and gameplay/pause/death/countdown gates.
            var maps = InputGetKeys.Maps;
            if (maps == null) return;
            var previous = maps.Global.QKey;
            var next = maps.Global.EKey;
            bool goPrevious = previous != null && previous.enabled && previous.WasPressedThisFrame();
            bool goNext = next != null && next.enabled && next.WasPressedThisFrame();
            if (!goPrevious && !goNext) return;
            int oldIndex = __instance.CurrentWeaponIndex;
            // Native HUD gives Q/previous precedence when both are pressed.
            if (goPrevious) __instance.PreviousWeapon();
            else __instance.NextWeapon();
            if (oldIndex != __instance.CurrentWeaponIndex)
                log("Weapon switched: " + __instance.PlayerRef.gameObject.name + " " +
                    oldIndex + " -> " + __instance.CurrentWeaponIndex + ".");
        }
        catch (Exception error)
        {
            if (Time.unscaledTime < nextErrorLog) return;
            nextErrorLog = Time.unscaledTime + 5f;
            log("Per-player weapon selection failed: " + error.Message);
        }
    }
}
