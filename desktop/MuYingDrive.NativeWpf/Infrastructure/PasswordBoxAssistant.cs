using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MuYingDrive.NativeWpf.Infrastructure;

/// <summary>Minimal two-way binding bridge for WPF PasswordBox, which intentionally lacks a Password dependency property.</summary>
public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword",
        typeof(string),
        typeof(PasswordBoxAssistant),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject element) => element.GetValue(BoundPasswordProperty) as string ?? string.Empty;

    public static void SetBoundPassword(DependencyObject element, string value) => element.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        // Temporarily detach while applying a source-to-control update to prevent feedback.
        passwordBox.PasswordChanged -= HandlePasswordChanged;
        var newPassword = args.NewValue as string ?? string.Empty;
        if (!string.Equals(passwordBox.Password, newPassword, StringComparison.Ordinal))
        {
            passwordBox.Password = newPassword;
        }

        passwordBox.PasswordChanged += HandlePasswordChanged;
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs args)
    {
        if (sender is PasswordBox passwordBox)
        {
            passwordBox.SetCurrentValue(BoundPasswordProperty, passwordBox.Password);
            BindingOperations.GetBindingExpression(passwordBox, BoundPasswordProperty)?.UpdateSource();
        }
    }
}
