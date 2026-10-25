using System;
using System.Runtime.ExceptionServices;
using GameManager.OnlineBase;
using HarmonyLib;
using UnityEngine;

namespace WheelchairLocalCoop;

/// <summary>Release stale wheel contacts before the native physics update reads them.</summary>
internal static class WheelchairMovement
{
    private static bool installed;
    private static Action<string> log = _ => { };
    [ThreadStatic] private static bool nativeTestCall;
    [ThreadStatic] private static Exception nativeTestException;
    internal static int InvalidContactsCleared { get; private set; }

    internal static void Install(Harmony harmony, Action<string> logger)
    {
        log = logger;
        if (installed) return;
        harmony.Patch(AccessTools.DeclaredMethod(typeof(OnlineBaseWheelchairController), "GetPlatformVelocity"),
            prefix: new HarmonyMethod(typeof(WheelchairMovement), nameof(BeforeGetPlatformVelocity)),
            finalizer: Plugin.TestMode
                ? new HarmonyMethod(typeof(WheelchairMovement), nameof(CaptureNativeFixtureException)) : null);
        installed = true;
        log("Wheel contact lifetime guard installed.");
    }

    private static Exception CaptureNativeFixtureException(Exception __exception)
    {
        if (!nativeTestCall || __exception == null) return __exception;
        // Keep the exception inside the managed detour. Otherwise the outer
        // IL2CPP-to-managed trampoline may log and consume it before the fixture
        // can observe the failure. This hook is never installed in production.
        nativeTestException = __exception;
        return null;
    }

    private static bool BeforeGetPlatformVelocity(OnlineBaseWheelchairController __instance, ref Vector3 __result)
    {
        if ((Plugin.TestMode && nativeTestCall) || !LocalPlayers.Active ||
            !InputRouting.IsRegistered(__instance)) return true;
        var collider = __instance.groundWheelHit.collider;
        // The native method uses CLR ?. semantics: a destroyed Unity Collider
        // still has a non-null managed reference, so attachedRigidbody throws.
        // A genuinely null contact already has a correct native zero fallback.
        if (ReferenceEquals(collider, null) || collider) return true;
        __instance.groundWheelHit = new WheelHit();
        __result = Vector3.zero;
        InvalidContactsCleared++;
        log("Cleared destroyed wheel-ground contact for netId=" + __instance.netId +
            "; native physics can refresh the contact. Total=" + InvalidContactsCleared + ".");
        return false;
    }

    // A synchronous test fixture can compare the same native method with and
    // without this guard. The bypass cannot run in the normal game profile.
    internal static Vector3 NativeForTest(OnlineBaseWheelchairController controller)
    {
        if (!Plugin.TestMode) throw new InvalidOperationException("Native movement fixture requires test mode.");
        bool previous = nativeTestCall;
        Exception previousException = nativeTestException;
        nativeTestCall = true;
        nativeTestException = null;
        try
        {
            Vector3 result = controller.GetPlatformVelocity();
            if (nativeTestException != null) ExceptionDispatchInfo.Capture(nativeTestException).Throw();
            return result;
        }
        finally
        {
            nativeTestCall = previous;
            nativeTestException = previousException;
        }
    }
}
