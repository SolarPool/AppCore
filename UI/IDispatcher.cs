using System;
using System.Reactive;
using System.Threading.Tasks;

namespace Ciphernote.UI
{
    public interface IDispatcher
    {
        void Invoke(Action action);
    }
}
