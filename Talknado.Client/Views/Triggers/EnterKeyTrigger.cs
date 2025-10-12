using Microsoft.Xaml.Behaviors;
using System.Windows;
using System.Windows.Input;

namespace TalknadoClientWPF.Views.Triggers
{
    public class EnterKeyTrigger : TriggerBase<UIElement>
    {
        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.PreviewKeyDown += OnKeyDown;
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            AssociatedObject.PreviewKeyDown -= OnKeyDown;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                InvokeActions(null);
            }
        }
    }
}