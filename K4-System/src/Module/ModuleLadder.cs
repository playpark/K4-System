namespace K4System;

using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core.Plugin;
using CounterStrikeSharp.API.Modules.Timers;
using K4System.Models;
using Dapper;

public partial class ModuleLadder : IModuleLadder
{
	public ModuleLadder(ILogger<ModuleLadder> logger, IPluginContext pluginContext)
	{
		this.Logger = logger;
		this.pluginContext = pluginContext;
	}

	public Timer? scoreRefreshTimer = null;
	public Timer? dailySaveTimer = null;
	public Timer? dailySnapshotTimer = null;
	private DateTime lastSnapshotDate = DateTime.MinValue;

	public void Initialize(bool hotReload)
	{
		this.plugin = (pluginContext.Plugin as Plugin)!;
		this.Config = plugin.Config;

		if (Config.GeneralSettings.LoadMessages)
			this.Logger.LogInformation("Initializing '{0}'", this.GetType().Name);

		// Generate server ID if not set
		if (string.IsNullOrEmpty(Config.LadderSettings.ServerId))
		{
			// Use a combination of server name and a unique identifier
			CurrentServerId = $"{Config.LadderSettings.ServerName}_{Guid.NewGuid().ToString()[..8]}";
			Logger.LogWarning("No server-id configured. Using generated ID: {0}. Please set 'server-id' in config for consistency.", CurrentServerId);
		}
		else
		{
			CurrentServerId = Config.LadderSettings.ServerId;
		}

		// Initialize module parts
		Initialize_Events();
		Initialize_Commands();
		Initialize_Menus();

		// Register/load server and mode
		Task.Run(async () =>
		{
			await RegisterServerAndModeAsync();

			if (hotReload)
			{
				// Load ladder data for existing players
				foreach (var k4player in plugin.K4Players)
				{
					if (k4player.IsValid && k4player.IsPlayer)
					{
						await LoadPlayerLadderDataAsync(k4player);
					}
				}
			}
		});

		// Timer to refresh ladder scores periodically
		if (Config.LadderSettings.ScoreRefreshSeconds > 0)
		{
			scoreRefreshTimer = plugin.AddTimer(Config.LadderSettings.ScoreRefreshSeconds, () =>
			{
				Task.Run(() => RefreshLadderScoresAsync());
			}, TimerFlags.REPEAT);
		}

		// Timer to save daily stats periodically (every 60 seconds)
		dailySaveTimer = plugin.AddTimer(60, () =>
		{
			Task.Run(SaveAllPlayersDailyStatsAsync);
		}, TimerFlags.REPEAT);

		// Timer to check for new UTC day and create snapshots (every 5 minutes)
		// Uses distributed lock so only one server creates snapshots
		dailySnapshotTimer = plugin.AddTimer(300, () =>
		{
			DateTime todayUtc = DateTime.UtcNow.Date;
			if (lastSnapshotDate < todayUtc)
			{
				lastSnapshotDate = todayUtc;
				Task.Run(CreateDailySnapshotsAsync);
			}
		}, TimerFlags.REPEAT);

		// Also run snapshot check and one-time cleanup on initialization
		Task.Run(async () =>
		{
			// Wait for server to fully start
			await Task.Delay(10000);

			// Run one-time cleanup of stale scores (safe to run multiple times, uses lock)
			await plugin.CleanupStaleLadderScoresAsync();

			// Then refresh scores for current mode to recalculate rankings
			await RefreshLadderScoresAsync();

			// Check if we need to create today's snapshot
			DateTime todayUtc = DateTime.UtcNow.Date;
			if (lastSnapshotDate < todayUtc)
			{
				lastSnapshotDate = todayUtc;
				await CreateDailySnapshotsAsync();
			}
		});
	}

	public void Release(bool hotReload)
	{
		if (Config.GeneralSettings.LoadMessages)
			this.Logger.LogInformation("Releasing '{0}'", this.GetType().Name);

		// Save all player data before release
		Task.Run(SaveAllPlayersDailyStatsAsync).Wait();

		// Kill timers
		scoreRefreshTimer?.Kill();
		dailySaveTimer?.Kill();
		dailySnapshotTimer?.Kill();
	}

	/// <summary>
	/// Register this server and ensure the mode exists
	/// </summary>
	private async Task RegisterServerAndModeAsync()
	{
		try
		{
			using var connection = plugin.CreateConnection(Config);
			await connection.OpenAsync();

			// First, ensure the mode exists
			// Uses empty string for variation (not NULL) to ensure uniqueness works correctly
			string modeQuery = $@"
				INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4modes` 
					(`game_type`, `variation`, `display_name`, `short_name`, `is_active`)
				VALUES 
					(@GameType, @Variation, @DisplayName, @ShortName, TRUE)
				ON DUPLICATE KEY UPDATE 
					`display_name` = VALUES(`display_name`),
					`is_active` = TRUE;
				
				SELECT `mode_id`, `game_type`, `variation`, `display_name`, `short_name`, `is_active`
				FROM `{Config.DatabaseSettings.TablePrefix}k4modes`
				WHERE `game_type` = @GameType AND `variation` = @Variation;
			";

			string displayName = GenerateModeDisplayName();
			string shortName = GenerateModeShortName();

			var mode = await connection.QueryFirstOrDefaultAsync<GameMode>(modeQuery, new
			{
				GameType = Config.LadderSettings.GameType.ToLower(),
				// Use empty string instead of null for uniqueness constraint to work correctly
				Variation = string.IsNullOrEmpty(Config.LadderSettings.Variation) ? "" : Config.LadderSettings.Variation.ToLower(),
				DisplayName = displayName,
				ShortName = shortName
			});

			if (mode != null)
			{
				CurrentModeId = mode.ModeId;
				CurrentMode = mode;

				Server.NextFrame(() =>
				{
					Logger.LogInformation("Ladder module registered with mode: {0} (ID: {1})", mode.DisplayName, mode.ModeId);
				});
			}

			// Register the server
			// IP and port are nullable - server_id is the unique identifier
			string serverQuery = $@"
				INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4servers` 
					(`server_id`, `name`, `mode_id`, `is_active`, `last_seen`)
				VALUES 
					(@ServerId, @ServerName, @ModeId, TRUE, NOW())
				ON DUPLICATE KEY UPDATE 
					`name` = VALUES(`name`),
					`mode_id` = VALUES(`mode_id`),
					`is_active` = TRUE,
					`last_seen` = NOW();
			";

			await connection.ExecuteAsync(serverQuery, new
			{
				ServerId = CurrentServerId,
				ServerName = Config.LadderSettings.ServerName,
				ModeId = CurrentModeId
			});
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to register server/mode: {0}", ex.Message);
			});
		}
	}

	private string GenerateModeDisplayName()
	{
		string gameType = Config.LadderSettings.GameType.ToLower();
		string variation = (Config.LadderSettings.Variation ?? "").ToLower();

		string typeName = gameType switch
		{
			"retake" => "Retake",
			"standard" => "",
			"deathmatch" => "Deathmatch",
			_ => char.ToUpper(gameType[0]) + gameType[1..]
		};

		string varName = variation switch
		{
			"deagle" => "Deagle Only",
			"awp" => "AWP Only",
			"pistol" => "Pistol Only",
			"" => "",
			_ => char.ToUpper(variation[0]) + variation[1..]
		};

		if (string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(varName))
			return "Standard";

		if (string.IsNullOrEmpty(typeName))
			return varName;

		if (string.IsNullOrEmpty(varName))
			return typeName;

		return $"{typeName} {varName}";
	}

	private string GenerateModeShortName()
	{
		string gameType = Config.LadderSettings.GameType.ToLower();
		string variation = (Config.LadderSettings.Variation ?? "").ToLower();

		string typeShort = gameType switch
		{
			"retake" => "RT",
			"standard" => "",
			"deathmatch" => "DM",
			_ => gameType[..Math.Min(2, gameType.Length)].ToUpper()
		};

		string varShort = variation switch
		{
			"deagle" => "DE",
			"awp" => "AWP",
			"pistol" => "P",
			"" => "",
			_ => variation[..Math.Min(2, variation.Length)].ToUpper()
		};

		if (string.IsNullOrEmpty(typeShort) && string.IsNullOrEmpty(varShort))
			return "STD";

		if (string.IsNullOrEmpty(typeShort))
			return varShort;

		if (string.IsNullOrEmpty(varShort))
			return typeShort;

		return $"{typeShort}-{varShort}";
	}

	public int GetCurrentModeId() => CurrentModeId;

	public string GetCurrentModeDisplayName() => CurrentMode?.DisplayName ?? "Unknown";
}
