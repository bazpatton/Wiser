namespace Wiser.Control;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override void OnStart()
	{
		base.OnStart();
#if WINDOWS
		WindowsBackgroundHubPoller.EnsureStarted();
#endif
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}