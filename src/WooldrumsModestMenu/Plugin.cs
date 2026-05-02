using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace WooldrumsModestMenu;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "local.wooldrum.modestmenu";
    public const string PluginName = "Wooldrum's Modest Menu";
    public const string PluginVersion = "0.0.1";

    internal static ManualLogSource LogSource = null!;

    public override void Load()
    {
        LogSource = Log;

        EnsureInteropAssembliesLoaded();

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ModestMenuBehaviour>();
            var go = new GameObject(PluginName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ModestMenuBehaviour>();
        }
        catch (Exception ex)
        {
            Log.LogError(ex);
        }

        try
        {
            var harmony = new Harmony(PluginGuid);

            TryPatch(
                harmony,
                ComponentAmountPatch.ResolveTarget(),
                postfix: AccessTools.Method(typeof(ComponentAmountPatch), nameof(ComponentAmountPatch.Postfix)),
                label: "Core.Singleton.GetAvailableComponents");

            TryPatch(
                harmony,
                SpaceshipCreationPatch.ResolveTarget(),
                transpiler: AccessTools.Method(typeof(SpaceshipCreationPatch), nameof(SpaceshipCreationPatch.Transpiler)),
                label: "SpaceshipSystem.CanCreateSpaceship");

            foreach (var target in CoreAvailabilityRefreshPatch.ResolveTargets())
            {
                TryPatch(
                    harmony,
                    target,
                    prefix: AccessTools.Method(typeof(CoreAvailabilityRefreshPatch), nameof(CoreAvailabilityRefreshPatch.Prefix)),
                    postfix: AccessTools.Method(typeof(CoreAvailabilityRefreshPatch), nameof(CoreAvailabilityRefreshPatch.Postfix)),
                    label: $"{target.DeclaringType?.FullName}.{target.Name}");
            }

            foreach (var target in HandAvailabilityColorPatch.ResolveTargets())
            {
                TryPatch(
                    harmony,
                    target,
                    postfix: AccessTools.Method(typeof(HandAvailabilityColorPatch), nameof(HandAvailabilityColorPatch.Postfix)),
                    label: $"{target.DeclaringType?.FullName}.{target.Name}");
            }

            TryPatch(
                harmony,
                NetcoreSetAvailableComponentCreatePatch.ResolveTarget(),
                prefix: AccessTools.Method(typeof(NetcoreSetAvailableComponentCreatePatch), nameof(NetcoreSetAvailableComponentCreatePatch.Prefix)),
                label: "NetcoreEvent.Create<NetcoreEvent_SetAvailableComponent>");
        }
        catch (Exception ex)
        {
            Log.LogWarning("Harmony patch setup failed: " + ex.Message);
        }

        Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
    }

    private void TryPatch(Harmony harmony, MethodBase? target, MethodInfo? prefix = null, MethodInfo? postfix = null, MethodInfo? transpiler = null, string? label = null)
    {
        if (target == null)
        {
            Log.LogWarning($"Patch target not found: {label ?? "(unknown)"}");
            return;
        }

        try
        {
            harmony.Patch(
                target,
                prefix: prefix == null ? null : new HarmonyMethod(prefix),
                postfix: postfix == null ? null : new HarmonyMethod(postfix),
                transpiler: transpiler == null ? null : new HarmonyMethod(transpiler));
            Log.LogInfo($"Patched {label ?? $"{target.DeclaringType?.FullName}.{target.Name}"}.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Patch failed for {label ?? $"{target.DeclaringType?.FullName}.{target.Name}"}: {ex.Message}");
        }
    }

    private void EnsureInteropAssembliesLoaded()
    {
        try
        {
            var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (string.IsNullOrEmpty(pluginDir))
                return;

            var interopDir = Path.GetFullPath(Path.Combine(pluginDir, "..", "interop"));
            if (!Directory.Exists(interopDir))
                return;

            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetName().Name; } catch { return null; } })
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet();

            var targets = new[] { "Assembly-CSharp.dll" };
            foreach (var name in targets)
            {
                var path = Path.Combine(interopDir, name);
                if (!File.Exists(path))
                    continue;

                var asmName = Path.GetFileNameWithoutExtension(path);
                if (loaded.Contains(asmName))
                    continue;

                try
                {
                    Assembly.LoadFrom(path);
                    Log.LogInfo($"Force-loaded interop assembly: {name}");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Could not pre-load {name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning("EnsureInteropAssembliesLoaded failed: " + ex.Message);
        }
    }
}

public sealed class ModestMenuBehaviour : MonoBehaviour
{
    internal const int UnlimitedAvailableAmount = 999999;
    public const string MenuOpenHint =
        "F8 to open and close. Other players shouldn't need this.\n"
        + "If they don't have unlimited items, refresh co-op cap, kick them, then reinvite them.";
    private const int ListViewportRows = 16;
    private static readonly string[] ScanTabs = { "Garage", "World" };

    private readonly List<ItemEntry> _items = new();
    private Rect _window = new(40, 40, 760, 688);
    private bool _visible;
    private string _filter = "";
    private int _scanTab;
    private int _slot;
    private float _listScrollY;
    private string _teleportCountText = "25";
    private int _teleportCount = 25;
    private string _status = "Loading…";
    private float _nextCapRefreshAt;
    private float _nextClientProbeAt;
    private float _nextAvailabilityReplayAt;
    private float _nextAvailabilityHeartbeatAt;
    private float _nextPlayerFeatureApplyAt;
    private float _nextPlayerFeatureBroadcastAt;
    private float _nextPlayerScanAt;
    private float _nextCoreStampAt;
    private int _lastOnlineClientCount = -1;
    private int _lastPlayerCount = -1;
    private int _pendingAvailabilityReplays;
    private bool _initialOpenScansDone;
    private int _toggleApplyFailures;
    private float _toggleCooldownUntil;
    private bool _noPlayerWind;
    // "No static" bortplockad — gäster räknar GPU-sidan själva. Toggle → TryApplyNoStatic när du orkar.
    private bool _noStatic;
    private bool _noStaticNotImplementedLogged;

    private static MethodInfo? _findObjectsOfTypeAll;
    private static MethodInfo? _findObjectsOfTypeAllAttempted;
    private static MethodInfo? _il2cppTypeFrom;
    private static readonly Dictionary<Type, List<MemberInfo>> PrefabMemberCache = new();
    private static readonly HashSet<string> LoggedMissingOptionalTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> EpcDisplayNames = new(StringComparer.Ordinal)
    {
        ["EPC_SCFloodlight"] = "Spotlight / Floodlight",
        ["EPC_SpotLight"] = "Spotlight",
    };
    /// IL2CPP: <see cref="GUIContent.none"/> finns inte alltid.
    private static readonly GUIContent EmptyGroupContent = new GUIContent("");

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8))
        {
            _visible = !_visible;
            if (_visible)
            {
                Plugin.LogSource.LogInfo(MenuOpenHint.Replace("\n", " "));
                if (!_initialOpenScansDone)
                {
                    _initialOpenScansDone = true;
                    RunInitialAutoScans();
                }
            }
        }

        var now = Time.unscaledTime;

        if (now >= _nextCapRefreshAt)
        {
            _nextCapRefreshAt = now + 2f;
            TryRefreshLoadedComponentCaps();
        }

        TryProcessAvailabilityReplay();
        TryAutoScanPlayers(now);
        TryCoopAvailabilityHeartbeat(now);

        if (now >= _nextCoreStampAt)
        {
            // Unlimited direkt när map skapas — annars första gästen får spar-filens gamla värden tills rejoin.
            _nextCoreStampAt = now + 1f;
            try
            {
                var core = GetCoreInstance();
                if (core != null)
                    SetCoreComponentAmountsUnlimited(core);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning("Core map stamp failed: " + ex.Message);
            }
        }

        if (now >= _nextPlayerFeatureApplyAt && now >= _toggleCooldownUntil)
        {
            // 1 Hz räcker; oftare = hitch (query + sync varje gång).
            _nextPlayerFeatureApplyAt = now + 1f;
            ApplyToggleSafelyFromUpdate();
        }
    }

    [HideFromIl2Cpp]
    private void RunInitialAutoScans()
    {
        try { ScanItems(); } catch (Exception ex) { Plugin.LogSource.LogWarning("Auto Scan Items failed: " + ex.Message); }
        _status = $"Auto-scan complete: {_items.Count} items.";
    }

    [HideFromIl2Cpp]
    private void TryAutoScanPlayers(float now)
    {
        if (now < _nextPlayerScanAt)
            return;

        _nextPlayerScanAt = now + 1.5f;

        try
        {
            var prevCount = _lastPlayerCount;
            var newCount = TryCountPlayers();
            _lastPlayerCount = newCount;

            // Fire om count hoppar över max(0, prev) — täcker ny spelare OCH första load in i värld (gammal kod missade det).
            if (newCount > Math.Max(0, prevCount))
            {
                Plugin.LogSource.LogInfo($"Player count rose ({prevCount}->{newCount}); auto-refreshing co-op caps.");
                RefreshAvailabilityUiAndCore(updateStatus: false);
                ScheduleAvailabilityReplayBurst(8);
                _nextAvailabilityHeartbeatAt = Math.Min(_nextAvailabilityHeartbeatAt <= 0f ? float.MaxValue : _nextAvailabilityHeartbeatAt, now + 4f);
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Auto player scan failed: " + ex.Message);
        }
    }

    [HideFromIl2Cpp]
    private void ApplyToggleSafelyFromUpdate()
    {
        try
        {
            TryApplyServerPlayerFeatureToggles();
            _toggleApplyFailures = 0;
        }
        catch (Exception ex)
        {
            _toggleApplyFailures++;
            Plugin.LogSource.LogWarning($"Toggle apply threw ({_toggleApplyFailures}): {ex.Message}");
            if (_toggleApplyFailures >= 3)
            {
                _toggleCooldownUntil = Time.unscaledTime + 10f;
                _toggleApplyFailures = 0;
                _status = "Toggles paused 10s after repeated failures (see BepInEx log).";
            }
        }
    }

    private void OnGUI()
    {
        if (!_visible)
            return;

        try
        {
            DrawMenu();
        }
        catch (Exception ex)
        {
            _visible = false;
            Plugin.LogSource.LogError(ex);
        }
    }

    private void DrawMenu()
    {
        var x = _window.x;
        var y = _window.y;
        var w = _window.width;

        GUI.Box(_window, Plugin.PluginName);
        const float hintH = 44f;
        GUI.Label(new Rect(x + 14, y + 28, w - 28, hintH), MenuOpenHint, GUI.skin.label);
        var rowButtonsY = y + 28f + hintH + 6f;

        if (GUI.Button(new Rect(x + 14, rowButtonsY, 110, 24), "Scan Items"))
            ScanItems();

        if (GUI.Button(new Rect(x + 132, rowButtonsY, 155, 24), "Refresh Co-op Caps"))
            RefreshAvailabilityUiAndCore(updateStatus: true);

        if (GUI.Button(new Rect(x + 294, rowButtonsY, 84, 24), _scanTab == 0 ? "[Garage]" : "Garage"))
            _scanTab = 0;
        if (GUI.Button(new Rect(x + 380, rowButtonsY, 84, 24), _scanTab == 1 ? "[World]" : "World"))
            _scanTab = 1;

        GUI.Label(new Rect(x + 472, rowButtonsY + 2, w - 486, 24), _status, GUI.skin.label);

        var searchY = rowButtonsY + 36f;
        GUI.Label(new Rect(x + 14, searchY + 2, 52, 22), "Search", GUI.skin.label);
        _filter = GUI.TextField(new Rect(x + 68, searchY, 300, 24), _filter ?? "", GUI.skin.textField);

        var featureRowY = rowButtonsY + 62f;
        DrawFeatureControls(x, featureRowY, w);

        GUI.Label(new Rect(x + 376, searchY + 2, 78, 22), "Hotbar slot", GUI.skin.label);
        var slotText = GUI.TextField(new Rect(x + 458, searchY, 44, 24), (_slot + 1).ToString(), GUI.skin.textField);
        if (int.TryParse(slotText, out var parsed))
            _slot = Mathf.Clamp(parsed - 1, 0, 9);

        GUI.Label(new Rect(x + 512, searchY + 2, 62, 22), "TP #", GUI.skin.label);
        _teleportCountText = GUI.TextField(new Rect(x + 566, searchY, 44, 24), _teleportCountText ?? "25", GUI.skin.textField);
        if (int.TryParse(_teleportCountText, out var tpParsed))
            _teleportCount = Mathf.Clamp(tpParsed, 1, 9999);

        var filtered = FilteredItems();
        const float rowH = 24f;
        var listViewportW = w - 28f;
        var listViewportH = ListViewportRows * rowH + 8f;
        var headerY = featureRowY + 38f;
        var listY = featureRowY + 62f;
        var listViewport = new Rect(x + 14, listY, listViewportW, listViewportH);

        GUI.Label(new Rect(x + 14, headerY, 95, 20), "Action", GUI.skin.label);
        GUI.Label(new Rect(x + 114, headerY, 275, 20), "Item", GUI.skin.label);
        GUI.Label(new Rect(x + 390, headerY, 62, 20), "Type", GUI.skin.label);
        GUI.Label(new Rect(x + 454, headerY, 145, 20), "Prefab ID", GUI.skin.label);
        GUI.Label(new Rect(x + 604, headerY, 60, 20), "Amt", GUI.skin.label);

        var contentHeight = filtered.Count * rowH + 4f;
        var maxScroll = Mathf.Max(0f, contentHeight - listViewportH);
        _listScrollY = Mathf.Clamp(_listScrollY, 0f, maxScroll);

        var uiEvent = Event.current;
        if (uiEvent.type == EventType.ScrollWheel && listViewport.Contains(uiEvent.mousePosition))
        {
            _listScrollY = Mathf.Clamp(_listScrollY + uiEvent.delta.y * 12f, 0f, maxScroll);
            uiEvent.Use();
        }

        GUI.BeginGroup(listViewport, EmptyGroupContent, GUI.skin.box);
        var innerW = listViewport.width - 4f;
        for (var i = 0; i < filtered.Count; i++)
        {
            var item = filtered[i];
            var currentY = i * rowH - _listScrollY;
            if (currentY + rowH < 0f || currentY > listViewportH)
                continue;

            if (_scanTab == 1)
            {
                // World: TP = dra befintlig entity till dig.
                var tpRect = new Rect(2f, currentY, 60f, 22f);
                if (GUI.Button(tpRect, "TP"))
                    TeleportWorldTypeToPlayer(item, _teleportCount);
            }
            else
            {
                // Garage: Put In Slot = ship-del i vald hotbar-ruta.
                var actionRect = new Rect(2f, currentY, 92f, 22f);
                if (item.IsSpaceshipComponent && !item.IsTypeOnly && GUI.Button(actionRect, "Put In Slot"))
                    SetHandSlot(item);
            }

            GUI.Label(new Rect(100f, currentY + 2f, 272f, 20f), item.Name, GUI.skin.label);
            GUI.Label(new Rect(376f, currentY + 2f, 60f, 20f), item.Kind, GUI.skin.label);
            GUI.Label(new Rect(440f, currentY + 2f, innerW - 448f, 20f), item.PrefabIdText, GUI.skin.label);
            GUI.Label(new Rect(Mathf.Max(440f, innerW - 78f), currentY + 2f, 60f, 20f), item.AmountText, GUI.skin.label);
        }

        GUI.EndGroup();

        var navY = listViewport.yMax + 8f;
        GUI.Label(new Rect(x + 14, navY, w - 28, 22), $"{filtered.Count} matching {ScanTabs[_scanTab]} prefabs (scroll wheel on list)", GUI.skin.label);

        var footerY = navY + 30f;
        if (GUI.Button(new Rect(x + w - 82, footerY, 68, 26), "Close"))
            _visible = false;
    }

    [HideFromIl2Cpp]
    private void DrawFeatureControls(float x, float featureRowY, float w)
    {
        var changedToggle = false;
        changedToggle |= UpdateToggle(new Rect(x + 14, featureRowY, 130, 24), ref _noPlayerWind, "No Wind");
        // No static TODO — bara stub, se TryApplyNoStatic.
        changedToggle |= UpdateToggle(new Rect(x + 154, featureRowY, 170, 24), ref _noStatic, "No Static (TODO)");

        if (changedToggle)
        {
            _toggleCooldownUntil = 0f;
            _toggleApplyFailures = 0;
            _nextPlayerFeatureBroadcastAt = 0f;
            try
            {
                var changed = TryApplyServerPlayerFeatureToggles();
                _status = changed > 0 ? $"Applied toggles to {changed} ECS records." : "Toggles applied (no matching ECS records yet).";
            }
            catch (Exception ex)
            {
                _status = "Toggle apply failed; auto-paused. See log.";
                _toggleCooldownUntil = Time.unscaledTime + 5f;
                Plugin.LogSource.LogWarning("Immediate toggle apply failed: " + ex.Message);
            }
        }
    }

    [HideFromIl2Cpp]
    private static bool UpdateToggle(Rect rect, ref bool value, string label)
    {
        if (!GUI.Button(rect, $"{(value ? "[x]" : "[ ]")} {label}"))
            return false;
        value = !value;
        return true;
    }

    [HideFromIl2Cpp]
    private List<ItemEntry> FilteredItems()
    {
        var filtered = new List<ItemEntry>();
        foreach (var i in _items)
        {
            var isTabMatch = _scanTab == 0 ? i.IsSpaceshipComponent : !i.IsSpaceshipComponent;
            if (!isTabMatch)
                continue;

            if (string.IsNullOrWhiteSpace(_filter) ||
                i.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                i.Kind.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                i.PrefabIdText.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                filtered.Add(i);
            }
        }

        return filtered;
    }

    [HideFromIl2Cpp]
    private int TryCountPlayers()
    {
        var playerDataType = FindType("PlayerControllerData");
        if (playerDataType == null)
            return 0;

        var total = 0;
        foreach (var (entityManager, _) in EnumerateEntityManagers(serverFirst: true))
        {
            var entitiesArr = TryCreateEntityArray(entityManager, new[] { playerDataType }, readWriteLast: false, "Count Players");
            if (entitiesArr == null)
                continue;

            if (!TryGetEcsMethods(entityManager, out var hasComponentGeneric, out _, out _))
                continue;

            var hasPlayerData = hasComponentGeneric.MakeGenericMethod(playerDataType);
            foreach (var entity in EnumerateAny(entitiesArr))
            {
                if (entity == null)
                    continue;

                try
                {
                    if ((bool)(hasPlayerData.Invoke(entityManager, new[] { entity }) ?? false))
                        total++;
                }
                catch
                {
                }
            }
        }

        return total;
    }

    [HideFromIl2Cpp]
    private void ScanItems()
    {
        try
        {
            _items.Clear();

            var spaceshipType = FindType("EPC_SpaceshipComponent");
            var entityPrefabType = FindType("EntityPrefabComponent");
            if (spaceshipType == null || entityPrefabType == null)
            {
                _status = "Required prefab types not found yet. Load into the garage first.";
                return;
            }

            var uniqueKeys = new HashSet<string>(StringComparer.Ordinal);
            var spaceshipCount = 0;
            var worldPrefabCount = 0;

            var coreComponents = GetCoreSpaceshipComponents();
            if (coreComponents != null)
            {
                foreach (var obj in coreComponents)
                    AddItemIfMatch(obj, spaceshipType, uniqueKeys, ref spaceshipCount, ref worldPrefabCount);
            }
            var coreMapComponents = GetCoreSpaceshipComponentMapValues();
            if (coreMapComponents != null)
            {
                foreach (var obj in coreMapComponents)
                    AddItemIfMatch(obj, spaceshipType, uniqueKeys, ref spaceshipCount, ref worldPrefabCount);
            }

            foreach (var epcType in FindLoadedEpcTypes(entityPrefabType))
            {
                var allPrefabs = FindObjectsOfTypeAll(epcType);
                if (allPrefabs == null)
                    continue;

                foreach (var obj in allPrefabs)
                    AddItemIfMatch(obj, spaceshipType, uniqueKeys, ref spaceshipCount, ref worldPrefabCount);
            }

            ScanPrefabReferencesFromLoadedBehaviours(entityPrefabType, spaceshipType, uniqueKeys, ref spaceshipCount, ref worldPrefabCount);
            ScanPrefabReferencesFromCore(entityPrefabType, spaceshipType, uniqueKeys, ref spaceshipCount, ref worldPrefabCount);
            AddWorldTypeFallbackEntries(entityPrefabType, spaceshipType, uniqueKeys, ref worldPrefabCount);

            _items.Sort((a, b) =>
            {
                var kindCompare = string.Compare(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase);
                if (kindCompare != 0)
                    return kindCompare;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            _status = $"Scanned {_items.Count} prefabs ({spaceshipCount} components, {worldPrefabCount} world-prefabs).";
        }
        catch (Exception ex)
        {
            _status = "Scan failed. See BepInEx log.";
            Plugin.LogSource.LogError(ex);
        }
    }

    [HideFromIl2Cpp]
    private void ScanPrefabReferencesFromCore(Type entityPrefabType, Type spaceshipType, HashSet<string> uniqueKeys, ref int spaceshipCount, ref int worldPrefabCount)
    {
        var core = GetCoreInstance();
        if (core == null)
            return;

        ScanPrefabReferencesFromObject(
            root: core,
            entityPrefabType: entityPrefabType,
            spaceshipType: spaceshipType,
            uniqueKeys: uniqueKeys,
            spaceshipCount: ref spaceshipCount,
            worldPrefabCount: ref worldPrefabCount,
            maxDepth: 3);
    }

    [HideFromIl2Cpp]
    private void ScanPrefabReferencesFromLoadedBehaviours(Type entityPrefabType, Type spaceshipType, HashSet<string> uniqueKeys, ref int spaceshipCount, ref int worldPrefabCount)
    {
        var behaviours = FindObjectsOfTypeAll(typeof(MonoBehaviour));
        if (behaviours == null)
            return;

        foreach (var behaviour in behaviours)
        {
            if (behaviour == null)
                continue;

            ScanPrefabReferencesFromObject(
                root: behaviour,
                entityPrefabType: entityPrefabType,
                spaceshipType: spaceshipType,
                uniqueKeys: uniqueKeys,
                spaceshipCount: ref spaceshipCount,
                worldPrefabCount: ref worldPrefabCount,
                maxDepth: 2);
        }
    }

    [HideFromIl2Cpp]
    private void ScanPrefabReferencesFromObject(
        object root,
        Type entityPrefabType,
        Type spaceshipType,
        HashSet<string> uniqueKeys,
        ref int spaceshipCount,
        ref int worldPrefabCount,
        int maxDepth)
    {
        var queue = new Queue<(object Obj, int Depth)>();
        var visited = new HashSet<int>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (obj, depth) = queue.Dequeue();
            if (obj == null)
                continue;

            var id = RuntimeHelpers.GetHashCode(obj);
            if (!visited.Add(id))
                continue;

            if (entityPrefabType.IsInstanceOfType(obj))
            {
                AddItemIfMatch(obj, spaceshipType, uniqueKeys, ref spaceshipCount, ref worldPrefabCount);
                continue;
            }

            if (depth >= maxDepth)
                continue;

            var objectType = obj.GetType();
            foreach (var member in GetPrefabReferenceMembers(objectType))
            {
                object? value;
                try
                {
                    if (member is FieldInfo field)
                        value = field.GetValue(obj);
                    else if (member is PropertyInfo property)
                        value = property.GetValue(obj);
                    else
                        continue;
                }
                catch
                {
                    continue;
                }

                if (value == null)
                    continue;

                if (entityPrefabType.IsInstanceOfType(value))
                {
                    AddItemIfMatch(value, spaceshipType, uniqueKeys, ref spaceshipCount, ref worldPrefabCount);
                    continue;
                }

                if (value is string)
                    continue;

                foreach (var nested in EnumerateAny(value))
                {
                    if (nested == null)
                        continue;
                    if (entityPrefabType.IsInstanceOfType(nested))
                    {
                        AddItemIfMatch(nested, spaceshipType, uniqueKeys, ref spaceshipCount, ref worldPrefabCount);
                    }
                    else if (depth + 1 < maxDepth)
                    {
                        queue.Enqueue((nested, depth + 1));
                    }
                }
            }
        }
    }

    [HideFromIl2Cpp]
    private static List<MemberInfo> GetPrefabReferenceMembers(Type type)
    {
        if (PrefabMemberCache.TryGetValue(type, out var cached))
            return cached;

        var members = new List<MemberInfo>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(flags))
            {
                if (LooksLikePrefabReferenceMember(field.Name))
                    members.Add(field);
            }

            foreach (var property in current.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    continue;
                if (LooksLikePrefabReferenceMember(property.Name))
                    members.Add(property);
            }
        }

        PrefabMemberCache[type] = members;
        return members;
    }

    [HideFromIl2Cpp]
    private static bool LooksLikePrefabReferenceMember(string memberName)
    {
        if (string.IsNullOrWhiteSpace(memberName))
            return false;

        var name = memberName.ToLowerInvariant();
        return name.Contains("prefab")
               || name.Contains("package")
               || name.Contains("epc")
               || name.Contains("eel");
    }

    [HideFromIl2Cpp]
    private static IEnumerable<Type> FindLoadedEpcTypes(Type entityPrefabType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type == null)
                    continue;
                if (!entityPrefabType.IsAssignableFrom(type))
                    continue;
                if (!type.Name.StartsWith("EPC_", StringComparison.Ordinal))
                    continue;
                if (seen.Add(type.AssemblyQualifiedName ?? type.FullName ?? type.Name))
                    yield return type;
            }
        }
    }

    [HideFromIl2Cpp]
    private void AddWorldTypeFallbackEntries(Type entityPrefabType, Type spaceshipType, HashSet<string> uniqueKeys, ref int worldPrefabCount)
    {
        var existingWorldTypes = new HashSet<string>(
            _items.Where(i => !i.IsSpaceshipComponent)
                .Select(i => i.SourceType.AssemblyQualifiedName ?? i.SourceType.FullName ?? i.SourceType.Name),
            StringComparer.Ordinal);

        foreach (var epcType in FindLoadedEpcTypes(entityPrefabType))
        {
            if (epcType.IsAbstract)
                continue;
            if (spaceshipType.IsAssignableFrom(epcType))
                continue;

            var typeKey = epcType.AssemblyQualifiedName ?? epcType.FullName ?? epcType.Name;
            if (existingWorldTypes.Contains(typeKey))
                continue;

            var key = $"TYPE:{typeKey}";
            if (!uniqueKeys.Add(key))
                continue;

            _items.Add(new ItemEntry(
                name: NormalizeEpcTypeName(epcType.Name),
                kind: "World",
                prefabId: 0,
                prefabIdText: "-",
                component: epcType,
                sourceType: epcType,
                amountText: "-",
                isSpaceshipComponent: false,
                isTypeOnly: true));
            worldPrefabCount++;
        }
    }

    [HideFromIl2Cpp]
    private static string NormalizeEpcTypeName(string typeName)
    {
        if (EpcDisplayNames.TryGetValue(typeName, out var displayName))
            return displayName;

        if (typeName.StartsWith("EPC_", StringComparison.Ordinal))
            return typeName.Substring(4);
        return typeName;
    }

    [HideFromIl2Cpp]
    private void AddItemIfMatch(object? obj, Type spaceshipType, HashSet<string> uniqueKeys, ref int spaceshipCount, ref int worldPrefabCount)
    {
        if (obj == null)
            return;

        var type = obj.GetType();
        var typeName = type.Name ?? "";
        if (!typeName.StartsWith("EPC_", StringComparison.Ordinal))
            return;

        var isSpaceship = spaceshipType.IsInstanceOfType(obj);
        ulong prefabId = 0;
        string amountText;
        string prefabIdText;

        if (isSpaceship)
        {
            TrySetAvailableAmount(obj, type, UnlimitedAvailableAmount);
            prefabId = GetPrefabId(obj, spaceshipType);
            prefabIdText = prefabId.ToString();
            amountText = GetMemberValue(obj, type, "_availableAmount")?.ToString() ?? "?";
        }
        else
        {
            prefabIdText = "-";
            amountText = "-";
        }

        var name = GetDisplayName(obj, type);
        var key = isSpaceship ? $"SC:{prefabId}:{name}" : $"OBJ:{type.FullName}:{name}";
        if (!uniqueKeys.Add(key))
            return;

        _items.Add(new ItemEntry(
            name: name,
            kind: isSpaceship ? "Ship" : "World",
            prefabId: prefabId,
            prefabIdText: prefabIdText,
            component: obj,
            sourceType: type,
            amountText: amountText,
            isSpaceshipComponent: isSpaceship,
            isTypeOnly: false));

        if (isSpaceship)
            spaceshipCount++;
        else
            worldPrefabCount++;
    }

    [HideFromIl2Cpp]
    private static string GetDisplayName(object obj, Type type)
    {
        var name = InvokeString(obj, type, "GetName");
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var customName = GetMemberValue(obj, type, "_name") as string;
        if (!string.IsNullOrWhiteSpace(customName))
            return customName!;

        var unityName = GetUnityObjectName(obj);
        if (!string.IsNullOrWhiteSpace(unityName))
            return unityName!;

        return NormalizeEpcTypeName(type.Name);
    }

    [HideFromIl2Cpp]
    private void SetHandSlot(ItemEntry item)
    {
        try
        {
            if (!item.IsSpaceshipComponent)
            {
                _status = "That prefab is not a ship component.";
                return;
            }
            if (item.IsTypeOnly)
            {
                _status = "Type-only world entry cannot be put in hotbar.";
                return;
            }

            TrySetAvailableAmount(item.Component, item.SourceType, UnlimitedAvailableAmount);

            var uiManagerType = FindType("UIManager");
            var uiManager = GetStaticMember(uiManagerType, "_singleton");
            var handList = GetMemberValue(uiManager, uiManagerType, "_handComponentsList");
            if (handList == null)
            {
                _status = "Hand component list not available. Load into the garage first.";
                return;
            }

            var method = handList.GetType().GetMethod("SetItemAtIndex", BindingFlags.Instance | BindingFlags.Public);
            method?.Invoke(handList, new object[] { _slot, item.Component });

            RefreshCoreAvailabilityMaps();
            _status = $"Put {item.Name} in slot {_slot + 1}.";
        }
        catch (Exception ex)
        {
            _status = "Set slot failed. See BepInEx log.";
            Plugin.LogSource.LogError(ex);
        }
    }

    [HideFromIl2Cpp]
    private void TeleportWorldTypeToPlayer(ItemEntry item, int maxCount)
    {
        try
        {
            GetSpawnTransform(out var posV3, out _);
            var marker = GuessEcsMarkerComponentName(item.SourceType);
            if (string.IsNullOrEmpty(marker))
            {
                _status = $"Teleport unsupported for {item.Name} (no known ECS marker type).";
                return;
            }

            if (FindType(marker) == null)
            {
                _status = $"Teleport unsupported for {item.Name} (no ECS type '{marker}' found).";
                return;
            }

            var moved = TryTeleportEntitiesWithComponentTo(marker, posV3, maxCount);
            _status = moved > 0
                ? $"Teleported {moved} {item.Name} entities."
                : $"No {item.Name} entities found (or ECS not accessible yet).";
        }
        catch (Exception ex)
        {
            _status = "Teleport failed. See BepInEx log.";
            Plugin.LogSource.LogError(ex);
        }
    }

    [HideFromIl2Cpp]
    private static string? GuessEcsMarkerComponentName(Type worldType)
    {
        // Guess: EPC_Foo → FooData
        var n = worldType.Name ?? "";
        if (n.StartsWith("EPC_", StringComparison.Ordinal))
            return n.Substring(4) + "Data";
        return null;
    }

    [HideFromIl2Cpp]
    private static bool ReflectedTypesMatch(Type a, Type b)
    {
        if (ReferenceEquals(a, b))
            return true;
        var af = a.FullName;
        var bf = b.FullName;
        return af != null && bf != null && string.Equals(af, bf, StringComparison.Ordinal);
    }

    [HideFromIl2Cpp]
    private static MethodInfo? TryFindStaticMethod(Type declaringType, string name, params Type[] parameterTypes)
    {
        foreach (var m in declaringType.GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (m.Name != name || m.IsGenericMethodDefinition)
                continue;
            var ps = m.GetParameters();
            if (ps.Length != parameterTypes.Length)
                continue;
            var match = true;
            for (var i = 0; i < ps.Length; i++)
            {
                if (!ReflectedTypesMatch(ps[i].ParameterType, parameterTypes[i]))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return m;
        }
        return null;
    }

    [HideFromIl2Cpp]
    private static object MakeFloat3Instance(Type float3Type, float x, float y, float z)
    {
        var ctor = float3Type.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });
        if (ctor != null)
            return ctor.Invoke(new object[] { x, y, z })!;
        var v = Activator.CreateInstance(float3Type)!;
        float3Type.GetField("x")?.SetValue(v, x);
        float3Type.GetField("y")?.SetValue(v, y);
        float3Type.GetField("z")?.SetValue(v, z);
        return v;
    }

    [HideFromIl2Cpp]
    private static object? MakeFloat4Instance(Type float4Type, float x, float y, float z, float w)
    {
        var ctor = float4Type.GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
        if (ctor != null)
            return ctor.Invoke(new object[] { x, y, z, w });
        var v = Activator.CreateInstance(float4Type);
        if (v == null)
            return null;
        float4Type.GetField("x")?.SetValue(v, x);
        float4Type.GetField("y")?.SetValue(v, y);
        float4Type.GetField("z")?.SetValue(v, z);
        float4Type.GetField("w")?.SetValue(v, w);
        return v;
    }

    [HideFromIl2Cpp]
    private static bool EntityIndexLooksValid(object boxedEntity)
    {
        try
        {
            var t = boxedEntity.GetType();
            var indexField = t.GetField("Index", BindingFlags.Instance | BindingFlags.Public);
            if (indexField?.GetValue(boxedEntity) is int idx)
                return idx >= 0;
            var indexProp = t.GetProperty("Index", BindingFlags.Instance | BindingFlags.Public);
            if (indexProp?.GetValue(boxedEntity) is int idx2)
                return idx2 >= 0;
        }
        catch
        {
        }
        return true;
    }

    [HideFromIl2Cpp]
    private static MethodInfo? TryFindMathInverse(Type? mathType, Type float4x4Type)
    {
        if (mathType == null)
            return null;
        foreach (var m in mathType.GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (m.Name != "inverse" || m.IsGenericMethodDefinition || m.GetParameters().Length != 1)
                continue;
            if (ReflectedTypesMatch(m.GetParameters()[0].ParameterType, float4x4Type))
                return m;
        }
        return null;
    }

    [HideFromIl2Cpp]
    private static MethodInfo? TryFindMathMulMatrixVector(Type? mathType, Type float4x4Type, Type float4Type)
    {
        if (mathType == null)
            return null;
        foreach (var m in mathType.GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (m.Name != "mul" || m.IsGenericMethodDefinition || m.GetParameters().Length != 2)
                continue;
            var p = m.GetParameters();
            if (ReflectedTypesMatch(p[0].ParameterType, float4x4Type) && ReflectedTypesMatch(p[1].ParameterType, float4Type))
                return m;
        }
        return null;
    }

    /// World pos → parent-local float3 (måste ha Parent).
    [HideFromIl2Cpp]
    private static bool TryConvertWorldPointToParentLocalSpace(
        object entityManager,
        object entity,
        Vector3 worldPosition,
        Type parentType,
        Type localToWorldType,
        Type float3Type,
        Type float4Type,
        MethodInfo mathInverse,
        MethodInfo mathMul,
        MethodInfo hasComponentGeneric,
        MethodInfo getComponentGeneric,
        out object localFloat3)
    {
        localFloat3 = MakeFloat3Instance(float3Type, 0f, 0f, 0f);
        try
        {
            var parentData = getComponentGeneric.MakeGenericMethod(parentType).Invoke(entityManager, new[] { entity });
            if (parentData == null)
                return false;

            var peField =
                parentType.GetField("Value", BindingFlags.Instance | BindingFlags.Public)
                ?? parentType.GetField("Parent", BindingFlags.Instance | BindingFlags.Public);
            var parentEntity = peField?.GetValue(parentData);
            if (parentEntity == null || !EntityIndexLooksValid(parentEntity))
                return false;

            var hasLtw = hasComponentGeneric.MakeGenericMethod(localToWorldType);
            if (!(bool)(hasLtw.Invoke(entityManager, new[] { parentEntity }) ?? false))
                return false;

            var ltwData = getComponentGeneric.MakeGenericMethod(localToWorldType).Invoke(entityManager, new[] { parentEntity });
            if (ltwData == null)
                return false;

            var valueField = localToWorldType.GetField("Value", BindingFlags.Instance | BindingFlags.Public);
            var worldMatrix = valueField?.GetValue(ltwData);
            if (worldMatrix == null)
                return false;

            var invMatrix = mathInverse.Invoke(null, new[] { worldMatrix });
            if (invMatrix == null)
                return false;

            var p4 = MakeFloat4Instance(float4Type, worldPosition.x, worldPosition.y, worldPosition.z, 1f);
            if (p4 == null)
                return false;

            var local4Obj = mathMul.Invoke(null, new[] { invMatrix, p4 });
            if (local4Obj == null)
                return false;

            var l4t = local4Obj.GetType();
            float lx = worldPosition.x, ly = worldPosition.y, lz = worldPosition.z;
            var xf = l4t.GetField("x", BindingFlags.Instance | BindingFlags.Public);
            var yf = l4t.GetField("y", BindingFlags.Instance | BindingFlags.Public);
            var zf = l4t.GetField("z", BindingFlags.Instance | BindingFlags.Public);
            if (xf?.GetValue(local4Obj) is float vx) lx = vx;
            if (yf?.GetValue(local4Obj) is float vy) ly = vy;
            if (zf?.GetValue(local4Obj) is float vz) lz = vz;
            localFloat3 = MakeFloat3Instance(float3Type, lx, ly, lz);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [HideFromIl2Cpp]
    private static object? TryMakeUniversePositionFromLtw(Type universePositionType, Type float3Type, object ltwPosition)
    {
        var double3Type = FindType("Unity.Mathematics.double3") ?? FindType("double3");
        if (double3Type == null)
            return null;

        object? universePositionValue = null;
        try
        {
            var universeCoreType = FindType("UniverseCore");
            if (universeCoreType != null)
            {
                var ltwToUpr = TryFindStaticMethod(universeCoreType, "LtwToUpr", float3Type);
                if (ltwToUpr != null)
                    universePositionValue = ltwToUpr.Invoke(null, new[] { ltwPosition });

                if (universePositionValue == null)
                {
                    var getCenter = universeCoreType.GetMethod("GetCenterEntityUniversePosition", BindingFlags.Static | BindingFlags.Public);
                    var ltwToUprWithCenter = TryFindStaticMethod(universeCoreType, "LtwToUpr", float3Type, double3Type);
                    var center = getCenter?.Invoke(null, Array.Empty<object>());
                    if (ltwToUprWithCenter != null && center != null)
                        universePositionValue = ltwToUprWithCenter.Invoke(null, new[] { ltwPosition, center });
                }
            }

            if (universePositionValue == null)
                return null;

            var upr = Activator.CreateInstance(universePositionType);
            if (upr == null)
                return null;

            var positionField = universePositionType.GetField("_position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (positionField == null)
                return null;

            positionField.SetValue(upr, universePositionValue);
            return upr;
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Teleport: failed to build UniversePosition: " + ex.Message);
            return null;
        }
    }

    [HideFromIl2Cpp]
    private static int TryTeleportEntitiesWithComponentTo(string componentTypeName, Vector3 targetPosition, int maxToMove)
    {
        var entityManager = TryGetDefaultEntityManager(out var worldName);
        if (entityManager == null)
        {
            Plugin.LogSource.LogWarning("Teleport: could not get EntityManager.");
            return 0;
        }

        var markerType = FindType(componentTypeName);
        if (markerType == null)
        {
            Plugin.LogSource.LogWarning($"Teleport: component type not found: {componentTypeName}");
            return 0;
        }

        var localTransformType = FindType("Unity.Transforms.LocalTransform") ?? FindType("LocalTransform");
        if (localTransformType == null)
        {
            Plugin.LogSource.LogWarning("Teleport: LocalTransform type not found.");
            return 0;
        }

        // EntityQuery > GetAllEntities (signaturer skiljer sig mellan Entities-versioner).
        var componentTypeType = FindType("Unity.Entities.ComponentType") ?? FindType("ComponentType");
        if (componentTypeType == null)
        {
            Plugin.LogSource.LogWarning("Teleport: Unity.Entities.ComponentType type not found.");
            return 0;
        }

        var readOnly = componentTypeType.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ReadOnly" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var readWrite = componentTypeType.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ReadWrite" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        if (readOnly == null || readWrite == null)
        {
            Plugin.LogSource.LogWarning("Teleport: ComponentType.ReadOnly<T>/ReadWrite<T> not found.");
            return 0;
        }

        var ctMarker = readOnly.MakeGenericMethod(markerType).Invoke(null, null);
        var ctLt = readWrite.MakeGenericMethod(localTransformType).Invoke(null, null);
        if (ctMarker == null || ctLt == null)
        {
            Plugin.LogSource.LogWarning("Teleport: failed to construct ComponentType filters.");
            return 0;
        }

        var createEq = entityManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "CreateEntityQuery"
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType.IsArray);
        if (createEq == null)
        {
            Plugin.LogSource.LogWarning("Teleport: EntityManager.CreateEntityQuery(ComponentType[]) not found.");
            return 0;
        }

        var ctArray = Array.CreateInstance(componentTypeType, 2);
        ctArray.SetValue(ctMarker, 0);
        ctArray.SetValue(ctLt, 1);

        var query = createEq.Invoke(entityManager, new object[] { ctArray });
        if (query == null)
        {
            Plugin.LogSource.LogWarning("Teleport: CreateEntityQuery returned null.");
            return 0;
        }

        var allocatorType = FindType("Unity.Collections.Allocator") ?? FindType("Allocator");
        var tempAllocator = allocatorType != null ? Enum.Parse(allocatorType, "Temp") : null;
        if (tempAllocator == null)
        {
            Plugin.LogSource.LogWarning("Teleport: Allocator.Temp not found.");
            return 0;
        }

        var toEntityArray = query.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ToEntityArray" && m.GetParameters().Length == 1);
        if (toEntityArray == null)
        {
            Plugin.LogSource.LogWarning("Teleport: EntityQuery.ToEntityArray not found.");
            return 0;
        }

        object entitiesArr;
        try
        {
            var paramType = toEntityArray.GetParameters()[0].ParameterType;
            object allocatorArg = tempAllocator;
            // Nyare Entities: Allocator → AllocatorHandle implicit.
            if (paramType.FullName != null && paramType.FullName.Contains("AllocatorManager+AllocatorHandle"))
                allocatorArg = CreateAllocatorHandleFromAllocator(tempAllocator, paramType) ?? tempAllocator;

            entitiesArr = toEntityArray.Invoke(query, new[] { allocatorArg })!;
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Teleport: ToEntityArray invoke failed: " + ex.Message);
            return 0;
        }
        finally
        {
            TryDisposeEntityQuery(query);
        }

        if (!TryGetEcsMethods(entityManager, out var hasComponentGeneric, out var getComponentGeneric, out var setComponentGeneric))
        {
            Plugin.LogSource.LogWarning("Teleport: EntityManager Has/Get/SetComponentData<T> methods not found.");
            return 0;
        }

        var hasMarker = hasComponentGeneric.MakeGenericMethod(markerType);
        var hasLocalTransform = hasComponentGeneric.MakeGenericMethod(localTransformType);
        var getLocalTransform = getComponentGeneric.MakeGenericMethod(localTransformType);
        var setLocalTransform = setComponentGeneric.MakeGenericMethod(localTransformType);

        var float3Type = FindType("Unity.Mathematics.float3") ?? FindType("float3");
        if (float3Type == null)
        {
            Plugin.LogSource.LogWarning("Teleport: float3 type not found.");
            return 0;
        }

        var qType = FindType("Unity.Mathematics.quaternion") ?? FindType("quaternion");
        var qIdentity = qType?.GetProperty("identity", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);

        var floatType = typeof(float);
        MethodInfo? fromPRS = null;
        object? fromPRSScaleArg = null;
        if (qType != null)
        {
            fromPRS = TryFindStaticMethod(localTransformType, "FromPositionRotationScale", float3Type, qType, float3Type);
            if (fromPRS != null)
                fromPRSScaleArg = MakeFloat3Instance(float3Type, 1f, 1f, 1f);
            else
            {
                fromPRS = TryFindStaticMethod(localTransformType, "FromPositionRotationScale", float3Type, qType, floatType);
                if (fromPRS != null)
                    fromPRSScaleArg = 1f;
            }
        }

        var fromPR = qType != null ? TryFindStaticMethod(localTransformType, "FromPositionRotation", float3Type, qType) : null;
        var fromP = TryFindStaticMethod(localTransformType, "FromPosition", float3Type);

        var targetF3 = MakeFloat3Instance(float3Type, targetPosition.x, targetPosition.y, targetPosition.z);
        var moved = 0;
        var universePositionUpdates = 0;
        var physicsStepUpdates = 0;
        var velocityClears = 0;

        var universePositionType = FindType("UniversePosition");
        var applyPhysicsStepType = FindType("ApplyPhysicsStepToUniversePosition");
        var physicsVelocityType = FindType("Unity.Physics.PhysicsVelocity") ?? FindType("PhysicsVelocity");

        MethodInfo? hasUniversePosition = null;
        MethodInfo? setUniversePosition = null;
        object? targetUniversePosition = null;
        if (universePositionType != null)
        {
            hasUniversePosition = hasComponentGeneric.MakeGenericMethod(universePositionType);
            setUniversePosition = setComponentGeneric.MakeGenericMethod(universePositionType);
            targetUniversePosition = TryMakeUniversePositionFromLtw(universePositionType, float3Type, targetF3);
        }

        MethodInfo? hasApplyPhysicsStep = null;
        MethodInfo? getApplyPhysicsStep = null;
        MethodInfo? setApplyPhysicsStep = null;
        FieldInfo? applyPhysicsStepSavedPositionField = null;
        if (applyPhysicsStepType != null)
        {
            hasApplyPhysicsStep = hasComponentGeneric.MakeGenericMethod(applyPhysicsStepType);
            getApplyPhysicsStep = getComponentGeneric.MakeGenericMethod(applyPhysicsStepType);
            setApplyPhysicsStep = setComponentGeneric.MakeGenericMethod(applyPhysicsStepType);
            applyPhysicsStepSavedPositionField = applyPhysicsStepType.GetField("_savedPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        MethodInfo? hasPhysicsVelocity = null;
        MethodInfo? setPhysicsVelocity = null;
        object? zeroPhysicsVelocity = null;
        if (physicsVelocityType != null)
        {
            hasPhysicsVelocity = hasComponentGeneric.MakeGenericMethod(physicsVelocityType);
            setPhysicsVelocity = setComponentGeneric.MakeGenericMethod(physicsVelocityType);
            zeroPhysicsVelocity = Activator.CreateInstance(physicsVelocityType);
        }

        void ApplyPersistentTeleportState(object entity, object localPosition)
        {
            if (hasUniversePosition != null && setUniversePosition != null && targetUniversePosition != null)
            {
                try
                {
                    if ((bool)(hasUniversePosition.Invoke(entityManager, new[] { entity }) ?? false))
                    {
                        setUniversePosition.Invoke(entityManager, new[] { entity, targetUniversePosition });
                        universePositionUpdates++;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogWarning("Teleport: UniversePosition update failed: " + ex.Message);
                }
            }

            if (hasApplyPhysicsStep != null && getApplyPhysicsStep != null && setApplyPhysicsStep != null && applyPhysicsStepSavedPositionField != null)
            {
                try
                {
                    if ((bool)(hasApplyPhysicsStep.Invoke(entityManager, new[] { entity }) ?? false))
                    {
                        var applyPhysicsStep = getApplyPhysicsStep.Invoke(entityManager, new[] { entity });
                        if (applyPhysicsStep != null)
                        {
                            applyPhysicsStepSavedPositionField.SetValue(applyPhysicsStep, localPosition);
                            setApplyPhysicsStep.Invoke(entityManager, new[] { entity, applyPhysicsStep });
                            physicsStepUpdates++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogWarning("Teleport: physics-step position update failed: " + ex.Message);
                }
            }

            if (hasPhysicsVelocity != null && setPhysicsVelocity != null && zeroPhysicsVelocity != null)
            {
                try
                {
                    if ((bool)(hasPhysicsVelocity.Invoke(entityManager, new[] { entity }) ?? false))
                    {
                        setPhysicsVelocity.Invoke(entityManager, new[] { entity, zeroPhysicsVelocity });
                        velocityClears++;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogWarning("Teleport: PhysicsVelocity clear failed: " + ex.Message);
                }
            }
        }

        var parentType = FindType("Unity.Transforms.Parent") ?? FindType("Parent");
        var localToWorldType = FindType("Unity.Transforms.LocalToWorld") ?? FindType("LocalToWorld");
        var float4Type = FindType("Unity.Mathematics.float4") ?? FindType("float4");
        var float4x4Type = FindType("Unity.Mathematics.float4x4") ?? FindType("float4x4");
        var mathType = FindType("Unity.Mathematics.math") ?? FindType("math");

        MethodInfo? mathInverse = null;
        MethodInfo? mathMul = null;
        if (mathType != null && float4x4Type != null && float4Type != null)
        {
            mathInverse = TryFindMathInverse(mathType, float4x4Type);
            mathMul = TryFindMathMulMatrixVector(mathType, float4x4Type, float4Type);
        }

        MethodInfo? hasParentComp = null;
        if (parentType != null)
            hasParentComp = hasComponentGeneric.MakeGenericMethod(parentType);

        var transformParentChainReady = parentType != null && localToWorldType != null && float4Type != null &&
                                        float4x4Type != null && mathInverse != null && mathMul != null &&
                                        hasParentComp != null;

        foreach (var entity in EnumerateAny(entitiesArr))
        {
            if (moved >= maxToMove)
                break;
            if (entity == null)
                continue;

            bool marker;
            bool lt;
            try
            {
                marker = (bool)(hasMarker.Invoke(entityManager, new[] { entity }) ?? false);
                if (!marker)
                    continue;
                lt = (bool)(hasLocalTransform.Invoke(entityManager, new[] { entity }) ?? false);
                if (!lt)
                    continue;
            }
            catch
            {
                continue;
            }

            var positionForLt = targetF3;
            if (hasParentComp != null)
            {
                bool hasP;
                try
                {
                    hasP = (bool)(hasParentComp.Invoke(entityManager, new[] { entity }) ?? false);
                }
                catch
                {
                    continue;
                }

                if (hasP)
                {
                    if (!transformParentChainReady)
                        continue;
                    if (!TryConvertWorldPointToParentLocalSpace(
                            entityManager,
                            entity,
                            targetPosition,
                            parentType!,
                            localToWorldType!,
                            float3Type,
                            float4Type!,
                            mathInverse!,
                            mathMul!,
                            hasComponentGeneric,
                            getComponentGeneric,
                            out var lf3))
                        continue;
                    positionForLt = lf3;
                }
            }

            object? currentLt = null;
            try
            {
                currentLt = getLocalTransform.Invoke(entityManager, new[] { entity });
            }
            catch
            {
                currentLt = null;
            }

            if (currentLt != null)
            {
                var posField = localTransformType.GetField("Position", BindingFlags.Instance | BindingFlags.Public);
                if (posField != null)
                {
                    try
                    {
                        posField.SetValue(currentLt, positionForLt);
                        setLocalTransform.Invoke(entityManager, new[] { entity, currentLt });
                        ApplyPersistentTeleportState(entity, positionForLt);
                        moved++;
                        continue;
                    }
                    catch
                    {
                        /* fallbacks nästa */
                    }
                }
            }

            object newLt;
            if (fromPRS != null && qIdentity != null && fromPRSScaleArg != null)
                newLt = fromPRS.Invoke(null, new[] { positionForLt, qIdentity, fromPRSScaleArg })!;
            else if (fromPR != null && qIdentity != null)
                newLt = fromPR.Invoke(null, new[] { positionForLt, qIdentity })!;
            else if (fromP != null)
                newLt = fromP.Invoke(null, new[] { positionForLt })!;
            else if (currentLt != null)
            {
                var current = currentLt;
                localTransformType.GetField("Position")?.SetValueDirect(__makeref(current), positionForLt);
                newLt = current;
            }
            else
                continue;

            try
            {
                setLocalTransform.Invoke(entityManager, new[] { entity, newLt });
                ApplyPersistentTeleportState(entity, positionForLt);
                moved++;
            }
            catch
            {
                // strunta i per-entity fel
            }
        }

        Plugin.LogSource.LogInfo(
            $"Teleport: moved {moved} entities with {componentTypeName} in world '{worldName ?? "?"}' " +
            $"(UniversePosition={universePositionUpdates}, savedPhysicsPosition={physicsStepUpdates}, velocityCleared={velocityClears}).");
        return moved;
    }

    [HideFromIl2Cpp]
    private static object? CreateAllocatorHandleFromAllocator(object allocatorEnum, Type allocatorHandleType)
    {
        try
        {
            var implicitOp = allocatorHandleType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "op_Implicit" && m.GetParameters().Length == 1);
            if (implicitOp == null)
                return null;
            return implicitOp.Invoke(null, new[] { allocatorEnum });
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Teleport: failed to create AllocatorHandle: " + ex.Message);
            return null;
        }
    }

    [HideFromIl2Cpp]
    private static object? TryGetDefaultEntityManager(out string? worldName)
    {
        worldName = null;
        var worldType = FindType("Unity.Entities.World") ?? FindType("World");
        if (worldType == null)
            return null;

        var worlds = CollectWorldCandidates(worldType);
        if (worlds.Count == 0)
            return null;

        // Host har ofta flera Worlds — server-world först.
        foreach (var world in worlds)
        {
            var name = TryGetWorldName(worldType, world);
            if (string.IsNullOrEmpty(name) || name.IndexOf("server", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!TryGetWorldEntityManager(worldType, world, out var em) || em == null)
                continue;
            worldName = name;
            return em;
        }

        var ncSingletonType = FindType("Netcore+Singleton") ?? FindType("Netcore.Singleton") ?? FindType("Netcore/Singleton");
        if (ncSingletonType != null)
        {
            foreach (var world in worlds)
            {
                if (!TryGetWorldEntityManager(worldType, world, out var em) || em == null)
                    continue;
                if (!IsServerEntityManager(em, ncSingletonType))
                    continue;
                worldName = TryGetWorldName(worldType, world);
                return em;
            }
        }

        foreach (var world in worlds)
        {
            if (!TryGetWorldEntityManager(worldType, world, out var em) || em == null)
                continue;
            worldName = TryGetWorldName(worldType, world);
            return em;
        }

        return null;
    }

    private static IEnumerable<(object EntityManager, string? WorldName)> EnumerateEntityManagers(bool serverFirst)
    {
        var worldType = FindType("Unity.Entities.World") ?? FindType("World");
        if (worldType == null)
            yield break;

        var candidates = new List<(object EntityManager, string? WorldName, bool IsServer)>();
        var ncSingletonType = FindType("Netcore+Singleton") ?? FindType("Netcore.Singleton") ?? FindType("Netcore/Singleton");
        foreach (var world in CollectWorldCandidates(worldType))
        {
            if (!TryGetWorldEntityManager(worldType, world, out var em) || em == null)
                continue;

            var name = TryGetWorldName(worldType, world);
            var isServer = name?.IndexOf("server", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (ncSingletonType != null && IsServerEntityManager(em, ncSingletonType));
            candidates.Add((em, name, isServer));
        }

        foreach (var item in candidates.OrderByDescending(c => serverFirst && c.IsServer))
            yield return (item.EntityManager, item.WorldName);
    }

    private static bool TryGetEcsMethods(
        object entityManager,
        out MethodInfo hasComponentGeneric,
        out MethodInfo getComponentGeneric,
        out MethodInfo setComponentGeneric)
    {
        hasComponentGeneric = null!;
        getComponentGeneric = null!;
        setComponentGeneric = null!;

        try
        {
            var methods = entityManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
            hasComponentGeneric = methods.FirstOrDefault(m => m.Name == "HasComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)!;
            getComponentGeneric = methods.FirstOrDefault(m => m.Name == "GetComponentData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)!;
            setComponentGeneric = methods.FirstOrDefault(m => m.Name == "SetComponentData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)!;
            return hasComponentGeneric != null && getComponentGeneric != null && setComponentGeneric != null;
        }
        catch
        {
            return false;
        }
    }

    [HideFromIl2Cpp]
    private static int TryCountOnlineNetcoreClients()
    {
        var clientType = FindType("NetcoreClient");
        var ncSingletonType = FindType("Netcore+Singleton") ?? FindType("Netcore.Singleton") ?? FindType("Netcore/Singleton");
        if (clientType == null || ncSingletonType == null)
        {
            if (clientType == null)
                LogMissingOptionalType("Count Netcore clients", "NetcoreClient");
            if (ncSingletonType == null)
                LogMissingOptionalType("Count Netcore clients", "Netcore+Singleton");
            return -1;
        }

        try
        {
            foreach (var (entityManager, _) in EnumerateEntityManagers(serverFirst: true))
            {
                if (!IsServerEntityManager(entityManager, ncSingletonType))
                    continue;

                var entitiesArr = TryCreateEntityArray(entityManager, new[] { clientType }, readWriteLast: false, "CountNetcoreClients");
                if (entitiesArr == null || !TryGetEcsMethods(entityManager, out var hasComponentGeneric, out var getComponentGeneric, out _))
                    continue;

                var hasClient = hasComponentGeneric.MakeGenericMethod(clientType);
                var getClient = getComponentGeneric.MakeGenericMethod(clientType);
                var count = 0;

                foreach (var entity in EnumerateAny(entitiesArr))
                {
                    if (entity == null)
                        continue;
                    if (!(bool)(hasClient.Invoke(entityManager, new[] { entity }) ?? false))
                        continue;

                    var client = getClient.Invoke(entityManager, new[] { entity });
                    if (client == null)
                        continue;

                    var isOnline = GetMemberValue(client, clientType, "_isOnline");
                    if (isOnline is bool online)
                    {
                        if (online)
                            count++;
                    }
                    else
                    {
                        count++;
                    }
                }

                return count;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Count Netcore clients failed: " + ex.Message);
        }

        return -1;
    }

    [HideFromIl2Cpp]
    private int TryApplyServerPlayerFeatureToggles()
    {
        var changed = 0;
        var broadcast = Time.unscaledTime >= _nextPlayerFeatureBroadcastAt;
        if (broadcast)
            _nextPlayerFeatureBroadcastAt = Time.unscaledTime + 1f;

        var ncSingletonType = FindType("Netcore+Singleton") ?? FindType("Netcore.Singleton") ?? FindType("Netcore/Singleton");

        try
        {
            foreach (var (entityManager, _) in EnumerateEntityManagers(serverFirst: true))
            {
                // Bara skriv på server-world; client mirrors = desync / crash.
                if (ncSingletonType != null && !IsServerEntityManager(entityManager, ncSingletonType))
                    continue;

                if (_noPlayerWind)
                {
                    try { changed += TryZeroPlanetWind(entityManager, broadcast); }
                    catch (Exception ex) { Plugin.LogSource.LogWarning("No Wind apply failed: " + ex.Message); }
                }

                if (_noStatic)
                {
                    try { changed += TryApplyNoStatic(entityManager, broadcast); }
                    catch (Exception ex) { Plugin.LogSource.LogWarning("No Static apply failed: " + ex.Message); }
                }

                // Hittade server world, stoppa sök.
                break;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Apply player feature toggles failed: " + ex.Message);
        }

        return changed;
    }

    /// No static: avstängd (gäst-GPU). Loggar en gång. Riktig lösning = client-mod eller net-hooks.
    [HideFromIl2Cpp]
    private int TryApplyNoStatic(object entityManager, bool broadcast)
    {
        // TODO: kod — lämna signatur så UI/apply fortsätter lira.
        if (!_noStaticNotImplementedLogged)
        {
            _noStaticNotImplementedLogged = true;
            Plugin.LogSource.LogWarning("No Static: feature is a placeholder and currently does nothing.");
        }
        _ = entityManager;
        _ = broadcast;
        return 0;
    }

    [HideFromIl2Cpp]
    private static int TryZeroPlanetWind(object entityManager, bool broadcast)
    {
        var planetWindType = FindType("PlanetWindData");
        if (planetWindType == null)
            return 0;

        var entitiesArr = TryCreateEntityArray(entityManager, new[] { planetWindType }, readWriteLast: true, "No Wind");
        if (entitiesArr == null || !TryGetEcsMethods(entityManager, out _, out var getComponentGeneric, out var setComponentGeneric))
            return 0;

        var getWind = getComponentGeneric.MakeGenericMethod(planetWindType);
        var setWind = setComponentGeneric.MakeGenericMethod(planetWindType);
        var changed = 0;

        foreach (var entity in EnumerateAny(entitiesArr))
        {
            if (entity == null)
                continue;

            try
            {
                var wind = getWind.Invoke(entityManager, new[] { entity });
                if (wind == null)
                    continue;

                SetMemberValue(wind, planetWindType, "_soundStrength", 0f);
                SetMemberValue(wind, planetWindType, "_physicsStrength", 0f);
                setWind.Invoke(entityManager, new[] { entity, wind });

                changed++;
            }
            catch
            {
            }
        }

        _ = broadcast;
        return changed;
    }

    private static List<object> CollectWorldCandidates(Type worldType)
    {
        var worlds = new List<object>(4);

        try
        {
            var prop = worldType.GetProperty("DefaultGameObjectInjectionWorld", BindingFlags.Static | BindingFlags.Public);
            AddWorldCandidate(worlds, prop?.GetValue(null));
        }
        catch
        {
        }

        try
        {
            var field = worldType.GetField("DefaultGameObjectInjectionWorld", BindingFlags.Static | BindingFlags.Public);
            AddWorldCandidate(worlds, field?.GetValue(null));
        }
        catch
        {
        }

        try
        {
            var allProp = worldType.GetProperty("All", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            AddWorldCandidatesFromCollection(worlds, allProp?.GetValue(null));
        }
        catch
        {
        }

        try
        {
            var allField = worldType.GetField("All", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            AddWorldCandidatesFromCollection(worlds, allField?.GetValue(null));
        }
        catch
        {
        }

        return worlds;
    }

    private static void AddWorldCandidatesFromCollection(List<object> worlds, object? collection)
    {
        if (collection == null)
            return;

        foreach (var world in EnumerateAny(collection))
            AddWorldCandidate(worlds, world);
    }

    private static void AddWorldCandidate(List<object> worlds, object? world)
    {
        if (world == null)
            return;

        foreach (var existing in worlds)
        {
            if (ReferenceEquals(existing, world))
                return;
        }

        worlds.Add(world);
    }

    private static string? TryGetWorldName(Type worldType, object world)
    {
        try
        {
            return worldType.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public)?.GetValue(world) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetWorldEntityManager(Type worldType, object world, out object? entityManager)
    {
        entityManager = null;
        try
        {
            var emProp = worldType.GetProperty("EntityManager", BindingFlags.Instance | BindingFlags.Public);
            entityManager = emProp?.GetValue(world);
            return entityManager != null;
        }
        catch
        {
            entityManager = null;
            return false;
        }
    }

    private static bool IsServerEntityManager(object entityManager, Type ncSingletonType)
    {
        try
        {
            var entitiesArr = TryCreateEntityArray(entityManager, new[] { ncSingletonType }, readWriteLast: false, "FindServerWorld");
            if (entitiesArr == null)
                return false;

            if (!TryGetEcsMethods(entityManager, out var hasComponentGeneric, out var getComponentGeneric, out _))
                return false;

            var hasNcSingleton = hasComponentGeneric.MakeGenericMethod(ncSingletonType);
            var getNcSingleton = getComponentGeneric.MakeGenericMethod(ncSingletonType);

            foreach (var entity in EnumerateAny(entitiesArr))
            {
                if (!(bool)(hasNcSingleton.Invoke(entityManager, new[] { entity }) ?? false))
                    continue;

                var ncSingleton = getNcSingleton.Invoke(entityManager, new[] { entity });
                if (ncSingleton == null)
                    continue;

                var isServer = GetMemberValue(ncSingleton, ncSingletonType, "_isServer");
                if (isServer is bool b && b)
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    [HideFromIl2Cpp]
    private static bool TryGetServerNetcoreNewEventBuffer(
        Type wrapperType,
        string feature,
        out object? entityManager,
        out object? buffer,
        out MethodInfo? bufferAdd,
        out string? worldName)
    {
        entityManager = null;
        buffer = null;
        bufferAdd = null;
        worldName = null;

        var ncSingletonType = FindType("Netcore+Singleton") ?? FindType("Netcore.Singleton") ?? FindType("Netcore/Singleton");
        if (ncSingletonType == null)
        {
            LogMissingOptionalType(feature, "Netcore+Singleton");
            return false;
        }

        foreach (var (em, name) in EnumerateEntityManagers(serverFirst: true))
        {
            if (!TryGetEcsMethods(em, out var hasComponentGeneric, out var getComponentGeneric, out _))
                continue;

            var entitiesArr = TryCreateEntityArray(em, new[] { ncSingletonType }, readWriteLast: false, feature);
            if (entitiesArr == null)
                continue;

            var hasNcSingleton = hasComponentGeneric.MakeGenericMethod(ncSingletonType);
            var getNcSingleton = getComponentGeneric.MakeGenericMethod(ncSingletonType);
            object? singletonEntity = null;
            object? ncSingleton = null;

            foreach (var entity in EnumerateAny(entitiesArr))
            {
                if (entity == null || !(bool)(hasNcSingleton.Invoke(em, new[] { entity }) ?? false))
                    continue;

                var data = getNcSingleton.Invoke(em, new[] { entity });
                if (!(GetMemberValue(data, ncSingletonType, "_isServer") is bool isServer && isServer))
                    continue;

                singletonEntity = entity;
                ncSingleton = data;
                break;
            }

            if (singletonEntity == null || ncSingleton == null)
                continue;

            var getBufferGeneric = em.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "GetBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
            if (getBufferGeneric == null)
                continue;

            var getBuffer = getBufferGeneric.MakeGenericMethod(wrapperType);
            var bufferArgs = new object?[getBuffer.GetParameters().Length];
            bufferArgs[0] = singletonEntity;
            for (var i = 1; i < bufferArgs.Length; i++)
                bufferArgs[i] = false;

            var resolvedBuffer = getBuffer.Invoke(em, bufferArgs);
            if (resolvedBuffer == null)
                continue;

            var resolvedAdd = resolvedBuffer.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == wrapperType);
            if (resolvedAdd == null)
                continue;

            entityManager = em;
            buffer = resolvedBuffer;
            bufferAdd = resolvedAdd;
            worldName = name;
            return true;
        }

        return false;
    }

    [HideFromIl2Cpp]
    private static void GetSpawnTransform(out Vector3 spawnPosition, out Quaternion spawnRotation)
    {
        spawnPosition = new Vector3(0f, 2f, 0f);
        spawnRotation = Quaternion.identity;
        var cam = Camera.main;
        if (cam == null)
            return;

        spawnPosition = cam.transform.position + (cam.transform.forward * 3f) + (Vector3.up * 0.4f);
        spawnRotation = cam.transform.rotation;
    }

    private bool RefreshAvailabilityUiAndCore(bool updateStatus)
    {
        var coreRefreshed = RefreshCoreAvailabilityMaps();

        if (updateStatus)
        {
            if (coreRefreshed)
                _status = "Co-op caps refreshed and broadcast; local inventory UI will update naturally.";
            else
                _status = "Nothing to refresh yet. Load into the garage first.";
        }

        return coreRefreshed;
    }

    private bool RefreshCoreAvailabilityMaps()
    {
        var core = GetCoreInstance();
        if (core == null)
            return false;

        SetCoreComponentAmountsUnlimited(core);
        var coreType = core.GetType();
        var refreshed = false;

        refreshed |= TryInvokeNoArgs(core, coreType, "RefreshSharedAvailableComponents");
        refreshed |= TryInvokeNoArgs(core, coreType, "RefreshPrivateAvailableComponents");
        SetCoreComponentAmountsUnlimited(core);

        TryBroadcastUnlimitedAvailability(core);
        ScheduleAvailabilityReplayBurst(6);
        return refreshed;
    }

    [HideFromIl2Cpp]
    private void ScheduleAvailabilityReplayBurst(int count)
    {
        _pendingAvailabilityReplays = Math.Max(_pendingAvailabilityReplays, count);
        var next = Time.unscaledTime;
        if (_nextAvailabilityReplayAt <= 0f || _nextAvailabilityReplayAt > next)
            _nextAvailabilityReplayAt = next;
    }

    [HideFromIl2Cpp]
    private void TryProcessAvailabilityReplay()
    {
        var now = Time.unscaledTime;
        if (now >= _nextClientProbeAt)
        {
            _nextClientProbeAt = now + 0.5f;
            var online = TryCountOnlineNetcoreClients();
            if (online >= 0 && online != _lastOnlineClientCount)
            {
                var previous = _lastOnlineClientCount;
                _lastOnlineClientCount = online;

                if (online > 0 && online > Math.Max(0, previous))
                {
                    // Skicka direkt så ny klients UI hinner före första burst (första invite buggen).
                    try
                    {
                        var core = GetCoreInstance();
                        if (core != null)
                        {
                            SetCoreComponentAmountsUnlimited(core);
                            TryBroadcastUnlimitedAvailability(core);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource.LogWarning("Immediate cap broadcast on count up failed: " + ex.Message);
                    }

                    ScheduleAvailabilityReplayBurst(previous < 0 ? 8 : 6);
                    _nextAvailabilityHeartbeatAt = now + 4f;
                    Plugin.LogSource.LogInfo($"Co-op cap replay scheduled after Netcore client count changed {previous}->{online}.");
                }
            }
        }

        if (_pendingAvailabilityReplays <= 0 || now < _nextAvailabilityReplayAt)
            return;

        _nextAvailabilityReplayAt = now + 2f;
        if (TryReplayUnlimitedAvailability())
            _pendingAvailabilityReplays--;
        else
            _pendingAvailabilityReplays = Math.Min(_pendingAvailabilityReplays, 3);
    }

    [HideFromIl2Cpp]
    private bool TryReplayUnlimitedAvailability()
    {
        var core = GetCoreInstance();
        if (core == null)
            return false;

        SetCoreComponentAmountsUnlimited(core);
        TryBroadcastUnlimitedAvailability(core, precedeWithClear: false);
        return true;
    }

    /// Server: SetAvailableComponent 999999 + refresh för varje SCPrefab till alla. Client: noop.
    [HideFromIl2Cpp]
    internal static void TryBroadcastUnlimitedAvailability(object core, bool precedeWithClear = false)
    {
        try
        {
            var newEventType = FindType("NetcoreNewEvent");
            var setAvailEventType = FindType("NetcoreEvent_SetAvailableComponent");
            var netcoreEventType = FindType("NetcoreEvent");
            var scPrefabType = FindType("SCPrefab");
            if (newEventType == null || setAvailEventType == null || netcoreEventType == null || scPrefabType == null)
            {
                if (newEventType == null) LogMissingOptionalType("Broadcast Avail", "NetcoreNewEvent");
                if (setAvailEventType == null) LogMissingOptionalType("Broadcast Avail", "NetcoreEvent_SetAvailableComponent");
                if (netcoreEventType == null) LogMissingOptionalType("Broadcast Avail", "NetcoreEvent");
                if (scPrefabType == null) LogMissingOptionalType("Broadcast Avail", "SCPrefab");
                return;
            }

            if (!TryGetServerNetcoreNewEventBuffer(newEventType, "BroadcastAvail", out _, out var buffer, out var bufferAdd, out var worldName) ||
                buffer == null || bufferAdd == null)
                return;

            var createGeneric = netcoreEventType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);
            if (createGeneric == null)
                return;
            var createForSetAvail = createGeneric.MakeGenericMethod(setAvailEventType);
            _ = precedeWithClear; // Legacy arg — clear aldrig klient-map mitt i session.

            var componentsMap = GetMemberValue(core, core.GetType(), "_componentsMap");
            if (componentsMap == null)
                return;

            var sent = 0;
            foreach (var prefabKey in EnumerateDictionaryKeys(componentsMap))
            {
                if (prefabKey == null)
                    continue;

                var data = Activator.CreateInstance(setAvailEventType);
                if (data == null)
                    continue;

                if (!SetMemberValue(data, setAvailEventType, "_scPrefab", prefabKey)) continue;
                if (!SetMemberValue(data, setAvailEventType, "_amount", UnlimitedAvailableAmount)) continue;
                SetMemberValue(data, setAvailEventType, "_refreshUI", true);
                SetMemberValue(data, setAvailEventType, "_highlightAsNew", false);

                object? netcoreEvent;
                try
                {
                    netcoreEvent = createForSetAvail.Invoke(null, new object[] { data, true });
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogWarning("Broadcast Avail: NetcoreEvent.Create failed: " + ex.Message);
                    return;
                }
                if (netcoreEvent == null)
                    continue;

                var newEvent = Activator.CreateInstance(newEventType);
                if (newEvent == null)
                    continue;

                if (!SetMemberValue(newEvent, newEventType, "_event", netcoreEvent))
                    continue;
                SetMemberValue(newEvent, newEventType, "_sortValue", 0);

                bufferAdd.Invoke(buffer, new[] { newEvent });
                sent++;
            }

            if (sent > 0)
                Plugin.LogSource.LogInfo($"Broadcast Avail: queued {sent} SetAvailableComponent events to clients via '{worldName ?? "server"}'.");
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Broadcast Avail failed: " + ex.Message);
        }
    }

    private static IEnumerable<object?> EnumerateDictionaryKeys(object dictionaryLike)
    {
        if (dictionaryLike is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
                yield return entry.Key;
            yield break;
        }

        var keys = GetMemberValue(dictionaryLike, dictionaryLike.GetType(), "Keys");
        if (keys != null)
        {
            foreach (var key in EnumerateAny(keys))
                yield return key;
        }
    }

    private static bool TryInvokeNoArgs(object instance, Type type, string methodName)
    {
        try
        {
            var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return false;
            method.Invoke(instance, Array.Empty<object>());
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning($"{type.FullName}.{methodName} invocation failed: {ex.Message}");
            return false;
        }
    }

    private static object? GetCoreInstance()
    {
        var coreType = FindType("Core");
        if (coreType == null)
            return null;

        var singleton = GetStaticMember(coreType, "_singleton");
        if (singleton != null)
            return singleton;

        try
        {
            return coreType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(null, Array.Empty<object>());
        }
        catch
        {
            return null;
        }
    }

    private void TryRefreshLoadedComponentCaps()
    {
        try
        {
            var spaceshipType = FindType("EPC_SpaceshipComponent");
            if (spaceshipType != null)
            {
                var allSpaceshipComponents = FindObjectsOfTypeAll(spaceshipType);
                if (allSpaceshipComponents != null)
                {
                    foreach (var component in allSpaceshipComponents)
                    {
                        if (component != null)
                            TrySetAvailableAmount(component, component.GetType(), UnlimitedAvailableAmount);
                    }
                }
            }

            var core = GetCoreInstance();
            if (core != null)
                SetCoreComponentAmountsUnlimited(core);
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Background cap refresh failed: " + ex.Message);
        }
    }

    internal static void SetCoreComponentAmountsUnlimited(object coreInstance)
    {
        try
        {
            var coreType = coreInstance.GetType();
            var componentsMap = GetMemberValue(coreInstance, coreType, "_componentsMap");
            if (componentsMap == null)
                return;

            foreach (var value in EnumerateDictionaryValues(componentsMap))
            {
                if (value != null)
                    TrySetAvailableAmount(value, value.GetType(), UnlimitedAvailableAmount);
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Failed to update Core._componentsMap caps: " + ex.Message);
        }
    }

    [HideFromIl2Cpp]
    private static object? TryCreateEntityArray(object entityManager, Type[] componentTypes, bool readWriteLast, string logPrefix)
    {
        var componentTypeType = FindType("Unity.Entities.ComponentType") ?? FindType("ComponentType");
        if (componentTypeType == null)
        {
            Plugin.LogSource.LogWarning($"{logPrefix}: Unity.Entities.ComponentType type not found.");
            return null;
        }

        var readOnly = componentTypeType.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ReadOnly" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var readWrite = componentTypeType.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ReadWrite" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        if (readOnly == null || readWrite == null)
        {
            Plugin.LogSource.LogWarning($"{logPrefix}: ComponentType.ReadOnly<T>/ReadWrite<T> not found.");
            return null;
        }

        var createEq = entityManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "CreateEntityQuery"
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType.IsArray);
        if (createEq == null)
        {
            Plugin.LogSource.LogWarning($"{logPrefix}: EntityManager.CreateEntityQuery(ComponentType[]) not found.");
            return null;
        }

        var ctArray = Array.CreateInstance(componentTypeType, componentTypes.Length);
        for (var i = 0; i < componentTypes.Length; i++)
        {
            var factory = readWriteLast && i == componentTypes.Length - 1 ? readWrite : readOnly;
            ctArray.SetValue(factory.MakeGenericMethod(componentTypes[i]).Invoke(null, null), i);
        }

        var query = createEq.Invoke(entityManager, new object[] { ctArray });
        if (query == null)
        {
            Plugin.LogSource.LogWarning($"{logPrefix}: CreateEntityQuery returned null.");
            return null;
        }

        var allocatorType = FindType("Unity.Collections.Allocator") ?? FindType("Allocator");
        var tempAllocator = allocatorType != null ? Enum.Parse(allocatorType, "Temp") : null;
        if (tempAllocator == null)
        {
            Plugin.LogSource.LogWarning($"{logPrefix}: Allocator.Temp not found.");
            return null;
        }

        var toEntityArray = query.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ToEntityArray" && m.GetParameters().Length == 1);
        if (toEntityArray == null)
        {
            Plugin.LogSource.LogWarning($"{logPrefix}: EntityQuery.ToEntityArray not found.");
            return null;
        }

        try
        {
            var paramType = toEntityArray.GetParameters()[0].ParameterType;
            object allocatorArg = tempAllocator;
            if (paramType.FullName != null && paramType.FullName.Contains("AllocatorManager+AllocatorHandle"))
                allocatorArg = CreateAllocatorHandleFromAllocator(tempAllocator, paramType) ?? tempAllocator;

            return toEntityArray.Invoke(query, new[] { allocatorArg });
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning($"{logPrefix}: ToEntityArray invoke failed: {ex.Message}");
            return null;
        }
        finally
        {
            // Dispose queries — leak = långsamt död.
            TryDisposeEntityQuery(query);
        }
    }

    [HideFromIl2Cpp]
    private static void TryDisposeEntityQuery(object? query)
    {
        if (query == null)
            return;
        try
        {
            var dispose = query.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            dispose?.Invoke(query, null);
        }
        catch
        {
            // Dispose best effort; vissa builds = struct ref, reflection strular.
        }
    }

    internal static bool TrySetAvailableAmount(object component, Type componentType, int amount)
    {
        return SetMemberValue(component, componentType, "_availableAmount", amount);
    }

    private static IEnumerable<object>? GetCoreSpaceshipComponents()
    {
        var coreType = FindType("Core");
        if (coreType == null)
            return null;

        var coreSingleton = GetStaticMember(coreType, "_singleton");
        if (coreSingleton == null)
            return null;

        var array = GetMemberValue(coreSingleton, coreType, "_spaceshipComponents");
        if (array == null)
            return null;

        return EnumerateAny(array);
    }

    private static IEnumerable<object?>? GetCoreSpaceshipComponentMapValues()
    {
        var core = GetCoreInstance();
        if (core == null)
            return null;

        var componentsMap = GetMemberValue(core, core.GetType(), "_componentsMap");
        return componentsMap == null ? null : EnumerateDictionaryValues(componentsMap);
    }

    private static IEnumerable<object>? FindObjectsOfTypeAll(Type epcType)
    {
        if (_findObjectsOfTypeAll == null && _findObjectsOfTypeAllAttempted == null)
        {
            _findObjectsOfTypeAllAttempted = ResolveFindObjectsMethod();
            _findObjectsOfTypeAll = _findObjectsOfTypeAllAttempted;
        }

        if (_findObjectsOfTypeAll == null)
            return null;

        var paramType = _findObjectsOfTypeAll.GetParameters()[0].ParameterType;
        object? typeArg;
        if (paramType.IsAssignableFrom(typeof(Type)))
        {
            typeArg = epcType;
        }
        else
        {
            typeArg = ConvertToIl2CppType(epcType);
            if (typeArg == null)
            {
                Plugin.LogSource.LogWarning("Could not convert System.Type to Il2CppSystem.Type for FindObjectsOfTypeAll.");
                return null;
            }
        }

        var result = _findObjectsOfTypeAll.Invoke(null, new[] { typeArg });
        if (result == null)
            return Array.Empty<object>();

        return EnumerateAny(result);
    }

    private static MethodInfo? ResolveFindObjectsMethod()
    {
        var resourcesType = typeof(Resources);
        return resourcesType.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll"
                              && !m.IsGenericMethod
                              && m.GetParameters().Length == 1);
    }

    private static object? ConvertToIl2CppType(Type systemType)
    {
        if (_il2cppTypeFrom == null)
        {
            var il2cppTypeClass = FindType("Il2CppInterop.Runtime.Il2CppType");
            if (il2cppTypeClass == null)
                return null;

            _il2cppTypeFrom = il2cppTypeClass.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => m.Name == "From" && !m.IsGenericMethod)
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        if (_il2cppTypeFrom == null)
            return null;

        var parameters = _il2cppTypeFrom.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = systemType;
        for (var i = 1; i < parameters.Length; i++)
        {
            if (parameters[i].HasDefaultValue)
                args[i] = parameters[i].DefaultValue;
            else if (parameters[i].ParameterType == typeof(bool))
                args[i] = true;
            else
                args[i] = null;
        }

        return _il2cppTypeFrom.Invoke(null, args);
    }

    private static IEnumerable<object> EnumerateAny(object source)
    {
        try
        {
            if (source is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null)
                        yield return item;
                }
                yield break;
            }

            var lengthProp = source.GetType().GetProperty("Length") ?? source.GetType().GetProperty("Count");
            var indexer = source.GetType().GetMethod("get_Item", new[] { typeof(int) });
            if (lengthProp != null && indexer != null)
            {
                var length = (int)(lengthProp.GetValue(source) ?? 0);
                for (var i = 0; i < length; i++)
                {
                    var item = indexer.Invoke(source, new object[] { i });
                    if (item != null)
                        yield return item;
                }
            }
        }
        finally
        {
            TryDisposeNativeCollection(source);
        }
    }

    [HideFromIl2Cpp]
    private void TryCoopAvailabilityHeartbeat(float now)
    {
        if (now < _nextAvailabilityHeartbeatAt)
            return;

        _nextAvailabilityHeartbeatAt = now + 8f;
        if (_lastOnlineClientCount <= 0 && _lastPlayerCount <= 1)
            return;

        try
        {
            var core = GetCoreInstance();
            if (core != null)
                SetCoreComponentAmountsUnlimited(core);
            ScheduleAvailabilityReplayBurst(6);
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning("Co-op cap heartbeat failed: " + ex.Message);
        }
    }

    [HideFromIl2Cpp]
    private static void LogMissingOptionalType(string feature, string typeName)
    {
        var key = feature + ":" + typeName;
        if (!LoggedMissingOptionalTypes.Add(key))
            return;
        Plugin.LogSource.LogWarning($"{feature}: optional game/network type not found: {typeName}. Skipping that sync path.");
    }

    [HideFromIl2Cpp]
    private static void TryDisposeNativeCollection(object? source)
    {
        if (source == null)
            return;

        try
        {
            var typeName = source.GetType().FullName ?? source.GetType().Name;
            if (typeName.IndexOf("NativeArray", StringComparison.OrdinalIgnoreCase) < 0 &&
                typeName.IndexOf("NativeList", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            var dispose = source.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            dispose?.Invoke(source, null);
        }
        catch
        {
        }
    }

    private static string? GetUnityObjectName(object obj)
    {
        try
        {
            var prop = obj.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public);
            return prop?.GetValue(obj) as string;
        }
        catch
        {
            return null;
        }
    }

    [HideFromIl2Cpp]
    private ulong GetPrefabId(object component, Type epcType)
    {
        var scPrefabType = FindType("SCPrefab");
        if (scPrefabType == null)
            return 0;

        try
        {
            var ctor = scPrefabType.GetConstructor(BindingFlags.Instance | BindingFlags.Public, null, new[] { epcType }, null);
            var scPrefab = ctor?.Invoke(new[] { component });
            if (scPrefab == null)
                return 0;

            var prefabField = scPrefabType.GetField("_prefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prefabField?.GetValue(scPrefab) is ulong direct)
                return direct;

            var toUlong = scPrefabType.GetMethod("ToUlong", BindingFlags.Instance | BindingFlags.Public);
            if (toUlong?.Invoke(scPrefab, Array.Empty<object>()) is ulong fromMethod)
                return fromMethod;
        }
        catch
        {
        }

        return 0;
    }

    private static string InvokeString(object instance, Type type, string methodName)
    {
        try
        {
            return type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(instance, Array.Empty<object>()) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    internal static object? GetStaticMember(Type? type, string name)
    {
        if (type == null) return null;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.CanRead)
                return prop.GetValue(null);
        }
        catch
        {
        }

        try
        {
            return type.GetField(name, flags)?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    internal static object? GetMemberValue(object? instance, Type? type, string name)
    {
        if (instance == null || type == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        for (var current = type; current != null; current = current.BaseType)
        {
            try
            {
                var prop = current.GetProperty(name, flags);
                if (prop != null && prop.CanRead)
                    return prop.GetValue(instance);
            }
            catch
            {
            }

            try
            {
                var field = current.GetField(name, flags);
                if (field != null)
                    return field.GetValue(instance);
            }
            catch
            {
            }
        }

        return null;
    }

    internal static bool SetMemberValue(object? instance, Type? type, string name, object? value)
    {
        if (instance == null || type == null) return false;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        for (var current = type; current != null; current = current.BaseType)
        {
            try
            {
                var prop = current.GetProperty(name, flags);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(instance, value);
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                var field = current.GetField(name, flags);
                if (field != null)
                {
                    field.SetValue(instance, value);
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static IEnumerable<object?> EnumerateDictionaryValues(object dictionaryLike)
    {
        if (dictionaryLike is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
                yield return entry.Value;
            yield break;
        }

        var values = GetMemberValue(dictionaryLike, dictionaryLike.GetType(), "Values");
        if (values != null)
        {
            foreach (var value in EnumerateAny(values))
                yield return value;
        }
    }

    internal static Type? FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = asm.GetType(name);
                if (type != null)
                    return type;
            }
            catch
            {
            }

            Type?[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types ?? Array.Empty<Type?>();
            }
            catch
            {
                continue;
            }

            foreach (var t in types)
            {
                if (t == null)
                    continue;
                if (t.Name == name || t.FullName == name)
                    return t;
            }
        }

        return null;
    }

    private sealed class ItemEntry
    {
        public ItemEntry(string name, string kind, ulong prefabId, string prefabIdText, object component, Type sourceType, string amountText, bool isSpaceshipComponent, bool isTypeOnly)
        {
            Name = name;
            Kind = kind;
            PrefabId = prefabId;
            PrefabIdText = prefabIdText;
            Component = component;
            SourceType = sourceType;
            AmountText = amountText;
            IsSpaceshipComponent = isSpaceshipComponent;
            IsTypeOnly = isTypeOnly;
        }

        public string Name { get; }
        public string Kind { get; }
        public ulong PrefabId { get; }
        public string PrefabIdText { get; }
        public object Component { get; }
        public Type SourceType { get; }
        public string AmountText { get; }
        public bool IsSpaceshipComponent { get; }
        public bool IsTypeOnly { get; }
    }
}

internal static class NetcoreSetAvailableComponentCreatePatch
{
    private static bool _loggedFirstMutation;

    internal static MethodBase? ResolveTarget()
    {
        var netcoreEventType = ModestMenuBehaviour.FindType("NetcoreEvent");
        var setAvailableType = ModestMenuBehaviour.FindType("NetcoreEvent_SetAvailableComponent");
        if (netcoreEventType == null || setAvailableType == null)
            return null;

        return netcoreEventType.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
            ?.MakeGenericMethod(setAvailableType);
    }

    internal static void Prefix(object[] __args)
    {
        if (__args.Length == 0 || __args[0] == null)
            return;

        var data = __args[0];
        var type = data.GetType();
        ModestMenuBehaviour.SetMemberValue(data, type, "_amount", ModestMenuBehaviour.UnlimitedAvailableAmount);
        ModestMenuBehaviour.SetMemberValue(data, type, "_refreshUI", true);
        ModestMenuBehaviour.SetMemberValue(data, type, "_highlightAsNew", false);
        __args[0] = data;

        if (!_loggedFirstMutation)
        {
            _loggedFirstMutation = true;
            Plugin.LogSource.LogInfo("Netcore cap patch: forced outgoing SetAvailableComponent amount to unlimited.");
        }
    }
}

internal static class ComponentAmountPatch
{
    internal static MethodBase? ResolveTarget()
    {
        var candidates = new[]
        {
            "Core+Singleton",
            "Core/Singleton",
            "Core.Singleton",
            "Singleton",
        };

        foreach (var name in candidates)
        {
            var type = ModestMenuBehaviour.FindType(name);
            if (type == null)
                continue;

            var method = AccessTools.Method(type, "GetAvailableComponents");
            if (method != null)
                return method;
        }

        return null;
    }

    internal static void Postfix(ref int __result)
    {
        __result = ModestMenuBehaviour.UnlimitedAvailableAmount;
    }
}

internal static class SpaceshipCreationPatch
{
    private const int LimitOverflowFlag = 8;

    internal static MethodBase? ResolveTarget()
    {
        var type = ModestMenuBehaviour.FindType("SpaceshipSystem");
        if (type == null)
            return null;
        return AccessTools.Method(type, "CanCreateSpaceship");
    }

    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var returnType = (__originalMethod as MethodInfo)?.ReturnType;
        if (returnType == null || !returnType.IsEnum)
            return instructions;

        var clearMethod = AccessTools.Method(typeof(SpaceshipCreationPatch), nameof(ClearLimitOverflowFlag));
        if (clearMethod == null)
            return instructions;

        var patched = new List<CodeInstruction>();
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ret)
            {
                patched.Add(new CodeInstruction(OpCodes.Box, returnType));
                patched.Add(new CodeInstruction(OpCodes.Call, clearMethod));
                patched.Add(new CodeInstruction(OpCodes.Unbox_Any, returnType));
            }
            patched.Add(instruction);
        }

        return patched;
    }

    internal static object? ClearLimitOverflowFlag(object? value)
    {
        try
        {
            if (value == null)
                return value;

            var enumType = value.GetType();
            if (!enumType.IsEnum)
                return value;

            var rawValue = Convert.ToInt32(value);
            rawValue &= ~LimitOverflowFlag;
            return Enum.ToObject(enumType, rawValue);
        }
        catch
        {
            return value;
        }
    }
}

internal static class CoreAvailabilityRefreshPatch
{
    internal static IEnumerable<MethodBase> ResolveTargets()
    {
        var coreType = ModestMenuBehaviour.FindType("Core");
        if (coreType == null)
            yield break;

        var shared = AccessTools.Method(coreType, "RefreshSharedAvailableComponents");
        if (shared != null)
            yield return shared;

        var privateMap = AccessTools.Method(coreType, "RefreshPrivateAvailableComponents");
        if (privateMap != null)
            yield return privateMap;
    }

    internal static void Prefix(object __instance)
    {
        ModestMenuBehaviour.SetCoreComponentAmountsUnlimited(__instance);
    }

    internal static void Postfix(object __instance)
    {
        ModestMenuBehaviour.SetCoreComponentAmountsUnlimited(__instance);
        ModestMenuBehaviour.TryBroadcastUnlimitedAvailability(__instance);
    }
}

internal static class HandAvailabilityColorPatch
{
    internal static IEnumerable<MethodBase> ResolveTargets()
    {
        var type = ModestMenuBehaviour.FindType("UIHandComponentItem");
        if (type == null)
            yield break;

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var names = new[] { "UpdateRemainingText", "SetUIData", "SetItem", "LateUpdate" };

        foreach (var name in names)
        {
            var method = type.GetMethod(name, flags);
            if (method != null)
                yield return method;
        }
    }

    internal static void Postfix(object __instance)
    {
        try
        {
            var itemType = __instance.GetType();
            var handList = ModestMenuBehaviour.GetMemberValue(__instance, itemType, "_handComponentsList");
            var remainingText = ModestMenuBehaviour.GetMemberValue(__instance, itemType, "_remainingText");
            if (handList == null || remainingText == null)
                return;

            var goodColor = ModestMenuBehaviour.GetMemberValue(handList, handList.GetType(), "_amountTextColorGood");
            if (goodColor == null)
                return;

            ModestMenuBehaviour.SetMemberValue(remainingText, remainingText.GetType(), "color", goodColor);
        }
        catch
        {
        }
    }
}
