using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;

using Object = UnityEngine.Object;

namespace SupermarketMorePlayers;

[BepInPlugin("dev.codex.supermarket.moreplayers", "Supermarket More Players", "0.1.0")]
public sealed class Plugin : BasePlugin
{
	private readonly struct MethodCallResult
	{
		public bool Success { get; }

		public object Value { get; }

		public MethodCallResult(bool success, object value)
		{
			Success = success;
			Value = value;
		}
	}

	public const string PluginGuid = "dev.codex.supermarket.moreplayers";

	public const string PluginName = "Supermarket More Players";

	public const string PluginVersion = "0.1.0";

	private static readonly string[] PhotonRoomMethodNames = new string[3] { "CreateRoom", "JoinOrCreateRoom", "JoinRandomOrCreateRoom" };

	private static readonly string[] LobbyManagerMethodNames = new string[5] { "CreateRoom", "JoinTargetRoom", "ExternalJoinTargetRoom", "ConnectToServer", "OnConnectedToMaster" };

	private static ManualLogSource s_log;

	private static ConfigEntry<int> s_maxPlayers;

	private static ConfigEntry<bool> s_patchSteamLobby;

	private static ConfigEntry<bool> s_shrinkOnRoomClose;

	private static ConfigEntry<bool> s_verboseLogging;

	private static ConfigEntry<bool> s_patchPlayerPool;

	private static ConfigEntry<bool> s_bypassLoadingGate;

	private static ConfigEntry<bool> s_selfTestRealloc;

	private static ConfigEntry<int> s_forceMinPlayerInstances;

	private static ConfigEntry<bool> s_forceSyncTargetZero;

	private static readonly List<PlayerInstance> s_templateSnapshot = new List<PlayerInstance>();

	private Harmony _harmony;

	private static bool s_selfTestRan;

	private static int MaxPlayers => Mathf.Clamp(s_maxPlayers?.Value ?? 8, 1, 255);

	internal static bool ShrinkOnRoomClose => s_shrinkOnRoomClose?.Value ?? true;

	internal static bool VerboseLogging => s_verboseLogging?.Value ?? true;

	internal static bool PatchPlayerPool => s_patchPlayerPool?.Value ?? true;

	internal static bool BypassLoadingGate => s_bypassLoadingGate?.Value ?? true;

	internal static bool SelfTestRealloc => s_selfTestRealloc?.Value ?? false;

	internal static int ForceMinPlayerInstances => Mathf.Clamp(s_forceMinPlayerInstances?.Value ?? 0, 0, 255);

	internal static bool ForceSyncTargetZero => s_forceSyncTargetZero?.Value ?? false;

	internal static ManualLogSource Logger => s_log;

	public override void Load()
	{
		//IL_0131: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Expected O, but got Unknown
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Expected O, but got Unknown
		s_log = ((BasePlugin)this).Log;
		s_maxPlayers = ((BasePlugin)this).Config.Bind<int>("General", "MaxPlayers", 8, "Target Photon room and Steam lobby capacity. Photon supports 1-255 here.");
		s_patchSteamLobby = ((BasePlugin)this).Config.Bind<bool>("General", "PatchSteamLobby", true, "Also patches Facepunch/Steamworks lobby creation limits when present.");
		s_shrinkOnRoomClose = ((BasePlugin)this).Config.Bind<bool>("General", "ShrinkPhotonRoomWhenClosed", true, "When the host closes the room, reduce Photon MaxPlayers to the actual connected player count.");
		s_verboseLogging = ((BasePlugin)this).Config.Bind<bool>("Diagnostics", "VerboseLogging", true, "Log room creation arguments and current Photon room state.");
		s_patchPlayerPool = ((BasePlugin)this).Config.Bind<bool>("General", "PatchPlayerPool", true, "When the game runs out of player bodies (scene only has 4), clone an existing player body at runtime so the 5th+ player can spawn. REQUIRED for more than 4 players.");
		s_bypassLoadingGate = ((BasePlugin)this).Config.Bind<bool>("General", "BypassLoadingGate", true, "The game's multiplayer loading waits for SyncedSystemCount to reach its target, which never completes past 4 players. With this enabled, the loading screen is allowed to finish. REQUIRED for more than 4 players.");
		s_selfTestRealloc = ((BasePlugin)this).Config.Bind<bool>("Diagnostics", "SelfTestPhotonRealloc", false, "When true, the host (any room, even solo) clones one player body and runs the exact PhotonView re-allocation used for the 5th player, then destroys the clone. Used to verify the Photon call works WITHOUT needing friends. Set false after testing.");
		s_forceMinPlayerInstances = ((BasePlugin)this).Config.Bind<int>("Diagnostics", "ForceMinPlayerInstances", 0, "For solo testing: force the host to grow the player body pool to at least this many instances, even if fewer players are in the room. 0 disables. Set to 0 before real multiplayer tests.");
		s_forceSyncTargetZero = ((BasePlugin)this).Config.Bind<bool>("Diagnostics", "ForceSyncTargetToZero", false, "When hosting, force NetworkManager.m_TargetSyncSystemCount to 0 so the game's sync-gated logic (control enabling, loading) completes without waiting for the never-advancing SyncedSystemCount. Helps solo testing and 5+ player sessions.");
		_harmony = new Harmony("dev.codex.supermarket.moreplayers");
		PatchKnownTargets(_harmony);
		ClassInjector.RegisterTypeInIl2Cpp<RoomCapacityMonitor>();
		((BasePlugin)this).AddComponent<RoomCapacityMonitor>();
		ManualLogSource log = ((BasePlugin)this).Log;
		log.LogInfo($"Supermarket More Players 0.1.0 loaded. MaxPlayers={MaxPlayers}");

		// Headless auto-test: when launched with -autotest, bypass the Steam/lobby UI,
		// connect to Photon directly, create a room, run the realloc self-test, and exit.
		var args = Environment.GetCommandLineArgs();
		if (Array.IndexOf(args, "-autotest") >= 0)
		{
			log.LogInfo("AUTOTEST: command-line mode detected (-autotest).");
			s_autoTestActive = true;
			((BasePlugin)this).AddComponent<AutoTestDriver>();
		}
	}

	private static bool s_autoTestActive;

	// Set when the realloc self-test has logged its final DONE line (used by AutoTestDriver).
	internal static bool s_autoTestComplete;

	// Drives a fully headless connect -> create-room -> selftest -> quit sequence.
	private sealed class AutoTestDriver : MonoBehaviour
	{
		private float _retryAt;
		private int _step;
		private bool _startedPhoton;

		public AutoTestDriver(IntPtr pointer)
			: base(pointer)
		{
		}

		private void Update()
		{
			try
			{
				if (Time.unscaledTime < _retryAt)
				{
					return;
				}

				_retryAt = Time.unscaledTime + 1f;

				if (_step == 0)
				{
					if (PhotonNetwork.IsConnectedAndReady)
					{
						_step = 1;
					}
					else if (!_startedPhoton)
					{
						PhotonNetwork.ConnectUsingSettings();
						_startedPhoton = true;
						Plugin.Logger.LogInfo("AUTOTEST: connecting to Photon...");
					}

					return;
				}

				if (_step == 1)
				{
					if (!PhotonNetwork.InRoom)
					{
						Plugin.Logger.LogInfo("AUTOTEST: creating auto-test room...");
						PhotonNetwork.CreateRoom("autotest_" + Guid.NewGuid().ToString("N"));
					}
					else
					{
						_step = 2;
					}

					return;
				}

				if (_step == 2)
				{
					// After the room is open and PlayerInstances populate, run the self-test.
					if (PlayerManager.HasInstance && PlayerManager.Instance != null && PlayerManager.Instance.PlayerInstances != null && PlayerManager.Instance.PlayerInstances.Count > 0)
					{
						if (!Plugin.s_autoTestComplete)
						{
							Plugin.RunSelfTestRealloc();
						}
						else
						{
							_step = 3;
						}
					}

					// If guards blocked it (room not open yet / empty pool), retry next tick.
					return;
				}

				if (_step == 3)
				{
					// Grow the pool to the forced minimum (6) and verify every body has a
					// UNIQUE PhotonView ID. That's the headless "players 5 and 6" check.
					Plugin.EnsurePlayerBodyCapacity();
					int total = 0;
					int nonNull = 0;
					int unique = 0;
					try
					{
						var pool = PlayerManager.Instance.PlayerInstances;
						if (pool != null)
						{
							total = pool.Count;
							var seen = new System.Collections.Generic.List<int>();
							int idx = 0;
							foreach (var pi in pool)
							{
								idx++;
								if ((Object)(object)pi == (Object)null)
								{
									Plugin.Logger.LogWarning($"AUTOTEST6: body {idx}/{total} is null.");
									continue;
								}
								nonNull++;
								var view = Plugin.GetMemberSafe(pi, "PhotonView");
								int id = 0;
								if (view == null)
								{
									Plugin.Logger.LogWarning($"AUTOTEST6: body {idx}/{total} has NO PhotonView member.");
								}
								else
								{
									id = Convert.ToInt32(Plugin.GetMemberSafe(view, "ViewID") ?? 0);
									Plugin.Logger.LogInfo($"AUTOTEST6: body {idx}/{total} ViewID = " + id + ".");
								}
								if (id != 0 && !seen.Contains(id))
								{
									seen.Add(id);
									unique++;
								}
								else if (id == 0)
								{
									Plugin.Logger.LogWarning("AUTOTEST6: body has ViewID 0 (unconfigured).");
								}
								else
								{
									Plugin.Logger.LogWarning("AUTOTEST6: duplicate ViewID " + id + " found!");
								}
							}
						}
					}
					catch (Exception verr)
					{
						Plugin.Logger.LogWarning("AUTOTEST6 verification error: " + verr.Message);
					}

					bool pass = total >= 6 && unique == nonNull;
					Plugin.Logger.LogInfo($"AUTOTEST6: {nonNull}/{total} non-null bodies, {unique} unique non-zero ViewIDs - {(pass ? "PASS" : "FAIL")}.");
					_step = 4;
					return;
				}

				if (_step == 4)
				{
					Plugin.Logger.LogInfo("AUTOTEST: complete. Exiting.");
					Application.Quit();
				}
			}
			catch (Exception ex)
			{
				Plugin.Logger.LogWarning("AUTOTEST driver failed: " + ex.Message);
				Application.Quit(1);
			}
		}
	}

	private static void PatchKnownTargets(Harmony harmony)
	{
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Expected O, but got Unknown
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Expected O, but got Unknown
		Type type = AccessTools.TypeByName("Photon.Pun.PhotonNetwork");
		if (type == null)
		{
			LogWarning("Photon.Pun.PhotonNetwork was not found. Photon room cap patches were not installed.");
		}
		else
		{
			HarmonyMethod prefix = new HarmonyMethod(AccessTools.Method(typeof(Plugin), "PhotonRoomCreatePrefix", (Type[])null, (Type[])null));
			int value = PatchNamedMethods(harmony, type, (System.Collections.Generic.IReadOnlyCollection<string>)(object)PhotonRoomMethodNames, prefix, null);
			LogInfo($"Patched {value} PhotonNetwork room creation method(s).");
		}
		Type type2 = AccessTools.TypeByName("LobbyManager") ?? AccessTools.TypeByName("__Project__.Scripts.Multiplayer.LobbyManager");
		if (type2 == null)
		{
			LogWarning("LobbyManager was not found. Game field patch was not installed.");
		}
		else
		{
			HarmonyMethod prefix2 = new HarmonyMethod(AccessTools.Method(typeof(Plugin), "LobbyManagerPrefix", (Type[])null, (Type[])null));
			int value2 = PatchNamedMethods(harmony, type2, (System.Collections.Generic.IReadOnlyCollection<string>)(object)LobbyManagerMethodNames, prefix2, null);
			LogInfo($"Patched {value2} LobbyManager method(s).");
		}
		ConfigEntry<bool> obj = s_patchSteamLobby;
		if (obj != null && obj.Value)
		{
			PatchSteamLobbyTargets(harmony);
		}
		PatchPlayerManagerSkinTargets(harmony);
		PatchDiagnosticsTargets(harmony);
		PatchLoadingGate(harmony);
		PatchCameraSteal(harmony);
	}

	private static void PatchCameraSteal(Harmony harmony)
	{
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Expected O, but got Unknown
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Expected O, but got Unknown
		Type type = AccessTools.TypeByName("PlayerInstance");
		if (type == null)
		{
			LogWarning("PatchCameraSteal: PlayerInstance not found.");
			return;
		}
		MethodInfo methodInfo = AccessTools.Method(type, "ConfigureRemotePlayer", (Type[])null, (Type[])null);
		if (methodInfo == null)
		{
			LogWarning("PatchCameraSteal: PlayerInstance.ConfigureRemotePlayer not found.");
			return;
		}
		harmony.Patch((MethodBase)methodInfo, (HarmonyMethod)null, new HarmonyMethod(AccessTools.Method(typeof(Plugin), "ConfigureRemotePlayerPostfix", (Type[])null, (Type[])null)), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
		string[] array = new string[3] { "ConfigureLocalPlayer", "SetupNewPlayer", "ConfigureInstanceVisuals" };
		foreach (string text in array)
		{
			MethodInfo methodInfo2 = AccessTools.Method(type, text, (Type[])null, (Type[])null);
			if (methodInfo2 != null)
			{
				harmony.Patch((MethodBase)methodInfo2, (HarmonyMethod)null, new HarmonyMethod(AccessTools.Method(typeof(Plugin), "PlayerConfigPostfix", (Type[])null, (Type[])null)), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			}
		}
		LogInfo("Patched PlayerInstance.ConfigureRemotePlayer (camera-steal guard).");
	}

	private static void PlayerConfigPostfix(object __instance, MethodBase __originalMethod)
	{
		try
		{
			if (__instance != null)
			{
				PlayerInstance val = CastTo<PlayerInstance>(__instance);
				object obj = (((Object)(object)val != (Object)null) ? GetMemberSafe(val, "PhotonView") : null);
				int value = ((obj != null) ? Convert.ToInt32(GetMemberSafe(obj, "ViewID")) : (-1));
				string value2 = (((Object)(object)val != (Object)null && (bool)(GetMemberSafe(val, "IsLocalPlayerInstance") ?? ((object)false))) ? "LOCAL" : "remote");
				LogInfo($"PlayerConfig: {__originalMethod.Name} on {((val != null) ? ((Object)val).name : null)} [{value2}] viewId={value}.");
			}
		}
		catch (Exception ex)
		{
			LogDebug("PlayerConfigPostfix failed: " + ex.Message);
		}
	}

	private static void ConfigureRemotePlayerPostfix(object __instance)
	{
		try
		{
			if (__instance == null)
			{
				return;
			}
			PlayerInstance val = CastTo<PlayerInstance>(__instance);
			if (!((Object)(object)val == (Object)null))
			{
				object memberSafe = GetMemberSafe(val, "m_FPSVCamObject");
				Component val2 = (Component)((memberSafe is Component) ? memberSafe : null);
				if (val2 != null && (Object)(object)val2 != (Object)null)
				{
					val2.gameObject.SetActive(false);
					LogInfo("ConfigureRemotePlayerPostfix: hid FPS camera on remote player " + ((Object)val).name + ".");
				}
			}
		}
		catch (Exception ex)
		{
			LogDebug("ConfigureRemotePlayerPostfix failed: " + ex.Message);
		}
	}

	private static void PatchLoadingGate(Harmony harmony)
	{
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Expected O, but got Unknown
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Expected O, but got Unknown
		if (!BypassLoadingGate)
		{
			LogInfo("BypassLoadingGate is disabled; loading-gate patches were not installed.");
			return;
		}
		Type type = AccessTools.TypeByName("NetworkManager");
		if (type == null)
		{
			LogWarning("PatchLoadingGate: NetworkManager not found.");
			return;
		}
		MethodInfo methodInfo = AccessTools.Method(type, "_InitialLoadingUIRoutine_b__36_0", (Type[])null, (Type[])null);
		if (methodInfo != null)
		{
			harmony.Patch((MethodBase)methodInfo, (HarmonyMethod)null, new HarmonyMethod(AccessTools.Method(typeof(Plugin), "LoadingGatePostfix", (Type[])null, (Type[])null)), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			LogInfo("Patched _InitialLoadingUIRoutine_b__36_0 with loading-gate bypass.");
		}
		else
		{
			LogWarning("PatchLoadingGate: _InitialLoadingUIRoutine_b__36_0 not found.");
		}
		MethodInfo methodInfo2 = AccessTools.Method(type, "_InitialLoadingWarningRoutine_b__37_0", (Type[])null, (Type[])null);
		if (methodInfo2 != null)
		{
			harmony.Patch((MethodBase)methodInfo2, (HarmonyMethod)null, new HarmonyMethod(AccessTools.Method(typeof(Plugin), "LoadingGatePostfix", (Type[])null, (Type[])null)), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			LogInfo("Patched _InitialLoadingWarningRoutine_b__37_0 with loading-gate bypass.");
		}
		else
		{
			LogWarning("PatchLoadingGate: _InitialLoadingWarningRoutine_b__37_0 not found.");
		}
	}

	private static void LoadingGatePostfix(ref bool __result)
	{
		try
		{
			if (BypassLoadingGate && !__result)
			{
				LogDebug("LoadingGatePostfix: forcing loading gate to complete.");
				__result = true;
			}
		}
		catch (Exception ex)
		{
			LogWarning("LoadingGatePostfix failed: " + ex.Message);
		}
	}

	private static void PatchPlayerManagerSkinTargets(Harmony harmony)
	{
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Expected O, but got Unknown
		Type type = AccessTools.TypeByName("PlayerManager");
		if (type == null)
		{
			LogWarning("PlayerManager was not found. Skin index fallback patch was not installed.");
			return;
		}
		MethodInfo methodInfo = AccessTools.Method(type, "GetAvailableSkinIndex", (Type[])null, (Type[])null);
		if (methodInfo == null)
		{
			LogWarning("PlayerManager.GetAvailableSkinIndex was not found. Skin index fallback patch was not installed.");
			return;
		}
		HarmonyMethod val = new HarmonyMethod(AccessTools.Method(typeof(Plugin), "GetAvailableSkinIndexPostfix", (Type[])null, (Type[])null));
		harmony.Patch((MethodBase)methodInfo, (HarmonyMethod)null, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
		LogInfo("Patched PlayerManager.GetAvailableSkinIndex with fallback.");
	}

	private static void GetAvailableSkinIndexPostfix(ref int __result)
	{
		try
		{
			if (__result < 0)
			{
				if (VerboseLogging)
				{
					LogInfo($"PlayerManager.GetAvailableSkinIndex returned invalid index {__result}; forcing 0 so the player can spawn.");
				}
				__result = 0;
			}
		}
		catch (Exception ex)
		{
			LogWarning("GetAvailableSkinIndex postfix failed: " + ex.Message);
		}
	}

	private static void PatchDiagnosticsTargets(Harmony harmony)
	{
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Expected O, but got Unknown
		//IL_016b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0178: Expected O, but got Unknown
		//IL_011f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0126: Expected O, but got Unknown
		//IL_0141: Unknown result type (might be due to invalid IL or missing references)
		//IL_014e: Expected O, but got Unknown
		Type type = AccessTools.TypeByName("PlayerManager");
		if (type != null)
		{
			MethodInfo methodInfo = AccessTools.Method(type, "GetUnownedPlayer", (Type[])null, (Type[])null);
			if (methodInfo != null)
			{
				harmony.Patch((MethodBase)methodInfo, (HarmonyMethod)null, new HarmonyMethod(AccessTools.Method(typeof(Plugin), "GetUnownedPlayerPostfix", (Type[])null, (Type[])null)), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				LogInfo("Patched PlayerManager.GetUnownedPlayer (diagnostics).");
			}
			else
			{
				LogWarning("PlayerManager.GetUnownedPlayer was not found. Instance-count diagnostics were not installed.");
			}
		}
		Type type2 = AccessTools.TypeByName("NetworkManager");
		if (type2 == null)
		{
			LogWarning("NetworkManager was not found. Network diagnostics were not installed.");
			return;
		}
		int num = 0;
		string[] array = new string[6] { "Start", "OnPlayerEnteredRoom", "OnPlayerLeftRoom", "OnLoadingDone", "SyncYourself", "UpdatePlayerList" };
		foreach (string text in array)
		{
			MethodInfo methodInfo2 = AccessTools.Method(type2, text, (Type[])null, (Type[])null);
			if (methodInfo2 == null)
			{
				LogWarning("NetworkManager." + text + " was not found; skipping diagnostics for it.");
				continue;
			}
			try
			{
				if (PatchPlayerPool)
				{
					HarmonyMethod val = new HarmonyMethod(AccessTools.Method(typeof(Plugin), "PlayerJoinedPrefix", (Type[])null, (Type[])null));
					harmony.Patch((MethodBase)methodInfo2, val, new HarmonyMethod(AccessTools.Method(typeof(Plugin), "NetworkManagerEventPostfix", (Type[])null, (Type[])null)), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				else
				{
					harmony.Patch((MethodBase)methodInfo2, (HarmonyMethod)null, new HarmonyMethod(AccessTools.Method(typeof(Plugin), "NetworkManagerEventPostfix", (Type[])null, (Type[])null)), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				num++;
			}
			catch (Exception ex)
			{
				LogWarning("Could not patch NetworkManager." + text + ": " + ex.Message);
			}
		}
		LogInfo($"Patched {num} NetworkManager method(s) with diagnostics.");
	}

	private static void PlayerJoinedPrefix(object __instance, MethodBase __originalMethod, object[] __args)
	{
		try
		{
			EnsurePlayerBodyCapacity();
		}
		catch (Exception ex)
		{
			LogWarning("PlayerJoinedPrefix failed: " + ex.Message);
		}
	}

	internal static void EnsurePlayerBodyCapacity()
	{
		if (PatchPlayerPool && PhotonNetwork.IsMasterClient)
		{
			int num = CurrentRoomPlayerCount();
			int num2 = Mathf.Max(Mathf.Max(4, num), ForceMinPlayerInstances);
			if (num2 > 4 && (!NoktaSingleton<PlayerManager>.HasInstance || !((Object)(object)NoktaSingleton<PlayerManager>.Instance != (Object)null) || NoktaSingleton<PlayerManager>.Instance.PlayerInstances == null || NoktaSingleton<PlayerManager>.Instance.PlayerInstances.Count < num2))
			{
				GrowPlayerBodies(null, num2);
			}
		}
	}

	internal static void ForceSyncTargetIfNeeded()
	{
		if (!ForceSyncTargetZero || !PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient)
		{
			return;
		}
		try
		{
			if (NoktaSingletonPunCallbacks<NetworkManager>.HasInstance && !((Object)(object)NoktaSingletonPunCallbacks<NetworkManager>.Instance == (Object)null))
			{
				int num = Convert.ToInt32(GetMemberSafe(NoktaSingletonPunCallbacks<NetworkManager>.Instance, "m_TargetSyncSystemCount") ?? ((object)(-1)));
				if (num > 0)
				{
					SetMember(NoktaSingletonPunCallbacks<NetworkManager>.Instance, "m_TargetSyncSystemCount", 0);
					LogInfo($"ForceSyncTargetZero: NetworkManager.m_TargetSyncSystemCount {num} -> 0 (host). The game's sync-gated logic can now complete.");
				}
			}
		}
		catch (Exception ex)
		{
			LogDebug("ForceSyncTargetZero failed: " + ex.Message);
		}
	}

	private static int CurrentRoomPlayerCount()
	{
		try
		{
			Type type = AccessTools.TypeByName("Photon.Pun.PhotonNetwork");
			if (type == null)
			{
				return 0;
			}
			object staticMemberSafe = GetStaticMemberSafe2(type, "CurrentRoom");
			if (staticMemberSafe == null)
			{
				return 0;
			}
			return Convert.ToInt32(GetMemberSafe(staticMemberSafe, "PlayerCount"));
		}
		catch
		{
			return 0;
		}
	}

	private static object GetStaticMemberSafe2(Type type, string name)
	{
		try
		{
			PropertyInfo propertyInfo = AccessTools.Property(type, name);
			if ((object)propertyInfo != null && propertyInfo.CanRead)
			{
				return propertyInfo.GetValue(null);
			}
			return AccessTools.Field(type, name)?.GetValue(null);
		}
		catch
		{
			return null;
		}
	}

	private static void GrowPlayerBodies(object networkManager, int target)
	{
		try
		{
			if (!NoktaSingleton<PlayerManager>.HasInstance)
			{
				LogWarning("GrowPlayerBodies: PlayerManager.Instance is not available yet.");
			}
			else
			{
				List<PlayerInstance> playerInstances = NoktaSingleton<PlayerManager>.Instance.PlayerInstances;
				if (playerInstances == null)
				{
					LogWarning("GrowPlayerBodies: PlayerManager.PlayerInstances is null.");
				}
				else
				{
					LogInfo($"GrowPlayerBodies: PlayerInstances size={playerInstances.Count} before grow (target {target}).");
					for (int i = playerInstances.Count; i < target; i++)
					{
						if (CreateExtraPlayerBody() == null)
						{
							break;
						}
					}
					LogInfo($"GrowPlayerBodies: PlayerInstances at {playerInstances.Count} (target {target}).");
				}
			}
		}
		catch (Exception ex)
		{
			LogWarning("GrowPlayerBodies(instances) failed: " + ex.Message);
		}
		try
		{
			if (!NoktaSingletonPunCallbacks<NetworkManager>.HasInstance)
			{
				LogWarning("GrowPlayerBodies: NetworkManager.Instance is not available yet.");
				return;
			}
			List<TMP_Text> playerList = NoktaSingletonPunCallbacks<NetworkManager>.Instance.m_PlayerList;
			if (playerList == null)
			{
				LogWarning("GrowPlayerBodies: NetworkManager.m_PlayerList is null.");
				return;
			}
			LogInfo($"GrowPlayerBodies: m_PlayerList size={playerList.Count} before grow (target {target}).");
			for (int j = playerList.Count; j < target; j++)
			{
				if (!GrowLabelList(playerList))
				{
					break;
				}
			}
			LogInfo($"GrowPlayerBodies: m_PlayerList at {playerList.Count} (target {target}).");
		}
		catch (Exception ex2)
		{
			LogWarning("GrowPlayerBodies(labels) failed: " + ex2.Message);
		}
	}

	private static object ReadMemberTarget(Type type, string name)
	{
		PropertyInfo propertyInfo = AccessTools.Property(type, name);
		try
		{
			if ((object)propertyInfo != null && propertyInfo.CanRead)
			{
				return propertyInfo.GetValue(null);
			}
		}
		catch (Exception ex)
		{
			LogDebug("ReadMemberTarget(" + name + ") null-target failed: " + ex.Message);
		}
		Object val = Object.FindFirstObjectByType(Il2CppType.From(type));
		if (val != (Object)null)
		{
			LogDebug($"ReadMemberTarget({name}) found live instance {((object)val).GetType().FullName}; invoking property getter on it.");
			try
			{
				if ((object)propertyInfo != null && propertyInfo.CanRead)
				{
					return propertyInfo.GetValue(val);
				}
			}
			catch (Exception ex2)
			{
				LogDebug("ReadMemberTarget(" + name + ") instance get failed: " + ex2.Message);
			}
			MethodInfo methodInfo = AccessTools.Method(type, "get_" + name, (Type[])null, (Type[])null);
			if (methodInfo != null)
			{
				try
				{
					return methodInfo.Invoke(val, null);
				}
				catch (Exception ex3)
				{
					LogDebug("ReadMemberTarget(" + name + ") getter invoke failed: " + ex3.Message);
				}
			}
			return GetMemberSafe(val, name);
		}
		return null;
	}

	private static bool GrowLabelList(List<TMP_Text> labels)
	{
		if (labels.Count <= 0)
		{
			return false;
		}
		TMP_Text val = null;
		for (int i = 0; i < labels.Count; i++)
		{
			val = labels[i];
			if ((Object)(object)val != (Object)null)
			{
				break;
			}
		}
		if ((Object)(object)val == (Object)null)
		{
			LogWarning("GrowLabelList: no non-null TMP_Text template found.");
			return false;
		}
		GameObject gameObject = ((Component)val).gameObject;
		if ((Object)(object)gameObject == (Object)null)
		{
			return false;
		}
		object obj = InvokeStaticInstantiate(gameObject);
		if (obj == null)
		{
			return false;
		}
		TMP_Text val2 = CastTo<TMP_Text>(GetComponentOn(obj, AccessTools.TypeByName("TMPro.TMP_Text")));
		if ((Object)(object)val2 == (Object)null)
		{
			LogWarning("GrowLabelList: cloned GameObject has no TMP_Text component.");
			return false;
		}
		labels.Add(val2);
		return true;
	}

	private static void GetUnownedPlayerPostfix(ref object __result)
	{
		try
		{
			if (__result != null)
			{
				PlayerInstance val = CastTo<PlayerInstance>(__result);
				if ((Object)(object)val != (Object)null && (Object)(object)((Component)val).gameObject != (Object)null && !((Component)val).gameObject.activeSelf)
				{
					((Component)val).gameObject.SetActive(true);
					LogInfo("GetUnownedPlayerPostfix: activated sleeping clone " + ((Object)val).name + " so it can be assigned to a joining player.");
				}
				else if (VerboseLogging)
				{
					LogInfo("PlayerManager.GetUnownedPlayer returned a valid PlayerInstance.");
				}
				return;
			}
			if (!PatchPlayerPool)
			{
				LogWarning("PlayerManager.GetUnownedPlayer returned NULL and the PlayerPool patch is disabled. The scene only has 4 pre-placed player objects; player #5+ cannot spawn.");
				return;
			}
			if (!PhotonNetwork.IsMasterClient)
			{
				LogDebug("PlayerManager.GetUnownedPlayer returned NULL on a non-master client; skipping clone fallback (only the master assigns bodies).");
				return;
			}
			object obj = CreateExtraPlayerBody();
			PlayerInstance val2 = CastTo<PlayerInstance>(obj);
			if ((Object)(object)val2 != (Object)null)
			{
				if ((Object)(object)((Component)val2).gameObject != (Object)null && !((Component)val2).gameObject.activeSelf)
				{
					((Component)val2).gameObject.SetActive(true);
				}
				__result = val2;
				LogInfo("PlayerManager.GetUnownedPlayer returned NULL; cloned an extra player body into the pool so a player >=#5 can spawn.");
			}
			else if (obj != null)
			{
				__result = obj;
				LogInfo("PlayerManager.GetUnownedPlayer returned NULL; cloned extra player body (cast fallback) so a player >=#5 can spawn.");
			}
			else
			{
				LogWarning("PlayerManager.GetUnownedPlayer returned NULL and cloning an extra player body failed. Player #5+ still cannot spawn.");
			}
		}
		catch (Exception ex)
		{
			LogWarning("GetUnownedPlayer postfix failed: " + ex.Message);
		}
	}

	private static object CreateExtraPlayerBody()
	{
		try
		{
			if (!NoktaSingleton<PlayerManager>.HasInstance || (Object)(object)NoktaSingleton<PlayerManager>.Instance == (Object)null)
			{
				LogWarning("CreateExtraPlayerBody: PlayerManager.Instance not available.");
				return null;
			}
			List<PlayerInstance> playerInstances = NoktaSingleton<PlayerManager>.Instance.PlayerInstances;
			if (playerInstances == null)
			{
				LogWarning("CreateExtraPlayerBody: PlayerInstances list is null.");
				return null;
			}
			if (playerInstances.Count <= 0)
			{
				LogWarning("CreateExtraPlayerBody: PlayerInstances list is empty; nothing to clone.");
				return null;
			}
			if (s_templateSnapshot.Count == 0)
			{
				for (int i = 0; i < playerInstances.Count; i++)
				{
					PlayerInstance val = playerInstances[i];
					if ((Object)(object)val != (Object)null)
					{
						s_templateSnapshot.Add(val);
					}
				}
				LogInfo($"CreateExtraPlayerBody: captured {s_templateSnapshot.Count} original body template(s) for cloning.");
			}
			PlayerInstance val2 = null;
			for (int j = 1; j < s_templateSnapshot.Count; j++)
			{
				PlayerInstance val3 = s_templateSnapshot[j];
				if (!((Object)(object)val3 == (Object)null) && !(bool)(GetMemberSafe(val3, "IsLocalPlayerInstance") ?? ((object)false)))
				{
					val2 = val3;
					break;
				}
			}
			if ((Object)(object)val2 == (Object)null)
			{
				for (int k = 1; k < s_templateSnapshot.Count; k++)
				{
					PlayerInstance val4 = s_templateSnapshot[k];
					if ((Object)(object)val4 != (Object)null)
					{
						val2 = val4;
						break;
					}
				}
			}
			if ((Object)(object)val2 == (Object)null)
			{
				for (int l = 0; l < playerInstances.Count; l++)
				{
					PlayerInstance val5 = playerInstances[l];
					if ((Object)(object)val5 != (Object)null)
					{
						val2 = val5;
						break;
					}
				}
			}
			if ((Object)(object)val2 == (Object)null)
			{
				LogWarning("CreateExtraPlayerBody: no non-null PlayerInstance found to use as template.");
				return null;
			}
			GameObject gameObject = ((Component)val2).gameObject;
			if ((Object)(object)gameObject == (Object)null)
			{
				LogWarning("CreateExtraPlayerBody: template PlayerInstance has no GameObject; cannot clone.");
				return null;
			}
			object obj = InvokeStaticInstantiate(gameObject);
			if (obj == null)
			{
				LogWarning("CreateExtraPlayerBody: GameObject.Instantiate returned null.");
				return null;
			}
			PlayerInstance val6 = CastTo<PlayerInstance>(GetComponentOn(obj, AccessTools.TypeByName("PlayerInstance")));
			if ((Object)(object)val6 == (Object)null)
			{
				LogWarning("CreateExtraPlayerBody: cloned GameObject has no PlayerInstance component.");
				return null;
			}
			DisableCloneConflictingComponents(val6);
			MarkCloneUnowned(val6);
			ReidClonePhotonViews(val6);
			if (GetMemberSafe(val6, "PhotonView") == null)
			{
				PhotonView componentInChildren = ((Component)val6).GetComponentInChildren<PhotonView>(true);
				if ((Object)(object)componentInChildren != (Object)null && SetMemberRaw(val6, "PhotonView", componentInChildren))
				{
					LogInfo($"CreateExtraPlayerBody: restored null PhotonView member on {((Object)val6).name} -> ViewID {componentInChildren.ViewID}.");
				}
				else
				{
					LogWarning("CreateExtraPlayerBody: could not restore null PhotonView member on " + ((Object)val6).name + "; assignment for this body may fail.");
				}
			}
			playerInstances.Add(val6);
			if ((Object)(object)((Component)val6).gameObject != (Object)null && ((Component)val6).gameObject.activeSelf)
			{
				((Component)val6).gameObject.SetActive(false);
				LogInfo("CreateExtraPlayerBody: deactivated clone " + ((Object)val6).name + " until assignment.");
			}
			LogInfo($"CreateExtraPlayerBody: cloned PlayerInstance; pool grew from {playerInstances.Count - 1} to {playerInstances.Count}.");
			return val6;
		}
		catch (Exception ex)
		{
			LogWarning("CreateExtraPlayerBody failed: " + ex.Message);
			return null;
		}
	}

	private static void ReidClonePhotonViews(PlayerInstance clone)
	{
		try
		{
			LogInfo($"ReidClonePhotonViews: begin (InRoom={PhotonNetwork.InRoom}).");
Il2CppArrayBase<PhotonView> componentsInChildren = ((Component)clone).GetComponentsInChildren<PhotonView>(true);
					int num = 0;
					PhotonView lastFresh = null;
					foreach (PhotonView item in componentsInChildren)
					{
						if ((Object)(object)item == (Object)null)
						{
							continue;
						}
						try
						{
							int viewID = item.ViewID;
							GameObject viewGo = ((Component)item).gameObject;
							if (viewGo == null)
							{
								continue;
							}

							// KEY FINDING (verified headless): the inherited PhotonView is hardwired
							// to the scene ID it was copied from (24). Destroy it and add a BRAND-NEW
							// PhotonView with a runtime-allocated ID. selftest proved 24 -> 10020 works.
							Object.Destroy((Object)(object)item);

							PhotonView fresh = viewGo.AddComponent<PhotonView>();
							if (fresh != null)
							{
								fresh.ViewID = 0;
								ClearPhotonViewOwner(fresh);
								fresh.OwnerActorNr = 0;
								int freshId = PhotonNetwork.AllocateViewID(false);
								fresh.ViewID = freshId;
								PhotonNetwork.RegisterPhotonView(fresh);
								if (fresh.ViewID != viewID && fresh.ViewID != 0)
								{
									num++;
									lastFresh = fresh;
									LogInfo($"ReidClonePhotonViews: {viewGo.name} ViewID {viewID} -> {fresh.ViewID} (fresh component).");
								}
								else
								{
									LogWarning($"ReidClonePhotonViews: fresh PhotonView on {viewGo.name} did not change from {viewID}.");
								}
							}
						}
						catch (Exception ex)
						{
							LogWarning("ReidClonePhotonViews item failed: " + ex.Message);
						}
					}
					if (num == 0)
					{
						LogWarning("ReidClonePhotonViews: no PhotonViews were re-allocated on the clone.");
					}
					else
					{
						// The PlayerInstance.PhotonView member may still point at the old
						// destroyed/stale component (destroy is deferred). Repoint it at the
						// fresh component so GetUnownedPlayer/assignment uses the live view.
						try
						{
							if ((Object)(object)lastFresh != (Object)null && lastFresh.ViewID != 0)
							{
								SetMemberRaw(clone, "PhotonView", lastFresh);
								LogInfo($"ReidClonePhotonViews: repointed PlayerInstance.PhotonView -> ViewID {lastFresh.ViewID}.");
							}
						}
						catch (Exception ex3)
						{
							LogWarning("ReidClonePhotonViews repoint failed: " + ex3.Message);
						}
					}
		}
		catch (Exception ex2)
		{
			LogWarning("ReidClonePhotonViews failed: " + ex2.Message);
		}
	}

	private static void ClearPhotonViewOwner(object photonView)
	{
		try
		{
			PropertyInfo propertyInfo = AccessTools.Property(photonView.GetType(), "Owner");
			if ((object)propertyInfo != null && propertyInfo.CanWrite)
			{
				propertyInfo.SetValue(photonView, null);
				return;
			}
			FieldInfo fieldInfo = AccessTools.Field(photonView.GetType(), "_Owner_k__BackingField");
			if (fieldInfo != null)
			{
				fieldInfo.SetValue(photonView, null);
			}
		}
		catch (Exception ex)
		{
			LogDebug("ClearPhotonViewOwner failed: " + ex.Message);
		}
	}

	internal static void RunSelfTestRealloc()
	{
		try
		{
			if (!SelfTestRealloc || s_selfTestRan || !PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom || !NoktaSingleton<PlayerManager>.HasInstance || (Object)(object)NoktaSingleton<PlayerManager>.Instance == (Object)null || NoktaSingleton<PlayerManager>.Instance.PlayerInstances == null || NoktaSingleton<PlayerManager>.Instance.PlayerInstances.Count == 0)
			{
				return;
			}
			Room currentRoom = PhotonNetwork.CurrentRoom;
			if (currentRoom == null || !currentRoom.IsOpen)
			{
				return;
			}
			s_selfTestRan = true;
			LogInfo("SELFTEST: beginning PhotonView re-allocation test (solo room, no friends needed).");
			PlayerInstance val = null;
			List<PlayerInstance> playerInstances = NoktaSingleton<PlayerManager>.Instance.PlayerInstances;
			for (int i = 0; i < playerInstances.Count; i++)
			{
				PlayerInstance val2 = playerInstances[i];
				if (!((Object)(object)val2 == (Object)null) && !(bool)(GetMemberSafe(val2, "IsLocalPlayerInstance") ?? ((object)false)))
				{
					val = val2;
					break;
				}
			}
			if ((Object)(object)val == (Object)null)
			{
				val = ((playerInstances.Count > 0) ? playerInstances[0] : null);
			}
			if ((Object)(object)val == (Object)null)
			{
				LogWarning("SELFTEST: no usable PlayerInstance template found.");
				return;
			}
			object obj = InvokeStaticInstantiate(((Component)val).gameObject);
			PlayerInstance val3 = CastTo<PlayerInstance>(GetComponentOn(obj, AccessTools.TypeByName("PlayerInstance")));
			if ((Object)(object)val3 == (Object)null)
			{
				LogWarning("SELFTEST: clone has no PlayerInstance.");
				return;
			}
			Il2CppArrayBase<PhotonView> componentsInChildren = ((Component)val3).GetComponentsInChildren<PhotonView>(true);
			int num = 0;
			int num2 = 0;
			foreach (PhotonView item in componentsInChildren)
			{
				if (!((Object)(object)item == (Object)null))
				{
					int viewID = item.ViewID;
					ClearPhotonViewOwner(item);
					try
					{
						item.ViewID = 0;
					}
					catch (Exception exx)
					{
						LogWarning("SELFTEST: could not zero ViewID: " + exx.Message);
					}

					bool changed = false;
					try
					{
						// Try 1: room view allocation (runtime range, distinct from scene).
						int old = item.ViewID;
						if (PhotonNetwork.AllocateRoomViewID(item) && item.ViewID != old && item.ViewID != viewID)
						{
							changed = true;
							LogInfo($"SELFTEST: AllocateRoomViewID PASS - ViewID {viewID} -> {item.ViewID}.");
						}
						else
						{
							LogInfo($"SELFTEST: AllocateRoomViewID no-change (was {old}, now {item.ViewID}).");
						}
					}
					catch (Exception ex1)
					{
						LogInfo("SELFTEST: AllocateRoomViewID threw: " + ex1.Message);
					}

					if (!changed)
					{
						try
						{
							// Try 2: manual runtime ID. ONLY pass if the final ViewID differs.
							int allocated = PhotonNetwork.AllocateViewID(false);
							item.ViewID = allocated;
							PhotonNetwork.RegisterPhotonView(item);
							if (item.ViewID != viewID && item.ViewID != 0)
							{
								changed = true;
								LogInfo($"SELFTEST: manual AllocateViewID PASS - ViewID {viewID} -> {item.ViewID}.");
							}
							else
							{
								LogInfo($"SELFTEST: manual AllocateViewID no-change (allocated {allocated}, but ViewID stayed {item.ViewID}).");
							}
						}
						catch (Exception ex2)
						{
							LogInfo("SELFTEST: manual AllocateViewID threw: " + ex2.Message);
						}
					}

					if (!changed)
					{
						// Try 3: destroy the inherited PhotonView and add a BRAND-NEW one.
						// The clone's inherited view is hardwired to the scene ID (24); a fresh
						// component with a runtime AllocateViewID may get a genuinely new ID.
						try
						{
							GameObject ownerGo = ((Component)item).gameObject;
							if (ownerGo != null)
							{
								LogInfo($"SELFTEST: strategy3 destroying inherited PhotonView on {ownerGo.name}...");
Object.DestroyImmediate((Object)(object)item);
								// Deleting the component may be deferred to end of frame; remove
								// references now by creating the new view on the same GameObject.
								PhotonView fresh = ownerGo.AddComponent<PhotonView>();
								if (fresh != null)
								{
									int freshId = PhotonNetwork.AllocateViewID(false);
									ClearPhotonViewOwner(fresh);
									fresh.ViewID = freshId;
									PhotonNetwork.RegisterPhotonView(fresh);
									if (fresh.ViewID != viewID && fresh.ViewID != 0)
									{
										changed = true;
										LogInfo($"SELFTEST: strategy3 PASS - ViewID {viewID} -> {fresh.ViewID} (new component).");
									}
									else
									{
										LogInfo($"SELFTEST: strategy3 no-change (freshId {freshId}, but ViewID stayed {fresh.ViewID}).");
									}
								}
								else
								{
									LogInfo("SELFTEST: strategy3 could not AddComponent<PhotonView>.");
								}
							}
						}
						catch (Exception ex3)
						{
							LogInfo("SELFTEST: strategy3 threw: " + ex3.Message);
						}
					}

					if (changed)
					{
						num++;
					}
					else
					{
						num2++;
						LogWarning("SELFTEST: FAIL - " + ((Object)item).name + " ViewID " + viewID + " stayed " + item.ViewID + ".");
					}
				}
			}
			LogInfo($"SELFTEST: DONE - {num} re-allocated, {num2} failed.");
			s_autoTestComplete = true;
			Object val4 = (Object)((obj is Object) ? obj : null);
			if (val4 != null)
			{
				Object.Destroy(val4);
			}
		}
		catch (Exception ex)
		{
			s_selfTestRan = true;
			s_autoTestComplete = true;
			LogWarning("SELFTEST failed: " + ex.Message);
		}
	}

	private static void MarkCloneUnowned(PlayerInstance clone)
	{
		try
		{
			object memberSafe = GetMemberSafe(clone, "PhotonView");
			if (memberSafe == null)
			{
				LogInfo("MarkCloneUnowned: clone already has no PhotonView.");
				return;
			}
			PropertyInfo propertyInfo = AccessTools.Property(memberSafe.GetType(), "Owner");
			if ((object)propertyInfo != null && propertyInfo.CanWrite)
			{
				propertyInfo.SetValue(memberSafe, null);
				LogInfo("MarkCloneUnowned: cleared Owner on clone " + ((Object)clone).name + "'s PhotonView.");
				return;
			}
			FieldInfo fieldInfo = AccessTools.Field(memberSafe.GetType(), "_Owner_k__BackingField");
			if (fieldInfo != null)
			{
				fieldInfo.SetValue(memberSafe, null);
				LogInfo("MarkCloneUnowned: cleared _Owner_k__BackingField on clone " + ((Object)clone).name + "'s PhotonView.");
			}
			else
			{
				LogWarning("MarkCloneUnowned: could not clear PhotonView.Owner.");
			}
		}
		catch (Exception ex)
		{
			LogWarning("MarkCloneUnowned failed: " + ex.Message);
		}
	}

	private static void DisableCloneConflictingComponents(PlayerInstance clone)
	{
		try
		{
			GameObject gameObject = ((Component)clone).gameObject;
			if ((Object)(object)gameObject == (Object)null)
			{
				return;
			}
			PropertyInfo propertyInfo = AccessTools.Property(AccessTools.TypeByName("PlayerInstance"), "m_FPSVCamObject");
			if ((object)propertyInfo != null && propertyInfo.CanWrite)
			{
				propertyInfo.SetValue(clone, null);
				LogInfo("DisableCloneConflictingComponents: nulled m_FPSVCamObject on clone " + ((Object)gameObject).name + ".");
				return;
			}
			object memberSafe = GetMemberSafe(clone, "m_FPSVCamObject");
			Component val = (Component)((memberSafe is Component) ? memberSafe : null);
			if (val != null && (Object)(object)val != (Object)null && NoktaSingleton<PlayerManager>.HasInstance && (Object)(object)NoktaSingleton<PlayerManager>.Instance != (Object)null && (Object)(object)NoktaSingleton<PlayerManager>.Instance.LocalPlayer != (Object)null && val != GetMemberSafe(NoktaSingleton<PlayerManager>.Instance.LocalPlayer, "m_FPSVCamObject"))
			{
				val.gameObject.SetActive(false);
			}
			LogWarning("DisableCloneConflictingComponents: could not null m_FPSVCamObject; attempted safe disable.");
		}
		catch (Exception ex)
		{
			LogInfo("DisableCloneConflictingComponents failed: " + ex.Message);
		}
	}

	private static bool IsDescendantOf(Transform child, Transform parent)
	{
		while ((Object)(object)child != (Object)null)
		{
			if ((Object)(object)child == (Object)(object)parent)
			{
				return true;
			}
			child = child.parent;
		}
		return false;
	}

	private static object InvokeStaticInstantiate(object templateGameObject)
	{
		Object val = (Object)((templateGameObject is Object) ? templateGameObject : null);
		if (val == (Object)null)
		{
			LogWarning("InvokeStaticInstantiate: template is not a UnityEngine.Object.");
			return null;
		}
		return Object.Instantiate(val);
	}

	private static T CastTo<T>(object obj) where T : Il2CppObjectBase
	{
		if (obj == null)
		{
			return default(T);
		}
		T val = (T)((obj is T) ? obj : null);
		if (val != null)
		{
			return val;
		}
		Il2CppObjectBase val2 = (Il2CppObjectBase)((obj is Il2CppObjectBase) ? obj : null);
		if (val2 != null)
		{
			return val2.Cast<T>();
		}
		LogWarning($"CastTo<{typeof(T).Name}>: object is {obj.GetType().Name}, not an Il2CppObjectBase.");
		return default(T);
	}

	private static object GetComponentOn(object gameObject, Type componentType)
	{
		GameObject val = CastTo<GameObject>(gameObject);
		if ((Object)(object)val == (Object)null)
		{
			LogWarning("GetComponentOn: template GameObject is not a UnityEngine.GameObject.");
			return null;
		}
		return val.GetComponent(Il2CppType.From(componentType));
	}

	private static int GetListElementCount(object list)
	{
		if (list == null)
		{
			return 0;
		}
		try
		{
			MethodCallResult methodCallResult = InvokeListMethod(list, "get_Count", null);
			if (methodCallResult.Success)
			{
				return Convert.ToInt32(methodCallResult.Value);
			}
		}
		catch (Exception ex)
		{
			LogDebug("GetListElementCount: get_Count failed: " + ex.Message);
		}
		object obj = GetMemberSafe(list, "Size") ?? GetMemberSafe(list, "Length") ?? GetMemberSafe(list, "Count");
		if (obj == null)
		{
			LogWarning("GetListElementCount: list " + list.GetType().FullName + " has no usable Count member.");
			return 0;
		}
		return Convert.ToInt32(obj);
	}

	private static object GetListItem(object list, int index)
	{
		if (list == null)
		{
			return null;
		}
		try
		{
			MethodCallResult methodCallResult = InvokeListMethod(list, "get_Item", index);
			if (methodCallResult.Success)
			{
				return methodCallResult.Value;
			}
		}
		catch (Exception ex)
		{
			LogDebug("GetListItem: get_Item failed: " + ex.Message);
		}
		object memberSafe = GetMemberSafe(list, $"Item{index}");
		if (memberSafe != null)
		{
			return memberSafe;
		}
		LogWarning("GetListItem: list " + list.GetType().FullName + " has no usable item accessor.");
		return null;
	}

	private static void AddToList(object list, object element)
	{
		if (list == null)
		{
			LogWarning("AddToList: list is null.");
			return;
		}
		Object val = (Object)((element is Object) ? element : null);
		if (val == null)
		{
			LogWarning("AddToList: element is not an Il2CppSystem.Object; cannot append.");
			return;
		}
		try
		{
			if (InvokeListMethod(list, "Add", val).Success)
			{
				return;
			}
		}
		catch (Exception ex)
		{
			LogDebug("AddToList: Add failed: " + ex.Message);
		}
		LogWarning("AddToList: list " + list.GetType().FullName + " could not be appended to.");
	}

	private static MethodCallResult InvokeListMethod(object list, string methodName, object arg)
	{
		MethodInfo methodInfo = AccessTools.Method(list.GetType(), methodName, (Type[])null, (Type[])null);
		if (methodInfo == null)
		{
			LogDebug($"InvokeListMethod: {methodName} not found on {list.GetType().FullName}.");
			return new MethodCallResult(success: false, null);
		}
		return new MethodCallResult(success: true, methodInfo.Invoke(list, (arg == null) ? null : new object[1] { arg }));
	}

	private static void NetworkManagerEventPostfix(object __instance, MethodBase __originalMethod, object[] __args)
	{
		try
		{
			StringBuilder stringBuilder = new StringBuilder("NetworkManager." + __originalMethod.Name);
			if (__args != null)
			{
				foreach (object obj in __args)
				{
					if (obj != null)
					{
						object memberSafe = GetMemberSafe(obj, "ActorNumber");
						if (memberSafe != null)
						{
							StringBuilder stringBuilder2 = stringBuilder;
							StringBuilder stringBuilder3 = stringBuilder2;
							StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(23, 2, stringBuilder2);
							handler.AppendLiteral(" | player actor=");
							handler.AppendFormatted<object>(memberSafe);
							handler.AppendLiteral(", nick=");
							handler.AppendFormatted<object>(GetMemberSafe(obj, "NickName") ?? "?");
							stringBuilder3.Append(ref handler);
						}
					}
				}
			}
			if (__instance != null)
			{
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(22, 2, stringBuilder2);
				handler.AppendLiteral(" | SyncedSystemCount=");
				handler.AppendFormatted<object>(GetMemberSafe(__instance, "SyncedSystemCount") ?? "?");
				handler.AppendLiteral("/");
				handler.AppendFormatted<object>(GetMemberSafe(__instance, "m_TargetSyncSystemCount") ?? "?");
				stringBuilder4.Append(ref handler);
			}
			LogInfo(stringBuilder.ToString());
		}
		catch (Exception ex)
		{
			LogWarning("NetworkManager diagnostics postfix failed for " + __originalMethod.Name + ": " + ex.Message);
		}
	}

	private static object GetMemberSafe(object instance, string name)
	{
		try
		{
			return GetMember(instance, name);
		}
		catch
		{
			return null;
		}
	}

	private static void PatchSteamLobbyTargets(Harmony harmony)
	{
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Expected O, but got Unknown
		HarmonyMethod prefix = new HarmonyMethod(AccessTools.Method(typeof(Plugin), "SteamLobbyCreatePrefix", (Type[])null, (Type[])null));
		int num = 0;
		string[] array = new string[2] { "Steamworks.SteamMatchmaking", "Steamworks.ISteamMatchmaking" };
		for (int i = 0; i < array.Length; i++)
		{
			Type type = AccessTools.TypeByName(array[i]);
			if (!(type == null))
			{
				num += PatchNamedMethods(harmony, type, (System.Collections.Generic.IReadOnlyCollection<string>)(object)new string[2] { "CreateLobbyAsync", "CreateLobby" }, prefix, null);
			}
		}
		if (num == 0)
		{
			LogWarning("No Steamworks lobby creation methods were found. This may be fine if the game build does not create Steam lobbies through managed Facepunch APIs.");
			return;
		}
		LogInfo($"Patched {num} Steamworks lobby creation method(s).");
	}

	private static int PatchNamedMethods(Harmony harmony, Type type, System.Collections.Generic.IReadOnlyCollection<string> names, HarmonyMethod prefix, HarmonyMethod postfix)
	{
		int num = 0;
		foreach (MethodInfo declaredMethod in AccessTools.GetDeclaredMethods(type))
		{
			if (!names.Contains(declaredMethod.Name) || declaredMethod.ContainsGenericParameters)
			{
				continue;
			}
			try
			{
				harmony.Patch((MethodBase)declaredMethod, prefix, postfix, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				num++;
				LogDebug($"Patched {type.FullName}.{declaredMethod.Name}({string.Join(", ", from p in declaredMethod.GetParameters()
					select p.ParameterType.Name)})");
			}
			catch (Exception ex)
			{
				LogWarning($"Could not patch {type.FullName}.{declaredMethod.Name}: {ex.Message}");
			}
		}
		return num;
	}

	private static void PhotonRoomCreatePrefix(MethodBase __originalMethod, object[] __args)
	{
		try
		{
			RewriteRoomCreationArguments(__originalMethod, __args);
		}
		catch (Exception value)
		{
			LogWarning($"Photon prefix failed for {__originalMethod.Name}: {value}");
		}
	}

	private static void SteamLobbyCreatePrefix(MethodBase __originalMethod, object[] __args)
	{
		try
		{
			RewriteMaxPlayerArguments(__originalMethod, __args, "Steam lobby");
		}
		catch (Exception value)
		{
			LogWarning($"Steam lobby prefix failed for {__originalMethod.Name}: {value}");
		}
	}

	private static void LobbyManagerPrefix(object __instance, MethodBase __originalMethod)
	{
		try
		{
			SetMember(__instance, "m_MaxPlayersPerRoom", MaxPlayers);
			if (VerboseLogging)
			{
				LogInfo($"LobbyManager.{__originalMethod.Name}: m_MaxPlayersPerRoom forced to {MaxPlayers}.");
			}
		}
		catch (Exception ex)
		{
			LogWarning("LobbyManager prefix failed for " + __originalMethod.Name + ": " + ex.Message);
		}
	}

	private static void RewriteRoomCreationArguments(MethodBase method, object[] args)
	{
		ParameterInfo[] parameters = method.GetParameters();
		for (int i = 0; i < args.Length && i < parameters.Length; i++)
		{
			ParameterInfo parameterInfo = parameters[i];
			if (parameterInfo.ParameterType.FullName == "Photon.Realtime.RoomOptions")
			{
				int num = i;
				if (args[num] == null)
				{
					args[num] = Activator.CreateInstance(parameterInfo.ParameterType);
				}
				SetRoomOptionsMaxPlayers(args[i], "PhotonNetwork." + method.Name);
			}
			else if (IsMaxPlayerParameter(parameterInfo))
			{
				args[i] = ConvertMaxPlayersFor(parameterInfo.ParameterType);
			}
		}
		if (VerboseLogging)
		{
			LogInfo($"PhotonNetwork.{method.Name}: room creation arguments inspected; target MaxPlayers={MaxPlayers}.");
		}
	}

	private static void RewriteMaxPlayerArguments(MethodBase method, object[] args, string source)
	{
		ParameterInfo[] parameters = method.GetParameters();
		for (int i = 0; i < args.Length && i < parameters.Length; i++)
		{
			if (IsMaxPlayerParameter(parameters[i]))
			{
				object value = args[i];
				args[i] = ConvertMaxPlayersFor(parameters[i].ParameterType);
				if (VerboseLogging)
				{
					LogInfo($"{source} {method.Name}: {parameters[i].Name} {value} -> {args[i]}.");
				}
			}
		}
	}

	private static bool IsMaxPlayerParameter(ParameterInfo parameter)
	{
		string text = parameter.Name ?? string.Empty;
		if (!text.Contains("max", StringComparison.OrdinalIgnoreCase) && !text.Contains("player", StringComparison.OrdinalIgnoreCase) && !text.Contains("member", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		Type type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
		if (!(type == typeof(byte)) && !(type == typeof(sbyte)) && !(type == typeof(short)) && !(type == typeof(ushort)) && !(type == typeof(int)))
		{
			return type == typeof(uint);
		}
		return true;
	}

	private static object ConvertMaxPlayersFor(Type targetType)
	{
		targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
		if (targetType == typeof(byte))
		{
			return (byte)MaxPlayers;
		}
		if (targetType == typeof(sbyte))
		{
			return (sbyte)Mathf.Min(MaxPlayers, 127);
		}
		if (targetType == typeof(short))
		{
			return (short)MaxPlayers;
		}
		if (targetType == typeof(ushort))
		{
			return (ushort)MaxPlayers;
		}
		if (targetType == typeof(uint))
		{
			return (uint)MaxPlayers;
		}
		return MaxPlayers;
	}

	private static void SetRoomOptionsMaxPlayers(object roomOptions, string source)
	{
		object member = GetMember(roomOptions, "MaxPlayers");
		SetMember(roomOptions, "MaxPlayers", ConvertMaxPlayersFor(GetMemberType(roomOptions.GetType(), "MaxPlayers") ?? typeof(byte)));
		if (VerboseLogging)
		{
			LogInfo($"{source}: RoomOptions.MaxPlayers {member ?? "<null>"} -> {MaxPlayers}.");
		}
	}

	internal static object GetMember(object instance, string name)
	{
		Type type = instance.GetType();
		PropertyInfo propertyInfo = AccessTools.Property(type, name);
		if ((object)propertyInfo != null && propertyInfo.CanRead)
		{
			return propertyInfo.GetValue(instance);
		}
		return AccessTools.Field(type, name)?.GetValue(instance);
	}

	internal static void SetMember(object instance, string name, object value)
	{
		Type type = instance.GetType();
		PropertyInfo propertyInfo = AccessTools.Property(type, name);
		if ((object)propertyInfo != null && propertyInfo.CanWrite)
		{
			propertyInfo.SetValue(instance, Convert.ChangeType(value, Nullable.GetUnderlyingType(propertyInfo.PropertyType) ?? propertyInfo.PropertyType));
			return;
		}
		FieldInfo fieldInfo = AccessTools.Field(type, name);
		if (fieldInfo == null)
		{
			throw new MissingMemberException(type.FullName, name);
		}
		fieldInfo.SetValue(instance, Convert.ChangeType(value, Nullable.GetUnderlyingType(fieldInfo.FieldType) ?? fieldInfo.FieldType));
	}

	internal static bool SetMemberRaw(object instance, string name, object value)
	{
		try
		{
			Type type = instance.GetType();
			PropertyInfo propertyInfo = AccessTools.Property(type, name);
			if ((object)propertyInfo != null && propertyInfo.CanWrite)
			{
				propertyInfo.SetValue(instance, value);
				return true;
			}
			FieldInfo fieldInfo = AccessTools.Field(type, name);
			if (fieldInfo != null)
			{
				fieldInfo.SetValue(instance, value);
				return true;
			}
		}
		catch (Exception ex)
		{
			LogDebug("SetMemberRaw(" + name + ") failed: " + ex.Message);
		}
		return false;
	}

	private static Type GetMemberType(Type type, string name)
	{
		object obj = AccessTools.Property(type, name)?.PropertyType;
		if (obj == null)
		{
			FieldInfo fieldInfo = AccessTools.Field(type, name);
			if ((object)fieldInfo == null)
			{
				return null;
			}
			obj = fieldInfo.FieldType;
		}
		return (Type)obj;
	}

	internal static Type TypeByName(string name)
	{
		return AccessTools.TypeByName(name);
	}

	internal static void LogInfo(string message)
	{
		ManualLogSource obj = s_log;
		if (obj != null)
		{
			obj.LogInfo((object)message);
		}
	}

	internal static void LogDebug(string message)
	{
		ManualLogSource obj = s_log;
		if (obj != null)
		{
			obj.LogDebug((object)message);
		}
	}

	internal static void LogWarning(string message)
	{
		ManualLogSource obj = s_log;
		if (obj != null)
		{
			obj.LogWarning((object)message);
		}
	}
}
public sealed class RoomCapacityMonitor : MonoBehaviour
{
	private float _nextCheckTime;

	private bool? _wasOpen;

	private object _networkManager;

	private int _lastSynced = -1;

	private int _lastTarget = -1;

	public RoomCapacityMonitor(IntPtr pointer)
		: base(pointer)
	{
	}

	private void Update()
	{
		if (!(Time.unscaledTime < _nextCheckTime))
		{
			_nextCheckTime = Time.unscaledTime + 0.5f;
			InspectCurrentRoom();
			WatchLoadingProgress();
			Plugin.RunSelfTestRealloc();
			Plugin.EnsurePlayerBodyCapacity();
			Plugin.ForceSyncTargetIfNeeded();
		}
	}

	private void WatchLoadingProgress()
	{
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Expected O, but got Unknown
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Expected O, but got Unknown
		bool flag = default(bool);
		try
		{
			Type type = Plugin.TypeByName("NetworkManager");
			if (type == null)
			{
				return;
			}
			if (_networkManager == null)
			{
				_networkManager = Object.FindFirstObjectByType(Il2CppType.From(type));
				if (_networkManager == null)
				{
					return;
				}
			}
			int num = Convert.ToInt32(Plugin.GetMember(_networkManager, "SyncedSystemCount"));
			int num2 = Convert.ToInt32(Plugin.GetMember(_networkManager, "m_TargetSyncSystemCount"));
			if (num != _lastSynced || num2 != _lastTarget)
			{
				ManualLogSource logger = Plugin.Logger;
				logger.LogInfo($"Loading progress: SyncedSystemCount={num}/{num2}.");
				_lastSynced = num;
				_lastTarget = num2;
			}
		}
		catch (Exception ex)
		{
			ManualLogSource logger2 = Plugin.Logger;
			logger2.LogWarning($"Loading progress watch failed: {ex.Message}");
		}
	}

	private void InspectCurrentRoom()
	{
		//IL_01c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ca: Expected O, but got Unknown
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Expected O, but got Unknown
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_0168: Expected O, but got Unknown
		bool flag2 = default(bool);
		try
		{
			Type type = Plugin.TypeByName("Photon.Pun.PhotonNetwork");
			if (type == null)
			{
				return;
			}
			if (!ReadStaticBool(type, "InRoom") || !ReadStaticBool(type, "IsMasterClient"))
			{
				_wasOpen = null;
				return;
			}
			object obj = ReadStaticMember(type, "CurrentRoom");
			if (obj == null)
			{
				_wasOpen = null;
				return;
			}
			bool flag = Convert.ToBoolean(Plugin.GetMember(obj, "IsOpen"));
			int num = Convert.ToInt32(Plugin.GetMember(obj, "PlayerCount"));
			int num2 = Convert.ToInt32(Plugin.GetMember(obj, "MaxPlayers"));
			if (Plugin.VerboseLogging)
			{
				ManualLogSource logger = Plugin.Logger;
				logger.LogInfo($"CurrentRoom: PlayerCount={num}, MaxPlayers={num2}, IsOpen={flag}.");
			}
			if (Plugin.ShrinkOnRoomClose)
			{
				bool? wasOpen = _wasOpen;
				flag2 = true;
				if (wasOpen == flag2 && !flag)
				{
					int num3 = Mathf.Clamp(num, 1, 255);
					if (num3 < num2)
					{
						Plugin.SetMember(obj, "MaxPlayers", num3);
						ManualLogSource logger2 = Plugin.Logger;
						logger2.LogInfo($"Room closed; Photon CurrentRoom.MaxPlayers shrunk {num2} -> {num3}.");
					}
				}
			}
			_wasOpen = flag;
		}
		catch (Exception ex)
		{
			ManualLogSource logger3 = Plugin.Logger;
			logger3.LogWarning($"Room monitor failed: {ex.Message}");
		}
	}

	private static bool ReadStaticBool(Type type, string propertyName)
	{
		return Convert.ToBoolean(ReadStaticMember(type, propertyName));
	}

	private static object ReadStaticMember(Type type, string propertyName)
	{
		PropertyInfo propertyInfo = AccessTools.Property(type, propertyName);
		if ((object)propertyInfo != null && propertyInfo.CanRead)
		{
			return propertyInfo.GetValue(null);
		}
		return AccessTools.Field(type, propertyName)?.GetValue(null);
	}
}



