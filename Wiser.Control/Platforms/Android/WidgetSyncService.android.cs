using Android.App;
using Wiser.Control.Services;

namespace Wiser.Control.Services;

public static partial class WidgetSyncService
{
	static partial void NotifyChangedPlatform() =>
		WiserStatusWidgetUpdater.UpdateAllWidgets(Android.App.Application.Context);
}
