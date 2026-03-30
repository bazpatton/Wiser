namespace Wiser.Control;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
		Routing.RegisterRoute(nameof(ReorderRoomsPage), typeof(ReorderRoomsPage));
		Routing.RegisterRoute(nameof(RoomGroupsPage), typeof(RoomGroupsPage));
		Routing.RegisterRoute(nameof(BoostPresetsPage), typeof(BoostPresetsPage));
		Routing.RegisterRoute(nameof(ActionHistoryPage), typeof(ActionHistoryPage));
		Routing.RegisterRoute(nameof(TrendChartsPage), typeof(TrendChartsPage));
		Routing.RegisterRoute(nameof(SchedulesPage), typeof(SchedulesPage));
	}
}
