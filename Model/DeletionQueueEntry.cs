using System;
using ReactiveUI;

namespace Ciphernote.Model
{
    public enum DeletionQueueEntryType
    {
        Note = 1,
    }

    public class DeletionQueueEntry
    {
        public long Id { get; set; }
        public Guid Uid { get; set; }
        public DeletionQueueEntryType EntryType { get; set; }
    }
}
