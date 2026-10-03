using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SupermarketMorePlayers;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "dev.codex.supermarket.moreplayers";
    public const string PluginName = "Supermarket More Players";
    public const string PluginVersion = "0.1.0";

    private static readonly string[] PhotonRoomMethodNames =
    {
        "CreateRoom",
        "JoinOrCreateRoom",
        "JoinRandomOrCreateRoom"
    };

    private static readonly string[] LobbyManagerMethodNames =
    {
        "CreateRoom",
        "JoinTargetRoom",
        "ExternalJoinTargetRoom",
        "ConnectToServer",
        "OnConnectedToMaster"
    };

    private static ManualLogSource s_log;
    private static ConfigEntry<int> s_maxPlayers;
    private static ConfigEntry<bool> s_patchSteamLobby;
    private static ConfigEntry<bool> s_shrinkOnRoomClose;
    private static ConfigEntry<bool> s_verboseLogging;

    private Harmony _harmony;

    public override void Load()
    {
        s_log = Log;
        s_maxPlayers = Config.Bind("General", "MaxPlayers", 8, "Target Photon room and Steam lobby capacity. Photon supports 1-255 here.");
        s_patchSteamLobby = Config.Bind("General", "PatchSteamLobby", true, "Also patches Facepunch/Steamworks lobby creation limits when present.");
        s_shrinkOnRoomClose = Config.Bind("General", "ShrinkPhotonRoomWhenClosed", true, "When the host closes the room, reduce Photon MaxPlayers to the actual connected player count.");
        s_verboseLogging = Config.Bind("Diagnostics", "VerboseLogging", true, "Log room creation arguments and current Photon room state.");

        _harmony = new Harmony(PluginGuid);
        PatchKnownTargets(_harmony);

        ClassInjector.RegisterTypeInIl2Cpp<RoomCapacityMonitor>();
        AddComponent<RoomCapacityMonitor>();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. MaxPlayers={MaxPlayers}");
    }

    private static int MaxPlayers => Mathf.Clamp(s_maxPlayers?.Value ?? 8, 1, 255);
    internal static bool ShrinkOnRoomClose => s_shrinkOnRoomClose?.Value ?? true;
    internal static bool VerboseLogging => s_verboseLogging?.Value ?? true;
    internal static ManualLogSource Logger => s_log!;

    private static void PatchKnownTargets(Harmony harmony)
    {
        var photonNetwork = AccessTools.TypeByName("Photon.Pun.PhotonNetwork");
        if (photonNetwork == null)
        {
            LogWarning("Photon.Pun.PhotonNetwork was not found. Photon room cap patches were not installed.");
        }
        else
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(PhotonRoomCreatePrefix)));
            var count = PatchNamedMethods(harmony, photonNetwork, PhotonRoomMethodNames, prefix, postfix: null);
            LogInfo($"Patched {count} PhotonNetwork room creation method(s).");
        }

        var lobbyManager = AccessTools.TypeByName("LobbyManager")
            ?? AccessTools.TypeByName("__Project__.Scripts.Multiplayer.LobbyManager");
        if (lobbyManager == null)
        {
            LogWarning("LobbyManager was not found. Game field patch was not installed.");
        }
        else
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(LobbyManagerPrefix)));
            var count = PatchNamedMethods(harmony, lobbyManager, LobbyManagerMethodNames, prefix, postfix: null);
            LogInfo($"Patched {count} LobbyManager method(s).");
        }

        if (s_patchSteamLobby?.Value == true)
        {
            PatchSteamLobbyTargets(harmony);
        }
    }

    private static void PatchSteamLobbyTargets(Harmony harmony)
    {
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SteamLobbyCreatePrefix)));
        var patched = 0;

        foreach (var typeName in new[]
        {
            "Steamworks.SteamMatchmaking",
            "Steamworks.ISteamMatchmaking"
        })
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                continue;
            }

            patched += PatchNamedMethods(harmony, type, new[] { "CreateLobbyAsync", "CreateLobby" }, prefix, postfix: null);
        }

        if (patched == 0)
        {
            LogWarning("No Steamworks lobby creation methods were found. This may be fine if the game build does not create Steam lobbies through managed Facepunch APIs.");
            return;
        }

        LogInfo($"Patched {patched} Steamworks lobby creation method(s).");
    }

    private static int PatchNamedMethods(Harmony harmony, Type type, IReadOnlyCollection<string> names, HarmonyMethod prefix, HarmonyMethod postfix)
    {
        var count = 0;
        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (!names.Contains(method.Name) || method.ContainsGenericParameters)
            {
                continue;
            }

            try
            {
                harmony.Patch(method, prefix, postfix);
                count++;
                LogDebug($"Patched {type.FullName}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
            }
            catch (Exception ex)
            {
                LogWarning($"Could not patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }

        return count;
    }

    private static void PhotonRoomCreatePrefix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            RewriteRoomCreationArguments(__originalMethod, __args);
        }
        catch (Exception ex)
        {
            LogWarning($"Photon prefix failed for {__originalMethod.Name}: {ex}");
        }
    }

    private static void SteamLobbyCreatePrefix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            RewriteMaxPlayerArguments(__originalMethod, __args, "Steam lobby");
        }
        catch (Exception ex)
        {
            LogWarning($"Steam lobby prefix failed for {__originalMethod.Name}: {ex}");
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
            LogWarning($"LobbyManager prefix failed for {__originalMethod.Name}: {ex.Message}");
        }
    }

    private static void RewriteRoomCreationArguments(MethodBase method, object[] args)
    {
        var parameters = method.GetParameters();
        for (var i = 0; i < args.Length && i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.ParameterType.FullName == "Photon.Realtime.RoomOptions")
            {
                args[i] ??= Activator.CreateInstance(parameter.ParameterType);
                SetRoomOptionsMaxPlayers(args[i]!, $"PhotonNetwork.{method.Name}");
                continue;
            }

            if (IsMaxPlayerParameter(parameter))
            {
                args[i] = ConvertMaxPlayersFor(parameter.ParameterType);
            }
        }

        if (VerboseLogging)
        {
            LogInfo($"PhotonNetwork.{method.Name}: room creation arguments inspected; target MaxPlayers={MaxPlayers}.");
        }
    }

    private static void RewriteMaxPlayerArguments(MethodBase method, object[] args, string source)
    {
        var parameters = method.GetParameters();
        for (var i = 0; i < args.Length && i < parameters.Length; i++)
        {
            if (!IsMaxPlayerParameter(parameters[i]))
            {
                continue;
            }

            var oldValue = args[i];
            args[i] = ConvertMaxPlayersFor(parameters[i].ParameterType);
            if (VerboseLogging)
            {
                LogInfo($"{source} {method.Name}: {parameters[i].Name} {oldValue} -> {args[i]}.");
            }
        }
    }

    private static bool IsMaxPlayerParameter(ParameterInfo parameter)
    {
        var name = parameter.Name ?? string.Empty;
        if (!name.Contains("max", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("player", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("member", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        return type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint);
    }

    private static object ConvertMaxPlayersFor(Type targetType)
    {
        targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (targetType == typeof(byte)) return (byte)MaxPlayers;
        if (targetType == typeof(sbyte)) return (sbyte)Mathf.Min(MaxPlayers, sbyte.MaxValue);
        if (targetType == typeof(short)) return (short)MaxPlayers;
        if (targetType == typeof(ushort)) return (ushort)MaxPlayers;
        if (targetType == typeof(uint)) return (uint)MaxPlayers;
        return MaxPlayers;
    }

    private static void SetRoomOptionsMaxPlayers(object roomOptions, string source)
    {
        var oldValue = GetMember(roomOptions, "MaxPlayers");
        SetMember(roomOptions, "MaxPlayers", ConvertMaxPlayersFor(GetMemberType(roomOptions.GetType(), "MaxPlayers") ?? typeof(byte)));

        if (VerboseLogging)
        {
            LogInfo($"{source}: RoomOptions.MaxPlayers {oldValue ?? "<null>"} -> {MaxPlayers}.");
        }
    }

    internal static object GetMember(object instance, string name)
    {
        var type = instance.GetType();
        var property = AccessTools.Property(type, name);
        if (property?.CanRead == true)
        {
            return property.GetValue(instance);
        }

        return AccessTools.Field(type, name)?.GetValue(instance);
    }

    internal static void SetMember(object instance, string name, object value)
    {
        var type = instance.GetType();
        var property = AccessTools.Property(type, name);
        if (property?.CanWrite == true)
        {
            property.SetValue(instance, Convert.ChangeType(value, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));
            return;
        }

        var field = AccessTools.Field(type, name);
        if (field == null)
        {
            throw new MissingMemberException(type.FullName, name);
        }

        field.SetValue(instance, Convert.ChangeType(value, Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType));
    }

    private static Type GetMemberType(Type type, string name)
    {
        return AccessTools.Property(type, name)?.PropertyType
            ?? AccessTools.Field(type, name)?.FieldType;
    }

    internal static Type TypeByName(string name) => AccessTools.TypeByName(name);

    internal static void LogInfo(string message) => s_log?.LogInfo(message);
    internal static void LogDebug(string message) => s_log?.LogDebug(message);
    internal static void LogWarning(string message) => s_log?.LogWarning(message);
}

public sealed class RoomCapacityMonitor : MonoBehaviour
{
    private float _nextCheckTime;
    private bool? _wasOpen;

    public RoomCapacityMonitor(IntPtr pointer) : base(pointer)
    {
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextCheckTime)
        {
            return;
        }

        _nextCheckTime = Time.unscaledTime + 0.5f;
        InspectCurrentRoom();
    }

    private void InspectCurrentRoom()
    {
        try
        {
            var photonNetwork = Plugin.TypeByName("Photon.Pun.PhotonNetwork");
            if (photonNetwork == null)
            {
                return;
            }

            if (!ReadStaticBool(photonNetwork, "InRoom") || !ReadStaticBool(photonNetwork, "IsMasterClient"))
            {
                _wasOpen = null;
                return;
            }

            var room = ReadStaticMember(photonNetwork, "CurrentRoom");
            if (room == null)
            {
                _wasOpen = null;
                return;
            }

            var isOpen = Convert.ToBoolean(Plugin.GetMember(room, "IsOpen"));
            var playerCount = Convert.ToInt32(Plugin.GetMember(room, "PlayerCount"));
            var maxPlayers = Convert.ToInt32(Plugin.GetMember(room, "MaxPlayers"));

            if (Plugin.VerboseLogging)
            {
                Plugin.Logger.LogInfo($"CurrentRoom: PlayerCount={playerCount}, MaxPlayers={maxPlayers}, IsOpen={isOpen}.");
            }

            if (Plugin.ShrinkOnRoomClose && _wasOpen == true && !isOpen)
            {
                var newMax = Mathf.Clamp(playerCount, 1, 255);
                if (newMax < maxPlayers)
                {
                    Plugin.SetMember(room, "MaxPlayers", newMax);
                    Plugin.Logger.LogInfo($"Room closed; Photon CurrentRoom.MaxPlayers shrunk {maxPlayers} -> {newMax}.");
                }
            }

            _wasOpen = isOpen;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Room monitor failed: {ex.Message}");
        }
    }

    private static bool ReadStaticBool(Type type, string propertyName)
    {
        return Convert.ToBoolean(ReadStaticMember(type, propertyName));
    }

    private static object ReadStaticMember(Type type, string propertyName)
    {
        var property = AccessTools.Property(type, propertyName);
        if (property?.CanRead == true)
        {
            return property.GetValue(null);
        }

        return AccessTools.Field(type, propertyName)?.GetValue(null);
    }
}
