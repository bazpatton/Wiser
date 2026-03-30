namespace Wiser.Control.Services;

public static partial class WidgetSyncService
{
	public static void NotifyChanged()
	{
		NotifyChangedPlatform();
	}

	static partial void NotifyChangedPlatform();
}
