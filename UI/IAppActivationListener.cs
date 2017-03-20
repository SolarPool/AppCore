using System;
using System.Reactive;

namespace Ciphernote.UI
{
    public interface IAppActivationListener
    {
        IObservable<Unit> Activated { get; }
        IObservable<Unit> Deactivated { get; }
        bool IsEnabled { get; set; }
    }
}
