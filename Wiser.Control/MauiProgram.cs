using Microsoft.Extensions.Logging;
using Plugin.LocalNotification;
using Plugin.LocalNotification.AndroidOption;

namespace Wiser.Control;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
#if !WINDOWS
			.UseLocalNotification(config =>
			{
#if ANDROID
				config.AddAndroid(android =>
				{
					android.AddChannel(new NotificationChannelRequest
					{
						Id = "wiser_warm",
						Name = "Temperature alerts",
						Description = "When a room exceeds your alert temperature",
						Importance = AndroidImportance.High,
						LockScreenVisibility = AndroidVisibilityType.Public,
					});
				});
#endif
			})
#endif
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("FluentSystemIcons-Filled.ttf", "FluentIconsFilled");
				fonts.AddFont("fa-solid-900.ttf", "FontAwesomeSolid");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
