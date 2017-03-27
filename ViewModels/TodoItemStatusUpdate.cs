using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ciphernote.ViewModels
{
    public enum TodoItemStatusAction
    {
        Register = 1,
        Unregister,
        CompletedChanged,
    }

    public class TodoItemStatusUpdate
    {
        public object Handle { get; set; }
        public TodoItemStatusAction Action { get; set; }
        public bool Completed { get; set; }
    }
}
