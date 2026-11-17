using System.Windows;
using System.Windows.Media;

namespace AiUsage.Ui;

public static class VisualTree
{
    /// <summary>
    /// Walks up from whatever was clicked until it finds a data context of the
    /// wanted type. Templates put the model on the card, but the click lands on
    /// whichever TextBlock or Path happens to be under the cursor.
    /// </summary>
    public static T? DataContextOf<T>(DependencyObject? source) where T : class
    {
        while (source != null)
        {
            if (source is FrameworkElement { DataContext: T match }) return match;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
}
