namespace Pivot;

internal static class PivotActionRouter
{
    private static readonly string[] QInteractionPriority =
    {
        "Switch Direction",
        "Change Input Direction",
        "Toggle",
        "Mirror"
    };

    private static readonly string[] QDirectMethodPriority =
    {
        "ToggleDirection",
        "Toggle",
        "MirrorObject"
    };

    internal static void RotateAction(PlayerController owner)
    {
        if (!TryGetLookedBuilding(owner, out BuildingObject building))
        {
            PivotPlugin.ToolDebug("Rotate: no valid building target.");
            return;
        }

        if (!CanRotateBuilding(building))
        {
            PivotPlugin.ToolDebug("Rotate: target cannot rotate (" + DescribeBuilding(building) + ").");
            return;
        }

        building.transform.rotation = building.transform.rotation * Quaternion.Euler(0f, 90f, 0f);
        RefreshConveyorRuntimeState(building);
        building.UpdateSupportsAbove(isDestroyingThis: false);
        PivotPlugin.ToolDebug("Rotate: rotated +90 (" + DescribeBuilding(building) + ").");
    }

    internal static void AlternateAction(PlayerController owner)
    {
        if (!TryGetLookedBuilding(owner, out BuildingObject building))
        {
            PivotPlugin.ToolDebug("Alt: no valid building target.");
            return;
        }

        if (!CanRotateBuilding(building))
        {
            PivotPlugin.ToolDebug("Alt: target cannot run alt action (" + DescribeBuilding(building) + ").");
            return;
        }

        if (TryCyclePlacedBuildingVariant(building, out string cycleInfo))
        {
            PivotPlugin.ToolDebug("Alt: variant-cycle (" + cycleInfo + ").");
            return;
        }

        if (TryRunQInteraction(building, out string interactionName, out string interactableType))
        {
            PivotPlugin.ToolDebug("Alt: interact '" + interactionName + "' via " + interactableType + " (" + DescribeBuilding(building) + ").");
            return;
        }

        if (TryRunQDirectMethod(building, out string methodName, out string behaviourType))
        {
            PivotPlugin.ToolDebug("Alt: method '" + methodName + "' via " + behaviourType + " (" + DescribeBuilding(building) + ").");
            return;
        }

        building.transform.rotation = building.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
        RefreshConveyorRuntimeState(building);
        building.UpdateSupportsAbove(isDestroyingThis: false);
        PivotPlugin.ToolDebug("Alt: fallback rotate 180 (" + DescribeBuilding(building) + ").");
    }

    private static bool TryGetLookedBuilding(PlayerController owner, out BuildingObject building)
    {
        building = null!;
        if (owner == null || owner.PlayerCamera == null)
        {
            return false;
        }

        Transform cameraTransform = owner.PlayerCamera.transform;
        if (!Physics.Raycast(cameraTransform.position, cameraTransform.forward, out RaycastHit hit, 8f, ~0, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        building = hit.collider.GetComponentInParent<BuildingObject>();
        return building != null && building.Definition != null;
    }

    private static bool CanRotateBuilding(BuildingObject building)
    {
        if (building == null || building.IsGhost)
        {
            return false;
        }

        return building.Definition != null;
    }

    private static bool TryCyclePlacedBuildingVariant(BuildingObject building, out string info)
    {
        info = string.Empty;
        BuildingInventoryDefinition definition = building.Definition;
        if (definition == null || definition.BuildingPrefabs == null || definition.BuildingPrefabs.Count <= 1)
        {
            return false;
        }

        List<BuildingObject> prefabs = definition.BuildingPrefabs;
        int currentIndex = prefabs.FindIndex(p => p != null && p.SavableObjectID == building.SavableObjectID);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int nextIndex = (currentIndex + 1) % prefabs.Count;
        BuildingObject nextPrefab = prefabs[nextIndex];
        if (nextPrefab == null)
        {
            return false;
        }

        Vector3 position = building.transform.position;
        Quaternion rotation = building.transform.rotation;
        bool supportsEnabled = building.GetBuildingSupportsEnabled();

        string oldCustomData = building.GetCustomSaveData();
        BuildingObject oldBuilding = building;

        BuildingObject replacement = UnityEngine.Object.Instantiate(nextPrefab, position, rotation);
        replacement.Definition = definition;
        replacement.BuildingSupportsEnabled = supportsEnabled;

        if (!string.IsNullOrEmpty(oldCustomData))
        {
            replacement.LoadFromSave(oldCustomData);
        }

        RefreshConveyorRuntimeState(replacement);
        replacement.UpdateSupportsAbove(isDestroyingThis: false);

        UnityEngine.Object.Destroy(oldBuilding.gameObject);
        info = DescribeBuilding(oldBuilding) + " idx " + currentIndex + "->" + nextIndex +
               " id " + (int)oldBuilding.SavableObjectID + "->" + (int)replacement.SavableObjectID;
        return true;
    }

    private static bool TryRunQInteraction(BuildingObject building, out string interactionName, out string interactableType)
    {
        interactionName = string.Empty;
        interactableType = string.Empty;
        IEnumerable<IInteractable> interactables = building.GetComponentsInChildren<MonoBehaviour>(includeInactive: true).OfType<IInteractable>();
        foreach (IInteractable interactable in interactables)
        {
            if (interactable is BuildingObject)
            {
                continue;
            }

            List<Interaction> interactions = interactable.GetInteractions();
            if (interactions == null || interactions.Count == 0)
            {
                continue;
            }

            for (int i = 0; i < QInteractionPriority.Length; i++)
            {
                string preferredName = QInteractionPriority[i];
                Interaction selected = interactions.FirstOrDefault(i2 => i2 != null && string.Equals(i2.Name, preferredName, StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                {
                    continue;
                }

                interactable.Interact(selected);
                interactionName = selected.Name;
                interactableType = interactable.GetType().Name;
                return true;
            }
        }

        return false;
    }

    private static bool TryRunQDirectMethod(BuildingObject building, out string methodNameUsed, out string behaviourType)
    {
        methodNameUsed = string.Empty;
        behaviourType = string.Empty;
        MonoBehaviour[] behaviours = building.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
        for (int b = 0; b < behaviours.Length; b++)
        {
            MonoBehaviour behaviour = behaviours[b];
            if (behaviour == null || behaviour is BuildingObject)
            {
                continue;
            }

            Type type = behaviour.GetType();
            for (int i = 0; i < QDirectMethodPriority.Length; i++)
            {
                string methodName = QDirectMethodPriority[i];
                MethodInfo method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (method == null)
                {
                    continue;
                }

                method.Invoke(behaviour, null);
                methodNameUsed = methodName;
                behaviourType = type.Name;
                return true;
            }
        }

        return false;
    }

    private static void RefreshConveyorRuntimeState(BuildingObject building)
    {
        ConveyorBelt[] belts = building.GetComponentsInChildren<ConveyorBelt>(includeInactive: true);
        for (int i = 0; i < belts.Length; i++)
        {
            ConveyorBelt belt = belts[i];
            if (belt == null || !belt.enabled)
            {
                continue;
            }

            belt.enabled = false;
            belt.enabled = true;
            belt.ClearNullObjectsOnBelt();
        }
    }

    private static string DescribeBuilding(BuildingObject building)
    {
        string name = building != null ? building.name : "null";
        int id = building != null ? (int)building.SavableObjectID : -1;
        return name + " [ID=" + id + "]";
    }

}
