using System.Windows.Controls;

namespace Talknado.Client.Views.Controls;

public partial class MessageListControl : UserControl
{
    // Допуск в пикселях: у низа ленты смещение редко совпадает с пределом
    // ровно, а «почти внизу» пользователь воспринимает как «внизу».
    private const double BottomTolerance = 4;

    private bool _wasAtBottom = true;

    public MessageListControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Лента доезжает до конца, только если пользователь был внизу. Без изменений
    /// размеров двигался пользователь — запоминаем, у низа ли он. Изменились
    /// содержимое или высота ленты — доезжаем: при уменьшении высоты смещение
    /// остаётся прежним, и низ уходит за край.
    /// </summary>
    private void Messages_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scroll)
            return;

        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            _wasAtBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - BottomTolerance;
            return;
        }

        if (_wasAtBottom)
            scroll.ScrollToEnd();
    }
}
