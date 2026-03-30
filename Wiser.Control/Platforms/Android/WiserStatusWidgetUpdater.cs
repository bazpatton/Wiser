using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Wiser.Control.Services;

namespace Wiser.Control;

internal static class WiserStatusWidgetUpdater
{
	public static void UpdateAllWidgets(Context context)
	{
		var manager = AppWidgetManager.GetInstance(context);
		if (manager is null)
			return;
		var component = new ComponentName(context, Java.Lang.Class.FromType(typeof(WiserStatusWidgetProvider)));
		var ids = manager.GetAppWidgetIds(component);
		if (ids is null || ids.Length == 0)
			return;

		UpdateWidgets(context, manager, ids);
	}

	public static void UpdateWidgets(Context context, AppWidgetManager manager, int[] appWidgetIds)
	{
		var snapshot = WidgetStatusStore.Load();
		foreach (var widgetId in appWidgetIds)
		{
			var rv = new RemoteViews(context.PackageName, Resource.Layout.wiser_status_widget);
			rv.SetTextViewText(Resource.Id.widget_title, snapshot.Title);
			rv.SetTextViewText(Resource.Id.widget_subtitle, snapshot.Subtitle);
			rv.SetTextViewText(Resource.Id.widget_meta, snapshot.Meta);

			rv.SetOnClickPendingIntent(Resource.Id.widget_btn_refresh, BuildActionIntent(context, WiserStatusWidgetProvider.ActionRefresh));
			rv.SetOnClickPendingIntent(Resource.Id.widget_btn_home, BuildActionIntent(context, WiserStatusWidgetProvider.ActionHome));
			rv.SetOnClickPendingIntent(Resource.Id.widget_btn_away, BuildActionIntent(context, WiserStatusWidgetProvider.ActionAway));

			manager.UpdateAppWidget(widgetId, rv);
		}
	}

	private static PendingIntent BuildActionIntent(Context context, string action)
	{
		var intent = new Intent(context, typeof(WiserStatusWidgetProvider));
		intent.SetAction(action);
#pragma warning disable CA1416
		var flags = PendingIntentFlags.UpdateCurrent;
		if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.M)
			flags |= PendingIntentFlags.Immutable;
#pragma warning restore CA1416

		return PendingIntent.GetBroadcast(context, action.GetHashCode(), intent, flags)!;
	}
}
