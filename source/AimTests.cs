using System;
using System.Collections.Generic;
using GameManager.OnlineBase;
using UnityEngine;

namespace WheelchairLocalCoop;

// Explicit test-command diagnostics; never called by normal gameplay.
internal static class AimTests
{
    private sealed class ExpectedScopeException : Exception { }
    private sealed class SavedLayer
    {
        internal GameObject Target;
        internal int Layer;
    }

    internal static object Run()
    {
        int scopesBefore = WeaponAiming.ActiveScopes;
        var players = new List<object>();
        bool pass = LocalPlayers.Players.Count > 0 && scopesBefore == 0;
        if (scopesBefore == 0)
        {
            for (int i = 0; i < LocalPlayers.Players.Count; ++i)
            {
                var actor = LocalPlayers.Players[i];
                try
                {
                    var result = CheckPlayer(actor, i + 1);
                    players.Add(result.Report);
                    pass &= result.Pass;
                }
                catch (Exception ex)
                {
                    pass = false;
                    players.Add(new { player = i + 1, pass = false, error = ex.ToString() });
                }
            }
        }
        int scopesAfter = WeaponAiming.ActiveScopes;
        pass &= scopesAfter == 0;
        return new { pass, scopesBefore, scopesAfter, players };
    }

    private static (bool Pass, object Report) CheckPlayer(OnlineBasePlayerRef actor, int player)
    {
        if (!actor || !actor.CamController || !actor.CamController.mainCamera)
            throw new InvalidOperationException("Local player camera is not ready.");

        Camera camera = actor.CamController.mainCamera;
        Rect originalRect = camera.rect;
        float originalAspect = camera.aspect;
        var ownLayers = SnapshotLayers(actor);
        var otherLayers = new List<SavedLayer>();
        foreach (var other in LocalPlayers.Players)
            if (other && other.Pointer != actor.Pointer) otherLayers.AddRange(SnapshotLayers(other));
        WeaponAiming.AimScope normal = null, outer = null, inner = null;
        float originError = -1f, directionError = -1f;
        bool oneScope = false, normalRestored = false, normalDepthRestored = false;
        bool twoScopes = false, innerRestored = false, exceptionObserved = false;
        bool nestedRestored = false, nestedDepthRestored = false, cleanupApplied = false;
        bool ownIgnored = false, normalLayersRestored = false, nestedLayersRestored = false;
        bool otherActorsUnchanged = true;
        string error = null;
        try
        {
            Vector2 center = camera.pixelRect.center;
            Ray expected = camera.ScreenPointToRay(new Vector3(center.x, center.y, 0f));
            try
            {
                normal = WeaponAiming.EnterScope(actor);
                if (normal == null) throw new InvalidOperationException("Viewport aim scope did not activate.");
                oneScope = WeaponAiming.ActiveScopes == 1;
                ownIgnored = AllIgnored(ownLayers);
                otherActorsUnchanged &= LayersMatch(otherLayers);
                Ray actual = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
                originError = (expected.origin - actual.origin).magnitude;
                directionError = (expected.direction - actual.direction).magnitude;
            }
            finally { WeaponAiming.ExitScope(normal); }
            normalRestored = SameRect(camera.rect, originalRect) && camera.aspect == originalAspect;
            normalDepthRestored = WeaponAiming.ActiveScopes == 0;
            normalLayersRestored = LayersMatch(ownLayers);
            otherActorsUnchanged &= LayersMatch(otherLayers);
            // A second exit must be harmless, including for the depth counter.
            WeaponAiming.ExitScope(normal);
            normalDepthRestored &= WeaponAiming.ActiveScopes == 0;

            try
            {
                outer = WeaponAiming.EnterScope(actor);
                if (outer == null) throw new InvalidOperationException("Outer scope did not activate.");
                Rect outerRect = camera.rect;
                float outerAspect = camera.aspect;
                try
                {
                    inner = WeaponAiming.EnterScope(actor);
                    if (inner == null) throw new InvalidOperationException("Inner scope did not activate.");
                    twoScopes = WeaponAiming.ActiveScopes == 2;
                    ownIgnored &= AllIgnored(ownLayers);
                    otherActorsUnchanged &= LayersMatch(otherLayers);
                    throw new ExpectedScopeException();
                }
                finally
                {
                    WeaponAiming.ExitScope(inner);
                    innerRestored = SameRect(camera.rect, outerRect) && camera.aspect == outerAspect &&
                        WeaponAiming.ActiveScopes == 1 && AllIgnored(ownLayers);
                    otherActorsUnchanged &= LayersMatch(otherLayers);
                }
            }
            catch (ExpectedScopeException) { exceptionObserved = true; }
            finally { WeaponAiming.ExitScope(outer); }
            nestedRestored = SameRect(camera.rect, originalRect) && camera.aspect == originalAspect;
            nestedDepthRestored = WeaponAiming.ActiveScopes == 0;
            nestedLayersRestored = LayersMatch(ownLayers);
            otherActorsUnchanged &= LayersMatch(otherLayers);
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            WeaponAiming.ExitScope(inner);
            WeaponAiming.ExitScope(outer);
            WeaponAiming.ExitScope(normal);
            // Record a restoration failure before repairing test side effects.
            if (camera && (!SameRect(camera.rect, originalRect) || camera.aspect != originalAspect))
            {
                cleanupApplied = true;
                camera.rect = originalRect;
                camera.aspect = originalAspect;
            }
            if (!LayersMatch(ownLayers) || !LayersMatch(otherLayers))
            {
                cleanupApplied = true;
                RestoreLayers(ownLayers);
                RestoreLayers(otherLayers);
            }
        }
        bool geometry = originError >= 0f && originError <= 0.0001f &&
            directionError >= 0f && directionError <= 0.00001f;
        bool pass = error == null && geometry && oneScope && normalRestored && normalDepthRestored &&
            twoScopes && innerRestored && exceptionObserved && nestedRestored && nestedDepthRestored &&
            ownIgnored && normalLayersRestored && nestedLayersRestored && otherActorsUnchanged &&
            !cleanupApplied && WeaponAiming.ActiveScopes == 0;
        return (pass, new
        {
            player, netId = actor.netId, camera = camera.name, pass,
            geometry = new { pass = geometry, originError, directionError },
            normal = new { oneScope, restoredExactly = normalRestored, depthRestored = normalDepthRestored },
            nestedException = new { twoScopes, innerRestored, exceptionObserved,
                restoredExactly = nestedRestored, depthRestored = nestedDepthRestored },
            layers = new { colliderObjects = ownLayers.Count, ownIgnored, normalLayersRestored,
                nestedLayersRestored, otherActorsUnchanged },
            scopesAfter = WeaponAiming.ActiveScopes, cleanupApplied, error
        });
    }

    private static bool SameRect(Rect a, Rect b) =>
        a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;

    private static List<SavedLayer> SnapshotLayers(OnlineBasePlayerRef actor)
    {
        var result = new List<SavedLayer>();
        var ids = new HashSet<int>();
        void Capture(Collider collider)
        {
            if (!collider) return;
            var target = collider.gameObject;
            if (target && ids.Add(target.GetInstanceID()))
                result.Add(new SavedLayer { Target = target, Layer = target.layer });
        }
        Capture(actor.PCollider);
        if (actor.AllColliders != null)
            foreach (var collider in actor.AllColliders) Capture(collider);
        foreach (var collider in actor.GetComponentsInChildren<Collider>(true)) Capture(collider);
        return result;
    }

    private static bool AllIgnored(List<SavedLayer> layers)
    {
        if (layers.Count == 0) return false;
        foreach (var saved in layers)
            if (!saved.Target || saved.Target.layer != 2) return false;
        return true;
    }

    private static bool LayersMatch(List<SavedLayer> layers)
    {
        foreach (var saved in layers)
            if (!saved.Target || saved.Target.layer != saved.Layer) return false;
        return true;
    }

    private static void RestoreLayers(List<SavedLayer> layers)
    {
        foreach (var saved in layers)
            if (saved.Target) saved.Target.layer = saved.Layer;
    }
}
