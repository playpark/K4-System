using K4System.Models;

namespace K4System;

public interface IModuleLadder
{
	void Initialize(bool hotReload);
	void Release(bool hotReload);

	/// <summary>
	/// Called when a player's points change (hook from ModuleRank)
	/// </summary>
	void OnPointsChanged(K4Player k4player, int delta, string reason);

	/// <summary>
	/// Called when a player gets a kill
	/// </summary>
	void OnPlayerKill(K4Player killer, K4Player? victim, bool headshot);

	/// <summary>
	/// Called when a player dies
	/// </summary>
	void OnPlayerDeath(K4Player victim);

	/// <summary>
	/// Called when a round ends
	/// </summary>
	void OnRoundEnd(K4Player k4player, bool won);

	/// <summary>
	/// Called when a player gets MVP
	/// </summary>
	void OnMVP(K4Player k4player);

	/// <summary>
	/// Get the current server's mode ID
	/// </summary>
	int GetCurrentModeId();

	/// <summary>
	/// Get the current server's mode display name
	/// </summary>
	string GetCurrentModeDisplayName();
}
