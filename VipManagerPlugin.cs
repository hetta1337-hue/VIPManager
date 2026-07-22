using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace VipManager;

// Esquema esperado (lo crea y actualiza la API externa, este plugin solo hace SELECT):
//   CREATE TABLE vip_users (
//       id        INT UNSIGNED    NOT NULL AUTO_INCREMENT PRIMARY KEY,
//       steamid   BIGINT UNSIGNED NOT NULL UNIQUE,
//       name      VARCHAR(64)     NOT NULL,
//       vip_start DATETIME        NOT NULL,
//       vip_end   DATETIME        NOT NULL
//   );
public class VipManagerConfig : BasePluginConfig
{
    public int ReminderDaysBefore { get; set; } = 7;
}

public class VipManagerPlugin : BasePlugin, IPluginConfig<VipManagerConfig>
{
    public override string ModuleName => "VipManager";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "you";

    public VipManagerConfig Config { get; set; } = new();

    private readonly ConcurrentDictionary<ulong, (DateTime Start, DateTime End)> _vipCache = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _lastReminded = new(); // solo en memoria, no se persiste
    private string _connectionString = "";

    public void OnConfigParsed(VipManagerConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        // Credenciales por variables de entorno del proceso, con fallback a un archivo .env
        // en la carpeta del plugin (util en paneles tipo Pterodactyl donde no se puede setear
        // env vars del contenedor sin tocar el egg). Ver .env.example.
        var env = LoadEnvFile(Path.Combine(ModuleDirectory, ".env"));
        string Setting(string key, string fallback) =>
            env.TryGetValue(key, out var v) ? v : Environment.GetEnvironmentVariable(key) ?? fallback;

        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = Setting("VIPMANAGER_DB_HOST", "127.0.0.1"),
            Port = uint.TryParse(Setting("VIPMANAGER_DB_PORT", "3306"), out var port) ? port : 3306,
            Database = Setting("VIPMANAGER_DB_NAME", "cs2vip"),
            UserID = Setting("VIPMANAGER_DB_USER", "root"),
            Password = Setting("VIPMANAGER_DB_PASSWORD", ""),
        }.ConnectionString;

        RefreshCache();

        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        AddCommandListener("say", OnSay, HookMode.Pre);
        AddCommandListener("say_team", OnSayTeam, HookMode.Pre);

        AddTimer(300.0f, RefreshCache, TimerFlags.REPEAT);
        AddTimer(900.0f, CheckReminders, TimerFlags.REPEAT);
    }

    // ---- commands ----

    [ConsoleCommand("css_vip", "Muestra cuanto VIP te queda")]
    public void OnVipCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player is null || !player.IsValid) return;

        var steamId = player.AuthorizedSteamID.SteamId64;
        if (IsVip(steamId))
        {
            var days = (_vipCache[steamId].End - DateTime.UtcNow).Days;
            player.PrintToChat($" {ChatColors.Green}[VIP]{ChatColors.Default} Te quedan {ChatColors.Gold}{days}{ChatColors.Default} dia(s) de VIP.");
        }
        else
        {
            player.PrintToChat($" {ChatColors.Grey}No tenes VIP activo.");
        }
    }

    // ---- chat color ----

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info) => HandleChat(player, info, teamOnly: false);
    private HookResult OnSayTeam(CCSPlayerController? player, CommandInfo info) => HandleChat(player, info, teamOnly: true);

    private HookResult HandleChat(CCSPlayerController? player, CommandInfo info, bool teamOnly)
    {
        if (player is null || !player.IsValid || !IsVip(player.AuthorizedSteamID.SteamId64))
            return HookResult.Continue;

        var message = info.GetArg(1);
        if (string.IsNullOrWhiteSpace(message)) return HookResult.Continue;

        var formatted = $" {ChatColors.Green}[VIP] {ChatColors.Gold}{player.PlayerName}{ChatColors.Default}: {ChatColors.Green}{message}";

        if (teamOnly)
        {
            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && p.Team == player.Team))
                p.PrintToChat(formatted);
        }
        else
        {
            Server.PrintToChatAll(formatted);
        }

        return HookResult.Handled;
    }

    // ---- reserved slot ----

    private void OnClientAuthorized(int slot, SteamID id)
    {
        if (!IsVip(id.SteamId64)) return;

        Server.NextFrame(() =>
        {
            var humans = Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot).ToList();
            if (humans.Count < Server.MaxPlayers) return; // hay lugar, no hace falta kickear a nadie

            var target = humans
                .Where(p => p.AuthorizedSteamID.SteamId64 != id.SteamId64 && !IsVip(p.AuthorizedSteamID.SteamId64))
                // Ponytail: highest slot index as a stand-in for "joined most recently" - track
                // real join timestamps per-session if you need exact recency.
                .OrderByDescending(p => p.Slot)
                .FirstOrDefault();

            if (target is not null)
                Server.ExecuteCommand($"kickid {target.UserId} \"Slot reservado para VIP\"");
        });
    }

    // ---- reminders (solo chat + memoria, no escribe en la DB) ----

    private void CheckReminders()
    {
        foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot))
        {
            var steamId = player.AuthorizedSteamID.SteamId64;
            if (!IsVip(steamId)) continue;

            var daysLeft = (_vipCache[steamId].End - DateTime.UtcNow).TotalDays;
            if (daysLeft > Config.ReminderDaysBefore) continue;

            if (_lastReminded.TryGetValue(steamId, out var last) && last.Date == DateTime.UtcNow.Date)
                continue;

            player.PrintToChat($" {ChatColors.Green}[VIP]{ChatColors.Default} Tu VIP vence en {ChatColors.Gold}{Math.Ceiling(daysLeft)}{ChatColors.Default} dia(s). Renova antes de que termine la semana para no perderlo.");
            _lastReminded[steamId] = DateTime.UtcNow;
        }
    }

    // ---- data (solo lectura) ----

    private bool IsVip(ulong steamId)
    {
        if (!_vipCache.TryGetValue(steamId, out var v)) return false;
        var now = DateTime.UtcNow;
        return now >= v.Start && now <= v.End;
    }

    private void RefreshCache()
    {
        Task.Run(async () =>
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT steamid, vip_start, vip_end FROM vip_users WHERE vip_end > UTC_TIMESTAMP()";
            await using var reader = await cmd.ExecuteReaderAsync();

            var fresh = new Dictionary<ulong, (DateTime, DateTime)>();
            while (await reader.ReadAsync())
                fresh[(ulong)reader.GetInt64(0)] = (reader.GetDateTime(1), reader.GetDateTime(2));

            foreach (var kv in fresh)
                _vipCache[kv.Key] = kv.Value;

            foreach (var key in _vipCache.Keys.Except(fresh.Keys).ToList())
                _vipCache.TryRemove(key, out _);
        });
    }

    private static Dictionary<string, string> LoadEnvFile(string path)
    {
        var result = new Dictionary<string, string>();
        if (!File.Exists(path)) return result;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            result[key] = value;
        }

        return result;
    }
}
