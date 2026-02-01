namespace K4System;

using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Core.Plugin;

public partial class ModuleLadder : IModuleLadder
{
	public required ILogger<ModuleLadder> Logger { get; set; }
	public required IPluginContext pluginContext { get; set; }
	public required Plugin plugin { get; set; }
	public required PluginConfig Config { get; set; }

	// Current server's mode info (loaded on init)
	public int CurrentModeId { get; set; } = -1;
	public string CurrentServerId { get; set; } = "";
	public GameMode? CurrentMode { get; set; } = null;

	// In-memory leaderboard cache
	public Dictionary<string, LeaderboardCache> LeaderboardCaches { get; set; } = new();

	/// <summary>
	/// Represents a game mode from the database
	/// </summary>
	public class GameMode
	{
		public int ModeId { get; set; }
		public string GameType { get; set; } = "";
		public string? Variation { get; set; }
		public string DisplayName { get; set; } = "";
		public string ShortName { get; set; } = "";
		public bool IsActive { get; set; } = true;
	}

	/// <summary>
	/// Cached daily stats for a player (in-memory, written to DB periodically)
	/// </summary>
	public class DailyStatsCache
	{
		public DateTime DayUtc { get; set; } = DateTime.UtcNow.Date;
		public int PointsEarned { get; set; } = 0;
		public int PointsLost { get; set; } = 0;
		public int PointsNet => PointsEarned - PointsLost;
		public int Kills { get; set; } = 0;
		public int Deaths { get; set; } = 0;
		public int Assists { get; set; } = 0;
		public int Headshots { get; set; } = 0;
		public int MVPs { get; set; } = 0;
		public int RoundsPlayed { get; set; } = 0;
		public int RoundsWon { get; set; } = 0;
		public int PlaytimeSeconds { get; set; } = 0;
		public DateTime LastUpdate { get; set; } = DateTime.UtcNow;

		/// <summary>
		/// Reset for a new day
		/// </summary>
		public void ResetForNewDay()
		{
			DayUtc = DateTime.UtcNow.Date;
			PointsEarned = 0;
			PointsLost = 0;
			Kills = 0;
			Deaths = 0;
			Assists = 0;
			Headshots = 0;
			MVPs = 0;
			RoundsPlayed = 0;
			RoundsWon = 0;
			PlaytimeSeconds = 0;
			LastUpdate = DateTime.UtcNow;
		}
	}

	/// <summary>
	/// Player's ladder data (attached to K4Player)
	/// </summary>
	public class LadderData
	{
		public DailyStatsCache TodayStats { get; set; } = new();
		public Dictionary<int, LadderPosition> ModePositions { get; set; } = new();
		public DateTime LastSaved { get; set; } = DateTime.MinValue;
	}

	/// <summary>
	/// A player's position on a specific ladder
	/// </summary>
	public class LadderPosition
	{
		public int ModeId { get; set; }
		public string ModeName { get; set; } = "";
		public string PeriodType { get; set; } = "weekly";
		public int Points { get; set; }
		public int RankPosition { get; set; }
		public int? RankChange { get; set; }  // null = new, positive = climbed, negative = dropped
		public int TotalPlayers { get; set; }
	}

	/// <summary>
	/// A single entry in a leaderboard
	/// </summary>
	public class LadderEntry
	{
		public int Position { get; set; }
		public ulong SteamId { get; set; }
		public string Name { get; set; } = "";
		public int Points { get; set; }
		public int Kills { get; set; }
		public int Deaths { get; set; }
		public int Assists { get; set; }
		public int Headshots { get; set; }
		public int RoundsPlayed { get; set; }
		public int DaysActive { get; set; }
		public decimal KDR => Deaths > 0 ? Math.Round((decimal)Kills / Deaths, 2) : Kills;
		public int? RankChange { get; set; }
	}

	/// <summary>
	/// Cached leaderboard data
	/// </summary>
	public class LeaderboardCache
	{
		public int ModeId { get; set; }
		public string PeriodType { get; set; } = "weekly";
		public List<LadderEntry> Entries { get; set; } = new();
		public DateTime CachedAt { get; set; } = DateTime.MinValue;
		public int TotalPlayers { get; set; }

		public bool IsExpired(int cacheSeconds)
		{
			return (DateTime.UtcNow - CachedAt).TotalSeconds > cacheSeconds;
		}
	}

	/// <summary>
	/// Player's detailed ladder statistics
	/// </summary>
	public class LadderPlayerStats
	{
		public int ModeId { get; set; }
		public string ModeName { get; set; } = "";
		public string PeriodType { get; set; } = "weekly";
		public int Points { get; set; }
		public int Position { get; set; }
		public int TotalPlayers { get; set; }
		public int? PositionChange { get; set; }
		public int Kills { get; set; }
		public int Deaths { get; set; }
		public int Assists { get; set; }
		public int Headshots { get; set; }
		public int RoundsPlayed { get; set; }
		public int RoundsWon { get; set; }
		public int DaysActive { get; set; }
		public int BestDayPoints { get; set; }
		public int CurrentStreak { get; set; }
		public decimal KDR => Deaths > 0 ? Math.Round((decimal)Kills / Deaths, 2) : Kills;
		public decimal WinRate => RoundsPlayed > 0 ? Math.Round((decimal)RoundsWon / RoundsPlayed * 100, 1) : 0;
		public decimal PointsPerDay => DaysActive > 0 ? Math.Round((decimal)Points / DaysActive, 1) : Points;
	}

	/// <summary>
	/// Mode summary for player (used in /ladderrank)
	/// </summary>
	public class PlayerModeSummary
	{
		public int ModeId { get; set; }
		public string GameType { get; set; } = "";
		public string? Variation { get; set; }
		public string DisplayName { get; set; } = "";
		public int Points { get; set; }
		public int Position { get; set; }
		public int TotalPlayers { get; set; }
		public int? RankChange { get; set; }
	}
}
