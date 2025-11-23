using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StockfishCompiler.Views;

public partial class DarkMessageBox : Window
{
    private readonly string _message;
    private readonly string _title;
    private readonly string _severityLabel;
    private readonly Brush _accentBrush;
    private MessageBoxResult _result = MessageBoxResult.None;

    private DarkMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage type, Brush accentBrush)
    {
        InitializeComponent();

        _message = message;
        _title = title;
        _severityLabel = type switch
        {
            MessageBoxImage.Error => "Error",
            MessageBoxImage.Warning => "Warning",
            MessageBoxImage.Information => "Info",
            _ => string.Empty
        };
        _accentBrush = accentBrush;
        var displayTitle = BuildDisplayTitle();
        Title = string.IsNullOrWhiteSpace(displayTitle) ? "Message" : displayTitle;
        TitleText.Text = string.IsNullOrWhiteSpace(displayTitle) ? title : displayTitle;
        MessageTextBox.Text = message;
        DialogBorder.BorderBrush = accentBrush;
        AccentStripe.Fill = accentBrush;
        TitleText.Foreground = accentBrush;
        ConfigureBadge();

        ConfigureButtons(buttons);
    }

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage type = MessageBoxImage.None,
        Window? owner = null)
    {
        var accentBrush = GetAccentBrush(type);

        var dialog = new DarkMessageBox(message, title, buttons, type, accentBrush)
        {
            Owner = owner ?? Application.Current?.MainWindow
        };

        dialog.WindowStartupLocation = dialog.Owner != null
            ? WindowStartupLocation.CenterOwner
            : WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
        return dialog._result;
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        void SetPrimary(string content, MessageBoxResult result, bool isDefault = true, bool isCancel = false)
        {
            PrimaryButton.Content = content;
            PrimaryButton.Tag = result;
            PrimaryButton.IsDefault = isDefault;
            PrimaryButton.IsCancel = isCancel;
        }

        void SetSecondary(string content, MessageBoxResult result, bool isCancel = false)
        {
            SecondaryButton.Content = content;
            SecondaryButton.Tag = result;
            SecondaryButton.IsCancel = isCancel;
            SecondaryButton.Visibility = Visibility.Visible;
        }

        void SetTertiary(string content, MessageBoxResult result, bool isCancel = false)
        {
            TertiaryButton.Content = content;
            TertiaryButton.Tag = result;
            TertiaryButton.IsCancel = isCancel;
            TertiaryButton.Visibility = Visibility.Visible;
        }

        switch (buttons)
        {
            case MessageBoxButton.OK:
                SetPrimary("OK", MessageBoxResult.OK, true, true);
                break;

            case MessageBoxButton.OKCancel:
                SetPrimary("OK", MessageBoxResult.OK);
                SetSecondary("Cancel", MessageBoxResult.Cancel, true);
                break;

            case MessageBoxButton.YesNo:
                SetPrimary("Yes", MessageBoxResult.Yes);
                SetSecondary("No", MessageBoxResult.No, true);
                break;

            case MessageBoxButton.YesNoCancel:
                SetPrimary("Yes", MessageBoxResult.Yes);
                SetSecondary("No", MessageBoxResult.No);
                SetTertiary("Cancel", MessageBoxResult.Cancel, true);
                break;

            default:
                SetPrimary("OK", MessageBoxResult.OK, true, true);
                break;
        }
    }

    private static Brush GetAccentBrush(MessageBoxImage type)
    {
        var application = Application.Current;
        var defaultBrush = application?.TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;

        return type switch
        {
            MessageBoxImage.Error => application?.TryFindResource("ErrorBrush") as Brush ?? Brushes.IndianRed,
            MessageBoxImage.Warning => application?.TryFindResource("WarningBrush") as Brush ?? Brushes.DarkOrange,
            MessageBoxImage.Information => application?.TryFindResource("AccentBrush") as Brush ?? defaultBrush,
            _ => defaultBrush
        };
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e) => CloseWithResult(sender);

    private void SecondaryButton_Click(object sender, RoutedEventArgs e) => CloseWithResult(sender);

    private void TertiaryButton_Click(object sender, RoutedEventArgs e) => CloseWithResult(sender);

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var header = BuildDisplayTitle();
        var textToCopy = string.IsNullOrWhiteSpace(header)
            ? _message
            : $"{header}\n\n{_message}";

        Clipboard.SetText(textToCopy);
    }

    private string BuildDisplayTitle()
    {
        if (!string.IsNullOrWhiteSpace(_title))
        {
            return _title;
        }

        if (!string.IsNullOrWhiteSpace(_severityLabel))
        {
            return _severityLabel;
        }

        return string.Empty;
    }

    private void ConfigureBadge()
    {
        if (string.IsNullOrWhiteSpace(_severityLabel))
        {
            SeverityBadge.Visibility = Visibility.Collapsed;
            return;
        }

        SeverityBadge.Background = _accentBrush;
        SeverityBadgeText.Text = _severityLabel.ToUpperInvariant();
        SeverityBadge.Visibility = Visibility.Visible;
    }

    private void CloseWithResult(object sender)
    {
        if (sender is Button button && button.Tag is MessageBoxResult result)
        {
            _result = result;
        }

        DialogResult = true;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Respect cancel buttons when present, otherwise just close.
            if (SecondaryButton.Visibility == Visibility.Visible && SecondaryButton.IsCancel)
            {
                SecondaryButton_Click(SecondaryButton, new RoutedEventArgs());
            }
            else if (TertiaryButton.Visibility == Visibility.Visible && TertiaryButton.IsCancel)
            {
                TertiaryButton_Click(TertiaryButton, new RoutedEventArgs());
            }
            else
            {
                Close();
            }
        }
    }
}
