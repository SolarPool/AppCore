using System;
using System.Reactive.Linq;
using ReactiveUI;

namespace Ciphernote.Model.Projections
{
    public class NoteSummary : ReactiveObject,
        IDisposable
    {
        public NoteSummary()
        {
            hasThumbnailUri = this.WhenAny(x => x.ThumbnailUri, x => x.Value)
                .Select(x => !string.IsNullOrEmpty(x))
                .ToProperty(this, x => x.HasThumbnailUri);

            hasTodoProgress = this.WhenAny(x => x.TodoProgress, x => x.Value)
                .Select(x => x.HasValue)
                .ToProperty(this, x => x.HasTodoProgress);

            isTodoComplete = this.WhenAny(x => x.TodoProgress, x => x.Value)
                .Select(x => x.HasValue && x.Value >= 1.0d)
                .ToProperty(this, x => x.IsTodoComplete);

            timestampHuman = this.WhenAnyValue(x => x.Timestamp)
                .Select(x => x.ToString("MMMM yyyy"))
                .ToProperty(this, x => x.TimestampHuman);

            hasConflicts = this.WhenAny(x => x.ConflictingNoteId, x => x.Value)
                .Select(x => x.HasValue)
                .ToProperty(this, x => x.HasConflicts);
        }

        public long Id { get; set; }
        public string BodyMimeType { get; set; }
        public string[] MediaRefs { get; set; }

        private ReactiveList<string> tags;

        public ReactiveList<string> Tags
        {
            get { return tags; }
            set { this.RaiseAndSetIfChanged(ref tags, value); }
        }

        private DateTime timestamp;

        public DateTime Timestamp
        {
            get { return timestamp; }
            set { this.RaiseAndSetIfChanged(ref timestamp, value); }
        }

        // Binding Properties
        private string title;

        public string Title
        {
            get { return title; }
            set { this.RaiseAndSetIfChanged(ref title, value); }
        }

        private string excerpt;

        public string Excerpt
        {
            get { return excerpt; }
            set { this.RaiseAndSetIfChanged(ref excerpt, value); }
        }

        private bool hasLocation;

        public bool HasLocation
        {
            get { return hasLocation; }
            set { this.RaiseAndSetIfChanged(ref hasLocation, value); }
        }

        private double? latitude;

        public double? Latitude
        {
            get { return latitude; }
            set { this.RaiseAndSetIfChanged(ref latitude, value); }
        }

        private double? longitude;

        public double? Longitude
        {
            get { return longitude; }
            set { this.RaiseAndSetIfChanged(ref longitude, value); }
        }

        private double? altitude;

        public double? Altitude
        {
            get { return altitude; }
            set { this.RaiseAndSetIfChanged(ref altitude, value); }
        }

        private string thumbnailUri;

        public string ThumbnailUri
        {
            get { return thumbnailUri; }
            set { this.RaiseAndSetIfChanged(ref thumbnailUri, value); }
        }

        private double? todoProgress;

        public double? TodoProgress
        {
            get { return todoProgress; }
            set { this.RaiseAndSetIfChanged(ref todoProgress, value); }
        }

        private long? conflictingNoteId;

        public long? ConflictingNoteId
        {
            get { return conflictingNoteId; }
            set { this.RaiseAndSetIfChanged(ref conflictingNoteId, value); }
        }

        private bool isSyncPending;

        public bool IsSyncPending
        {
            get { return isSyncPending; }
            set { this.RaiseAndSetIfChanged(ref isSyncPending, value); }
        }

        private readonly ObservableAsPropertyHelper<bool> hasThumbnailUri;
        private readonly ObservableAsPropertyHelper<bool> hasTodoProgress;
        private readonly ObservableAsPropertyHelper<bool> isTodoComplete;
        private readonly ObservableAsPropertyHelper<string> timestampHuman;
        private readonly ObservableAsPropertyHelper<bool> hasConflicts;

        public bool HasThumbnailUri => hasThumbnailUri.Value;
        public string TimestampHuman => timestampHuman.Value;
        public bool HasTodoProgress => hasTodoProgress.Value;
        public bool IsTodoComplete => isTodoComplete.Value;
        public bool HasConflicts => hasConflicts.Value;

        #region IDisposable implementation

        public virtual void Dispose()
        {
            // Please override in platform specific entity
        }

        #endregion
    }
}
