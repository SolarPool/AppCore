using System.Windows.Input;
using ReactiveUI;

namespace Ciphernote.Extensions
{
    public static class ReactiveUiExtensions
    {
        public static void Execute(this ReactiveCommand cmd, object parameter)
        {
            ((ICommand) cmd).Execute(parameter);
        }

        public static void Execute<TParam, TResult>(this ReactiveCommand<TParam, TResult> cmd, object parameter)
        {
            ((ICommand)cmd).Execute(parameter);
        }
    }
}
