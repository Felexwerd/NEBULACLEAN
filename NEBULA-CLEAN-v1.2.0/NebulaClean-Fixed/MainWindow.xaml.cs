using NebulaClean.Models;
using NebulaClean.Services;
using System.Windows.Navigation;
using System.IO;
using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using System.Text.Json;
using System.Windows.Threading;
using NAudio.Wave;
using System.Windows.Media.Imaging;

namespace NebulaClean;

public partial class MainWindow : Window
{
    private readonly CleanupService _cleanup = new();
    private readonly List<CleanupCategory> _categories;
    private bool _busy;
    private string _selectedDrive = "";
    private string _avatarData = "";
    private readonly string _prefsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NebulaClean", "profile.json");
    private DispatcherTimer? _chatTimer;
    private WaveInEvent? _voiceInput;
    private WaveFileWriter? _voiceWriter;
    private string? _voiceTempPath;
    private bool _isRecordingVoice;

    public MainWindow()
    {
        InitializeComponent();
        _categories = _cleanup.CreateCategories().ToList();
        CategoryList.ItemsSource = _categories;
        LoadDrives();
        LoadLocalPreferences();
        ApplyLanguage((LanguageSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en");
        RefreshDiskInfo();
        UpdateCategoryTotals();
    }

    private void RefreshDiskInfo()
    {
        try
        {
            string systemRoot = string.IsNullOrWhiteSpace(_selectedDrive) ? (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\") : _selectedDrive;
            var drive = new DriveInfo(systemRoot);
            if (!drive.IsReady) return;
            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;
            double pct = total > 0 ? free * 100d / total : 0;
            DiskFreePercent.Text = $"{pct:0}%";
            DiskFreeText.Text = CleanupCategory.FormatBytes(free);
            DiskTotalText.Text = $"Общий объём: {CleanupCategory.FormatBytes(total)}";
            DiskUsageBar.Value = 100 - pct;
            AnalyzerDiskSummary.Text = $"{CleanupCategory.FormatBytes(free)} свободно из {CleanupCategory.FormatBytes(total)}";
            AnalyzerUsageBar.Value = 100 - pct;
        }
        catch (Exception ex)
        {
            DiskFreePercent.Text = "—";
            DiskFreeText.Text = "Недоступно";
            DiskTotalText.Text = "Не удалось прочитать информацию о диске";
            AnalyzerDiskSummary.Text = $"Не удалось прочитать информацию о диске: {ex.Message}";
        }
    }

    private void ShowPage(string page, string eyebrow, string title, string subtitle)
    {
        ChatPage.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        ProfilePage.Visibility = page == "profile" ? Visibility.Visible : Visibility.Collapsed;
        DashboardPage.Visibility = page == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        CleanerPage.Visibility = page == "cleaner" ? Visibility.Visible : Visibility.Collapsed;
        AnalyzerPage.Visibility = page == "analyzer" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        SubscriptionsPage.Visibility = page == "subscriptions" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageEyebrow.Text = eyebrow;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
        var active = new SolidColorBrush(Color.FromRgb(39, 33, 63));
        NavDashboard.Background = page == "dashboard" ? active : Brushes.Transparent;
        NavCleaner.Background = page == "cleaner" ? active : Brushes.Transparent;
        NavAnalyzer.Background = page == "analyzer" ? active : Brushes.Transparent;
        NavHistory.Background = page == "history" ? active : Brushes.Transparent;
        NavSubscriptions.Background = page == "subscriptions" ? active : Brushes.Transparent;
        NavChat.Background = page == "chat" ? active : Brushes.Transparent;
        NavProfile.Background = page == "profile" ? active : Brushes.Transparent;
    }

    private void NavDashboard_Click(object sender, RoutedEventArgs e) => ShowPage("dashboard", "ОБЗОР СИСТЕМЫ", "Всё под контролем.", "Проверим, что занимает место на вашем компьютере.");
    private void NavCleaner_Click(object sender, RoutedEventArgs e) => ShowPage("cleaner", "БЕЗОПАСНАЯ ОЧИСТКА", "Освободите место.", "Выберите категории и проверьте результат до удаления.");
    private void NavAnalyzer_Click(object sender, RoutedEventArgs e) => ShowPage("analyzer", "ХРАНИЛИЩЕ", "Куда уходит место?", "Информация о системном диске.");
    private void NavHistory_Click(object sender, RoutedEventArgs e) => ShowPage("history", "ЖУРНАЛ", "Ваша история.", "Результаты последней операции.");
    private void Settings_Click(object sender, RoutedEventArgs e) { ShowPage("settings", "SETTINGS", "Settings", "Manage account, language and security."); AccountEmailText.Text = AccountService.Instance.CurrentUser?.Email ?? "Account"; }
    private async void Subscriptions_Click(object sender, RoutedEventArgs e) { ShowPage("subscriptions", "ПОДПИСКИ", "Ваш доступ к NEBULA.", "Бесплатный план или временный доступ по промокоду."); await RefreshSubscriptionAsync(); }
    private void LoadDrives()
    {
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).ToList();
        DriveSelector.Items.Clear();
        foreach (var d in drives) DriveSelector.Items.Add(new ComboBoxItem { Content = $"{d.Name}  ·  {CleanupCategory.FormatBytes(d.AvailableFreeSpace)} free", Tag = d.Name });
        string system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";
        var selected = drives.FindIndex(d => string.Equals(d.Name, system, StringComparison.OrdinalIgnoreCase));
        DriveSelector.SelectedIndex = selected >= 0 ? selected : (drives.Count > 0 ? 0 : -1);
        _selectedDrive = DriveSelector.SelectedItem is ComboBoxItem ci ? ci.Tag?.ToString() ?? system : system;
        DriveLabel.Text = string.IsNullOrWhiteSpace(_selectedDrive) ? "◈  ДИСК" : $"◈  {_selectedDrive.TrimEnd('\\')}";
    }

    private void DriveSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string root || string.IsNullOrWhiteSpace(root)) return;
        _selectedDrive = root;
        DriveLabel.Text = $"◈  {root.TrimEnd('\\')}";
        RefreshDiskInfo();
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";
        if (!string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase))
        {
            _categories.Clear();
            var paths = new List<string> { Path.Combine(root, "Temp") };
            if (Directory.Exists(Path.Combine(root, "Windows", "Temp"))) paths.Add(Path.Combine(root, "Windows", "Temp"));
            foreach (var path in paths.Where(Directory.Exists)) _categories.Add(new CleanupCategory { Name = $"Временные файлы {root}", Description = "Только временная папка на выбранном диске; личные файлы не затрагиваются.", LocationLabel = path, Paths = [path], IsSelected = true });
            if (_categories.Count == 0) _categories.Add(new CleanupCategory { Name = $"Нет безопасных временных папок на {root}", Description = "На этом диске не найдены стандартные временные папки. Ничего не будет удалено.", LocationLabel = root, Paths = [], IsSelected = false });
        }
        else
        {
            _categories.Clear(); _categories.AddRange(_cleanup.CreateCategories());
        }
        CategoryList.ItemsSource = null; CategoryList.ItemsSource = _categories; UpdateCategoryTotals();
    }

    private void LoadLocalPreferences()
    {
        try
        {
            if (File.Exists(_prefsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_prefsPath));
                if (doc.RootElement.TryGetProperty("prefix", out var prefix)) ProfilePrefixBox.Text = prefix.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("avatar", out var avatar)) _avatarData = avatar.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("language", out var lang))
                {
                    var code = lang.GetString() ?? "en";
                    LanguageSelector.SelectedIndex = code switch { "ru" => 1, "be" => 2, "uk" => 3, _ => 0 };
                }
                else LanguageSelector.SelectedIndex = 0;
            }
            else LanguageSelector.SelectedIndex = 0;
        } catch { LanguageSelector.SelectedIndex = 0; }
        ProfileEmail.Text = AccountService.Instance.CurrentUser?.Email ?? "Account";
        UpdateProfileAvatarPreview();
    }


    private void ApplyLanguage(string code)
    {
        bool ru = code == "ru", be = code == "be", uk = code == "uk";
        var labels = ru
            ? new[] { "Главная", "Очистка мусора", "Анализ диска", "Подписки", "История", "Чат", "Профиль", "Настройки", "Выйти из аккаунта", "ОБЗОР СИСТЕМЫ", "Всё под контролем.", "Проверим, что занимает место на вашем компьютере.", "Язык интерфейса", "Активировать", "Отправить", "Обновить", "ПРОМОКОД" }
            : be
            ? new[] { "Галоўная", "Ачыстка смецця", "Аналіз дыска", "Падпіскі", "Гісторыя", "Чат", "Профіль", "Налады", "Выйсці з акаўнта", "АГЛЯД СІСТЭМЫ", "Усё пад кантролем.", "Праверце, што займае месца на камп’ютары.", "Мова інтэрфейсу", "Актываваць", "Адправіць", "Абнавіць", "ПРАМОКОД" }
            : uk
            ? new[] { "Головна", "Очищення сміття", "Аналіз диска", "Підписки", "Історія", "Чат", "Профіль", "Налаштування", "Вийти з акаунта", "ОГЛЯД СИСТЕМИ", "Усе під контролем.", "Перевірте, що займає місце на комп’ютері.", "Мова інтерфейсу", "Активувати", "Надіслати", "Оновити", "ПРОМОКОД" }
            : new[] { "Home", "Junk cleanup", "Disk analyzer", "Subscriptions", "History", "Chat", "Profile", "Settings", "Log out", "SYSTEM OVERVIEW", "Everything under control.", "Check what is taking up space on your computer.", "Interface language", "Activate", "Send", "Refresh", "PROMO CODE" };
        NavDashboardLabel.Text = labels[0]; NavCleanerLabel.Text = labels[1]; NavAnalyzerLabel.Text = labels[2]; NavSubscriptionsLabel.Text = labels[3]; NavHistoryLabel.Text = labels[4]; NavChatLabel.Text = labels[5]; NavProfileLabel.Text = labels[6];
        if (SettingsNavLabel != null) SettingsNavLabel.Text = labels[7];
        if (LogoutNavLabel != null) LogoutNavLabel.Text = labels[8];
        PageEyebrow.Text = labels[9]; PageTitle.Text = labels[10]; PageSubtitle.Text = labels[11];
        TranslateVisibleStaticText(code);
        // Update visible account controls too; changing language no longer affects only the sidebar.
        foreach (var button in FindVisualChildren<Button>(this))
        {
            var content = button.Content?.ToString();
            if (content == "Активировать" || content == "Activate" || content == "Актываваць" || content == "Активувати") button.Content = labels[13];
            else if (content == "Отправить" || content == "Send" || content == "Адправіць" || content == "Надіслати") button.Content = labels[14];
            else if (content == "Обновить" || content == "Refresh" || content == "Абнавіць" || content == "Оновити") button.Content = labels[15];
            else if (content == "Выйти из аккаунта" || content == "Log out" || content == "Выйсці з акаўнта" || content == "Вийти з акаунта") button.Content = labels[8];
        }
        foreach (var tb in FindVisualChildren<TextBlock>(this))
        {
            if (tb.Text == "Язык интерфейса" || tb.Text == "Interface language" || tb.Text == "Мова інтэрфейсу") tb.Text = labels[12];
            if (tb.Text == "ПРОМОКОД" || tb.Text == "PROMO CODE") tb.Text = labels[16];
        }
    }


    private readonly Dictionary<string, (string en, string ru, string be, string uk)> _translations = new(StringComparer.Ordinal)
    {
        ["Настройки аккаунта"]=("Account settings","Настройки аккаунта","Налады акаўнта","Налаштування акаунта"),
        ["Изменить пароль"]=("Change password","Изменить пароль","Змяніць пароль","Змінити пароль"),
        ["Доступно для аккаунтов, созданных по почте и паролю."]=("Available for email and password accounts.","Доступно для аккаунтов, созданных по почте и паролю.","Даступна для акаўнтаў, створаных праз пошту і пароль.","Доступно для акаунтів, створених через пошту й пароль."),
        ["Введите текущий пароль"]=("Enter current password","Введите текущий пароль","Увядзіце бягучы пароль","Введіть поточний пароль"),
        ["Введите новый пароль (минимум 8 символов)"]=("Enter new password (at least 8 characters)","Введите новый пароль (минимум 8 символов)","Увядзіце новы пароль (мінімум 8 сімвалаў)","Введіть новий пароль (мінімум 8 символів)"),
        ["Повторите новый пароль"]=("Confirm new password","Повторите новый пароль","Паўтарыце новы пароль","Повторіть новий пароль"),
        ["Сохранить новый пароль"]=("Save new password","Сохранить новый пароль","Захаваць новы пароль","Зберегти новий пароль"),
        ["Мова інтэрфейсу"]=("Interface language","Язык интерфейса","Мова інтэрфейсу","Мова інтерфейсу"),
        ["Язык интерфейса"]=("Interface language","Язык интерфейса","Мова інтэрфейсу","Мова інтерфейсу"),
        ["По умолчанию используется English. Выберите язык и перезапустите приложение для полного применения."]=("English is the default. Select a language; restart to apply everywhere.","По умолчанию используется English. Выберите язык и перезапустите приложение для полного применения.","Па змаўчанні выкарыстоўваецца English. Выберыце мову; перазапусціце праграму.","За замовчуванням використовується English. Виберіть мову; перезапустіть застосунок."),
        ["Безопасность очистки"]=("Cleanup safety","Безопасность очистки","Бяспека ачысткі","Безпека очищення"),
        ["Перед удалением всегда требуется подтверждение. Файлы, которые заняты или недоступны, пропускаются."]=("Deletion always requires confirmation. Locked or inaccessible files are skipped.","Перед удалением всегда требуется подтверждение. Файлы, которые заняты или недоступны, пропускаются.","Выдаленне патрабуе пацвярджэння. Занятыя або недаступныя файлы прапускаюцца.","Видалення завжди потребує підтвердження. Зайняті або недоступні файли пропускаються."),
        ["Выйти из аккаунта"]=("Log out","Выйти из аккаунта","Выйсці з акаўнта","Вийти з акаунта"),
        ["Общий чат NEBULA"]=("NEBULA community chat","Общий чат NEBULA","Агульны чат NEBULA","Спільний чат NEBULA"),
        ["Сообщения видны вошедшим пользователям приложения."]=("Messages are visible to signed-in users.","Сообщения видны вошедшим пользователям приложения.","Паведамленні бачныя карыстальнікам, якія ўвайшлі.","Повідомлення бачать користувачі, які увійшли."),
        ["Обновить"]=("Refresh","Обновить","Абнавіць","Оновити"), ["😊 Эмодзи"]=("😊 Emoji","😊 Эмодзи","😊 Эмодзі","😊 Емодзі"),
        ["Отправить"]=("Send","Отправить","Адправіць","Надіслати"), ["＋ Фото"]=("＋ Photo","＋ Фото","＋ Фота","＋ Фото"), ["🎙 Голос"]=("🎙 Voice","🎙 Голос","🎙 Голас","🎙 Голос"),
        ["Профиль"]=("Profile","Профиль","Профіль","Профіль"), ["Ваше имя в чате, префикс и аватар."]=("Your chat name, prefix and avatar.","Ваше имя в чате, префикс и аватар.","Вашае імя ў чаце, прэфікс і аватар.","Ваше ім’я в чаті, префікс та аватар."),
        ["Выбрать аватарку…"]=("Choose avatar…","Выбрать аватарку…","Выбраць аватар…","Вибрати аватар…"), ["Префикс в чате"]=("Chat prefix","Префикс в чате","Прэфікс у чаце","Префікс у чаті"), ["Сохранить профиль"]=("Save profile","Сохранить профиль","Захаваць профіль","Зберегти профіль"),
        ["План подписки"]=("Subscription plan","План подписки","План падпіскі","План підписки"), ["Активировать"]=("Activate","Активировать","Актываваць","Активувати"), ["ПРОМОКОД"]=("PROMO CODE","ПРОМОКОД","ПРАМОКОД","ПРОМОКОД"),
        ["История"]=("History","История","Гісторыя","Історія"), ["Быстрые действия"]=("Quick actions","Быстрые действия","Хуткія дзеянні","Швидкі дії"), ["РЕКОМЕНДУЕМ"]=("RECOMMENDED","РЕКОМЕНДУЕМ","РЭКАМЕНДУЕМ","РЕКОМЕНДУЄМО"),
        ["Начать сканирование →"]=("Start scan →","Начать сканирование →","Пачаць сканаванне →","Почати сканування →"), ["Проверить мусор"]=("Scan junk","Проверить мусор","Праверыць смецце","Перевірити сміття"), ["Большие файлы"]=("Large files","Большие файлы","Вялікія файлы","Великі файли"),
        ["Свободно на выбранном диске:"]=("Free space on selected drive:","Свободно на выбранном диске:","Вольнае месца на выбраным дыску:","Вільне місце на вибраному диску:"), ["Потенциальная очистка"]=("Potential cleanup","Потенциальная очистка","Магчымая ачыстка","Потенційне очищення"),
        ["Защита данных"]=("DATA PROTECTION","Защита данных","АБАРОНА ДАДЗЕНЫХ","ЗАХИСТ ДАНИХ"), ["Настройки"]=("Settings","Настройки","Налады","Налаштування"),
        ["РАБОЧЕЕ ПРОСТРАНСТВО"]=("WORKSPACE","РАБОЧЕЕ ПРОСТРАНСТВО","ПРАЦОЎНАЯ ПРАСТОРА","РОБОЧИЙ ПРОСТІР"),
        ["Перед удалением вы увидите, что именно будет очищено."]=("Review exactly what will be cleaned before deletion.","Перед удалением вы увидите, что именно будет очищено.","Перад выдаленнем вы ўбачыце, што будзе ачышчана.","Перед видаленням ви побачите, що саме буде очищено."),
        ["ОБЗОР СИСТЕМЫ"]=("SYSTEM OVERVIEW","ОБЗОР СИСТЕМЫ","АГЛЯД СІСТЭМЫ","ОГЛЯД СИСТЕМИ"),
        ["Проверим, что занимает место на вашем компьютере."]=("See what is using space on your computer.","Проверим, что занимает место на вашем компьютере.","Праверым, што займае месца на камп’ютары.","Перевіримо, що займає місце на вашому комп’ютері."),
        ["Локальный режим"]=("Local mode","Локальный режим","Лакальны рэжым","Локальний режим"),
        ["Освободим место."]=("Free up space.","Освободим место.","Вызвалім месца.","Звільнимо місце."),
        ["Найдите временные файлы и освободите место без лишнего риска."]=("Find temporary files and free space safely.","Найдите временные файлы и освободите место без лишнего риска.","Знайдзіце часовыя файлы і бяспечна вызваліце месца.","Знайдіть тимчасові файли та безпечно звільніть місце."),
        ["свободно"]=("free","свободно","вольна","вільно"), ["СВОБОДНО НА ВЫБРАННОМ ДИСКЕ:"]=("FREE SPACE ON SELECTED DRIVE:","СВОБОДНО НА ВЫБРАННОМ ДИСКЕ:","ВОЛЬНАЕ МЕСЦА НА ВЫБРАНЫМ ДЫСКУ:","ВІЛЬНЕ МІСЦЕ НА ВИБРАНОМУ ДИСКУ:"),
        ["Общий объём: —"]=("Total size: —","Общий объём: —","Агульны аб’ём: —","Загальний обсяг: —"), ["ПОТЕНЦИАЛЬНАЯ ОЧИСТКА"]=("POTENTIAL CLEANUP","ПОТЕНЦИАЛЬНАЯ ОЧИСТКА","МАГЧЫМАЯ АЧЫСТКА","ПОТЕНЦІЙНЕ ОЧИЩЕННЯ"),
        ["Запустите сканирование"]=("Start a scan","Запустите сканирование","Запусціце сканаванне","Запустіть сканування"), ["Сканирование"]=("Scanning","Сканирование","Сканаванне","Сканування"), ["Открыть анализатор"]=("Open analyzer","Открыть анализатор","Адкрыць аналізатар","Відкрити аналізатор"), ["Результаты очисток"]=("Cleanup results","Результаты очисток","Вынікі ачысткі","Результати очищення"),
        ["Категории очистки"]=("Cleanup categories","Категории очистки","Катэгорыі ачысткі","Категорії очищення"), ["Сканирование ничего не удаляет."]=("Scanning does not delete anything.","Сканирование ничего не удаляет.","Сканаванне нічога не выдаляе.","Сканування нічого не видаляє."), ["Сканировать"]=("Scan","Сканировать","Сканаваць","Сканувати"),
        ["Обрабатываются только файлы верхнего уровня выбранных папок."]=("Only top-level files in selected folders are processed.","Обрабатываются только файлы верхнего уровня выбранных папок.","Апрацоўваюцца толькі файлы верхняга ўзроўню выбраных папак.","Обробляються лише файли верхнього рівня вибраних папок."), ["Очистить выбранное"]=("Clean selected","Очистить выбранное","Ачысціць выбранае","Очистити вибране"),
        ["Анализ выбранного диска"]=("Selected drive analysis","Анализ выбранного диска","Аналіз выбранага дыска","Аналіз вибраного диска"), ["Загрузка данных…"]=("Loading data…","Загрузка данных…","Загрузка даных…","Завантаження даних…"),
        ["Ищет крупные файлы на выбранном диске. Само по себе ничего не удаляет."]=("Finds large files on the selected drive; nothing is deleted automatically.","Ищет крупные файлы на выбранном диске. Само по себе ничего не удаляет.","Шукае вялікія файлы на выбраным дыску; нічога аўтаматычна не выдаляе.","Шукає великі файли на вибраному диску; нічого автоматично не видаляє."), ["Найти большие файлы"]=("Find large files","Найти большие файлы","Знайсці вялікія файлы","Знайти великі файли"),
        ["Минимальный размер:"]=("Minimum size:","Минимальный размер:","Мінімальны памер:","Мінімальний розмір:"), ["Сканирование ещё не запускалось"]=("Scan has not started yet","Сканирование ещё не запускалось","Сканаванне яшчэ не запускалася","Сканування ще не запускалося"),
        ["Открыть расположение"]=("Open location","Открыть расположение","Адкрыць размяшчэнне","Відкрити розташування"), ["Удалить выбранный файл…"]=("Delete selected file…","Удалить выбранный файл…","Выдаліць выбраны файл…","Видалити вибраний файл…"),
        ["История операций"]=("Operation history","История операций","Гісторыя аперацый","Історія операцій"), ["Пока очисток не было. Результаты появятся здесь после первой операции."]=("No cleanups yet. Results will appear here after the first operation.","Пока очисток не было. Результаты появятся здесь после первой операции.","Пакуль ачысткі не выконваліся. Вынікі з’явяцца пасля першай аперацыі.","Очищень ще не було. Результати з’являться тут після першої операції."),
        ["Ваши подписки"]=("Your subscriptions","Ваши подписки","Вашы падпіскі","Ваші підписки"), ["Выберите бесплатный план или активируйте промокод."]=("Use the free plan or redeem a promo code.","Выберите бесплатный план или активируйте промокод.","Выберыце бясплатны план або актывуйце промакод.","Виберіть безкоштовний план або активуйте промокод."),
        ["Бесплатная"]=("Free","Бесплатная","Бясплатная","Безкоштовна"), ["Базовые функции очистки и анализа диска."]=("Basic cleanup and disk analysis features.","Базовые функции очистки и анализа диска.","Базавыя функцыі ачысткі і аналізу дыска.","Базові функції очищення й аналізу диска."), ["Доступна всем"]=("Available to everyone","Доступна всем","Даступна ўсім","Доступно всім"),
        ["По промокоду"]=("Promo code","По промокоду","Па промакодзе","За промокодом"), ["Временный доступ по коду, который создаёт администратор."]=("Temporary access using a code created by an administrator.","Временный доступ по коду, который создаёт администратор.","Часовы доступ па кодзе, які стварае адміністратар.","Тимчасовий доступ за кодом, який створює адміністратор."), ["Не активирована"]=("Not activated","Не активирована","Не актывавана","Не активовано"),
        ["АДМИН-ПАНЕЛЬ · ПРОМОКОДЫ"]=("ADMIN PANEL · PROMO CODES","АДМИН-ПАНЕЛЬ · ПРОМОКОДЫ","ПАНЭЛЬ АДМІНА · ПРАМОКОДЫ","ПАНЕЛЬ АДМІНА · ПРОМОКОДИ"), ["Создать код с ограниченным сроком и количеством активаций."]=("Create a code with expiry and usage limits.","Создать код с ограниченным сроком и количеством активаций.","Стварыць код з абмежаваннем тэрміну і колькасці актывацый.","Створити код з обмеженням строку та кількості активацій."),
        ["Срок, дней"]=("Duration, days","Срок, дней","Тэрмін, дзён","Термін, днів"), ["Макс. активаций"]=("Max activations","Макс. активаций","Макс. актывацый","Макс. активацій"), ["Создать промокод"]=("Create promo code","Создать промокод","Стварыць промакод","Створити промокод"),
        ["ПРИНУДИТЕЛЬНОЕ ОБНОВЛЕНИЕ"]=("REQUIRED UPDATE","ПРИНУДИТЕЛЬНОЕ ОБНОВЛЕНИЕ","АБАВЯЗКОВАЕ АБНАЎЛЕННЕ","ОБОВ’ЯЗКОВЕ ОНОВЛЕННЯ"), ["Версии ниже минимальной будут заблокированы при следующем входе."]=("Versions below the minimum will be blocked at next sign-in.","Версии ниже минимальной будут заблокированы при следующем входе.","Версіі ніжэй за мінімальную будуць заблакаваныя пры наступным уваходзе.","Версії нижче мінімальної буде заблоковано під час наступного входу."),
        ["Последняя версия"]=("Latest version","Последняя версия","Апошняя версія","Остання версія"), ["Минимальная версия"]=("Minimum version","Минимальная версия","Мінімальная версія","Мінімальна версія"), ["Ссылка на скачивание обновления"]=("Update download URL","Ссылка на скачивание обновления","Спасылка для спампоўвання абнаўлення","Посилання для завантаження оновлення"), ["Сообщение пользователям"]=("Message to users","Сообщение пользователям","Паведамленне карыстальнікам","Повідомлення користувачам"),
        ["Установите актуальную версию NEBULA CLEAN."]=("Install the latest version of NEBULA CLEAN.","Установите актуальную версию NEBULA CLEAN.","Усталюйце актуальную версію NEBULA CLEAN.","Установіть актуальну версію NEBULA CLEAN."), ["Опубликовать требования обновления"]=("Publish update requirements","Опубликовать требования обновления","Апублікаваць патрабаванні абнаўлення","Опублікувати вимоги оновлення"),
        ["Готово к работе"]=("Ready","Готово к работе","Гатова да працы","Готово до роботи"),
    };

    private readonly Dictionary<DependencyObject, string> _originalStaticText = new();

    private void TranslateVisibleStaticText(string code)
    {
        int idx = code switch { "ru" => 1, "be" => 2, "uk" => 3, _ => 0 };
        foreach (var tb in FindVisualChildren<TextBlock>(this))
        {
            if (!_originalStaticText.TryGetValue(tb, out var original)) { original = tb.Text; _originalStaticText[tb] = original; }
            if (_translations.TryGetValue(original, out var t)) tb.Text = idx switch { 1 => t.ru, 2 => t.be, 3 => t.uk, _ => t.en };
        }
        foreach (var b in FindVisualChildren<Button>(this))
        {
            if (b.Content is not string value) continue;
            if (!_originalStaticText.TryGetValue(b, out var original)) { original = value; _originalStaticText[b] = original; }
            if (_translations.TryGetValue(original, out var t)) b.Content = idx switch { 1 => t.ru, 2 => t.be, 3 => t.uk, _ => t.en };
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void SaveLocalPreferences()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_prefsPath)!);
        var lang = (LanguageSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
        File.WriteAllText(_prefsPath, JsonSerializer.Serialize(new { prefix = ProfilePrefixBox.Text.Trim(), avatar = _avatarData, language = lang }));
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageSelector.SelectedItem is not ComboBoxItem item) return;
        try { SaveLocalPreferences(); ApplyLanguage(item.Tag?.ToString() ?? "en"); LanguageStatus.Text = item.Content + " selected."; }
        catch (Exception ex) { LanguageStatus.Text = ex.Message; }
    }

    private void UpdateProfileAvatarPreview()
    {
        try
        {
            ProfileAvatarImage.Source = null;
            ProfileAvatarFallback.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(_avatarData) || !_avatarData.StartsWith("data:image")) return;
            var data = _avatarData[(_avatarData.IndexOf(',') + 1)..];
            using var ms = new MemoryStream(Convert.FromBase64String(data));
            var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 240; bmp.StreamSource = ms; bmp.EndInit();
            ProfileAvatarImage.Source = bmp; ProfileAvatarFallback.Visibility = Visibility.Collapsed;
        }
        catch { ProfileAvatarImage.Source = null; ProfileAvatarFallback.Visibility = Visibility.Visible; }
    }

    private void ChooseAvatar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Выберите аватар", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.webp", CheckFileExists = true };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            if (bytes.Length > 2 * 1024 * 1024) { ProfileStatus.Text = "Аватар должен быть не больше 2 МБ."; return; }
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant(); var mime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".bmp" ? "image/bmp" : "image/jpeg";
            _avatarData = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            UpdateProfileAvatarPreview(); ProfileStatus.Text = "Аватар выбран. Нажмите «Сохранить профиль».";
        }
        catch (Exception ex) { ProfileStatus.Text = ex.Message; }
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try { SaveLocalPreferences(); await AccountService.Instance.SaveProfileAsync(ProfilePrefixBox.Text.Trim(), _avatarData); ProfileStatus.Text = "Профиль сохранён и синхронизирован."; }
        catch (Exception ex) { ProfileStatus.Text = "Локально сохранено; серверная синхронизация недоступна: " + ex.Message; }
    }

    private async void NavProfile_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("profile", "PROFILE", "Your profile", "Set your chat prefix and avatar.");
        ProfileEmail.Text = AccountService.Instance.CurrentUser?.Email ?? "Account";
        try { var profile = await AccountService.Instance.GetProfileAsync(); ProfilePrefixBox.Text = profile.Prefix; _avatarData = profile.Avatar; UpdateProfileAvatarPreview(); }
        catch { /* Local profile remains available if the server is offline. */ }
    }

    private async void NavChat_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("chat", "COMMUNITY", "NEBULA chat", "Messages are shared through your configured NEBULA server.");
        await LoadChatAsync();
        if (_chatTimer == null) { _chatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) }; _chatTimer.Tick += async (_, _) => await LoadChatAsync(); }
        _chatTimer.Start();
    }

    private async void RefreshChat_Click(object sender, RoutedEventArgs e) => await LoadChatAsync();
    private async Task LoadChatAsync()
    {
        try
        {
            var messages = await AccountService.Instance.GetChatMessagesAsync();
            ChatMessagesList.ItemsSource = messages;
            if (ChatMessagesList.Items.Count > 0) ChatMessagesList.ScrollIntoView(ChatMessagesList.Items[ChatMessagesList.Items.Count - 1]);
            ChatStatus.Text = "Подключено · обновление каждые 8 секунд";
        }
        catch (Exception ex) { ChatStatus.Text = "Чат недоступен: " + ex.Message; }
    }

    private void ChatMessage_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement el && el.DataContext is ChatMessageInfo message && message.MessageType is "image" or "audio")
        {
            try { var comma=message.Content.IndexOf(','); var bytes=Convert.FromBase64String(message.Content[(comma+1)..]); var ext=message.MessageType=="image"?(message.Content.StartsWith("data:image/png")?".png":message.Content.StartsWith("data:image/webp")?".webp":".jpg"):".wav"; var file=Path.Combine(Path.GetTempPath(),$"nebula-chat-{Guid.NewGuid():N}{ext}"); File.WriteAllBytes(file,bytes); Process.Start(new ProcessStartInfo(file){UseShellExecute=true}); }
            catch(Exception ex){ChatStatus.Text="Не удалось открыть сообщение: "+ex.Message;}
        }
    }

    private void ToggleEmojiPicker_Click(object sender, RoutedEventArgs e) => EmojiPicker.Visibility = EmojiPicker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ChatMessagesList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ChatAvatar_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (b.Tag is not ChatMessageInfo message || string.IsNullOrWhiteSpace(message.Avatar) || !message.Avatar.StartsWith("data:image")) return;
            try { var data=message.Avatar[(message.Avatar.IndexOf(',')+1)..]; using var ms=new MemoryStream(Convert.FromBase64String(data)); var bmp=new BitmapImage(); bmp.BeginInit(); bmp.CacheOption=BitmapCacheOption.OnLoad; bmp.DecodePixelWidth=80; bmp.StreamSource=ms; bmp.EndInit(); b.Content=new Image { Source=bmp, Width=38, Height=38, Stretch=Stretch.UniformToFill }; b.Padding=new Thickness(0); }
            catch { b.Content="✦"; }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ChatAvatar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ChatMessageInfo message) ShowUserProfile(message);
    }

    private void ShowUserProfile(ChatMessageInfo user)
    {
        var role = string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase) ? "ADMIN" : "FREE";
        var win = new Window { Title = "Профиль пользователя", Width = 390, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(15, 18, 30)), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
        var avatar = new Image { Width = 112, Height = 112, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,18) };
        try { if (!string.IsNullOrWhiteSpace(user.Avatar) && user.Avatar.StartsWith("data:image")) { var b64 = user.Avatar.Substring(user.Avatar.IndexOf(',')+1); using var ms = new MemoryStream(Convert.FromBase64String(b64)); var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); avatar.Source = bmp; } else avatar.Source = null; } catch { }
        panel.Children.Add(avatar);
        if (avatar.Source == null) panel.Children.Insert(0, new TextBlock { Text = "✦", FontSize = 64, Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = user.Email, FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,14) });
        panel.Children.Add(new TextBlock { Text = $"Префикс: {(string.IsNullOrWhiteSpace(user.Prefix) ? "free" : user.Prefix)}", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(196,181,253)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,8) });
        panel.Children.Add(new TextBlock { Text = $"Роль: {role}", FontSize = 14, Foreground = role == "ADMIN" ? Brushes.Gold : Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,20) });
        var close = new Button { Content = "Закрыть", Padding = new Thickness(24,10,24,10), Background = new SolidColorBrush(Color.FromRgb(124,58,237)), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        close.Click += (_,_) => win.Close(); panel.Children.Add(close); win.Content = panel; win.ShowDialog();
    }

    private async void SendChat_Click(object sender, RoutedEventArgs e)
    {
        var message = ChatInput.Text.Trim(); if (message.Length == 0) return;
        if (message.Length > 1000) { ChatStatus.Text = "Сообщение слишком длинное (максимум 1000 символов)."; return; }
        try { SaveLocalPreferences(); await AccountService.Instance.SaveProfileAsync(ProfilePrefixBox.Text.Trim(), _avatarData); await AccountService.Instance.SendChatMessageAsync(message, ProfilePrefixBox.Text.Trim(), _avatarData); ChatInput.Clear(); await LoadChatAsync(); ChatStatus.Text = "Сообщение отправлено."; }
        catch (Exception ex) { ChatStatus.Text = "Не удалось отправить: " + ex.Message; }
    }

    private async void SendPhoto_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Выберите фотографию", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp", CheckFileExists = true };
        if (dlg.ShowDialog() != true) return;
        try { var bytes = await File.ReadAllBytesAsync(dlg.FileName); if (bytes.Length > 5 * 1024 * 1024) { ChatStatus.Text = "Фотография должна быть не больше 5 МБ."; return; }
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant(); var mime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".bmp" ? "image/bmp" : "image/jpeg";
            await AccountService.Instance.SaveProfileAsync(ProfilePrefixBox.Text.Trim(), _avatarData); await AccountService.Instance.SendChatMediaAsync($"data:{mime};base64,{Convert.ToBase64String(bytes)}", "image", ProfilePrefixBox.Text.Trim(), _avatarData); await LoadChatAsync(); ChatStatus.Text = "Фотография отправлена.";
        } catch (Exception ex) { ChatStatus.Text = "Не удалось отправить фотографию: " + ex.Message; }
    }

    private void VoiceMessage_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRecordingVoice)
        {
            try { _voiceTempPath = Path.Combine(Path.GetTempPath(), $"nebula-voice-{Guid.NewGuid():N}.wav"); _voiceInput = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) }; _voiceWriter = new WaveFileWriter(_voiceTempPath, _voiceInput.WaveFormat); _voiceInput.DataAvailable += (_,a) => _voiceWriter?.Write(a.Buffer,0,a.BytesRecorded); _voiceInput.RecordingStopped += async (_,a) => { try { _voiceWriter?.Dispose(); _voiceWriter = null; _voiceInput?.Dispose(); _voiceInput = null; if (a.Exception != null) throw a.Exception; if (_voiceTempPath != null && File.Exists(_voiceTempPath)) { var bytes = await File.ReadAllBytesAsync(_voiceTempPath); if (bytes.Length > 5 * 1024 * 1024) throw new InvalidOperationException("Запись слишком длинная (максимум около 5 МБ)."); await Dispatcher.InvokeAsync(async () => { try { await AccountService.Instance.SaveProfileAsync(ProfilePrefixBox.Text.Trim(), _avatarData); await AccountService.Instance.SendChatMediaAsync($"data:audio/wav;base64,{Convert.ToBase64String(bytes)}", "audio", ProfilePrefixBox.Text.Trim(), _avatarData); await LoadChatAsync(); ChatStatus.Text = "Голосовое сообщение отправлено."; } catch(Exception ex) { ChatStatus.Text = "Не удалось отправить голосовое: " + ex.Message; } finally { try { File.Delete(_voiceTempPath); } catch {} } }); } } catch(Exception ex) { await Dispatcher.InvokeAsync(() => ChatStatus.Text = "Ошибка записи: " + ex.Message); } }; _voiceInput.StartRecording(); _isRecordingVoice = true; VoiceMessageButton.Content = "■ Остановить и отправить"; ChatStatus.Text = "Идёт запись голосового сообщения… нажмите кнопку ещё раз для отправки."; }
            catch (Exception ex) { ChatStatus.Text = "Не удалось начать запись: " + ex.Message; }
        }
        else { _isRecordingVoice = false; VoiceMessageButton.Content = "🎙 Голос"; _voiceInput?.StopRecording(); }
    }

    private void Emoji_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string emoji) { ChatInput.Text += emoji; ChatInput.CaretIndex = ChatInput.Text.Length; ChatInput.Focus(); EmojiPicker.Visibility = Visibility.Collapsed; }
    }

    private async Task RefreshSubscriptionAsync() { try { var sub = await AccountService.Instance.GetSubscriptionAsync(); FreePlanStatus.Text = sub.Plan == "Free" ? "Текущий план" : "Базовый план"; PromoPlanStatus.Text = sub.Plan == "Promo" ? "Активна" : "Не активирована"; SubscriptionDetail.Text = sub.Plan == "Promo" ? $"План: PROMO · действует до {sub.ExpiresAtUtc?.ToLocalTime():dd.MM.yyyy HH:mm}" : "План: FREE · без срока действия"; AdminPromoPanel.Visibility = sub.IsAdmin ? Visibility.Visible : Visibility.Collapsed; } catch (Exception ex) { SubscriptionDetail.Text = ex.Message; } }
    private async void RedeemPromo_Click(object sender, RoutedEventArgs e) { try { var sub = await AccountService.Instance.RedeemPromoAsync(PromoCodeBox.Text.Trim()); PromoStatus.Text = sub.Message; await RefreshSubscriptionAsync(); } catch (Exception ex) { PromoStatus.Text = ex.Message; } }
    private async void PublishVersion_Click(object sender, RoutedEventArgs e) { try { await AccountService.Instance.UpdateVersionAsync(LatestVersionBox.Text.Trim(), MinimumVersionBox.Text.Trim(), UpdateUrlBox.Text.Trim(), UpdateMessageBox.Text.Trim()); VersionAdminResult.Text = "Настройки обновления сохранены. Старые версии будут заблокированы при следующей проверке."; } catch (Exception ex) { VersionAdminResult.Text = ex.Message; } }
    private async void CreatePromo_Click(object sender, RoutedEventArgs e) { if (!int.TryParse(PromoDaysBox.Text, out int days) || !int.TryParse(PromoUsesBox.Text, out int uses)) { AdminPromoResult.Text = "Срок и количество активаций должны быть числами."; return; } try { var promo = await AccountService.Instance.CreatePromoAsync(AdminPromoCodeBox.Text.Trim(), days, uses); AdminPromoResult.Text = $"Создан код {promo.Code}; срок действия кода — до {promo.ExpiresAtUtc.ToLocalTime():dd.MM.yyyy}. Доступ после активации: {days} дней."; } catch (Exception ex) { AdminPromoResult.Text = ex.Message; } }
    private void PasswordFields_Changed(object sender, RoutedEventArgs e)
    {
        if (CurrentPasswordHint != null) CurrentPasswordHint.Visibility = string.IsNullOrEmpty(CurrentPasswordBox.Password) ? Visibility.Visible : Visibility.Collapsed;
        if (NewPasswordHint != null) NewPasswordHint.Visibility = string.IsNullOrEmpty(NewPasswordBox.Password) ? Visibility.Visible : Visibility.Collapsed;
        if (ConfirmPasswordHint != null) ConfirmPasswordHint.Visibility = string.IsNullOrEmpty(ConfirmNewPasswordBox.Password) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e) { PasswordStatus.Text = ""; if (NewPasswordBox.Password.Length < 8) { PasswordStatus.Text = "Новый пароль должен содержать минимум 8 символов."; return; } if (NewPasswordBox.Password != ConfirmNewPasswordBox.Password) { PasswordStatus.Text = "Новые пароли не совпадают."; return; } try { await AccountService.Instance.ChangePasswordAsync(CurrentPasswordBox.Password, NewPasswordBox.Password); PasswordStatus.Text = "Пароль успешно изменён."; CurrentPasswordBox.Clear(); NewPasswordBox.Clear(); ConfirmNewPasswordBox.Clear(); } catch (Exception ex) { PasswordStatus.Text = ex.Message; } }
    private void Logout_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show("Выйти из аккаунта?", "NEBULA CLEAN", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; AccountService.Instance.Logout(); var login = new LoginWindow(); Application.Current.MainWindow = login; login.Show(); Close(); }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ShowPage("cleaner", "БЕЗОПАСНАЯ ОЧИСТКА", "Освободите место.", "Выберите категории и проверьте результат до удаления.");
        SetBusy(true, "Сканирование выбранных категорий…");
        try
        {
            await _cleanup.ScanAsync(_categories);
            CategoryList.Items.Refresh();
            UpdateCategoryTotals();
            StatusText.Text = "Сканирование завершено. Файлы не удалялись.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Сканирование отменено."; }
        catch (Exception ex) { MessageBox.Show($"Не удалось завершить сканирование.\n\n{ex.Message}", "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _categories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Выберите хотя бы одну категорию.", "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        long estimate = selected.Sum(c => c.SizeBytes);
        string message = "Будут удалены файлы из выбранных временных папок и кэшей, включая вложенные папки. Личные документы, папки загрузок и системные файлы Windows не входят в очистку.\n\n"
            + string.Join("\n", selected.Select(c => $"• {c.Name} — {c.SizeText}"))
            + $"\n\nОценка объёма: {CleanupCategory.FormatBytes(estimate)}\n\nПродолжить? Занятые и недоступные файлы будут пропущены.";
        if (MessageBox.Show(message, "Подтверждение очистки", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetBusy(true, "Удаление выбранных файлов…");
        try
        {
            var result = await _cleanup.CleanAsync(selected);
            HistoryText.Text = $"{DateTime.Now:dd.MM.yyyy HH:mm}\nОсвобождено: {CleanupCategory.FormatBytes(result.FreedBytes)}\nУдалено файлов: {result.DeletedFiles:N0}\nПропущено: {result.SkippedFiles:N0}";
            MessageBox.Show($"Очистка завершена.\n\nОсвобождено: {CleanupCategory.FormatBytes(result.FreedBytes)}\nУдалено файлов: {result.DeletedFiles:N0}\nПропущено: {result.SkippedFiles:N0}", "NEBULA CLEAN — готово", MessageBoxButton.OK, MessageBoxImage.Information);
            await _cleanup.ScanAsync(_categories);
            CategoryList.Items.Refresh();
            RefreshDiskInfo();
            UpdateCategoryTotals();
            StatusText.Text = "Очистка завершена.";
        }
        catch (Exception ex) { MessageBox.Show($"Очистка завершилась с ошибкой.\n\n{ex.Message}", "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private async void LargeFilesScan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        long threshold = 500L * 1024 * 1024;
        if (LargeFilesThreshold.SelectedItem is ComboBoxItem item && long.TryParse(item.Tag?.ToString(), out long parsed)) threshold = parsed;
        SetBusy(true, $"Поиск больших файлов на диске {_selectedDrive}… Это может занять несколько минут.");
        LargeFilesSummary.Text = "Идёт полный обход доступных папок…";
        LargeFilesGrid.ItemsSource = null;
        var progress = new Progress<string>(message => LargeFilesSummary.Text = message);
        try
        {
            string root = string.IsNullOrWhiteSpace(_selectedDrive) ? (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\") : _selectedDrive;
            var files = await _cleanup.FindLargeFilesAsync(root, threshold, progress: progress);
            LargeFilesGrid.ItemsSource = files;
            LargeFilesSummary.Text = $"Найдено файлов: {files.Count:N0} · показаны крупнейшие доступные файлы";
            StatusText.Text = "Анализ больших файлов завершён.";
        }
        catch (Exception ex) { MessageBox.Show($"Не удалось завершить анализ.\n\n{ex.Message}", "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void OpenLargeFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (LargeFilesGrid.SelectedItem is not LargeFileItem file) { MessageBox.Show("Сначала выберите файл в списке.", "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Не удалось открыть расположение: {ex.Message}", "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void DeleteLargeFile_Click(object sender, RoutedEventArgs e)
    {
        if (LargeFilesGrid.SelectedItem is not LargeFileItem file) { MessageBox.Show("Сначала выберите файл в списке.", "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var answer = MessageBox.Show($"Переместить выбранный файл в Корзину?\n\n{file.Path}\nРазмер: {file.SizeText}\n\nНе удаляйте системные файлы, если не уверены в их назначении.", "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            FileSystem.DeleteFile(file.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            if (LargeFilesGrid.ItemsSource is List<LargeFileItem> list) { list.Remove(file); LargeFilesGrid.Items.Refresh(); LargeFilesSummary.Text = $"Осталось в списке: {list.Count:N0}"; }
            RefreshDiskInfo();
            MessageBox.Show("Файл перемещён в Корзину, если это разрешено Windows.", "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show($"Не удалось переместить файл в Корзину.\n\n{ex.Message}", "NEBULA CLEAN", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void UpdateCategoryTotals()
    {
        long total = _categories.Sum(c => c.SizeBytes);
        int files = _categories.Sum(c => c.FileCount);
        PotentialCleanupText.Text = CleanupCategory.FormatBytes(total);
        PotentialFilesText.Text = $"{files:N0} потенциальных файлов";
        CleanerTotalText.Text = $"Найдено: {CleanupCategory.FormatBytes(total)} · {files:N0} файлов";
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
        // Disable action buttons without disabling the window's visual tree/state wholesale.
        NavDashboard.IsEnabled = !busy;
        NavCleaner.IsEnabled = !busy;
        NavAnalyzer.IsEnabled = !busy;
        NavHistory.IsEnabled = !busy;
        NavSubscriptions.IsEnabled = !busy;
        NavChat.IsEnabled = !busy;
        NavProfile.IsEnabled = !busy;
    }
}
