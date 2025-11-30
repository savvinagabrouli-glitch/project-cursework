using System.Windows;
using Cursework.Wpf.ViewModels.Waiter;
using Microsoft.Extensions.DependencyInjection;

namespace Cursework.Wpf.Views.Waiter
{
    public partial class WaiterWindow : Window
    {
        public WaiterWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;

            // берём VM из DI
            var vm = App.Services.GetRequiredService<WaiterWindowViewModel>();
            DataContext = vm;

            // безопасная инициализация
            try
            {
                await vm.InitializeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось загрузить данные для официанта.\n\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
