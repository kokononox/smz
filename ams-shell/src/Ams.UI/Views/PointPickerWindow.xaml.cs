using System.Windows;
using System.Windows.Input;

namespace Ams.UI.Views;

/// <summary>Full virtual-screen one-click coordinate picker for Move to Position.</summary>
public partial class PointPickerWindow : Window
{
    public int ScreenX { get; private set; }
    public int ScreenY { get; private set; }

    private readonly int _ox;
    private readonly int _oy;

    public PointPickerWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        _ox = (int)SystemParameters.VirtualScreenLeft;
        _oy = (int)SystemParameters.VirtualScreenTop;
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(Canvas);
        ScreenX = (int)p.X + _ox;
        ScreenY = (int)p.Y + _oy;
        Horizontal.X1 = 0; Horizontal.X2 = Canvas.ActualWidth;
        Horizontal.Y1 = Horizontal.Y2 = p.Y;
        Vertical.Y1 = 0; Vertical.Y2 = Canvas.ActualHeight;
        Vertical.X1 = Vertical.X2 = p.X;
        System.Windows.Controls.Canvas.SetLeft(InfoText, p.X + 14);
        System.Windows.Controls.Canvas.SetTop(InfoText, p.Y + 14);
        InfoText.Text = $"({ScreenX}, {ScreenY})";
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Canvas);
        ScreenX = (int)p.X + _ox;
        ScreenY = (int)p.Y + _oy;
        DialogResult = true;
    }

    private void Window_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => DialogResult = false;

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
