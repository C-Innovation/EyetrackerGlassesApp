using System;
using System.IO;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using EyetrackerGlasses.Core;
using EyetrackerGlasses.Navigation;
using EyetrackerGlasses.ViewModels;
using EyetrackerGlasses.Views;

namespace EyetrackerGlasses;

public partial class App : Application
{
    /// <summary>
    /// Имя приложения. Задаёт каталоги настроек, журнала и отчётов.
    /// </summary>
    public const string APP_NAME = "EyetrackerGlasses";

    /// <summary>
    /// Имя организации. Добавляется в путь к каталогам данных.
    /// Пустая строка — каталоги создаются сразу под именем приложения.
    /// </summary>
    public const string COMPANY_NAME = "Medicom MTD";

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        var config = AppConfig.GetInstance();
        RegisterThemesAndLanguages(config);

        if (ApplicationLifetime is IActivityApplicationLifetime mobile && SharedStorageRoot is not null)
        {
            mobile.MainViewFactory = () =>
            {
                _sharedStorageView = new ContentControl();
                RefreshSharedStorageAccess();
                return _sharedStorageView;
            };
            base.OnFrameworkInitializationCompleted();
            return;
        }

        InitializeServices(config);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };

            config.SetApplicationLifetime(desktop);
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            // Android в Avalonia 12: представление создаётся по требованию,
            // потому что активность может быть пересоздана (поворот экрана,
            // возврат из фона). Проверка идёт раньше ISingleViewApplicationLifetime.
            activity.MainViewFactory = () => CreateMainView(config);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // iOS, браузер, встроенный Linux.
            singleView.MainView = CreateMainView(config);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static Func<string?>? SharedStorageRoot { get; set; }
    public static Action? RequestSharedStorageAccess { get; set; }
    public static Action<string>? PrepareSharedStorage { get; set; }

    private ContentControl? _sharedStorageView;
    private bool _servicesInitialized;

    public void RefreshSharedStorageAccess()
    {
        if (_sharedStorageView is null)
            return;

        string? root = SharedStorageRoot?.Invoke();
        if (root is null)
        {
            ShowStoragePrompt("Разрешите доступ к общему хранилищу для папки Documents/EyetrackerGlasses.");
            return;
        }

        if (_sharedStorageView.Content is MainView)
            return;

        try
        {
            var config = AppConfig.GetInstance();
            if (!_servicesInitialized)
            {
                PrepareSharedStorage?.Invoke(Path.Combine(root, APP_NAME));
                InitializeServices(config, root);
                _servicesInitialized = true;
            }

            _sharedStorageView.Content = CreateMainView(config);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStoragePrompt($"Общее хранилище недоступно: {error.Message}");
        }
    }

    private void ShowStoragePrompt(string text)
    {
        var button = new Button { Content = "Разрешить доступ" };
        button.Click += (_, _) => RequestSharedStorageAccess?.Invoke();
        _sharedStorageView!.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, button }
        };
    }

    private static void InitializeServices(AppConfig config, string? sharedRoot = null)
    {
        config.Initialize(APP_NAME, sharedRoot is null ? COMPANY_NAME : "", dataRoot: sharedRoot);
        IoC.Setup(ConfigureServices);
        PageRegistry.RegisterDefaults();
        IoC.Logger.Log("EyetrackerGlasses started.", LogLevel.Success);
    }

    /// <summary>
    /// Создаёт главное представление однооконной платформы и подключает его
    /// к освобождению ресурсов.
    /// </summary>
    private static MainView CreateMainView(AppConfig config)
    {
        var view = new MainView
        {
            DataContext = new MainViewModel(),
        };

        config.AttachToMainView(view);
        return view;
    }

    /// <summary>
    /// Регистрация служб приложения.
    /// <para>
    /// Базовые службы (файлы, задачи, навигация) уже зарегистрированы в
    /// <see cref="IoC.Setup"/>. Здесь добавляются те, что зависят от платформы
    /// или от путей, вычисленных <see cref="AppConfig"/>.
    /// </para>
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        var config = AppConfig.GetInstance();

        services.AddSingleton<ILogFactory>(_ => new BaseLogFactory(new ILogger[]
        {
            new FileLogger(config.GetLogFilePath()),
        }));

        services.AddSingleton<IUIManager, UIManager>();

        // Модели представления страниц регистрировать не обязательно:
        // IoC.Get создаст их через контейнер, внедрив зарегистрированные
        // зависимости. Регистрация нужна, если модель должна быть одиночкой
        // либо требует особого времени жизни.
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<RegistrationPageViewModel>();
    }

    /// <summary>
    /// Цветовые темы и языки интерфейса, поставляемые шаблоном.
    /// <para>
    /// Добавляя тему или язык: положите словарь в <c>Styles/Themes</c> либо
    /// <c>Styles/Languages</c> и допишите одну строку регистрации — страница
    /// настроек подхватит вариант сама.
    /// </para>
    /// </summary>
    private static void RegisterThemesAndLanguages(AppConfig config)
    {
        config.RegisterTheme(AppThemes.Light, "ThemeLightText",
            "avares://EyetrackerGlasses/Styles/Themes/LightTheme.axaml");

        config.RegisterTheme(AppThemes.Dark, "ThemeDarkText",
            "avares://EyetrackerGlasses/Styles/Themes/DarkTheme.axaml");

        config.RegisterLanguage(AppLanguages.English, "LanguageEnglishText",
            "avares://EyetrackerGlasses/Styles/Languages/en-US.axaml");

        config.RegisterLanguage(AppLanguages.Russian, "LanguageRussianText",
            "avares://EyetrackerGlasses/Styles/Languages/ru-RU.axaml");
    }
}
