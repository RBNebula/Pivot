using BepInEx.Logging;
using MineMogul.ToolImportApi.Api;

namespace Pivot;

[BepInPlugin(ModInfo.PLUGIN_GUID, ModInfo.PLUGIN_NAME, ModInfo.PLUGIN_VERSION)]
[BepInDependency(ModInfo.TOOL_IMPORT_API_GUID, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PivotPlugin : BaseUnityPlugin
{
    private const string DefaultModId = "pivotmod";
    private const string DefaultToolId = "screwdriver";
    private const string WorldModelResourceName = "Pivot.Resources.Models.screwdriver-world.glb";
    private const string ViewModelResourceName = "Pivot.Resources.Models.screwdriver-view.glb";
    private const string IconResourceName = "Pivot.Resources.Img.screwdriver-icon.png";

    internal static PivotPlugin? Instance;
    internal static ManualLogSource Log = null!;

    private static readonly string RuntimeImportedToolTypeName = "MineMogul.ToolImportApi.Game.RuntimeImportedTool";
    private static Type? _runtimeImportedToolType;
    private static PropertyInfo? _runtimeRegistrationKeyProperty;
    private static FieldInfo? _runtimeRegistrationKeyField;

    private ConfigEntry<string> _modId = null!;
    private ConfigEntry<string> _toolId = null!;
    private ConfigEntry<string> _cacheNamespace = null!;
    private ConfigEntry<string> _toolDisplayName = null!;
    private ConfigEntry<string> _toolDescription = null!;
    private ConfigEntry<int> _toolPrice = null!;
    private ConfigEntry<string> _shopCategory = null!;
    private ConfigEntry<int> _preferredSavableId = null!;
    private ConfigEntry<int> _shopBlockId = null!;
    private ConfigEntry<bool> _enableToolActionLogs = null!;

    private string? _registeredFullToolId;
    private bool _registerAttempted;
    private bool _registerSucceeded;
    private string? _lastObservedRegistrationKey;
    private int _lastObservedViewArmRenderers = -1;
    private int _lastObservedViewSkinnedRenderers = -1;
    private float _nextDiagnosticsLogTime;
    private string? _lastMismatchedToolName;
    private float _nextMismatchLogTime;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        BindConfig();
        if (IsToolActionDebugEnabled)
        {
            Log.LogInfo($"{ModInfo.LOG_PREFIX} {ModInfo.PLUGIN_NAME} {ModInfo.PLUGIN_VERSION} initialized.");
        }
    }

    private void BindConfig()
    {
        _modId = Config.Bind("ToolImportApi", "ModId", DefaultModId, "Tool Import API mod id for this plugin.");
        _toolId = Config.Bind("ToolImportApi", "ToolId", DefaultToolId, "Short tool id (without mod prefix).");
        _cacheNamespace = Config.Bind("ToolImportApi", "CacheNamespace", DefaultModId, "Optional Tool Import API cache namespace (subfolder under .tool-import-cache).");
        _toolDisplayName = Config.Bind("ToolImportApi", "DisplayName", "Pivot Screwdriver", "Display name in inventory/shop.");
        _toolDescription = Config.Bind("ToolImportApi", "Description", "A precision screwdriver for rotating and toggling nearby buildings.", "Inventory description.");
        _toolPrice = Config.Bind("Shop", "Price", 350, "Purchase price in shop.");
        _shopCategory = Config.Bind("Shop", "Category", "Tools", "Shop category for ModMogul registration.");
        _preferredSavableId = Config.Bind("ToolImportApi", "PreferredSavableId", 23010, "Preferred SavableObjectID (set <=0 to auto-assign).");
        _shopBlockId = Config.Bind("ToolImportApi", "ShopBlockId", 23011, "ModMogul Itemizer block id (set <=0 to use SavableObjectID).");
        _enableToolActionLogs = Config.Bind("Debug", "EnableToolActionLogs", false, "Enable verbose logs for Pivot action routing.");
    }

    private void Start()
    {
        TryRegisterTool();
    }

    private void Update()
    {
        if (!_registerSucceeded)
        {
            if (!_registerAttempted)
            {
                TryRegisterTool();
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(_registeredFullToolId))
        {
            return;
        }

        PlayerInventory inventory = UnityEngine.Object.FindFirstObjectByType<PlayerInventory>();
        if (inventory == null || inventory.ActiveTool == null)
        {
            return;
        }

        string expectedFullToolId = _registeredFullToolId!;
        if (!IsRegisteredPivotTool(inventory.ActiveTool, expectedFullToolId))
        {
            TryLogMismatchedActiveTool(inventory.ActiveTool, expectedFullToolId);
            return;
        }

        LogHeldToolDiagnostics(inventory.ActiveTool, expectedFullToolId);

        PlayerController owner = inventory.ActiveTool.Owner;
        if (owner == null)
        {
            return;
        }

        PlayerInputActions input = owner.GetInputActions();
        if (input == null)
        {
            return;
        }

        if (IsToolInputBlocked(owner))
        {
            return;
        }

        if (input.Player.PrimaryAttack.WasPressedThisFrame())
        {
            PivotActionRouter.RotateAction(owner);
        }

        if (input.Player.SecondaryAttack.WasPressedThisFrame())
        {
            PivotActionRouter.AlternateAction(owner);
        }
    }

    private void TryRegisterTool()
    {
        _registerAttempted = true;

        ToolRegistrationRequest request = new ToolRegistrationRequest
        {
            ModId = _modId.Value.Trim(),
            ToolId = _toolId.Value.Trim(),
            CacheNamespace = _cacheNamespace.Value.Trim(),
            DisplayName = _toolDisplayName.Value,
            Description = _toolDescription.Value,
            IconResourceName = IconResourceName,
            WorldModelResourceName = WorldModelResourceName,
            ViewModelResourceName = ViewModelResourceName,
            PreferredSavableId = _preferredSavableId.Value > 0 ? _preferredSavableId.Value : (int?)null,
            EnableCustomPositioning = false,
            CustomPositioningResourceName = null,
            ShopMetadata = new ToolShopMetadata
            {
                Price = Mathf.Max(0, _toolPrice.Value),
                Category = _shopCategory.Value,
                IsLockedByDefault = false,
                MaxStackSize = 1,
                QAction = "Rotate",
                ShopBlockId = _shopBlockId.Value > 0 ? _shopBlockId.Value : (int?)null
            }
        };

        ToolRegistrationResult result = ToolImportApi.RegisterTool(request);
        if (!result.Success)
        {
            Log.LogWarning("Pivot tool registration failed: " + result.Message);
            _registerAttempted = false;
            _registerSucceeded = false;
            return;
        }

        _registeredFullToolId = result.FullToolId;
        _registerSucceeded = true;
        if (IsToolActionDebugEnabled)
        {
            Log.LogInfo("Pivot registered Tool Import API tool: " + _registeredFullToolId + " (" + result.Message + ")");
        }
        ToolDebug("Registered expected full tool id: " + _registeredFullToolId);
    }

    private static bool IsRegisteredPivotTool(BaseHeldTool activeTool, string expectedFullToolId)
    {
        if (activeTool == null || string.IsNullOrWhiteSpace(expectedFullToolId))
        {
            return false;
        }

        EnsureRuntimeImportedToolReflection(activeTool.GetType());

        if (_runtimeImportedToolType != null && _runtimeImportedToolType.IsInstanceOfType(activeTool))
        {
            string? registrationKey = null;
            if (_runtimeRegistrationKeyProperty != null)
            {
                registrationKey = _runtimeRegistrationKeyProperty.GetValue(activeTool) as string;
            }
            else if (_runtimeRegistrationKeyField != null)
            {
                registrationKey = _runtimeRegistrationKeyField.GetValue(activeTool) as string;
            }

            if (!string.IsNullOrWhiteSpace(registrationKey) &&
                string.Equals(registrationKey, expectedFullToolId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string fallbackName = activeTool.FallbackName ?? string.Empty;
        string expectedToolId = expectedFullToolId.Substring(expectedFullToolId.LastIndexOf('.') + 1);
        return fallbackName.IndexOf(expectedToolId, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void LogHeldToolDiagnostics(BaseHeldTool activeTool, string expectedFullToolId)
    {
        if (!IsToolActionDebugEnabled || activeTool == null)
        {
            return;
        }

        if (Time.unscaledTime < _nextDiagnosticsLogTime)
        {
            return;
        }

        _nextDiagnosticsLogTime = Time.unscaledTime + 1f;

        string observedRegistrationKey = GetRuntimeImportedToolKey(activeTool) ?? "<null>";
        if (!string.Equals(observedRegistrationKey, _lastObservedRegistrationKey, StringComparison.Ordinal))
        {
            ToolDebug(
                "Tool registration key check: expected='" + expectedFullToolId +
                "', observed='" + observedRegistrationKey + "', type=" + activeTool.GetType().FullName);
            _lastObservedRegistrationKey = observedRegistrationKey;
        }

        GameObject viewModel = activeTool.ViewModel;
        int armRenderers = CountArmRenderers(viewModel);
        int skinnedRenderers = viewModel != null ? viewModel.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length : 0;
        Transform? fpsArmsRoot = viewModel != null ? FindTransformByName(viewModel.transform, "FP_Arms_Unarmed") : null;
        int externalBoneRefs = CountExternalBoneReferences(fpsArmsRoot);
        string fpsParent = fpsArmsRoot != null && fpsArmsRoot.parent != null ? GetTransformPath(fpsArmsRoot.parent) : "<null>";
        if (armRenderers != _lastObservedViewArmRenderers || skinnedRenderers != _lastObservedViewSkinnedRenderers)
        {
            ToolDebug(
                "Held viewmodel diagnostics: viewModel=" + (viewModel != null ? viewModel.name : "null") +
                ", armRenderers=" + armRenderers +
                ", skinnedRenderers=" + skinnedRenderers +
                ", hasFP_Arms_Unarmed=" + (fpsArmsRoot != null) +
                ", fpParent=" + fpsParent +
                ", externalBoneRefs=" + externalBoneRefs);
            _lastObservedViewArmRenderers = armRenderers;
            _lastObservedViewSkinnedRenderers = skinnedRenderers;
        }
    }

    private static string? GetRuntimeImportedToolKey(BaseHeldTool activeTool)
    {
        if (activeTool == null)
        {
            return null;
        }

        EnsureRuntimeImportedToolReflection(activeTool.GetType());

        if (_runtimeImportedToolType != null && _runtimeImportedToolType.IsInstanceOfType(activeTool))
        {
            if (_runtimeRegistrationKeyProperty != null)
            {
                return _runtimeRegistrationKeyProperty.GetValue(activeTool) as string;
            }

            if (_runtimeRegistrationKeyField != null)
            {
                return _runtimeRegistrationKeyField.GetValue(activeTool) as string;
            }
        }

        return null;
    }

    private static void EnsureRuntimeImportedToolReflection(Type activeToolType)
    {
        if (_runtimeImportedToolType != null)
        {
            return;
        }

        if (activeToolType != null)
        {
            _runtimeImportedToolType = activeToolType.Assembly.GetType(RuntimeImportedToolTypeName, throwOnError: false);
        }

        if (_runtimeImportedToolType == null)
        {
            Assembly apiAssembly = typeof(ToolImportApi).Assembly;
            _runtimeImportedToolType = apiAssembly.GetType(RuntimeImportedToolTypeName, throwOnError: false);
        }

        if (_runtimeImportedToolType == null)
        {
            return;
        }

        _runtimeRegistrationKeyProperty = _runtimeImportedToolType.GetProperty("RegistrationKey", BindingFlags.Instance | BindingFlags.Public);
        _runtimeRegistrationKeyField = _runtimeImportedToolType.GetField("RegistrationKey", BindingFlags.Instance | BindingFlags.Public);
    }

    private static int CountArmRenderers(GameObject? viewModel)
    {
        if (viewModel == null)
        {
            return 0;
        }

        int count = 0;
        MeshRenderer[] meshRenderers = viewModel.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < meshRenderers.Length; i++)
        {
            if (meshRenderers[i] != null && IsUnderArmBranch(meshRenderers[i].transform))
            {
                count++;
            }
        }

        SkinnedMeshRenderer[] skinned = viewModel.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinned.Length; i++)
        {
            if (skinned[i] != null && IsUnderArmBranch(skinned[i].transform))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsUnderArmBranch(Transform? t)
    {
        Transform? cursor = t;
        while (cursor != null)
        {
            if (cursor.name.StartsWith("Arm_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            cursor = cursor.parent;
        }

        return false;
    }

    private static Transform? FindTransformByName(Transform root, string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return null;
        }

        if (string.Equals(root.name, targetName, StringComparison.Ordinal))
        {
            return root;
        }

        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            Transform? found = FindTransformByName(child, targetName);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static int CountExternalBoneReferences(Transform? rigRoot)
    {
        if (rigRoot == null)
        {
            return 0;
        }

        int count = 0;
        SkinnedMeshRenderer[] skinned = rigRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinned.Length; i++)
        {
            SkinnedMeshRenderer smr = skinned[i];
            if (smr == null)
            {
                continue;
            }

            Transform[] bones = smr.bones;
            if (bones != null)
            {
                for (int b = 0; b < bones.Length; b++)
                {
                    Transform bone = bones[b];
                    if (bone != null && !bone.IsChildOf(rigRoot))
                    {
                        count++;
                    }
                }
            }

            if (smr.rootBone != null && !smr.rootBone.IsChildOf(rigRoot))
            {
                count++;
            }
        }

        return count;
    }

    private static string GetTransformPath(Transform t)
    {
        List<string> names = new List<string>(8);
        Transform? cursor = t;
        while (cursor != null)
        {
            names.Add(cursor.name);
            cursor = cursor.parent;
        }

        names.Reverse();
        return string.Join("/", names.ToArray());
    }

    internal static bool IsToolActionDebugEnabled => Instance?._enableToolActionLogs.Value == true;

    private static bool IsToolInputBlocked(PlayerController owner)
    {
        if (owner == null)
        {
            return true;
        }

        UIManager uiManager = Singleton<UIManager>.Instance;
        if (uiManager != null && uiManager.IsInAnyMenu())
        {
            return true;
        }

        return false;
    }

    private void TryLogMismatchedActiveTool(BaseHeldTool activeTool, string expectedFullToolId)
    {
        if (!IsToolActionDebugEnabled || activeTool == null)
        {
            return;
        }

        if (Time.unscaledTime < _nextMismatchLogTime)
        {
            return;
        }

        string activeName = activeTool.FallbackName ?? "<null>";
        if (string.Equals(activeName, _lastMismatchedToolName, StringComparison.Ordinal))
        {
            _nextMismatchLogTime = Time.unscaledTime + 1f;
            return;
        }

        _lastMismatchedToolName = activeName;
        _nextMismatchLogTime = Time.unscaledTime + 1f;
        ToolDebug("Active tool does not match expected key. expected=" + expectedFullToolId + ", activeName=" + activeName);
    }

    internal static void ToolDebug(string message)
    {
        if (IsToolActionDebugEnabled)
        {
            Log.LogInfo(ModInfo.LOG_PREFIX + " " + message);
        }
    }
}
