using System.Windows;
using NebulaClean.Services;

namespace NebulaClean;
public partial class LoginWindow : Window
{
    private bool _register;
    public LoginWindow() { InitializeComponent(); }
    private void LoginTab_Click(object sender, RoutedEventArgs e) => SetMode(false);
    private void RegisterTab_Click(object sender, RoutedEventArgs e) => SetMode(true);
    private void SetMode(bool register)
    {
        _register = register; ConfirmPanel.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        FormTitle.Text = register ? "Создать аккаунт" : "Добро пожаловать";
        FormSubtitle.Text = register ? "Зарегистрируйтесь по почте и придумайте надёжный пароль." : "Войдите, чтобы синхронизировать подписку и аккаунт.";
        SubmitButton.Content = register ? "Создать аккаунт  →" : "Войти в аккаунт  →";
        LoginTab.Background = register ? (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#252B40")! : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#4C328B")!;
        RegisterTab.Background = register ? (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#4C328B")! : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#252B40")!;
        StatusLabel.Text = "";
    }
    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "";
        var email = EmailBox.Text.Trim(); var password = PasswordBox.Password;
        if (!email.Contains('@') || password.Length < 8) { StatusLabel.Text = "Введите корректную почту и пароль длиной не менее 8 символов."; return; }
        if (_register && password != ConfirmPasswordBox.Password) { StatusLabel.Text = "Пароли не совпадают."; return; }
        SubmitButton.IsEnabled = false;
        try
        {
            var result = _register ? await AccountService.Instance.RegisterAsync(email, password) : await AccountService.Instance.LoginAsync(email, password);
            if (string.IsNullOrWhiteSpace(result.Token)) throw new InvalidOperationException(result.Error ?? "Не удалось войти.");
            await ContinueToAppAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally { SubmitButton.IsEnabled = true; }
    }
    private async void Google_Click(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "";
        try { await AccountService.Instance.GoogleLoginAsync(); await ContinueToAppAsync(); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private async Task ContinueToAppAsync()
    {
        try
        {
            var update = await AccountService.Instance.GetAppVersionAsync();
            if (update.MustUpdate)
            {
                var answer = MessageBox.Show($"Эта версия NEBULA CLEAN устарела и больше не поддерживается.\n\n{update.Message}\n\nОткрыть страницу обновления?", "Требуется обновление", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes && Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
                AccountService.Instance.Logout(); return;
            }
        }
        catch (Exception ex) { MessageBox.Show("Не удалось проверить версию приложения. Для входа и проверки обновлений требуется доступный сервер.\n\n" + ex.Message, "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Warning); AccountService.Instance.Logout(); return; }
        var main = new MainWindow(); Application.Current.MainWindow = main; main.Show(); Close();
    }
}
