using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using EyetrackerGlasses.ViewModels;

namespace EyetrackerGlasses
{
    public partial class RegistrationPage : BasePage<RegistrationPageViewModel>
    {
        public RegistrationPage() : this(null)
        {
            
        }
        public RegistrationPage(RegistrationPageViewModel? specificViewModel) : base(specificViewModel)
        {
            InitializeComponent();
        }
    }
}