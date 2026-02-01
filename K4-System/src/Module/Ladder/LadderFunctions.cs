namespace K4System;

using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;
using K4System.Models;
using Dapper;

public partial class ModuleLadder : IModuleLadder
{
	/// <summary>
	/// Load ladder data for a player when they connect
	/// </summary>
	public async Task LoadPlayerLadderDataAsync(K4Player k4player)
	{
		try
		{
			// Initialize ladder data
			k4player.ladderData = new LadderData();

			// Check if we need to reset for a new day
			if (k4player.ladderData.TodayStats.DayUtc != DateTime.UtcNow.Date)
			{
				k4player.ladderData.TodayStats.ResetForNewDay();
			}

			using var connection = plugin.CreateConnection(Config);
			await connection.OpenAsync();

			// Load today's stats if they exist
			string todayQuery = $@"
				SELECT 
					`points_earned`, `points_lost`, `points_net`,
					`kills`, `deaths`, `assists`, `headshots`, `mvps`,
					`rounds_played`, `rounds_won`, `playtime_seconds`
				FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_daily`
				WHERE `steam_id` = @SteamId 
					AND `day_utc` = @Today 
					AND `server_id` = @ServerId;
			";

			var todayStats = await connection.QueryFirstOrDefaultAsync<dynamic>(todayQuery, new
			{
				SteamId = k4player.SteamID.ToString(),
				Today = DateTime.UtcNow.Date,
				ServerId = CurrentServerId
			});

			if (todayStats != null)
			{
				k4player.ladderData.TodayStats.PointsEarned = (int)todayStats.points_earned;
				k4player.ladderData.TodayStats.PointsLost = (int)todayStats.points_lost;
				k4player.ladderData.TodayStats.Kills = (int)todayStats.kills;
				k4player.ladderData.TodayStats.Deaths = (int)todayStats.deaths;
				k4player.ladderData.TodayStats.Assists = (int)todayStats.assists;
				k4player.ladderData.TodayStats.Headshots = (int)todayStats.headshots;
				k4player.ladderData.TodayStats.MVPs = (int)todayStats.mvps;
				k4player.ladderData.TodayStats.RoundsPlayed = (int)todayStats.rounds_played;
				k4player.ladderData.TodayStats.RoundsWon = (int)todayStats.rounds_won;
				k4player.ladderData.TodayStats.PlaytimeSeconds = (int)todayStats.playtime_seconds;
			}

			// Load player's positions on all modes they've played (for weekly period)
			string positionsQuery = $@"
				SELECT 
					ls.`mode_id`,
					m.`display_name` as `mode_name`,
					ls.`points`,
					ls.`rank_position`,
					ls.`rank_change`,
					(SELECT COUNT(DISTINCT `steam_id`) FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores` 
					 WHERE `mode_id` = ls.`mode_id` AND `period_type` = 'weekly') as `total_players`
				FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores` ls
				JOIN `{Config.DatabaseSettings.TablePrefix}k4modes` m ON ls.`mode_id` = m.`mode_id`
				WHERE ls.`steam_id` = @SteamId AND ls.`period_type` = 'weekly';
			";

			var positions = await connection.QueryAsync<dynamic>(positionsQuery, new
			{
				SteamId = k4player.SteamID.ToString()
			});

			foreach (var pos in positions)
			{
				k4player.ladderData.ModePositions[(int)pos.mode_id] = new LadderPosition
				{
					ModeId = (int)pos.mode_id,
					ModeName = (string)pos.mode_name,
					PeriodType = "weekly",
					Points = (int)pos.points,
					RankPosition = pos.rank_position != null ? (int)pos.rank_position : 0,
					RankChange = pos.rank_change != null ? (int?)pos.rank_change : null,
					TotalPlayers = (int)pos.total_players
				};
			}
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to load ladder data for player {0}: {1}", k4player.PlayerName, ex.Message);
			});
		}
	}

	/// <summary>
	/// Save a player's daily stats to the database
	/// </summary>
	public async Task SavePlayerDailyStatsAsync(K4Player k4player)
	{
		if (k4player.ladderData == null || CurrentModeId < 0)
			return;

		var stats = k4player.ladderData.TodayStats;
		await SaveDailyStatsSnapshotAsync(k4player.SteamID, stats, stats.DayUtc);
		k4player.ladderData.LastSaved = DateTime.UtcNow;
	}

	/// <summary>
	/// Save a snapshot of daily stats (used for day rollover to avoid race conditions)
	/// </summary>
	public async Task SaveDailyStatsSnapshotAsync(ulong steamId, DailyStatsCache stats, DateTime dayUtc)
	{
		if (CurrentModeId < 0)
			return;

		try
		{
			using var connection = plugin.CreateConnection(Config);
			await connection.OpenAsync();

			string upsertQuery = $@"
				INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4ladder_daily`
					(`steam_id`, `day_utc`, `mode_id`, `server_id`, 
					 `points_earned`, `points_lost`, `points_net`,
					 `kills`, `deaths`, `assists`, `headshots`, `mvps`,
					 `rounds_played`, `rounds_won`, `playtime_seconds`)
				VALUES
					(@SteamId, @DayUtc, @ModeId, @ServerId,
					 @PointsEarned, @PointsLost, @PointsNet,
					 @Kills, @Deaths, @Assists, @Headshots, @MVPs,
					 @RoundsPlayed, @RoundsWon, @PlaytimeSeconds)
				ON DUPLICATE KEY UPDATE
					`points_earned` = @PointsEarned,
					`points_lost` = @PointsLost,
					`points_net` = @PointsNet,
					`kills` = @Kills,
					`deaths` = @Deaths,
					`assists` = @Assists,
					`headshots` = @Headshots,
					`mvps` = @MVPs,
					`rounds_played` = @RoundsPlayed,
					`rounds_won` = @RoundsWon,
					`playtime_seconds` = @PlaytimeSeconds;
			";

			await connection.ExecuteAsync(upsertQuery, new
			{
				SteamId = steamId.ToString(),
				DayUtc = dayUtc,
				ModeId = CurrentModeId,
				ServerId = CurrentServerId,
				stats.PointsEarned,
				stats.PointsLost,
				PointsNet = stats.PointsNet,
				stats.Kills,
				stats.Deaths,
				stats.Assists,
				stats.Headshots,
				stats.MVPs,
				stats.RoundsPlayed,
				stats.RoundsWon,
				stats.PlaytimeSeconds
			});
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to save ladder daily stats snapshot for {0}: {1}", steamId, ex.Message);
			});
		}
	}

	/// <summary>
	/// Save all players' daily stats
	/// </summary>
	public async Task SaveAllPlayersDailyStatsAsync()
	{
		foreach (var k4player in plugin.K4Players.ToList())
		{
			if (k4player.IsValid && k4player.IsPlayer && k4player.ladderData != null)
			{
				await SavePlayerDailyStatsAsync(k4player);
			}
		}
	}

	/// <summary>
	/// Refresh ladder scores for the current mode (default) or all modes
	/// For scalability, servers only refresh their own mode by default
	/// </summary>
	public async Task RefreshLadderScoresAsync(bool allModes = false)
	{
		try
		{
			using var connection = plugin.CreateConnection(Config);
			await connection.OpenAsync();

			IEnumerable<int> modeIds;

			if (allModes)
			{
				// Get all active modes (used by admin command)
				string modesQuery = $@"SELECT `mode_id` FROM `{Config.DatabaseSettings.TablePrefix}k4modes` WHERE `is_active` = TRUE;";
				modeIds = await connection.QueryAsync<int>(modesQuery);
			}
			else
			{
				// Only refresh current server's mode (default for timer - scalability)
				if (CurrentModeId < 0)
					return;
				modeIds = new[] { CurrentModeId };
			}

			foreach (var modeId in modeIds)
			{
				foreach (var period in Config.LadderSettings.LadderPeriods)
				{
					await RefreshLadderScoresForPeriodAsync(connection, modeId, period);
				}
			}
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to refresh ladder scores: {0}", ex.Message);
			});
		}
	}

	private async Task RefreshLadderScoresForPeriodAsync(MySqlConnector.MySqlConnection connection, int modeId, string periodType)
	{
		// Calculate UTC cutoff date (inclusive of today)
		int days = periodType switch
		{
			"daily" => 1,
			"weekly" => 7,
			"monthly" => 30,
			"alltime" => 36500, // ~100 years
			_ => 7
		};

		// Correct rolling window: today minus (days-1) to include exactly N days
		DateTime cutoffDate = DateTime.UtcNow.Date.AddDays(-(days - 1));

		// Use distributed lock to prevent concurrent refresh from multiple servers
		string lockName = $"k4ladder_refresh_{modeId}_{periodType}";
		string lockQuery = $"SELECT GET_LOCK(@LockName, 0) as acquired;";
		var lockResult = await connection.QueryFirstOrDefaultAsync<dynamic>(lockQuery, new { LockName = lockName });

		if (lockResult?.acquired != 1)
		{
			// Another server is refreshing this mode/period, skip
			return;
		}

		try
		{
			// Atomic rebuild: delete stale players and reinsert in a transaction
			using var transaction = await connection.BeginTransactionAsync();

			try
			{
				// Delete all existing scores for this mode/period (atomic rebuild)
				string deleteQuery = $@"
					DELETE FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores`
					WHERE `mode_id` = @ModeId AND `period_type` = @PeriodType;
				";
				await connection.ExecuteAsync(deleteQuery, new { ModeId = modeId, PeriodType = periodType }, transaction);

				// Aggregate data from daily stats and insert fresh scores
				string aggregateQuery = $@"
					INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4ladder_scores`
						(`mode_id`, `period_type`, `steam_id`, `name`, `points`, `kills`, `deaths`, 
						 `assists`, `headshots`, `rounds_played`, `days_active`, `rank_position`, `rank_change`, `updated_at`)
					SELECT 
						d.`mode_id`,
						@PeriodType as `period_type`,
						d.`steam_id`,
						COALESCE(r.`name`, 'Unknown') as `name`,
						SUM(d.`points_net`) as `points`,
						SUM(d.`kills`) as `kills`,
						SUM(d.`deaths`) as `deaths`,
						SUM(d.`assists`) as `assists`,
						SUM(d.`headshots`) as `headshots`,
						SUM(d.`rounds_played`) as `rounds_played`,
						COUNT(DISTINCT d.`day_utc`) as `days_active`,
						NULL as `rank_position`,
						NULL as `rank_change`,
						NOW() as `updated_at`
					FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_daily` d
					LEFT JOIN `{Config.DatabaseSettings.TablePrefix}k4ranks` r ON d.`steam_id` = r.`steam_id`
					WHERE d.`mode_id` = @ModeId
						AND d.`day_utc` >= @CutoffDate
					GROUP BY d.`mode_id`, d.`steam_id`;
				";

				await connection.ExecuteAsync(aggregateQuery, new
				{
					ModeId = modeId,
					PeriodType = periodType,
					CutoffDate = cutoffDate
				}, transaction);

				// Update rank positions using ROW_NUMBER() window function (MySQL 8.0+)
				// With deterministic tie-breakers: points DESC, kills DESC, deaths ASC, steam_id ASC
				string rankQuery = $@"
					UPDATE `{Config.DatabaseSettings.TablePrefix}k4ladder_scores` ls
					JOIN (
						SELECT 
							`steam_id`,
							ROW_NUMBER() OVER (ORDER BY `points` DESC, `kills` DESC, `deaths` ASC, `steam_id` ASC) as new_rank
						FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores`
						WHERE `mode_id` = @ModeId AND `period_type` = @PeriodType
					) ranked ON ls.`steam_id` = ranked.`steam_id`
					SET ls.`rank_position` = ranked.new_rank
					WHERE ls.`mode_id` = @ModeId AND ls.`period_type` = @PeriodType;
				";

				await connection.ExecuteAsync(rankQuery, new { ModeId = modeId, PeriodType = periodType }, transaction);

				await transaction.CommitAsync();
			}
			catch
			{
				await transaction.RollbackAsync();
				throw;
			}
		}
		finally
		{
			// Release the lock
			string releaseLockQuery = "SELECT RELEASE_LOCK(@LockName);";
			await connection.ExecuteAsync(releaseLockQuery, new { LockName = lockName });
		}
	}

	/// <summary>
	/// Get leaderboard for a specific mode and period
	/// </summary>
	public async Task<List<LadderEntry>> GetLeaderboardAsync(int modeId, string periodType, int limit = 10)
	{
		// Check cache first
		string cacheKey = $"{modeId}_{periodType}";
		if (LeaderboardCaches.TryGetValue(cacheKey, out var cached) && 
			!cached.IsExpired(Config.LadderSettings.LeaderboardCacheSeconds))
		{
			return cached.Entries.Take(limit).ToList();
		}

		try
		{
			using var connection = plugin.CreateConnection(Config);
			await connection.OpenAsync();

			string query = $@"
				SELECT 
					`rank_position` as `Position`,
					`steam_id` as `SteamId`,
					`name` as `Name`,
					`points` as `Points`,
					`kills` as `Kills`,
					`deaths` as `Deaths`,
					`assists` as `Assists`,
					`headshots` as `Headshots`,
					`rounds_played` as `RoundsPlayed`,
					`days_active` as `DaysActive`,
					`rank_change` as `RankChange`
				FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores`
				WHERE `mode_id` = @ModeId AND `period_type` = @PeriodType
				ORDER BY `points` DESC
				LIMIT @Limit;
			";

			var entries = (await connection.QueryAsync<LadderEntry>(query, new
			{
				ModeId = modeId,
				PeriodType = periodType,
				Limit = Math.Max(limit, 100) // Cache more than requested
			})).ToList();

			// Get total players
			string countQuery = $@"
				SELECT COUNT(*) FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores`
				WHERE `mode_id` = @ModeId AND `period_type` = @PeriodType;
			";
			int totalPlayers = await connection.QueryFirstOrDefaultAsync<int>(countQuery, new { ModeId = modeId, PeriodType = periodType });

			// Update cache
			LeaderboardCaches[cacheKey] = new LeaderboardCache
			{
				ModeId = modeId,
				PeriodType = periodType,
				Entries = entries,
				CachedAt = DateTime.UtcNow,
				TotalPlayers = totalPlayers
			};

			return entries.Take(limit).ToList();
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to get leaderboard: {0}", ex.Message);
			});
			return new List<LadderEntry>();
		}
	}

	/// <summary>
	/// Get all modes a player has played
	/// </summary>
	public async Task<List<PlayerModeSummary>> GetPlayerModesSummaryAsync(ulong steamId, string periodType = "weekly")
	{
		try
		{
			using var connection = plugin.CreateConnection(Config);
			await connection.OpenAsync();

			string query = $@"
				SELECT 
					ls.`mode_id` as `ModeId`,
					m.`game_type` as `GameType`,
					m.`variation` as `Variation`,
					m.`display_name` as `DisplayName`,
					ls.`points` as `Points`,
					ls.`rank_position` as `Position`,
					ls.`rank_change` as `RankChange`,
					(SELECT COUNT(*) FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores` 
					 WHERE `mode_id` = ls.`mode_id` AND `period_type` = @PeriodType) as `TotalPlayers`
				FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores` ls
				JOIN `{Config.DatabaseSettings.TablePrefix}k4modes` m ON ls.`mode_id` = m.`mode_id`
				WHERE ls.`steam_id` = @SteamId AND ls.`period_type` = @PeriodType
				ORDER BY m.`game_type`, m.`variation`;
			";

			var summaries = (await connection.QueryAsync<PlayerModeSummary>(query, new
			{
				SteamId = steamId.ToString(),
				PeriodType = periodType
			})).ToList();

			return summaries;
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to get player modes summary: {0}", ex.Message);
			});
			return new List<PlayerModeSummary>();
		}
	}

	/// <summary>
	/// Get detailed ladder stats for a player in a specific mode
	/// </summary>
	public async Task<LadderPlayerStats?> GetPlayerLadderStatsAsync(ulong steamId, int modeId, string periodType = "weekly")
	{
		try
		{
			using var connection = plugin.CreateConnection(Config);
			await connection.OpenAsync();

			int days = periodType switch
			{
				"daily" => 1,
				"weekly" => 7,
				"monthly" => 30,
				"alltime" => 36500,
				_ => 7
			};

			// Use UTC cutoff date for correct rolling window
			DateTime cutoffDate = DateTime.UtcNow.Date.AddDays(-(days - 1));

			string query = $@"
				SELECT 
					ls.`mode_id` as `ModeId`,
					m.`display_name` as `ModeName`,
					@PeriodType as `PeriodType`,
					ls.`points` as `Points`,
					ls.`rank_position` as `Position`,
					ls.`rank_change` as `PositionChange`,
					ls.`kills` as `Kills`,
					ls.`deaths` as `Deaths`,
					ls.`assists` as `Assists`,
					ls.`headshots` as `Headshots`,
					ls.`rounds_played` as `RoundsPlayed`,
					COALESCE(SUM(d.`rounds_won`), 0) as `RoundsWon`,
					ls.`days_active` as `DaysActive`,
					(SELECT COUNT(*) FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores` 
					 WHERE `mode_id` = @ModeId AND `period_type` = @PeriodType) as `TotalPlayers`,
					-- Best day: SUM points_net per day across all servers, then take MAX
					(SELECT MAX(day_total) FROM (
						SELECT SUM(`points_net`) as day_total
						FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_daily`
						WHERE `steam_id` = @SteamId AND `mode_id` = @ModeId 
							AND `day_utc` >= @CutoffDate
						GROUP BY `day_utc`
					) best_days) as `BestDayPoints`
				FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores` ls
				JOIN `{Config.DatabaseSettings.TablePrefix}k4modes` m ON ls.`mode_id` = m.`mode_id`
				LEFT JOIN `{Config.DatabaseSettings.TablePrefix}k4ladder_daily` d 
					ON ls.`steam_id` = d.`steam_id` AND ls.`mode_id` = d.`mode_id`
					AND d.`day_utc` >= @CutoffDate
				WHERE ls.`steam_id` = @SteamId 
					AND ls.`mode_id` = @ModeId 
					AND ls.`period_type` = @PeriodType
				GROUP BY ls.`mode_id`, ls.`steam_id`;
			";

			var stats = await connection.QueryFirstOrDefaultAsync<LadderPlayerStats>(query, new
			{
				SteamId = steamId.ToString(),
				ModeId = modeId,
				PeriodType = periodType,
				CutoffDate = cutoffDate
			});

			if (stats != null)
			{
				// Calculate current streak
				stats.CurrentStreak = await CalculatePlayerStreakAsync(connection, steamId, modeId, days);
			}

			return stats;
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to get player ladder stats: {0}", ex.Message);
			});
			return null;
		}
	}

	private async Task<int> CalculatePlayerStreakAsync(MySqlConnector.MySqlConnection connection, ulong steamId, int modeId, int maxDays)
	{
		// Use UTC cutoff date and DISTINCT to avoid duplicates from multi-server play
		DateTime cutoffDate = DateTime.UtcNow.Date.AddDays(-maxDays);

		string query = $@"
			SELECT DISTINCT `day_utc`
			FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_daily`
			WHERE `steam_id` = @SteamId AND `mode_id` = @ModeId
				AND `day_utc` >= @CutoffDate
			ORDER BY `day_utc` DESC;
		";

		var days = (await connection.QueryAsync<DateTime>(query, new
		{
			SteamId = steamId.ToString(),
			ModeId = modeId,
			CutoffDate = cutoffDate
		})).ToList();

		if (days.Count == 0)
			return 0;

		int streak = 0;
		DateTime expected = DateTime.UtcNow.Date;

		foreach (var day in days)
		{
			if (day.Date == expected)
			{
				streak++;
				expected = expected.AddDays(-1);
			}
			else if (day.Date == expected.AddDays(-1))
			{
				// Might have skipped today, still count
				streak++;
				expected = day.Date.AddDays(-1);
			}
			else
			{
				break;
			}
		}

		return streak;
	}

	/// <summary>
	/// Get all active game modes
	/// </summary>
	public async Task<List<GameMode>> GetAllModesAsync()
	{
		try
		{
			using var connection = plugin.CreateConnection(Config);
			await connection.OpenAsync();

			string query = $@"
				SELECT `mode_id` as `ModeId`, `game_type` as `GameType`, `variation` as `Variation`,
					   `display_name` as `DisplayName`, `short_name` as `ShortName`, `is_active` as `IsActive`
				FROM `{Config.DatabaseSettings.TablePrefix}k4modes`
				WHERE `is_active` = TRUE
				ORDER BY `game_type`, `variation`;
			";

			return (await connection.QueryAsync<GameMode>(query)).ToList();
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to get all modes: {0}", ex.Message);
			});
			return new List<GameMode>();
		}
	}

	/// <summary>
	/// Create daily rank snapshots (called once per day)
	/// Uses distributed lock to ensure only one server creates snapshots
	/// </summary>
	public async Task CreateDailySnapshotsAsync()
	{
		try
		{
			using var connection = plugin.CreateConnection(Config);
			await connection.OpenAsync();

			// Use distributed lock to prevent concurrent snapshot creation
			string lockName = "k4ladder_daily_snapshot";
			string lockQuery = "SELECT GET_LOCK(@LockName, 0) as acquired;";
			var lockResult = await connection.QueryFirstOrDefaultAsync<dynamic>(lockQuery, new { LockName = lockName });

			if (lockResult?.acquired != 1)
			{
				// Another server is creating snapshots, skip
				return;
			}

			try
			{
				// Use UTC dates consistently
				DateTime todayUtc = DateTime.UtcNow.Date;
				DateTime yesterdayUtc = todayUtc.AddDays(-1);
				DateTime snapshotCutoff = todayUtc.AddDays(-Config.LadderSettings.SnapshotRetentionDays);
				DateTime dailyCutoff = todayUtc.AddDays(-Config.LadderSettings.DailyRetentionDays);

				// Check if we already created today's snapshot (idempotency)
				string checkQuery = $@"
					SELECT COUNT(*) FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_snapshots`
					WHERE `snapshot_date` = @TodayUtc LIMIT 1;
				";
				var existingCount = await connection.QueryFirstOrDefaultAsync<int>(checkQuery, new { TodayUtc = todayUtc });

				if (existingCount > 0)
				{
					// Already created today's snapshots
					return;
				}

				// Create today's snapshot from current scores
				string snapshotQuery = $@"
					INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4ladder_snapshots`
						(`mode_id`, `period_type`, `steam_id`, `snapshot_date`, `rank_position`, `points`)
					SELECT 
						`mode_id`, `period_type`, `steam_id`, @TodayUtc, `rank_position`, `points`
					FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores`
					WHERE `rank_position` IS NOT NULL;
				";

				await connection.ExecuteAsync(snapshotQuery, new { TodayUtc = todayUtc });

				// Update rank_change based on yesterday's snapshot
				string updateRankChangeQuery = $@"
					UPDATE `{Config.DatabaseSettings.TablePrefix}k4ladder_scores` ls
					LEFT JOIN `{Config.DatabaseSettings.TablePrefix}k4ladder_snapshots` snap
						ON ls.`mode_id` = snap.`mode_id` 
						AND ls.`period_type` = snap.`period_type`
						AND ls.`steam_id` = snap.`steam_id`
						AND snap.`snapshot_date` = @YesterdayUtc
					SET ls.`rank_change` = CASE 
						WHEN snap.`rank_position` IS NULL THEN NULL
						ELSE snap.`rank_position` - ls.`rank_position`
					END;
				";

				await connection.ExecuteAsync(updateRankChangeQuery, new { YesterdayUtc = yesterdayUtc });

				// Purge old snapshots
				string purgeQuery = $@"
					DELETE FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_snapshots`
					WHERE `snapshot_date` < @SnapshotCutoff;
				";

				await connection.ExecuteAsync(purgeQuery, new { SnapshotCutoff = snapshotCutoff });

				// Purge old daily stats
				string purgeDailyQuery = $@"
					DELETE FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_daily`
					WHERE `day_utc` < @DailyCutoff;
				";

				await connection.ExecuteAsync(purgeDailyQuery, new { DailyCutoff = dailyCutoff });

				Server.NextFrame(() =>
				{
					Logger.LogInformation("Ladder daily snapshots created and old data purged");
				});
			}
			finally
			{
				// Release the lock
				string releaseLockQuery = "SELECT RELEASE_LOCK(@LockName);";
				await connection.ExecuteAsync(releaseLockQuery, new { LockName = lockName });
			}
		}
		catch (Exception ex)
		{
			Server.NextFrame(() =>
			{
				Logger.LogError("Failed to create daily snapshots: {0}", ex.Message);
			});
		}
	}
}
