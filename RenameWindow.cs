using System.Windows;
using System.Windows.Controls;

namespace Switch2Bridge;

/// <summary>Asks for a controller's name. Saving an empty name goes back to the default.</summary>
sealed class RenameWindow : Window
{
    readonly TextBox _name;

    /// <summary>The trimmed name after Save; null if cancelled.</summary>
    public string? Result { get; private set; }

    public RenameWindow(string current)
    {
        Title = "Rename controller";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var label = new Label { Content = "_Name", Padding = new Thickness(0, 0, 0, 4) };
        _name = new TextBox { Text = current, MaxLength = 40 };
        label.Target = _name;
        var save = new Button { Content = "Save", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
        if (TryFindResource("AccentButtonStyle") is Style accent) save.Style = accent;
        save.Click += (_, _) => { Result = _name.Text.Trim(); DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(label);
        panel.Children.Add(_name);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }
}
