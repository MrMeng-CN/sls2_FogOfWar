#nullable enable

using Godot;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace FogOfWar;

public static class MapVisibilityController
{
	private const string ConfigFileName = "FogOfWarMapVision.json";

	private static readonly BindingFlags AnyInstanceField =
		BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	private static readonly BindingFlags AnyInstanceMember =
		BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

	// 已知：社区示例已验证 NMapScreen 内部存在 _paths
	private static FieldInfo? _pathsField;

	// 房间节点字段名未稳定公开，采用“候选名 + 自动发现”
	private static FieldInfo? _roomEntriesField;

	private static readonly string[] RoomFieldCandidates =
	[
		"_points",
		"_mapPoints",
		"_pointNodes",
		"_pointViews",
		"_nodes",
		"_rooms",
		"_roomNodes",
		"_icons",
		"_pointsByCoord",
        "_nodeViews"
	];

	private static readonly string[] WrapperMemberCandidates =
	[
		"Node",
		"Root",
		"Control",
		"Icon",
		"Button",
		"TextureRect",
        "View"
	];

	private static readonly string[] CoordFloorMemberCandidates =
	[
		"Floor",
		"floor",
		"Y",
		"y",
		"Row",
		"row",
		"Level",
		"level",
		"Index",
        "index"
	];

	private static readonly Dictionary<CanvasItem, bool> OriginalVisibility = new();

	private static RunState? _runState;
	private static MapVisionConfig _config = new();

	private static string? _configPath;
	private static DateTime _configLastWriteUtc = DateTime.MinValue;

	private static bool _initialized;
	private static bool _pendingApply;
	private static bool _retryQueued;

	private static NMapScreen? _subscribedMapScreen;

	public static void Initialize()
	{
		if (_initialized)
			return;

		_initialized = true;

		LoadOrCreateConfig();
		InitializeReflection();

		var manager = RunManager.Instance;
		if (manager == null)
		{
			LogMsg("RunManager.Instance is null during Initialize()");
			return;
		}

		manager.RunStarted += OnRunStarted;
		manager.ActEntered += OnActEntered;
		manager.RoomEntered += OnRoomEntered;
		manager.RoomExited += OnRoomExited;

		LogMsg($"Initialized. visible_ahead_floors={_config.VisibleAheadFloors}, hide_lower_floors={_config.HideLowerFloors}");
		RequestApplyOnMapOpen();
	}

	public static void SetVisibleAheadFloors(int floors)
	{
		_config.VisibleAheadFloors = floors;
		SaveConfig();
		RequestApplyOnMapOpen();
	}

	public static void ShowAllFloors()
	{
		SetVisibleAheadFloors(-1);
	}

	public static void ReloadConfig()
	{
		LoadOrCreateConfig(forceReload: true);
		RequestApplyOnMapOpen();
	}

	private static void OnRunStarted(RunState runState)
	{
		_runState = runState;

		_pendingApply = false;
		_retryQueued = false;
		UnsubscribeFromMapOpened();
		RestoreVisibility();

		LogMsg("Run started");
		RequestApplyOnMapOpen();
	}

	private static void OnActEntered()
	{
		LogMsg("Act entered");
		RequestApplyOnMapOpen();
	}

	private static void OnRoomEntered()
	{
		LogMsg("Room entered");
		RequestApplyOnMapOpen();
	}

	private static void OnRoomExited()
	{
		LogMsg("Room exited");
		RequestApplyOnMapOpen();
	}

	private static void RequestApplyOnMapOpen()
	{
		MaybeReloadConfig();

		var mapScreen = NMapScreen.Instance;

		if (mapScreen != null && mapScreen.IsOpen)
		{
			_pendingApply = false;
			UnsubscribeFromMapOpened();
			ApplyVisibilityNow();
			return;
		}

		_pendingApply = true;

		if (mapScreen != null)
		{
			SubscribeToMapOpened(mapScreen);
			return;
		}

		QueueRetryNextFrame();
	}

	private static void OnMapScreenOpened()
	{
		if (!_pendingApply)
			return;

		_pendingApply = false;
		UnsubscribeFromMapOpened();
		ApplyVisibilityNow();
	}

	private static void SubscribeToMapOpened(NMapScreen mapScreen)
	{
		if (ReferenceEquals(_subscribedMapScreen, mapScreen))
			return;

		UnsubscribeFromMapOpened();

		mapScreen.Opened += OnMapScreenOpened;
		_subscribedMapScreen = mapScreen;

		LogMsg("Subscribed to mapScreen.Opened");
	}

	private static void UnsubscribeFromMapOpened()
	{
		if (_subscribedMapScreen == null)
			return;

		_subscribedMapScreen.Opened -= OnMapScreenOpened;
		_subscribedMapScreen = null;
	}

	private static void QueueRetryNextFrame()
	{
		if (_retryQueued)
			return;

		if (Engine.GetMainLoop() is not SceneTree tree)
		{
			LogMsg("Cannot queue retry: main loop is not SceneTree");
			return;
		}

		_retryQueued = true;

		void Retry()
		{
			tree.ProcessFrame -= Retry;
			_retryQueued = false;

			if (_pendingApply)
				RequestApplyOnMapOpen();
		}

		tree.ProcessFrame += Retry;
		LogMsg("Map screen not ready; retrying next frame");
	}

	private static void ApplyVisibilityNow()
	{
		MaybeReloadConfig();
		RestoreVisibility();

		var mapScreen = NMapScreen.Instance;
		if (mapScreen == null)
		{
			LogMsg("Apply skipped: map screen is null");
			return;
		}

		if (!mapScreen.IsOpen)
		{
			LogMsg("Apply skipped: map screen is not open");
			return;
		}

		int currentFloor = GetCurrentFloor();
		int maxVisibleFloor = _config.VisibleAheadFloors < 0
			? int.MaxValue
			: currentFloor + _config.VisibleAheadFloors;

		LogMsg($"Applying visibility. current_floor={currentFloor}, max_visible_floor={(maxVisibleFloor == int.MaxValue ? "ALL" : maxVisibleFloor)}");

		ApplyRoomVisibility(mapScreen, currentFloor, maxVisibleFloor);
		ApplyPathVisibility(mapScreen, currentFloor, maxVisibleFloor);
	}

	private static void ApplyRoomVisibility(NMapScreen mapScreen, int currentFloor, int maxVisibleFloor)
	{
		IDictionary? roomDict = TryGetRoomDictionary(mapScreen);
		if (roomDict == null)
		{
			LogMsg("Room dictionary not found. Paths may still work, but rooms won't hide until a matching field is discovered.");
			DumpCandidateFields(mapScreen);
			return;
		}

		int affected = 0;

		foreach (DictionaryEntry entry in roomDict)
		{
			if (!TryGetCoordFloor(entry.Key, out int roomFloor))
				continue;

			bool visible = IsFloorVisible(roomFloor, currentFloor, maxVisibleFloor);
			ApplyVisibilityToValue(entry.Value, visible);
			affected++;
		}

		LogMsg($"Room visibility applied to {affected} room entries");
	}

	private static void ApplyPathVisibility(NMapScreen mapScreen, int currentFloor, int maxVisibleFloor)
	{
		if (_pathsField == null)
		{
			LogMsg("Path field (_paths) not found; cannot hide future path ticks.");
			return;
		}

		if (_pathsField.GetValue(mapScreen) is not IDictionary pathDict)
		{
			LogMsg("_paths field exists but did not yield a dictionary");
			return;
		}

		int affected = 0;

		foreach (DictionaryEntry entry in pathDict)
		{
			if (!TryGetSegmentFloors(entry.Key, out int floorA, out int floorB))
				continue;

			bool visible = IsFloorVisible(floorA, currentFloor, maxVisibleFloor)
						&& IsFloorVisible(floorB, currentFloor, maxVisibleFloor);

			ApplyVisibilityToValue(entry.Value, visible);
			affected++;
		}

		LogMsg($"Path visibility applied to {affected} path entries");
	}

	private static bool IsFloorVisible(int targetFloor, int currentFloor, int maxVisibleFloor)
	{
		if (_config.HideLowerFloors && targetFloor < currentFloor)
			return false;

		return targetFloor <= maxVisibleFloor;
	}

	private static int GetCurrentFloor()
	{
		var point = _runState?.CurrentMapPoint ?? _runState?.Map?.StartingMapPoint;
		if (point != null)
		{
			if (TryGetCoordFloor(point.coord, out int floorFromCoord))
				return floorFromCoord;
		}

		if (_runState != null)
			return Math.Max(0, _runState.ActFloor);

		return 0;
	}

	private static void InitializeReflection()
	{
		try
		{
			var mapScreenType = typeof(NMapScreen);
			_pathsField = mapScreenType.GetField("_paths", AnyInstanceField);

			if (_pathsField != null)
				LogMsg("Reflection initialized: found _paths");
			else
				LogMsg("Reflection warning: _paths not found");
		}
		catch (Exception ex)
		{
			LogMsg($"Reflection init failed: {ex.Message}");
		}
	}

	private static IDictionary? TryGetRoomDictionary(NMapScreen mapScreen)
	{
		if (_roomEntriesField != null)
			return _roomEntriesField.GetValue(mapScreen) as IDictionary;

		var type = mapScreen.GetType();

		foreach (var name in RoomFieldCandidates)
		{
			var field = type.GetField(name, AnyInstanceField);
			if (field == null)
				continue;

			if (field.GetValue(mapScreen) is IDictionary dict && LooksLikeRoomDictionary(dict))
			{
				_roomEntriesField = field;
				LogMsg($"Room dictionary found by candidate name: {field.Name}");
				return dict;
			}
		}

		foreach (var field in type.GetFields(AnyInstanceField))
		{
			if (field.Name == "_paths")
				continue;

			if (field.GetValue(mapScreen) is not IDictionary dict)
				continue;

			if (LooksLikeRoomDictionary(dict))
			{
				_roomEntriesField = field;
				LogMsg($"Room dictionary auto-discovered: {field.Name}");
				return dict;
			}
		}

		return null;
	}

	private static bool LooksLikeRoomDictionary(IDictionary dict)
	{
		foreach (DictionaryEntry entry in dict)
		{
			if (entry.Key is MapCoord && ValueLooksLikeUi(entry.Value))
				return true;
		}

		return false;
	}

	private static bool ValueLooksLikeUi(object? value)
	{
		if (value == null)
			return false;

		if (value is CanvasItem)
			return true;

		if (value is IEnumerable enumerable && value is not string)
		{
			foreach (var item in enumerable)
			{
				if (ValueLooksLikeUi(item))
					return true;
			}
		}

		foreach (var name in WrapperMemberCandidates)
		{
			if (TryGetMemberValue(value, name, out var inner) && inner != null)
			{
				if (inner is CanvasItem)
					return true;
			}
		}

		return false;
	}

	private static void ApplyVisibilityToValue(object? value, bool visible)
	{
		if (value == null)
			return;

		if (value is CanvasItem canvasItem)
		{
			SetVisible(canvasItem, visible);
			return;
		}

		if (value is IDictionary dict)
		{
			foreach (DictionaryEntry entry in dict)
				ApplyVisibilityToValue(entry.Value, visible);

			return;
		}

		if (value is IEnumerable enumerable && value is not string)
		{
			foreach (var item in enumerable)
				ApplyVisibilityToValue(item, visible);

			return;
		}

		foreach (var name in WrapperMemberCandidates)
		{
			if (TryGetMemberValue(value, name, out var inner))
				ApplyVisibilityToValue(inner, visible);
		}
	}

	private static void SetVisible(CanvasItem item, bool visible)
	{
		if (!GodotObject.IsInstanceValid(item))
			return;

		if (!OriginalVisibility.ContainsKey(item))
			OriginalVisibility[item] = item.Visible;

		item.Visible = visible;
	}

	private static void RestoreVisibility()
	{
		if (OriginalVisibility.Count == 0)
			return;

		var snapshot = new List<KeyValuePair<CanvasItem, bool>>(OriginalVisibility);
		OriginalVisibility.Clear();

		foreach (var kvp in snapshot)
		{
			var item = kvp.Key;
			if (!GodotObject.IsInstanceValid(item))
				continue;

			item.Visible = kvp.Value;
		}
	}

	private static bool TryGetSegmentFloors(object? key, out int floorA, out int floorB)
	{
		floorA = 0;
		floorB = 0;

		if (key == null)
			return false;

		if (key is ITuple tuple && tuple.Length == 2)
		{
			return TryGetCoordFloor(tuple[0], out floorA)
				&& TryGetCoordFloor(tuple[1], out floorB);
		}

		if (TryGetMemberValue(key, "Item1", out var item1)
			&& TryGetMemberValue(key, "Item2", out var item2))
		{
			return TryGetCoordFloor(item1, out floorA)
				&& TryGetCoordFloor(item2, out floorB);
		}

		return false;
	}

	private static bool TryGetCoordFloor(object? coordLike, out int floor)
	{
		floor = 0;

		if (coordLike == null)
			return false;

		var type = coordLike.GetType();

		foreach (var name in CoordFloorMemberCandidates)
		{
			var prop = type.GetProperty(name, AnyInstanceMember);
			if (prop != null && prop.PropertyType == typeof(int))
			{
				floor = (int)prop.GetValue(coordLike)!;
				return true;
			}

			var field = type.GetField(name, AnyInstanceMember);
			if (field != null && field.FieldType == typeof(int))
			{
				floor = (int)field.GetValue(coordLike)!;
				return true;
			}
		}

		string s = coordLike.ToString() ?? string.Empty;
		var matches = Regex.Matches(s, @"-?\d+");
		if (matches.Count > 0 && int.TryParse(matches[^1].Value, out floor))
			return true;

		return false;
	}

	private static bool TryGetMemberValue(object obj, string memberName, out object? value)
	{
		value = null;

		var type = obj.GetType();

		var prop = type.GetProperty(memberName, AnyInstanceMember);
		if (prop != null)
		{
			value = prop.GetValue(obj);
			return true;
		}

		var field = type.GetField(memberName, AnyInstanceMember);
		if (field != null)
		{
			value = field.GetValue(obj);
			return true;
		}

		return false;
	}

	private static void DumpCandidateFields(NMapScreen mapScreen)
	{
		try
		{
			var fields = mapScreen.GetType().GetFields(AnyInstanceField);
			foreach (var field in fields)
			{
				object? value = null;

				try
				{
					value = field.GetValue(mapScreen);
				}
				catch
				{
					// ignore
				}

				string typeName = value?.GetType().FullName ?? field.FieldType.FullName ?? "unknown";
				LogMsg($"Field candidate: {field.Name} => {typeName}");
			}
		}
		catch (Exception ex)
		{
			LogMsg($"DumpCandidateFields failed: {ex.Message}");
		}
	}

	private static void MaybeReloadConfig()
	{
		if (string.IsNullOrWhiteSpace(_configPath) || !File.Exists(_configPath))
			return;

		DateTime lastWriteUtc = File.GetLastWriteTimeUtc(_configPath);
		if (lastWriteUtc <= _configLastWriteUtc)
			return;

		LoadOrCreateConfig(forceReload: true);
		LogMsg("Config hot-reloaded from disk");
	}

	private static void LoadOrCreateConfig(bool forceReload = false)
	{
		_configPath ??= ResolveConfigPath();

		var dir = Path.GetDirectoryName(_configPath);
		if (!string.IsNullOrWhiteSpace(dir))
			Directory.CreateDirectory(dir);

		if (!File.Exists(_configPath))
		{
			_config = new MapVisionConfig();
			SaveConfig();
			LogMsg($"Config created at {_configPath}");
			return;
		}

		if (!forceReload && _configLastWriteUtc != DateTime.MinValue)
			return;

		try
		{
			string json = File.ReadAllText(_configPath);
			_config = JsonSerializer.Deserialize<MapVisionConfig>(json) ?? new MapVisionConfig();
			_configLastWriteUtc = File.GetLastWriteTimeUtc(_configPath);

			LogMsg($"Config loaded from {_configPath}");
		}
		catch (Exception ex)
		{
			_config = new MapVisionConfig();
			LogMsg($"Config load failed, using defaults: {ex.Message}");
		}
	}

	private static void SaveConfig()
	{
		_configPath ??= ResolveConfigPath();

		var options = new JsonSerializerOptions
		{
			WriteIndented = true
		};

		File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, options));
		_configLastWriteUtc = File.GetLastWriteTimeUtc(_configPath);
	}

	private static string ResolveConfigPath()
	{
		string? assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		if (!string.IsNullOrWhiteSpace(assemblyDir))
			return Path.Combine(assemblyDir, ConfigFileName);

		string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";
		return Path.Combine(exeDir, "mods", "FogOfWar", ConfigFileName);
	}

	private static void LogMsg(string message)
	{
		Log.Warn($"[FogOfWar/MapVision] {message}");
	}

	private sealed class MapVisionConfig
	{
		[JsonPropertyName("visible_ahead_floors")]
		public int VisibleAheadFloors { get; set; } = 3;

		[JsonPropertyName("hide_lower_floors")]
		public bool HideLowerFloors { get; set; } = false;
	}
}
