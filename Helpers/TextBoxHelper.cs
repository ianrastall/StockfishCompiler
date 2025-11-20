using System.Windows;
using System.Windows.Controls;

namespace StockfishCompiler.Helpers;

public static class TextBoxHelper
{
    public static readonly DependencyProperty AlwaysScrollToEndProperty =
        DependencyProperty.RegisterAttached(
            "AlwaysScrollToEnd",
            typeof(bool),
            typeof(TextBoxHelper),
            new PropertyMetadata(false, OnAlwaysScrollToEndChanged));

    public static bool GetAlwaysScrollToEnd(DependencyObject obj)
    {
        return (bool)obj.GetValue(AlwaysScrollToEndProperty);
    }

    public static void SetAlwaysScrollToEnd(DependencyObject obj, bool value)
    {
        obj.SetValue(AlwaysScrollToEndProperty, value);
    }

    private static void OnAlwaysScrollToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox textBox)
        {
            textBox.TextChanged -= TextBox_TextChanged;
            textBox.Loaded -= TextBox_Loaded;
            
            if ((bool)e.NewValue)
            {
                textBox.TextChanged += TextBox_TextChanged;
                textBox.Loaded += TextBox_Loaded;
                ScrollToEnd(textBox);
            }
        }
    }

    private static void TextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ScrollToEnd(textBox);
        }
    }

    private static void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ScrollToEnd(textBox);
        }
    }

    private static void ScrollToEnd(TextBox textBox)
    {
        textBox.CaretIndex = textBox.Text.Length;
        textBox.ScrollToEnd();
    }
}
