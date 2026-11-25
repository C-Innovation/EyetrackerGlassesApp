using UIKit;

namespace EyetrackerGlasses.iOS;

/// <summary>
/// Запуск приложения iOS.
/// </summary>
public class Application
{
    private static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
