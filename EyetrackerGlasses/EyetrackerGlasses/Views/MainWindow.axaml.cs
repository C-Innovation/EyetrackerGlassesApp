using Avalonia.Controls;

namespace EyetrackerGlasses.Views;

/// <summary>
/// Главное окно настольного приложения.
/// Содержит <see cref="MainView"/>, чтобы разметка была общей
/// с однооконными платформами.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
