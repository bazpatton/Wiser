using Android.App;
using Android.Appwidget;
using Android.Content;
using Wiser.Control.Services;

namespace Wiser.Control;

[BroadcastReceiver(Label = "Wiser Status Widget", Enabled = true, Exported = true)]
[Android.App.IntentFilter([AppWidgetManager.ActionAppwidgetUpdate, ActionRefresh, ActionHome, ActionAway])]
[MetaData("android.appwidget.provider", Resource = "@xml/wiser_status_widget_info")]
public sealed class WiserStatusWidgetProvider : AppWidgetProvider
{
	public const string ActionRefresh = "com.companyname.wiser.control.WIDGET_REFRESH";
	public const string ActionHome = "com.companyname.wiser.control.WIDGET_HOME";
	public const string ActionAway = "com.companyname.wiser.control.WIDGET_AWAY";

	public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
	{
		if (context is null || appWidgetManager is null || appWidgetIds is null || appWidgetIds.Length == 0)
			return;

		WiserStatusWidgetUpdater.UpdateWidgets(context, appWidgetManager, appWidgetIds);
	}

	public override void OnReceive(Context? context, Intent? intent)
	{
		base.OnReceive(context, intent);
		if (context is null || intent?.Action is null)
			return;

		var action = intent.Action;
		if (action != ActionRefresh && action != ActionHome && action != ActionAway)
			return;

		var pending = GoAsync();
		_ = Task.Run(async () =>
		{
			try
			{
				await HandleWidgetActionAsync(action).ConfigureAwait(false);
			}
			finally
			{
				WiserStatusWidgetUpdater.UpdateAllWidgets(context);
				pending?.Finish();
			}
		});
	}

	private static async Task HandleWidgetActionAsync(string action)
	{
		try
		{
			var conn = await WiserKeys.LoadFromAppPackageAsync().ConfigureAwait(false);
			using var client = new WiserHubClient(conn);

			if (action == ActionHome)
				await client.SetHomeAwayAsync("HOME", null).ConfigureAwait(false);
			else if (action == ActionAway)
				await client.SetHomeAwayAsync("AWAY", 10).ConfigureAwait(false);

			var payload = await client.RefreshDomainAsync().ConfigureAwait(false);
			WidgetStatusStore.SaveFromDomain(payload, DateTimeOffset.Now);
		}
		catch (Exception ex)
		{
			WidgetStatusStore.SaveError(ex.Message);
		}
	}
}
