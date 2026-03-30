namespace Wiser.Control.Services;

internal static class BackgroundHubTemperatureRunner
{
	public static async Task RunAsync()
	{
		BackgroundPollDiagnosticsStore.RecordAttempt();
		try
		{
			var conn = await WiserKeys.LoadFromAppPackageAsync().ConfigureAwait(false);
			using var client = new WiserHubClient(conn);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			var payload = await client.RefreshDomainAsync(cts.Token).ConfigureAwait(false);
			TemperatureTrendStore.AppendFromDomain(payload, DateTimeOffset.UtcNow);
			WidgetStatusStore.SaveFromDomain(payload, DateTimeOffset.Now);
			WidgetSyncService.NotifyChanged();
			BackgroundPollDiagnosticsStore.RecordSuccess();
			if (AlertSettingsStore.RoomAlertsEnabled)
				await WarmRoomNotifier.OnRoomsRefreshedFromBackgroundAsync(payload.Room).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			BackgroundPollDiagnosticsStore.RecordFailure(ex.Message);
			// Background checks are best-effort and should never crash the app process.
		}
	}
}
