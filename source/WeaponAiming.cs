using System;
using System.Collections.Generic;
using GameManager.OnlineBase;
using GameManager.OnlineDefault;
using HarmonyLib;
using NewUIManager.OnlyUp;
using NewUIManager.TenLevels.Online;
using UnityEngine;

namespace WheelchairLocalCoop;

/// <summary>Correct viewport aim without intercepting IL2CPP nullable value parameters.</summary>
internal static class WeaponAiming
{
    internal sealed class AimScope
    {
        internal Camera Camera;
        internal Rect OriginalRect;
        internal float OriginalAspect;
        internal bool Active;
        internal readonly List<LayerRestore> Layers = new();
    }

    internal readonly struct LayerRestore
    {
        internal readonly GameObject Target;
        internal readonly int Layer;
        internal LayerRestore(GameObject target, int layer) { Target = target; Layer = layer; }
    }

    private static bool installed;
    private static bool reportedFailure;
    [ThreadStatic] private static int activeScopes;
    internal static int ActiveScopes => activeScopes;

    internal static void Install(Harmony harmony)
    {
        if (installed) return;
        // GetAimPoint has a Nullable<LayerMask> value parameter. The generated
        // native->managed trampoline mistakes that value for a pointer, so even
        // a prefix that ignores it cannot safely patch either overload.
        // Scope the existing, distinct parameterless weapon updates instead.
        var prefix = new HarmonyMethod(typeof(WeaponAiming), nameof(BeforeWeaponUpdate));
        var finalizer = new HarmonyMethod(typeof(WeaponAiming), nameof(AfterWeaponUpdate));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(RpgWeapon), "OnUpdate"),
            prefix: prefix, finalizer: finalizer);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(StickyBombWeapon), "OnUpdate"),
            prefix: prefix, finalizer: finalizer);
        // These distinct UI methods only animate the original full-screen aim
        // image. Preserve their parent Update's timer, pause and countdown work.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(OnTenLevelsGameplayUI), "Aiming"),
            prefix: new HarmonyMethod(typeof(WeaponAiming), nameof(BeforeTenLevelsReticle)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(OnlyUpGameplayUIScreen), "Aiming"),
            prefix: new HarmonyMethod(typeof(WeaponAiming), nameof(BeforeOnlyUpReticle)));
        installed = true;
    }

    private static void BeforeWeaponUpdate(WeaponBase __instance, out AimScope __state)
    {
        __state = EnterScope(__instance ? __instance.playerRef : null);
    }

    private static Exception AfterWeaponUpdate(Exception __exception, AimScope __state)
    {
        ExitScope(__state);
        return __exception;
    }

    internal static AimScope EnterScope(OnlineBasePlayerRef actor)
    {
        if (!actor || !InputRouting.IsRegistered(actor)) return null;
        var controller = actor.CamController;
        if (!controller || !controller.mainCamera) return null;
        Camera camera = controller.mainCamera;
        Rect viewport = camera.rect;
        if (viewport.width <= 0f || viewport.height <= 0f ||
            (Mathf.Approximately(viewport.x, 0f) && Mathf.Approximately(viewport.y, 0f) &&
             Mathf.Approximately(viewport.width, 1f) && Mathf.Approximately(viewport.height, 1f))) return null;

        var state = new AimScope { Camera = camera, OriginalRect = viewport, OriginalAspect = camera.aspect };
        try
        {
            state.Active = true;
            activeScopes++;
            // The native weapon queries Screen.width/2, Screen.height/2.
            // Translate only this camera's viewport so that this screen point
            // is its center. Its size, aspect, transform and projection remain
            // unchanged. All native raycast/mask/range/miss logic still runs.
            // The finalizer restores the viewport synchronously before render.
            camera.rect = new Rect((1f - viewport.width) * 0.5f, (1f - viewport.height) * 0.5f,
                viewport.width, viewport.height);
            camera.aspect = state.OriginalAspect;
            // Both native weapons pass an empty optional mask. GetAimPoint
            // therefore uses DefaultRaycastLayers (-5), excluding layer 2.
            // The third-person center ray must not target its own wheelchair.
            // Retain enabled/trigger/rigidbody state and restore every layer
            // before returning from this synchronous native weapon update.
            var seen = new HashSet<int>();
            IgnoreOwnCollider(state, actor, actor.PCollider, seen);
            var owned = actor.AllColliders;
            if (owned != null)
                foreach (var collider in owned) IgnoreOwnCollider(state, actor, collider, seen);
            foreach (var collider in actor.GetComponentsInChildren<Collider>(true))
                IgnoreOwnCollider(state, actor, collider, seen);
            return state;
        }
        catch (Exception ex)
        {
            ExitScope(state);
            ReportFailure(ex);
            return null;
        }
    }

    private static void IgnoreOwnCollider(AimScope state, OnlineBasePlayerRef actor, Collider collider,
        HashSet<int> seen)
    {
        if (!collider) return;
        var owner = collider.GetComponentInParent<OnlineBasePlayerRef>();
        if (owner && owner.Pointer != actor.Pointer) return;
        var target = collider.gameObject;
        if (!target || !seen.Add(target.GetInstanceID())) return;
        // Save before mutation, including an outer scope's existing layer 2.
        // Thus nested scopes restore to 2 and the outermost restores the game.
        state.Layers.Add(new LayerRestore(target, target.layer));
        target.layer = 2; // Unity's built-in Ignore Raycast layer.
    }

    internal static void ExitScope(AimScope state)
    {
        if (state == null || !state.Active) return;
        state.Active = false;
        try
        {
            if (state.Camera)
            {
                try { state.Camera.rect = state.OriginalRect; }
                catch (Exception ex) { ReportFailure(ex); }
                try { state.Camera.aspect = state.OriginalAspect; }
                catch (Exception ex) { ReportFailure(ex); }
            }
        }
        catch (Exception ex) { ReportFailure(ex); }
        finally
        {
            try
            {
                for (int i = state.Layers.Count - 1; i >= 0; --i)
                {
                    try
                    {
                        var saved = state.Layers[i];
                        if (saved.Target) saved.Target.layer = saved.Layer;
                    }
                    catch (Exception ex) { ReportFailure(ex); }
                }
            }
            finally { activeScopes--; }
        }
    }

    private static void ReportFailure(Exception ex)
    {
        if (reportedFailure) return;
        reportedFailure = true;
        Debug.LogWarning("LocalCoop: viewport aim scope failed. " + ex.Message);
    }

    private static bool BeforeTenLevelsReticle(OnTenLevelsGameplayUI __instance)
    {
        return KeepNativeReticle(__instance.aimImage);
    }

    private static bool BeforeOnlyUpReticle(OnlyUpGameplayUIScreen __instance)
    {
        return KeepNativeReticle(__instance.aimImage);
    }

    private static bool KeepNativeReticle(CanvasGroup image)
    {
        if (!LocalPlayers.Active) return true;
        if (image && image.gameObject.activeSelf) image.gameObject.SetActive(false);
        // Existing fade tweens cannot make an inactive object visible; the
        // native method resumes its normal activation when local co-op ends.
        return false;
    }
}
