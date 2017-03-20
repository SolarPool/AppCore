using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ciphernote.Model.Projections;
using ReactiveUI;

namespace Ciphernote.ViewModels
{
    public enum TagEditorItemKind
    {
        Tag = 1,
        Input = 2,
    }

    public class TagEditorItem : ReactiveObject
    {
        public TagEditorItem(TagEditorItemKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public TagEditorItemKind Kind { get;  }

        private string name;

        public string Name
        {
            get { return name; }
            set { this.RaiseAndSetIfChanged(ref name, value); }
        }

        /// <summary>
        /// This property is used to signal the view that this item is to be temporarily highlighted
        /// </summary>
        private bool requestHighlight;

        public bool RequestHighlight
        {
            get { return requestHighlight; }
            set { this.RaiseAndSetIfChanged(ref requestHighlight, value); }
        }

        /// <summary>
        /// This property is used to signal the view that this item (normally Kind.Input) is to be focussed
        /// </summary>
        private bool requestFocus;

        public bool RequestFocus
        {
            get { return requestFocus; }
            set { this.RaiseAndSetIfChanged(ref requestFocus, value); }
        }
    }
}
