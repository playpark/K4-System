namespace K4System
{
    using Microsoft.Extensions.Logging;
    using CounterStrikeSharp.API.Core;
    using CounterStrikeSharp.API.Core.Attributes;
    using CounterStrikeSharp.API;
    using K4System.Models;
    using Dapper;

    [MinimumApiVersion(200)]
    public sealed partial class Plugin : BasePlugin, IPluginConfig<PluginConfig>
    {
        //** ? PLUGIN GLOBALS */
        public required PluginConfig Config { get; set; } = new PluginConfig();
        public required string _ModuleDirectory { get; set; }
        public CCSGameRules? GameRules = null;

		//** ? MODULES */
		private readonly IModuleRank ModuleRank;
		private readonly IModuleStat ModuleStat;
		private readonly IModuleTime ModuleTime;
		private readonly IModuleUtils ModuleUtils;
		private readonly IModuleLadder ModuleLadder;

		public List<K4Player> K4Players = new List<K4Player>();

		public Plugin(ModuleRank moduleRank, ModuleStat moduleStat, ModuleTime moduleTime, ModuleUtils moduleUtils, ModuleLadder moduleLadder)
		{
			this.ModuleRank = moduleRank;
			this.ModuleStat = moduleStat;
			this.ModuleTime = moduleTime;
			this.ModuleUtils = moduleUtils;
			this.ModuleLadder = moduleLadder;
		}

		// Ladder module notification helpers
		public void NotifyLadderPointsChanged(K4System.Models.K4Player k4player, int delta, string reason)
		{
			if (Config.GeneralSettings.ModuleLadder)
				ModuleLadder.OnPointsChanged(k4player, delta, reason);
		}

		public void NotifyLadderKill(K4System.Models.K4Player killer, K4System.Models.K4Player? victim, bool headshot)
		{
			if (Config.GeneralSettings.ModuleLadder)
				ModuleLadder.OnPlayerKill(killer, victim, headshot);
		}

		public void NotifyLadderDeath(K4System.Models.K4Player victim)
		{
			if (Config.GeneralSettings.ModuleLadder)
				ModuleLadder.OnPlayerDeath(victim);
		}

		public void NotifyLadderMVP(K4System.Models.K4Player k4player)
		{
			if (Config.GeneralSettings.ModuleLadder)
				ModuleLadder.OnMVP(k4player);
		}

        public void OnConfigParsed(PluginConfig config)
        {
            if (config.Version < Config.Version)
            {
                base.Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", this.Config.Version, config.Version);
            }

            //** ? Save Config */

            this.Config = config;
        }

        public override void Load(bool hotReload)
        {
            _ModuleDirectory = ModuleDirectory;

            //** ? Core */

            Initialize_API();
            Initialize_Events();
            Initialize_Commands();

            //** ? Initialize Modules */

            if (Config.GeneralSettings.ModuleRanks)
                this.ModuleRank.Initialize(hotReload);

            if (Config.GeneralSettings.ModuleStats)
                this.ModuleStat.Initialize(hotReload);

            if (Config.GeneralSettings.ModuleTimes)
                this.ModuleTime.Initialize(hotReload);


			if (Config.GeneralSettings.ModuleUtils)
				this.ModuleUtils.Initialize(hotReload);

			if (Config.GeneralSettings.ModuleLadder)
				this.ModuleLadder.Initialize(hotReload);

			//** ? Initialize Database tables */

            Task.Run(CreateMultipleTablesAsync).Wait();

            if (hotReload)
            {
                //** ? Load Player Caches */

                LoadAllPlayersCache();

                GameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules;
            }
        }

        public override void Unload(bool hotReload)
        {
            //** ? Save Player Caches */

            Task.Run(SaveAllPlayersDataAsync);

            //** ? Release Modules */

            if (Config.GeneralSettings.ModuleRanks)
                this.ModuleRank.Release(hotReload);

            if (Config.GeneralSettings.ModuleStats)
                this.ModuleStat.Release(hotReload);

            if (Config.GeneralSettings.ModuleTimes)
                this.ModuleTime.Release(hotReload);

			if (Config.GeneralSettings.ModuleUtils)
				this.ModuleUtils.Release(hotReload);

			if (Config.GeneralSettings.ModuleLadder)
				this.ModuleLadder.Release(hotReload);

			this.Dispose();
        }

		public async Task<bool> CreateMultipleTablesAsync()
		{
			// Ladder module tables
			// variation is NOT NULL DEFAULT '' to ensure uniqueness works correctly
		// (MySQL allows multiple NULLs in unique indexes, which would break mode identity)
		string ladderModesTable = $@"CREATE TABLE IF NOT EXISTS `{this.Config.DatabaseSettings.TablePrefix}k4modes` (
					`mode_id` INT AUTO_INCREMENT PRIMARY KEY,
					`game_type` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
					`variation` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' NOT NULL DEFAULT '',
					`display_name` VARCHAR(64) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
					`short_name` VARCHAR(16) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
					`is_active` BOOLEAN NOT NULL DEFAULT TRUE,
					UNIQUE KEY `idx_type_var` (`game_type`, `variation`)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

			// Removed UNIQUE(ip, port) constraint as servers may not have real IP/port available
		// server_id is the primary key and must be unique (configured per server)
		string ladderServersTable = $@"CREATE TABLE IF NOT EXISTS `{this.Config.DatabaseSettings.TablePrefix}k4servers` (
					`server_id` VARCHAR(64) COLLATE 'utf8mb4_unicode_ci' PRIMARY KEY,
					`name` VARCHAR(128) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
					`ip` VARCHAR(45) COLLATE 'utf8mb4_unicode_ci' NULL,
					`port` INT NULL,
					`mode_id` INT NOT NULL,
					`is_active` BOOLEAN NOT NULL DEFAULT TRUE,
					`created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
					`last_seen` DATETIME NULL,
					INDEX `idx_mode` (`mode_id`)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

			string ladderDailyTable = $@"CREATE TABLE IF NOT EXISTS `{this.Config.DatabaseSettings.TablePrefix}k4ladder_daily` (
					`steam_id` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
					`day_utc` DATE NOT NULL,
					`mode_id` INT NOT NULL,
					`server_id` VARCHAR(64) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
					`points_earned` INT NOT NULL DEFAULT 0,
					`points_lost` INT NOT NULL DEFAULT 0,
					`points_net` INT NOT NULL DEFAULT 0,
					`kills` INT NOT NULL DEFAULT 0,
					`deaths` INT NOT NULL DEFAULT 0,
					`assists` INT NOT NULL DEFAULT 0,
					`headshots` INT NOT NULL DEFAULT 0,
					`mvps` INT NOT NULL DEFAULT 0,
					`rounds_played` INT NOT NULL DEFAULT 0,
					`rounds_won` INT NOT NULL DEFAULT 0,
					`playtime_seconds` INT NOT NULL DEFAULT 0,
					PRIMARY KEY (`steam_id`, `day_utc`, `server_id`),
					INDEX `idx_mode_day_points` (`mode_id`, `day_utc`, `points_net` DESC),
					INDEX `idx_player_mode` (`steam_id`, `mode_id`, `day_utc`),
					INDEX `idx_day` (`day_utc`)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

			string ladderScoresTable = $@"CREATE TABLE IF NOT EXISTS `{this.Config.DatabaseSettings.TablePrefix}k4ladder_scores` (
					`mode_id` INT NOT NULL,
					`period_type` ENUM('daily', 'weekly', 'monthly', 'alltime') NOT NULL,
					`steam_id` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
					`name` VARCHAR(255) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
					`points` INT NOT NULL DEFAULT 0,
					`kills` INT NOT NULL DEFAULT 0,
					`deaths` INT NOT NULL DEFAULT 0,
					`assists` INT NOT NULL DEFAULT 0,
					`headshots` INT NOT NULL DEFAULT 0,
					`rounds_played` INT NOT NULL DEFAULT 0,
					`days_active` INT NOT NULL DEFAULT 0,
					`rank_position` INT NULL,
					`rank_change` INT NULL,
					`updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
					PRIMARY KEY (`mode_id`, `period_type`, `steam_id`),
					INDEX `idx_leaderboard` (`mode_id`, `period_type`, `points` DESC),
					INDEX `idx_rank` (`mode_id`, `period_type`, `rank_position`)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

			string ladderSnapshotsTable = $@"CREATE TABLE IF NOT EXISTS `{this.Config.DatabaseSettings.TablePrefix}k4ladder_snapshots` (
					`id` BIGINT AUTO_INCREMENT PRIMARY KEY,
					`mode_id` INT NOT NULL,
					`period_type` ENUM('daily', 'weekly', 'monthly', 'alltime') NOT NULL,
					`steam_id` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
					`snapshot_date` DATE NOT NULL,
					`rank_position` INT NOT NULL,
					`points` INT NOT NULL,
					INDEX `idx_player_history` (`steam_id`, `mode_id`, `period_type`, `snapshot_date`),
					INDEX `idx_mode_date` (`mode_id`, `period_type`, `snapshot_date`)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

			string timesModuleTable = @$"CREATE TABLE IF NOT EXISTS `{this.Config.DatabaseSettings.TablePrefix}k4times` (
					`steam_id` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' PRIMARY KEY NOT NULL,
					`name` VARCHAR(255) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
                    `lastseen` DATETIME NOT NULL,
					`all` INT NOT NULL DEFAULT 0,
					`ct` INT NOT NULL DEFAULT 0,
					`t` INT NOT NULL DEFAULT 0,
					`spec` INT NOT NULL DEFAULT 0,
					`dead` INT NOT NULL DEFAULT 0,
					`alive` INT NOT NULL DEFAULT 0
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

            string statsModuleTable = $@"CREATE TABLE IF NOT EXISTS `{this.Config.DatabaseSettings.TablePrefix}k4stats` (
                `steam_id` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' PRIMARY KEY NOT NULL,
                `name` VARCHAR(255) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
                `lastseen` DATETIME NOT NULL,
                `kills` INT NOT NULL DEFAULT 0,
                `firstblood` INT NOT NULL DEFAULT 0,
                `deaths` INT NOT NULL DEFAULT 0,
                `assists` INT NOT NULL DEFAULT 0,
                `shoots` INT NOT NULL DEFAULT 0,
                `hits_taken` INT NOT NULL DEFAULT 0,
                `hits_given` INT NOT NULL DEFAULT 0,
                `headshots` INT NOT NULL DEFAULT 0,
                `chest_hits` INT NOT NULL DEFAULT 0,
                `stomach_hits` INT NOT NULL DEFAULT 0,
                `left_arm_hits` INT NOT NULL DEFAULT 0,
                `right_arm_hits` INT NOT NULL DEFAULT 0,
                `left_leg_hits` INT NOT NULL DEFAULT 0,
                `right_leg_hits` INT NOT NULL DEFAULT 0,
                `neck_hits` INT NOT NULL DEFAULT 0,
                `unused_hits` INT NOT NULL DEFAULT 0,
                `gear_hits` INT NOT NULL DEFAULT 0,
                `special_hits` INT NOT NULL DEFAULT 0,
                `grenades` INT NOT NULL DEFAULT 0,
                `mvp` INT NOT NULL DEFAULT 0,
                `round_win` INT NOT NULL DEFAULT 0,
                `round_lose` INT NOT NULL DEFAULT 0,
                `game_win` INT NOT NULL DEFAULT 0,
                `game_lose` INT NOT NULL DEFAULT 0,
                `rounds_overall` INT NOT NULL DEFAULT 0,
                `rounds_ct` INT NOT NULL DEFAULT 0,
                `rounds_t` INT NOT NULL DEFAULT 0,
                `bomb_planted` INT NOT NULL DEFAULT 0,
                `bomb_defused` INT NOT NULL DEFAULT 0,
                `hostage_rescued` INT NOT NULL DEFAULT 0,
                `hostage_killed` INT NOT NULL DEFAULT 0,
                `noscope_kill` INT NOT NULL DEFAULT 0,
                `penetrated_kill` INT NOT NULL DEFAULT 0,
                `thrusmoke_kill` INT NOT NULL DEFAULT 0,
                `flashed_kill` INT NOT NULL DEFAULT 0,
                `dominated_kill` INT NOT NULL DEFAULT 0,
                `revenge_kill` INT NOT NULL DEFAULT 0,
                `assist_flash` INT NOT NULL DEFAULT 0
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

            string ranksModuleTable = $@"CREATE TABLE IF NOT EXISTS `{this.Config.DatabaseSettings.TablePrefix}k4ranks` (
                    `steam_id` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' PRIMARY KEY NOT NULL,
                    `name` VARCHAR(255) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
                    `lastseen` DATETIME NOT NULL,
                    `rank` VARCHAR(255) COLLATE 'utf8mb4_unicode_ci' NOT NULL,
                    `points` INT NOT NULL DEFAULT 0
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

            string lvlranksModuleTable = @$"CREATE TABLE IF NOT EXISTS `{Config.DatabaseSettings.LvLRanksTableName}` (
                    `steam` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' PRIMARY KEY,
                    `name`  VARCHAR(255) COLLATE 'utf8mb4_unicode_ci',
                    `value` INT NOT NULL DEFAULT 0,
                    `rank` INT NOT NULL DEFAULT 0,
                    `kills` INT NOT NULL DEFAULT 0,
                    `deaths` INT NOT NULL DEFAULT 0,
                    `shoots` INT NOT NULL DEFAULT 0,
                    `hits` INT NOT NULL DEFAULT 0,
                    `headshots` INT NOT NULL DEFAULT 0,
                    `assists` INT NOT NULL DEFAULT 0,
                    `round_win` INT NOT NULL DEFAULT 0,
                    `round_lose` INT NOT NULL DEFAULT 0,
                    `playtime` INT NOT NULL DEFAULT 0,
                    `lastconnect` INT NOT NULL DEFAULT 0
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

			using (var connection = CreateConnection(Config))
			{
				await connection.OpenAsync();

				using (var transaction = await connection.BeginTransactionAsync())
				{
					await connection.ExecuteAsync(timesModuleTable, transaction: transaction);
					await connection.ExecuteAsync(statsModuleTable, transaction: transaction);
					await connection.ExecuteAsync(ranksModuleTable, transaction: transaction);

					if (Config.GeneralSettings.LevelRanksCompatibility)
					{
						await connection.ExecuteAsync(lvlranksModuleTable, transaction: transaction);
					}

					if (Config.GeneralSettings.ModuleLadder)
					{
						await connection.ExecuteAsync(ladderModesTable, transaction: transaction);
						await connection.ExecuteAsync(ladderServersTable, transaction: transaction);
						await connection.ExecuteAsync(ladderDailyTable, transaction: transaction);
						await connection.ExecuteAsync(ladderScoresTable, transaction: transaction);
						await connection.ExecuteAsync(ladderSnapshotsTable, transaction: transaction);
					}

					await transaction.CommitAsync();
				}

				// Run migrations for existing tables (safe to run multiple times)
				if (Config.GeneralSettings.ModuleLadder)
				{
					await MigrateLadderTablesAsync(connection);
				}
			}

            await PurgeTableRowsAsync();
            return true;
        }

		/// <summary>
		/// Migrate existing ladder tables to new schema (idempotent - safe to run multiple times)
		/// </summary>
		private async Task MigrateLadderTablesAsync(MySqlConnector.MySqlConnection connection)
		{
			try
			{
				// Migration 1: Convert NULL variations to empty string and change column to NOT NULL
				// Check if variation column allows NULL
				string checkVariationNull = $@"
					SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS 
					WHERE TABLE_SCHEMA = DATABASE() 
					AND TABLE_NAME = '{Config.DatabaseSettings.TablePrefix}k4modes' 
					AND COLUMN_NAME = 'variation';
				";
				var isNullable = await connection.QueryFirstOrDefaultAsync<string>(checkVariationNull);
				
				if (isNullable == "YES")
				{
					// Convert NULLs to empty string first
					string updateNulls = $@"
						UPDATE `{Config.DatabaseSettings.TablePrefix}k4modes` 
						SET `variation` = '' WHERE `variation` IS NULL;
					";
					await connection.ExecuteAsync(updateNulls);

					// Alter column to NOT NULL DEFAULT ''
					string alterVariation = $@"
						ALTER TABLE `{Config.DatabaseSettings.TablePrefix}k4modes` 
						MODIFY `variation` VARCHAR(32) COLLATE 'utf8mb4_unicode_ci' NOT NULL DEFAULT '';
					";
					await connection.ExecuteAsync(alterVariation);
					
					Logger.LogInformation("[K4-Ladder] Migrated k4modes.variation to NOT NULL DEFAULT ''");
				}

				// Migration 2: Remove ip/port unique constraint from k4servers if it exists
				string checkIpPortIndex = $@"
					SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS 
					WHERE TABLE_SCHEMA = DATABASE() 
					AND TABLE_NAME = '{Config.DatabaseSettings.TablePrefix}k4servers' 
					AND INDEX_NAME = 'idx_ip_port';
				";
				var indexExists = await connection.QueryFirstOrDefaultAsync<int>(checkIpPortIndex);
				
				if (indexExists > 0)
				{
					string dropIndex = $@"
						ALTER TABLE `{Config.DatabaseSettings.TablePrefix}k4servers` 
						DROP INDEX `idx_ip_port`;
					";
					await connection.ExecuteAsync(dropIndex);
					
					Logger.LogInformation("[K4-Ladder] Removed idx_ip_port unique constraint from k4servers");
				}

				// Migration 3: Make ip and port nullable in k4servers
				string checkIpNullable = $@"
					SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS 
					WHERE TABLE_SCHEMA = DATABASE() 
					AND TABLE_NAME = '{Config.DatabaseSettings.TablePrefix}k4servers' 
					AND COLUMN_NAME = 'ip';
				";
				var ipIsNullable = await connection.QueryFirstOrDefaultAsync<string>(checkIpNullable);
				
				if (ipIsNullable == "NO")
				{
					string alterIpPort = $@"
						ALTER TABLE `{Config.DatabaseSettings.TablePrefix}k4servers` 
						MODIFY `ip` VARCHAR(45) COLLATE 'utf8mb4_unicode_ci' NULL,
						MODIFY `port` INT NULL;
					";
					await connection.ExecuteAsync(alterIpPort);
					
					Logger.LogInformation("[K4-Ladder] Made k4servers.ip and k4servers.port nullable");
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning("[K4-Ladder] Migration warning (non-fatal): {0}", ex.Message);
			}
		}

		/// <summary>
		/// One-time cleanup of stale ladder scores after upgrading from old version
		/// Call this manually via command if needed: css_ladder_cleanup
		/// </summary>
		public async Task CleanupStaleLadderScoresAsync()
		{
			try
			{
				using var connection = CreateConnection(Config);
				await connection.OpenAsync();

				// Get lock to prevent concurrent cleanup
				string lockQuery = "SELECT GET_LOCK('k4ladder_cleanup', 0) as acquired;";
				var lockResult = await connection.QueryFirstOrDefaultAsync<dynamic>(lockQuery, new { });
				
				if (lockResult?.acquired != 1)
					return;

				try
				{
					Logger.LogInformation("[K4-Ladder] Running one-time stale scores cleanup...");

					// For each period type, remove players who have no daily data in the rolling window
					var periods = new[] { ("daily", 1), ("weekly", 7), ("monthly", 30) };

					foreach (var (periodType, days) in periods)
					{
						DateTime cutoffDate = DateTime.UtcNow.Date.AddDays(-(days - 1));

						string cleanupQuery = $@"
							DELETE ls FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_scores` ls
							WHERE ls.`period_type` = @PeriodType
							AND NOT EXISTS (
								SELECT 1 FROM `{Config.DatabaseSettings.TablePrefix}k4ladder_daily` d
								WHERE d.`steam_id` = ls.`steam_id` 
								AND d.`mode_id` = ls.`mode_id`
								AND d.`day_utc` >= @CutoffDate
							);
						";

						var deleted = await connection.ExecuteAsync(cleanupQuery, new { PeriodType = periodType, CutoffDate = cutoffDate });
						
						if (deleted > 0)
						{
							Logger.LogInformation("[K4-Ladder] Cleaned up {0} stale {1} ladder entries", deleted, periodType);
						}
					}

					Logger.LogInformation("[K4-Ladder] Stale scores cleanup complete");
				}
				finally
				{
					string releaseLockQuery = "SELECT RELEASE_LOCK('k4ladder_cleanup');";
					await connection.ExecuteAsync(releaseLockQuery);
				}
			}
			catch (Exception ex)
			{
				Logger.LogError("[K4-Ladder] Cleanup error: {0}", ex.Message);
			}
		}
    }
}