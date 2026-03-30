using Android.App;
using Android.Runtime;

namespace Wiser.Control;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	public override void OnCreate()
	{
		base.OnCreate();
		BackgroundAlertScheduler.EnsureScheduled(this);
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
