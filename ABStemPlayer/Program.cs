using Microsoft.Extensions.DependencyInjection;

namespace ABStemPlayer;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var services = ConfigureServices();
        var provider = services.BuildServiceProvider();

        return AppBuilder.Configure<App>(() => new App(provider))
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace();
    }

    private static ServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();
        
        services.AddAudioCore();

        // View models
        services.AddSingleton<PlaybackViewModel>();

        services.AddSingleton<MixerViewModel>();

        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
}
