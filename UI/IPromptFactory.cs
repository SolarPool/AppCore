using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ciphernote.UI
{
    public enum PromptType
    {
        Ok,
        OkCancel,
        YesNoCancel,
    }

    public enum PromptResult
    {
        Cancel,
        Ok,
        No
    }

    public enum PromptIcon
    {
        None,
        NoCloud = 1,
        Cloud = 2,
    }

    public enum ToastType
    {
        Info = 1,
        Warning,
        Error,
        Success,
    }

    public interface IPromptFactory
    {
        Task MessageBox(string title, string message);
        Task<PromptResult> Confirm(PromptType type, string title, string message);
        void ShowToast(ToastType type, string title, string message, Action action, TimeSpan? timeout, PromptIcon icon = PromptIcon.None);
        void ShowGeneralApplicationErrorToast();
    }
}
