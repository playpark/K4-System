using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;

namespace K4System;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using K4System.Models;

public partial class ModuleLadder : IModuleLadder
{
	public void Initialize_Events()
	{
		// Hook into player activation to load ladder data
		plugin.RegisterEventHandler((EventPlayerActivate @event, GameEventInfo info) =>
		{
			CCSPlayerController? player = @event.Userid;
			if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
				return HookResult.Continue;

			K4Player? k4player = plugin.GetK4Player(player);
			if (k4player == null)
				return HookResult.Continue;

			if (!AdminManager.PlayerHasPermissions(player, "@css/vip"))
			{
				k4player.Controller.PrintToChat($" {plugin.Localizer["k4.ladder.warning"]} {plugin.Localizer["k4.ladder.connect.intevip"]}");
			}
			
			// Load ladder data async and show connect message
			Task.Run(async () =>
			{
				await LoadPlayerLadderDataAsync(k4player);
				
				// Show rank change message on connect
				if (Config.LadderSettings.ShowRankChangeOnConnect && k4player.ladderData != null)
				{
					await ShowConnectRankMessageAsync(k4player);
				}
			});

			return HookResult.Continue;
		});

		// Hook into player disconnect to save ladder data
		plugin.RegisterEventHandler((EventPlayerDisconnect @event, GameEventInfo info) =>
		{
			CCSPlayerController? player = @event.Userid;
			if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
				return HookResult.Continue;

			K4Player? k4player = plugin.GetK4Player(player);
			if (k4player == null || k4player.ladderData == null)
				return HookResult.Continue;

			// Save ladder data async
			Task.Run(() => SavePlayerDailyStatsAsync(k4player));

			return HookResult.Continue;
		});

		// Hook into round end to increment rounds played
		plugin.RegisterEventHandler((EventRoundEnd @event, GameEventInfo info) =>
		{
			int winnerTeam = @event.Winner;

			foreach (var k4player in plugin.K4Players.ToList())
			{
				if (!k4player.IsValid || !k4player.IsPlayer || k4player.ladderData == null)
					continue;

				// Check if day changed
				EnsureCorrectDay(k4player);

				var stats = k4player.ladderData.TodayStats;
				stats.RoundsPlayed++;

				if (k4player.Controller.TeamNum == winnerTeam)
				{
					stats.RoundsWon++;
				}

				stats.LastUpdate = DateTime.UtcNow;
			}

			// Save all players at round end
			Task.Run(SaveAllPlayersDailyStatsAsync);

			return HookResult.Continue;
		});
	}

	/// <summary>
	/// Called when a player's points change
	/// </summary>
	public void OnPointsChanged(K4Player k4player, int delta, string reason)
	{
		if (k4player.ladderData == null || CurrentModeId < 0)
			return;

		EnsureCorrectDay(k4player);

		var stats = k4player.ladderData.TodayStats;

		if (delta > 0)
		{
			stats.PointsEarned += delta;
		}
		else if (delta < 0)
		{
			stats.PointsLost += Math.Abs(delta);
		}

		stats.LastUpdate = DateTime.UtcNow;
	}

	/// <summary>
	/// Called when a player gets a kill
	/// </summary>
	public void OnPlayerKill(K4Player killer, K4Player? victim, bool headshot)
	{
		if (killer.ladderData == null || CurrentModeId < 0)
			return;

		EnsureCorrectDay(killer);

		var stats = killer.ladderData.TodayStats;
		stats.Kills++;

		if (headshot)
		{
			stats.Headshots++;
		}

		stats.LastUpdate = DateTime.UtcNow;
	}

	/// <summary>
	/// Called when a player dies
	/// </summary>
	public void OnPlayerDeath(K4Player victim)
	{
		if (victim.ladderData == null || CurrentModeId < 0)
			return;

		EnsureCorrectDay(victim);

		victim.ladderData.TodayStats.Deaths++;
		victim.ladderData.TodayStats.LastUpdate = DateTime.UtcNow;
	}

	/// <summary>
	/// Called when a round ends for a player
	/// </summary>
	public void OnRoundEnd(K4Player k4player, bool won)
	{
		// Handled in event handler above
	}

	/// <summary>
	/// Called when a player gets MVP
	/// </summary>
	public void OnMVP(K4Player k4player)
	{
		if (k4player.ladderData == null || CurrentModeId < 0)
			return;

		EnsureCorrectDay(k4player);

		k4player.ladderData.TodayStats.MVPs++;
		k4player.ladderData.TodayStats.LastUpdate = DateTime.UtcNow;
	}

	/// <summary>
	/// Called when a player gets an assist
	/// </summary>
	public void OnPlayerAssist(K4Player assister)
	{
		if (assister.ladderData == null || CurrentModeId < 0)
			return;

		EnsureCorrectDay(assister);

		assister.ladderData.TodayStats.Assists++;
		assister.ladderData.TodayStats.LastUpdate = DateTime.UtcNow;
	}

	/// <summary>
	/// Ensure player's stats are for the correct day (reset if day changed)
	/// </summary>
	private void EnsureCorrectDay(K4Player k4player)
	{
		if (k4player.ladderData == null)
			return;

		if (k4player.ladderData.TodayStats.DayUtc != DateTime.UtcNow.Date)
		{
			// Day changed - snapshot yesterday's stats before reset to avoid race condition
			// The async save could run after reset otherwise, losing data
			var yesterdayStats = k4player.ladderData.TodayStats;
			var yesterdayDate = yesterdayStats.DayUtc;

			// Create fresh stats for the new day
			k4player.ladderData.TodayStats = new DailyStatsCache();

			// Save yesterday's stats in the background (using the snapshot)
			Task.Run(() => SaveDailyStatsSnapshotAsync(k4player.SteamID, yesterdayStats, yesterdayDate));
		}
	}

	/// <summary>
	/// Show rank change message on connect
	/// </summary>
	private async Task ShowConnectRankMessageAsync(K4Player k4player)
	{
		try
		{
			// Small delay to ensure player is fully connected
			await Task.Delay(2000);

			if (!k4player.IsValid || !k4player.IsPlayer)
				return;

			var summaries = await GetPlayerModesSummaryAsync(k4player.SteamID, "weekly");

			if (summaries.Count == 0)
				return;

			Server.NextFrame(() =>
			{
				if (!k4player.IsValid || !k4player.IsPlayer)
					return;

				k4player.Controller.PrintToChat($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.ladder.connect.welcome"]}");

				foreach (var summary in summaries.Take(3)) // Show top 3 modes
				{
					string rankChange = FormatRankChangeConnect(summary.RankChange);
					k4player.Controller.PrintToChat($" {plugin.Localizer["k4.ladder.connect.rankchange", summary.DisplayName, summary.Position, summary.Points, rankChange]}");
				}
			});
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to show connect rank message: {0}", ex.Message);
			});
		}
	}

	private string FormatRankChangeConnect(int? change)
	{
		if (change == null)
			return plugin.Localizer["k4.ladder.rank.change.new"];

		if (change > 0)
			return plugin.Localizer["k4.ladder.rank.change.up", change];

		if (change < 0)
			return plugin.Localizer["k4.ladder.rank.change.down", change];

		return "";
	}
}
