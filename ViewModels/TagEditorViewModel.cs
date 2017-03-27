using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Model;
using ReactiveUI;

namespace Ciphernote.ViewModels
{
    public class TagEditorViewModel : ViewModelBase
    {
        public TagEditorViewModel(IComponentContext ctx) : base(ctx)
        {
            AddTagCommand = ReactiveCommand.Create(ExecuteAddTag);
            DeleteTagCommand = ReactiveCommand.Create<TagEditorItem>(ExecuteDeleteTag);
        }

        private Note note;
        private CompositeDisposable noteDisposables;

        public ReactiveList<TagEditorItem> Items { get;  } = new ReactiveList<TagEditorItem>();
        public ReactiveCommand AddTagCommand { get; private set; }
        public ReactiveCommand DeleteTagCommand { get; private set; }

        public void Reset(Note note)
        {
            this.note = null;
            noteDisposables?.Dispose();

            using (Items.SuppressChangeNotifications())
            {
                Items.Clear();

                Items.AddRange(note.Tags.Select(tag => new TagEditorItem(TagEditorItemKind.Tag, tag)));
                Items.Add(new TagEditorItem(TagEditorItemKind.Input, null));
            }

            this.note = note;

            InitializeChangeTracking();
        }

        public void AddTags(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                text = text.Trim();

                if (!string.IsNullOrEmpty(text))
                {
                    var tags = text.Split(' ')
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .Select(x => x.Substring(0, Math.Min(AppCoreConstants.MaxTagLength, x.Length)))
                        .ToArray();

                    foreach (var tag in tags)
                    {
                        var existingTag = Items.FirstOrDefault(x => x.Kind == TagEditorItemKind.Tag && x.Name == tag);
                        if (existingTag == null)
                            Items.Insert(Items.Count - 1, new TagEditorItem(TagEditorItemKind.Tag, tag));
                        else
                        {
                            // pulse it
                            existingTag.RequestHighlight = true;
                            existingTag.RequestHighlight = false;
                        }
                    }
                }
            }
        }

        public void ExecuteAddTag()
        {
            var tagInputItem = Items.First(x => x.Kind == TagEditorItemKind.Input);

            // pulse it
            tagInputItem.RequestFocus = true;
            tagInputItem.RequestFocus = false;
        }

        private void ExecuteDeleteTag(TagEditorItem para)
        {
            Items.Remove(para);
        }

        private void InitializeChangeTracking()
        {
            noteDisposables = new CompositeDisposable();

            noteDisposables.Add(Items.Changed.Subscribe(_ =>
            {
                if (note != null)
                {
                    using (note.Tags.SuppressChangeNotifications())
                    {
                        note.Tags.Clear();

                        note.Tags.AddRange(Items
                            .Where(x => x.Kind == TagEditorItemKind.Tag)
                            .Select(x => x.Name));
                    }
                }
            }));
        }
    }
}
