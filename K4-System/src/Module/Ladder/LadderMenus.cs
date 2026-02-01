namespace K4System;

using CounterStrikeSharp.API.Modules.Menu;

public partial class ModuleLadder : IModuleLadder
{
	public ChatMenu? ladderModesMenu = null;
	public ChatMenu? ladderPeriodsMenu = null;

	public void Initialize_Menus()
	{
		// Modes menu - will be populated dynamically
		ladderModesMenu = new ChatMenu(plugin.Localizer["k4.ladder.menu.modes.title"]);

		// Periods menu
		ladderPeriodsMenu = new ChatMenu(plugin.Localizer["k4.ladder.menu.periods.title"]);
		
		foreach (var period in Config.LadderSettings.LadderPeriods)
		{
			string displayName = period switch
			{
				"daily" => plugin.Localizer["k4.ladder.period.daily"],
				"weekly" => plugin.Localizer["k4.ladder.period.weekly"],
				"monthly" => plugin.Localizer["k4.ladder.period.monthly"],
				"alltime" => plugin.Localizer["k4.ladder.period.alltime"],
				_ => period
			};

			ladderPeriodsMenu.AddMenuOption(displayName, (player, option) =>
			{
				// Execute ladder command with this period
				player.ExecuteClientCommandFromServer($"css_ladder {period}");
			});
		}
	}
}
