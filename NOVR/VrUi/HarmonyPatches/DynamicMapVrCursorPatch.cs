using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Collections.Generic;

namespace NOVR.VrUi.HarmonyPatches;

internal static class DynamicMapVrCursorPatch
{
    private static bool IsSelectableMapIcon(global::MapIcon? mapIcon)
    {
        if (mapIcon == null ||
            !mapIcon.gameObject.activeInHierarchy ||
            mapIcon.iconImage == null ||
            !mapIcon.iconImage.raycastTarget)
        {
            return false;
        }

        if (mapIcon is global::UnitMapIcon unitMapIcon)
        {
            if (unitMapIcon.unit == null) return false;
            
            if (global::SceneSingleton<global::TargetListSelector>.i != null &&
                global::SceneSingleton<global::TargetListSelector>.i.CheckExclusions(unitMapIcon.unit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVrActive => VrUiCursor.I != null && VrUiCursor.I.IsActive;

    private static bool TryGetVrCursor(out VrUiCursor cursor, out Camera camera)
    {
        cursor = VrUiCursor.I;
        camera = APIBus.CockpitHudCamera;
        return cursor != null && cursor.IsActive && camera != null;
    }

    private static bool TryGetLocalCursorPoint(global::DynamicMap map, out Vector2 localPoint)
    {
        localPoint = Vector2.zero;
        if (TryGetVrCursor(out var cursor, out var camera))
        {
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                map.mapImage.GetComponent<RectTransform>(),
                cursor.GetScreenPoint(),
                camera,
                out localPoint
            );
        }
        return false;
    }

    [HarmonyPatch(typeof(global::DynamicMap), "SelectFromMap")]
    private static class SelectFromMapPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(global::DynamicMap __instance)
        {
            if (TryGetVrCursor(out var cursor, out var camera))
            {
                // Calculate screen point exactly in the VR camera's screen/viewport space
                // to match the coordinate system of iconWorldPositions projected via the same camera.
                var cursorScreenPoint = (Vector2)camera.WorldToScreenPoint(cursor.CursorPosition);

                // 1. Try EventSystem raycast first (exact hit test)
                var eventSystem = EventSystem.current;
                if (eventSystem != null)
                {
                    var pointerEventData = new PointerEventData(eventSystem)
                    {
                        position = cursorScreenPoint
                    };
                    var results = new List<RaycastResult>();
                    eventSystem.RaycastAll(pointerEventData, results);
                    foreach (var result in results)
                    {
                        var icon = result.gameObject.GetComponentInParent<global::MapIcon>();
                        if (IsSelectableMapIcon(icon))
                        {
                            icon.ClickIcon(global::MapIcon.ClickSource.Mouse);
                            return false;
                        }
                    }
                }

                // 2. Fallback: Find closest selectable map icon in screen space
                var icons = UnityEngine.Object.FindObjectsOfType<global::MapIcon>();
                global::MapIcon? closestIcon = null;
                float closestSqrDistance = float.MaxValue;
                
                foreach (var icon in icons)
                {
                    if (IsSelectableMapIcon(icon))
                    {
                        Vector2 iconScreenPoint = camera.WorldToScreenPoint(icon.transform.position);
                        float sqrDistance = (iconScreenPoint - cursorScreenPoint).sqrMagnitude;
                        if (sqrDistance < closestSqrDistance)
                        {
                            closestSqrDistance = sqrDistance;
                            closestIcon = icon;
                        }
                    }
                }
                
                if (closestIcon != null && closestSqrDistance <= 10000f)
                {
                    closestIcon.ClickIcon(global::MapIcon.ClickSource.Mouse);
                }
                
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(global::DynamicMap), "IsCursorInMapRectangle")]
    private static class IsCursorInMapRectanglePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(global::DynamicMap __instance, ref bool __result)
        {
            if (TryGetVrCursor(out var cursor, out var camera))
            {
                __result = RectTransformUtility.RectangleContainsScreenPoint(
                    __instance.mapBackground.rectTransform,
                    cursor.GetScreenPoint(),
                    camera
                );
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(global::DynamicMap), "GetCursorCoordinates")]
    private static class GetCursorCoordinatesPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(global::DynamicMap __instance, ref global::GlobalPosition __result)
        {
            if (TryGetLocalCursorPoint(__instance, out var localPoint))
            {
                float scaleFactor = __instance.mapDimension / 900.0f;
                Vector2 worldCoords = localPoint * scaleFactor;
                __result = new global::GlobalPosition(worldCoords.x, 0f, worldCoords.y);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(global::MapWaypoint), MethodType.Constructor, new Type[] { typeof(Vector3), typeof(Vector3), typeof(GameObject), typeof(GameObject) })]
    private static class MapWaypointConstructorPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref Vector3 __0)
        {
            var dynamicMap = SceneSingleton<global::DynamicMap>.i;
            if (dynamicMap != null && TryGetLocalCursorPoint(dynamicMap, out var localPoint))
            {
                __0 = dynamicMap.mapImage.transform.TransformPoint(new Vector3(localPoint.x, localPoint.y, 0f));
            }
        }
    }

    private static void AlignWaypoint(
        GameObject marker,
        GameObject vector,
        ref Vector3 previousWaypoint,
        float scale,
        bool updateRotation)
    {
        marker.transform.localScale = Vector3.one * scale;
        previousWaypoint = new Vector3(previousWaypoint.x, previousWaypoint.y, 0f);

        Vector3 localMarkerPos = marker.transform.localPosition;
        Vector3 delta = localMarkerPos - previousWaypoint;
        delta.z = 0f;

        if (updateRotation)
        {
            float angle = -Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg + 180f;
            vector.transform.localEulerAngles = new Vector3(0f, 0f, angle);
        }

        vector.transform.localScale = new Vector3(4f * scale, delta.magnitude, 4f * scale);
    }

    [HarmonyPatch(typeof(global::MapWaypoint), "PlaceMarker")]
    private static class MapWaypointPlaceMarkerPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            global::MapWaypoint __instance,
            ref Vector3 ___waypointPosition,
            ref Vector3 ___previousWaypoint,
            ref GameObject ___marker,
            ref GameObject ___vector)
        {
            if (IsVrActive)
            {
                var dynamicMap = SceneSingleton<global::DynamicMap>.i;
                if (dynamicMap == null) return true;

                var iconLayer = dynamicMap.iconLayer.transform;
                Vector3 localWaypoint = iconLayer.InverseTransformPoint(___waypointPosition);
                localWaypoint.z = 0f;
                Vector3 flatWaypointPosition = iconLayer.TransformPoint(localWaypoint);

                ___waypointPosition = flatWaypointPosition;
                ___marker.transform.position = flatWaypointPosition;
                ___vector.transform.position = flatWaypointPosition;

                float scale = 1f / dynamicMap.mapImage.transform.localScale.x;
                AlignWaypoint(___marker, ___vector, ref ___previousWaypoint, scale, updateRotation: true);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(global::MapWaypoint), "UpdateMarker")]
    private static class MapWaypointUpdateMarkerPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            global::MapWaypoint __instance,
            ref Vector3 ___waypointPosition,
            ref Vector3 ___previousWaypoint,
            ref GameObject ___marker,
            ref GameObject ___vector)
        {
            if (IsVrActive)
            {
                var dynamicMap = SceneSingleton<global::DynamicMap>.i;
                if (dynamicMap == null || ___marker == null || ___vector == null) return true;

                float scale = 1f / dynamicMap.mapImage.transform.localScale.x;
                AlignWaypoint(___marker, ___vector, ref ___previousWaypoint, scale, updateRotation: false);
                return false;
            }
            return true;
        }
    }
}

