using Microsoft.UI.Xaml;

namespace SnapStack;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RootFrame.Navigate(typeof(MainPage));
    }
}
