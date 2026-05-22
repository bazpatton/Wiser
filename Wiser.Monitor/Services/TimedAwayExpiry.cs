namespace Wiser.Monitor.Services;

/// <summary>Expires monitor timed-away sessions whose <c>ends_at_unix</c> has passed and restores hub Home.</summary>
public static class TimedAwayExpiry
{
    public static async Task<bool> TryExpireDueSessionAsync(
        TemperatureStore store,
        WiserHubFetch hub,
        MonitorOptions options,
        ILogger? log,
        CancellationToken ct)
    {
        var session = store.TryGetActiveTimedAwaySession();
        if (session is null)
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < session.EndsAtUnix)
            return false;

        if (HubConfiguration.IsConfigured(options))
        {
            try
            {
                await hub.PatchSystemHomeAwayAsync(options, "HOME", null, ct).ConfigureAwait(false);
                var overview = await LiveHubRoomTemps.TryFetchOverviewAsync(hub, options, ct).ConfigureAwait(false);
                if (overview?.SystemAway == true)
                {
                    log?.LogWarning(
                        "[timed_away] event=hub_still_away session_id={SessionId} after HOME patch",
                        session.SessionId);
                    return false;
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning(
                    ex,
                    "[timed_away] event=hub_home_fail session_id={SessionId}",
                    session.SessionId);
                return false;
            }
        }
        else
            log?.LogInformation(
                "[timed_away] event=expire session_id={SessionId} hub=skipped_not_configured",
                session.SessionId);

        store.CompleteTimedAwaySession(session.SessionId);
        if (session.Source == TimedAwaySource.Smart)
            store.SetLastSmartAwayEndedUnix(now);

        log?.LogInformation(
            "[timed_away] event=expire session_id={SessionId} ends_at={EndsAt}",
            session.SessionId,
            session.EndsAtUnix);
        return true;
    }
}
