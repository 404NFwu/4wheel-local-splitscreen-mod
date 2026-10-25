using System;
using System.Collections.Generic;
using GameManager.OnlineBase;
using UnityEngine;

namespace WheelchairLocalCoop;

/// <summary>Synchronous cached-contact fixture; creates and destroys only its own objects.</summary>
internal static class MovementTests
{
    private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };

    internal static object Run()
    {
        if (!Plugin.TestMode || !LocalPlayers.Active)
            throw new InvalidOperationException("Movement fixtures require an active local host in test mode.");
        var cases = new List<object>();
        bool pass = true;
        foreach (var actor in LocalPlayers.Players)
        {
            if (!actor || !actor.WcController || !InputRouting.IsRegistered(actor))
                throw new InvalidOperationException("Each local actor must have a registered controller.");
            var result = RunActor(actor, out bool actorPassed);
            pass &= actorPassed;
            cases.Add(result);
        }
        return new { pass, mechanism = "Actual native GetPlatformVelocity compared with guarded call; fixture collider destroyed immediately, original cached hit restored in finally", cases };
    }

    private static object RunActor(OnlineBasePlayerRef actor, out bool pass)
    {
        var controller = actor.WcController;
        var originalHit = controller.groundWheelHit;
        GameObject fixture = null;
        pass = false;
        try
        {
            fixture = new GameObject("LocalCoop_MovementContactFixture_" + actor.netId);
            fixture.transform.position = new Vector3(20000f, 20000f, 20000f);
            var collider = fixture.AddComponent<BoxCollider>();
            var hit = new WheelHit();
            hit.m_Collider = collider;
            hit.m_Point = fixture.transform.position + new Vector3(2f, 0.5f, -1f);
            controller.groundWheelHit = hit;
            Vector3 staticNative = WheelchairMovement.NativeForTest(controller);
            Vector3 staticGuarded = controller.GetPlatformVelocity();

            var body = fixture.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.centerOfMass = Vector3.zero;
            body.linearVelocity = new Vector3(1.5f, -0.25f, 2f);
            body.angularVelocity = new Vector3(0f, 1.25f, 0.5f);
            Vector3 expected = body.linearVelocity + Vector3.Cross(body.angularVelocity,
                hit.point - body.worldCenterOfMass);
            Vector3 movingNative = WheelchairMovement.NativeForTest(controller);
            Vector3 movingGuarded = controller.GetPlatformVelocity();

            // DestroyImmediate is intentionally limited to this new fixture's
            // collider. The cached WheelHit retains its managed Unity wrapper.
            UnityEngine.Object.DestroyImmediate(collider);
            bool cachedWrapperRemains = !ReferenceEquals(controller.groundWheelHit.collider, null);
            bool cachedUnityObjectDestroyed = !controller.groundWheelHit.collider;
            string baselineException = null;
            try { WheelchairMovement.NativeForTest(controller); }
            catch (Exception error) { baselineException = error.ToString(); }
            int clearedBefore = WheelchairMovement.InvalidContactsCleared;
            Vector3 recovered = controller.GetPlatformVelocity();
            bool cacheCleared = ReferenceEquals(controller.groundWheelHit.collider, null);
            Vector3 repeated = controller.GetPlatformVelocity();
            bool staticPass = staticNative.sqrMagnitude < 0.000001f && staticGuarded.sqrMagnitude < 0.000001f;
            bool movingPass = Vector3.Distance(expected, movingNative) < 0.0001f &&
                Vector3.Distance(expected, movingGuarded) < 0.0001f;
            bool recoveryPass = cachedWrapperRemains && cachedUnityObjectDestroyed && baselineException != null &&
                cacheCleared && recovered.sqrMagnitude < 0.000001f && repeated.sqrMagnitude < 0.000001f &&
                WheelchairMovement.InvalidContactsCleared == clearedBefore + 1;
            pass = staticPass && movingPass && recoveryPass;
            return new
            {
                ownerNetId = actor.netId, pass,
                staticContact = new { pass = staticPass, native = V(staticNative), guarded = V(staticGuarded) },
                movingContact = new { pass = movingPass, expected = V(expected), native = V(movingNative), guarded = V(movingGuarded) },
                destroyedContact = new { pass = recoveryPass, cachedWrapperRemains, cachedUnityObjectDestroyed,
                    baselineException, cacheCleared, recovered = V(recovered), repeated = V(repeated) }
            };
        }
        finally
        {
            if (controller) controller.groundWheelHit = originalHit;
            if (fixture) UnityEngine.Object.DestroyImmediate(fixture);
        }
    }
}
