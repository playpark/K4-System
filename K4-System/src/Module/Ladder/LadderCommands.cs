namespace K4System;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using K4System.Models;

public partial class ModuleLadder : IModuleLadder
{
	public void Initialize_Commands()
	{
		CommandSettings commands = Config.CommandSettings;

		// /ladder - show leaderboard
		commands.LadderCommands.ForEach(commandString =>
		{
			plugin.AddCommand($"css_{commandString}", "View the ladder leaderboard", OnCommandLadder);
		});

		// /ladderrank - show your position in all modes
		commands.LadderRankCommands.ForEach(commandString =>
		{
			plugin.AddCommand($"css_{commandString}", "View your ladder rankings", OnCommandLadderRank);
		});

		// /ladderstats - show detailed stats
		commands.LadderStatsCommands.ForEach(commandString =>
		{
			plugin.AddCommand($"css_{commandString}", "View your detailed ladder statistics", OnCommandLadderStats);
		});

		// Admin commands
		plugin.AddCommand("css_ladder_refresh", "Force refresh ladder scores", OnCommandLadderRefresh);
		plugin.AddCommand("css_ladder_cleanup", "Cleanup stale ladder scores (one-time after upgrade)", OnCommandLadderCleanup);
	}

	public void OnCommandLadder(CCSPlayerController? player, CommandInfo info)
	{
		if (!plugin.CommandHelper(player, info, CommandUsage.CLIENT_ONLY))
			return;

		K4Player? k4player = plugin.GetK4Player(player!);
		if (k4player == null)
		{
			info.ReplyToCommand($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.general.loading"]}");
			return;
		}

		if (k4player.cooldownTimer != null)
		{
			info.ReplyToCommand($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.general.cooldown", Config.GeneralSettings.ExpensiveCommandCooldown]}");
			return;
		}

		k4player.cooldownTimer = plugin.AddTimer(Config.GeneralSettings.ExpensiveCommandCooldown, () =>
		{
			k4player.cooldownTimer?.Kill();
			k4player.cooldownTimer = null;
		});

		// Parse arguments
		string periodType = "weekly";
		int modeId = CurrentModeId;
		int limit = 10;

		string? arg1 = info.ArgByIndex(1)?.ToLower();
		string? arg2 = info.ArgByIndex(2)?.ToLower();

		// Check if arg1 is a period type
		if (arg1 == "daily" || arg1 == "weekly" || arg1 == "monthly" || arg1 == "alltime")
		{
			periodType = arg1;
			if (int.TryParse(arg2, out int parsedLimit))
				limit = Math.Clamp(parsedLimit, 1, 25);
		}
		else if (int.TryParse(arg1, out int parsedLimit))
		{
			limit = Math.Clamp(parsedLimit, 1, 25);
		}

		Task.Run(async () =>
		{
			var entries = await GetLeaderboardAsync(modeId, periodType, limit);
			string modeName = CurrentMode?.DisplayName ?? "Unknown";
			string periodDisplay = char.ToUpper(periodType[0]) + periodType[1..];

			Server.NextFrame(() =>
			{
				if (!k4player.IsValid || !k4player.IsPlayer)
					return;

				if (entries.Count == 0)
				{
					player!.PrintToChat($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.ladder.nodata"]}");
					return;
				}

				player!.PrintToChat($" {plugin.Localizer["k4.ladder.top.header", modeName, periodDisplay]}");

				for (int i = 0; i < entries.Count; i++)
				{
					var entry = entries[i];
					string rankChange = FormatRankChange(entry.RankChange);

					if (entry.SteamId == k4player.SteamID)
					{
						player.PrintToChat($" {plugin.Localizer["k4.ladder.top.you", entry.Position, entry.Points, entry.Kills, entry.Deaths, entry.KDR, rankChange]}");
					}
					else
					{
						player.PrintToChat($" {plugin.Localizer["k4.ladder.top.line", entry.Position, entry.Name, entry.Points, entry.Kills, entry.Deaths, entry.KDR, rankChange]}");
					}
				}
			});
		});
	}

	public void OnCommandLadderRank(CCSPlayerController? player, CommandInfo info)
	{
		if (!plugin.CommandHelper(player, info, CommandUsage.CLIENT_ONLY))
			return;

		K4Player? k4player = plugin.GetK4Player(player!);
		if (k4player == null)
		{
			info.ReplyToCommand($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.general.loading"]}");
			return;
		}

		if (k4player.cooldownTimer != null)
		{
			info.ReplyToCommand($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.general.cooldown", Config.GeneralSettings.ExpensiveCommandCooldown]}");
			return;
		}

		k4player.cooldownTimer = plugin.AddTimer(Config.GeneralSettings.ExpensiveCommandCooldown, () =>
		{
			k4player.cooldownTimer?.Kill();
			k4player.cooldownTimer = null;
		});

		string periodType = info.ArgByIndex(1)?.ToLower() ?? "weekly";
		if (periodType != "daily" && periodType != "weekly" && periodType != "monthly" && periodType != "alltime")
			periodType = "weekly";

		string periodDisplay = char.ToUpper(periodType[0]) + periodType[1..];

		Task.Run(async () =>
		{
			var summaries = await GetPlayerModesSummaryAsync(k4player.SteamID, periodType);

			Server.NextFrame(() =>
			{
				if (!k4player.IsValid || !k4player.IsPlayer)
					return;

				if (summaries.Count == 0)
				{
					player!.PrintToChat($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.ladder.nodata"]}");
					return;
				}

				player!.PrintToChat($" {plugin.Localizer["k4.ladder.rank.header", periodDisplay]}");

				// Group by game type
				var grouped = summaries.GroupBy(s => s.GameType);

				foreach (var group in grouped)
				{
					string gameType = char.ToUpper(group.Key[0]) + group.Key[1..];
					
					foreach (var summary in group)
					{
						string rankChange = FormatRankChange(summary.RankChange);
						string displayName = summary.Variation != null 
							? $"{gameType} {char.ToUpper(summary.Variation[0]) + summary.Variation[1..]}"
							: gameType;

						player.PrintToChat($" {plugin.Localizer["k4.ladder.rank.mode", displayName, summary.Position, summary.TotalPlayers, summary.Points, rankChange]}");
					}
				}
			});
		});
	}

	public void OnCommandLadderStats(CCSPlayerController? player, CommandInfo info)
	{
		if (!plugin.CommandHelper(player, info, CommandUsage.CLIENT_ONLY))
			return;

		K4Player? k4player = plugin.GetK4Player(player!);
		if (k4player == null)
		{
			info.ReplyToCommand($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.general.loading"]}");
			return;
		}

		if (k4player.cooldownTimer != null)
		{
			info.ReplyToCommand($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.general.cooldown", Config.GeneralSettings.ExpensiveCommandCooldown]}");
			return;
		}

		k4player.cooldownTimer = plugin.AddTimer(Config.GeneralSettings.ExpensiveCommandCooldown, () =>
		{
			k4player.cooldownTimer?.Kill();
			k4player.cooldownTimer = null;
		});

		string periodType = info.ArgByIndex(1)?.ToLower() ?? "weekly";
		if (periodType != "daily" && periodType != "weekly" && periodType != "monthly" && periodType != "alltime")
			periodType = "weekly";

		int modeId = CurrentModeId;

		Task.Run(async () =>
		{
			var stats = await GetPlayerLadderStatsAsync(k4player.SteamID, modeId, periodType);

			Server.NextFrame(() =>
			{
				if (!k4player.IsValid || !k4player.IsPlayer)
					return;

				if (stats == null)
				{
					player!.PrintToChat($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.ladder.nodata"]}");
					return;
				}

				string periodDisplay = char.ToUpper(periodType[0]) + periodType[1..];
				string rankChange = FormatRankChange(stats.PositionChange);

				player!.PrintToChat($" {plugin.Localizer["k4.ladder.stats.header", stats.ModeName, periodDisplay]}");
				player.PrintToChat($" {plugin.Localizer["k4.ladder.stats.position", stats.Position, stats.TotalPlayers, rankChange]}");
				player.PrintToChat($" {plugin.Localizer["k4.ladder.stats.points", stats.Points]}");
				player.PrintToChat($" {plugin.Localizer["k4.ladder.stats.combat", stats.Kills, stats.Deaths, stats.Assists, stats.KDR]}");
				player.PrintToChat($" {plugin.Localizer["k4.ladder.stats.rounds", stats.RoundsPlayed, stats.RoundsWon, stats.WinRate]}");
				player.PrintToChat($" {plugin.Localizer["k4.ladder.stats.activity", stats.DaysActive, stats.CurrentStreak]}");

				if (stats.BestDayPoints > 0)
				{
					player.PrintToChat($" {plugin.Localizer["k4.ladder.stats.best", stats.BestDayPoints, stats.PointsPerDay]}");
				}
			});
		});
	}

	public void OnCommandLadderRefresh(CCSPlayerController? player, CommandInfo info)
	{
		if (!plugin.CommandHelper(player, info, CommandUsage.CLIENT_AND_SERVER, 0, "", "@k4system/admin"))
			return;

		// Check if "all" argument was passed to refresh all modes
		bool refreshAll = info.ArgByIndex(1)?.ToLower() == "all";
		string scope = refreshAll ? "all modes" : "current mode";

		info.ReplyToCommand($" {plugin.Localizer["k4.general.prefix"]} Refreshing ladder scores ({scope})...");

		Task.Run(async () =>
		{
			await SaveAllPlayersDailyStatsAsync();
			await RefreshLadderScoresAsync(allModes: refreshAll);

			Server.NextFrame(() =>
			{
				if (player != null && player.IsValid)
				{
					player.PrintToChat($" {plugin.Localizer["k4.general.prefix"]} Ladder scores refreshed ({scope})!");
				}
				else
				{
					Server.PrintToConsole($"[K4-Ladder] Ladder scores refreshed ({scope})!");
				}
			});
		});
	}

	public void OnCommandLadderCleanup(CCSPlayerController? player, CommandInfo info)
	{
		if (!plugin.CommandHelper(player, info, CommandUsage.CLIENT_AND_SERVER, 0, "", "@k4system/admin"))
			return;

		info.ReplyToCommand($" {plugin.Localizer["k4.general.prefix"]} Running stale ladder scores cleanup...");

		Task.Run(async () =>
		{
			await plugin.CleanupStaleLadderScoresAsync();

			Server.NextFrame(() =>
			{
				if (player != null && player.IsValid)
				{
					player.PrintToChat($" {plugin.Localizer["k4.general.prefix"]} Ladder cleanup complete! Run css_ladder_refresh to recalculate rankings.");
				}
				else
				{
					Server.PrintToConsole("[K4-Ladder] Ladder cleanup complete! Run css_ladder_refresh to recalculate rankings.");
				}
			});
		});
	}

	private string FormatRankChange(int? change)
	{
		if (change == null)
			return plugin.Localizer["k4.ladder.rank.change.new"];

		if (change > 0)
			return plugin.Localizer["k4.ladder.rank.change.up", change];

		if (change < 0)
			return plugin.Localizer["k4.ladder.rank.change.down", change];

		return "=";
	}
}
