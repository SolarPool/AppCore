// ReSharper disable ReplaceWithSingleAssignment.False
// ReSharper disable ConvertIfToOrExpression

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AngleSharp;
using AngleSharp.Parser.Html;
using Autofac;
using Ciphernote.Crypto;
using Ciphernote.Data;
using Ciphernote.Dom;
using Ciphernote.Dom.EntityData;
using Ciphernote.Extensions;
using Ciphernote.Importers;
using Ciphernote.Importers.Dom;
using Ciphernote.Importers.Notes;
using Ciphernote.IO;
using Ciphernote.Logging;
using Ciphernote.Media;
using Ciphernote.Model;
using Ciphernote.Model.Projections;
using Ciphernote.Resources;
using Ciphernote.Services;
using Ciphernote.UI;
using Ciphernote.Util;
using CodeContracts;
using Newtonsoft.Json;
using ReactiveUI;
using Splat;

namespace Ciphernote.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        public MainViewModel(
            Repository repo, 
            IAppCoreSettings appSettings,
            IComponentContext ctx,
            IFileEx filEx,
            SyncService syncService,
            CryptoService cryptoService,
            MediaManager mediaManager,
            ICoreStrings coreStrings,
            IPromptFactory promptFactory,
            IAppActivationListener appActivationListener,
            MimeTypeProvider mimeTypeProvider) : base(ctx)
        {
            this.repo = repo;
            this.appSettings = appSettings;
            this.filex = filEx;
            this.syncService = syncService;
            this.cryptoService = cryptoService;
            this.mediaManager = mediaManager;
            this.coreStrings = coreStrings;
            this.promptFactory = promptFactory;
            this.appActivationListener = appActivationListener;
            this.mimeTypeProvider = mimeTypeProvider;

            this.WhenAnyValue(x => x.ViewMode)
                .Subscribe(_ => ApplyViewMode());

            ViewMode = NotesViewMode.Timeline;
            
            isNoteListEmpty = Notes.IsEmptyChanged.ToProperty(this, x => x.IsNoteListEmpty);

            isNoteListFiltered = this.WhenAny(x => x.SearchTerm, x => !string.IsNullOrEmpty(x.Value?.Trim()))
                .ToProperty(this, x => x.IsNoteListFiltered);

            NoteUpdated = noteUpdatedSubject.AsObservable();

            disposables.Add(NoteUpdated
                .Sample(TimeSpan.FromMilliseconds(200))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    var cmd = (ICommand) SyncCommand;
#if DEBUG
                    if (!appSettings.NoAutoSync && cmd.CanExecute(null))
#else
                    if (cmd.CanExecute(null))
#endif
                        cmd.Execute(null);
                }));

            syncService.PostProcessInboundNoteHandler = PostProcessInboundNoteAsync;
            syncService.PostProcessOutboundNoteHandler = PostProcessOutboundNoteAsync;
            syncService.PostProcessDeleteRequestForNoteHandler = PostProcessDeleteRequestForNoteAsync;

            CreateCommands();
        }

        private readonly Repository repo;
        private readonly IAppCoreSettings appSettings;
        private readonly Subject<Note> noteUpdatedSubject = new Subject<Note>();
        private readonly Subject<string> searchTermPreviewSubject = new Subject<string>();
        private readonly SyncService syncService;
        private readonly CryptoService cryptoService;
        private readonly IFileEx filex;
        private readonly MediaManager mediaManager;
        private readonly ICoreStrings coreStrings;
        private readonly MimeTypeProvider mimeTypeProvider;
        private readonly IPromptFactory promptFactory;
        private readonly IAppActivationListener appActivationListener;
        private readonly ObservableAsPropertyHelper<bool> isNoteListEmpty;
        private readonly ObservableAsPropertyHelper<bool> isNoteListFiltered;
        private readonly IReactiveList<NoteSummary> notesSource = new ReactiveList<NoteSummary>();

        private IReactiveList<NoteSummary> notes;
        private NoteSummary selectedNote;
        private string searchTerm;
        private NotesViewMode viewMode;
        private int supressNoteUpdatedNotificationCount = 0;
        private bool isLoadingPage;
        private HashSet<Note> queuedNoteUpdatedNotifications;

        public const string SearchQueryProcessingInstructionPrefix = "@";

        public IReactiveList<string> Tags { get; } = new ReactiveList<string>();
        public IObservable<Note> NoteUpdated { get; private set; }

        public ReactiveCommand ResetSearchCommand { get; private set; }
        public ReactiveCommand NewNoteCommand { get; private set; }
        public ReactiveCommand DeleteNotesCommand { get; private set; }
        public ReactiveCommand SyncCommand { get; private set; }

        public int NotesPageSize { get; set; } = 30;

        public IReactiveList<NoteSummary> Notes
        {
            get { return notes; }
            private set { this.RaiseAndSetIfChanged(ref notes, value); }
        }

        public NoteSummary SelectedNote
        {
            get { return selectedNote; }
            set { this.RaiseAndSetIfChanged(ref selectedNote, value); }
        }

        public string SearchTerm
        {
            get { return searchTerm; }
            set { this.RaiseAndSetIfChanged(ref searchTerm, value); }
        }

        public bool IsNoteListEmpty => isNoteListEmpty.Value;
        public bool IsNoteListFiltered => isNoteListFiltered.Value;

        public NotesViewMode ViewMode
        {
            get { return viewMode; }
            set { this.RaiseAndSetIfChanged(ref viewMode, value); }
        }

        private void ApplyViewMode()
        {
            switch (ViewMode)
            {
                case NotesViewMode.Timeline:
                    Notes = notesSource;
                    break;

                case NotesViewMode.Photos:
                    Notes = (IReactiveList<NoteSummary>) notesSource
                        .CreateDerivedCollection(noteSummary=> noteSummary, 
                            noteSummary => noteSummary.HasThumbnailUri);
                    break;

                case NotesViewMode.Map:
                    Notes = (IReactiveList<NoteSummary>)notesSource
                        .CreateDerivedCollection(noteSummary => noteSummary, 
                            noteSummary => noteSummary.HasLocation);
                    break;
            }
        }

        private void CreateCommands()
        {
            ResetSearchCommand = ReactiveCommand.Create(ExecuteResetSearch);
            NewNoteCommand = ReactiveCommand.CreateFromTask(ExecuteNewNote);
            DeleteNotesCommand = ReactiveCommand.CreateFromTask<IList<object>>(x=> ExecuteDeleteNotes(x));

            SyncCommand = ReactiveCommand.CreateFromTask(ExecuteSync, 
                syncService.WhenAnyValue(x=> x.IsSynching)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Select(x=> !x));
        }

        public async Task InitAsync()
        {
            try
            {
                repo.Open();

                // periodically refresh timestamps in view
                this.disposables.Add(Observable.Interval(TimeSpan.FromSeconds(60))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(x => RefreshTimestamps()));

                var searchTermValue = Observable.Merge(
                        this.WhenAny(x => x.SearchTerm, x => x.Value)
                            .Throttle(TimeSpan.FromMilliseconds(400)),
                        searchTermPreviewSubject)
                    .Select(x => x?.ToString()?.Trim())
                    .DistinctUntilChanged()
                    .Do(x=> Debug.WriteLine(x))
                    .Publish()
                    .RefCount();

                var searchTermValidity = searchTermValue
                    .Select(x=> !string.IsNullOrEmpty(x) && x.Length >= 2)
                    .DistinctUntilChanged()
                    .Publish()
                    .RefCount();

                var validSearchTermValues = Observable.CombineLatest(
                        searchTermValidity, 
                        searchTermValue, (valid, value)=> new { Valid = valid, Value = value })
                    .Where(x=> x.Valid)
                    .Select(x=> x.Value)
                    .DistinctUntilChanged()
                    .Publish()
                    .RefCount();

                var reloadTags = Observable.CombineLatest(
                    searchTermValidity.StartWith(false),
                    NoteUpdated
                        .Sample(TimeSpan.FromMilliseconds(1000))
                        .Select(_ => Unit.Default)
                        .StartWith(Unit.Default),
                    (stv, _)=> !stv)
                    .Where(x=> x);

                var reSearchTags = Observable.CombineLatest(
                    validSearchTermValues,
                    NoteUpdated
                        .Sample(TimeSpan.FromMilliseconds(1000))
                        .Select(_ => Unit.Default)
                        .StartWith(Unit.Default),
                    (vstv, _) => vstv);

                // wire search
                disposables.Add(validSearchTermValues
                    .Select(x => Observable.FromAsync(async () =>
                    {
                        try
                        {
                            await SearchNotesAsync(x);
                        }

                        catch (Exception ex)
                        {
                            this.Log().Error(() => "Note search subscription", ex);
                        }
                    }))
                    .Concat()
                    .Subscribe());

                disposables.Add(searchTermValidity.Where(x=> !x)
                    .Select(x => Observable.FromAsync(async () =>
                    {
                        try
                        {
                            await LoadNotesAsync();
                        }

                        catch (Exception ex)
                        {
                            this.Log().Error(() => "Note loading subscription", ex);
                        }
                    }))
                    .Concat()
                    .Subscribe());

                // wire tag search
                disposables.Add(reSearchTags
                    .Sample(TimeSpan.FromMilliseconds(200))
                    .Select(x => Observable.FromAsync(async () =>
                    {
                        try
                        {
                            await SearchTagsAsync(x);
                        }

                        catch (Exception ex)
                        {
                            this.Log().Error(() => "Tag search subscription", ex);
                        }
                    }))
                    .Concat()
                    .Subscribe());

                disposables.Add(reloadTags
                    .Sample(TimeSpan.FromMilliseconds(200))
                    .Select(x => Observable.FromAsync(async () =>
                    {
                        try
                        {
                            await LoadTagsAsync();
                        }

                        catch (Exception ex)
                        {
                            this.Log().Error(() => "Tag loading subscription", ex);
                        }
                    }))
                    .Concat()
                    .Subscribe());

                if (!appSettings.IntroNoteCreated)
                {
                    await CreateIntroNoteAsync();
                    appSettings.IntroNoteCreated = true;
                }
#if DEBUG
                if(!appSettings.NoAutoSync)
                    SyncCommand.Execute(null);
#else
                SyncCommand.Execute(null);
#endif
                appActivationListener.IsEnabled = true;
            }

            catch (Exception ex)
            {
                this.Log().Error(() => nameof(InitAsync), ex);
                throw;
            }
        }

        public async Task LoadNotesAsync()
        {
            // load initial page
            var result = await repo.GetNotesAsync(0, NotesPageSize);
            this.Log().Info(() => $"Loaded {result.Length} notes");

            dispatcher.Invoke(() =>
            {
                ReplaceNotes(result);
            });
        }

        private async Task SearchNotesAsync(string currentSearchTerm)
        {
            NoteSummary[] result;

            // load initial page
            bool deleted;
            currentSearchTerm = HandleSearchProcessingInstructions(currentSearchTerm, out deleted);

            // WARNING: do not remove, currentSearchTerm can become empty after being run through HandleSearchProcessingInstructions
            if (!string.IsNullOrEmpty(currentSearchTerm))
                result = await repo.SearchNotesAsync(0, NotesPageSize, currentSearchTerm, deleted);
            else
                result = await repo.GetNotesAsync(0, NotesPageSize, deleted);

            dispatcher.Invoke(() =>
            {
                ReplaceNotes(result);
            });
        }

        public async Task LoadNextPageAsync()
        {
            if (isLoadingPage)
                return;

            try
            {
                isLoadingPage = true;

                // WARNING: this method is assumed to be called from the main thread
                NoteSummary[] result;

                if (!IsNoteListFiltered)
                {
                    result = await repo.GetNotesAsync(notesSource.Count, NotesPageSize);
                }

                else
                {
                    bool deleted;
                    var currentSearchTerm = HandleSearchProcessingInstructions(SearchTerm, out deleted);

                    // WARNING: do not remove, currentSearchTerm can become empty after being run through HandleSearchProcessingInstructions
                    if (!string.IsNullOrEmpty(currentSearchTerm))
                        result = await repo.SearchNotesAsync(notesSource.Count, NotesPageSize, currentSearchTerm, deleted);
                    else
                        result = await repo.GetNotesAsync(notesSource.Count, NotesPageSize, deleted);
                }

                if (result.Length > 0)
                    AppendNotes(result);
            }

            catch (Exception ex)
            {
                this.Log().Error(() => nameof(LoadNextPageAsync), ex);
            }

            finally
            {
                isLoadingPage = false;
            }
        }

        private async Task SearchTagsAsync(string currentSearchTerm)
        {
            bool searchInDeletedNotes;
            currentSearchTerm = HandleSearchProcessingInstructions(currentSearchTerm, out searchInDeletedNotes);

            var result = await repo.SearchTagsAsync(currentSearchTerm, searchInDeletedNotes);

            dispatcher.Invoke(() =>
            {
                ReplaceTags(result);
            });
        }

        private string HandleSearchProcessingInstructions(string query, out bool searchInDeletedNotes)
        {
            searchInDeletedNotes = EvaluateSearchProcessingInstruction(
                coreStrings.DeletedNotesSearchProcessingInstructionName, ref query);

            return query;
        }

        private bool EvaluateSearchProcessingInstruction(string instruction, ref string query)
        {
            var regex = new Regex(SearchQueryProcessingInstructionPrefix + instruction, RegexOptions.IgnoreCase);

            var enabled = false;

            query = regex.Replace(query, m =>
            {
                enabled = true;
                return string.Empty;
            }).Trim();

            return enabled;
        }

        private void ExecuteResetSearch()
        {
            searchTermPreviewSubject.OnNext(null);
            SearchTerm = "";
        }

        /// <summary>
        /// Fast-track to search updates - bypassing throtteling
        /// </summary>
        public void PreviewSearchTerm(string value)
        {
            searchTermPreviewSubject.OnNext(value);
        }

        public async Task LoadTagsAsync()
        {
            var result = await repo.GetTagsAsync();
            this.Log().Info(() => $"Loaded {result.Length} tags");

            dispatcher.Invoke(() =>
            {
                ReplaceTags(result);
            });
        }

        public async Task<Note> LoadNoteAsync(long id)
        {
            Contract.Requires<ArgumentException>(id != 0, $"{nameof(id)} must not be empty");

            return await repo.GetNoteAsync(id);
        }

        public async Task SaveNoteAsync(Note note, Document body, bool updateVersioningInformation = true)
        {
            Contract.RequiresNonNull(note, nameof(note));

            this.Log().Info(() => $"{nameof(SaveNoteAsync)}");

            // assign Uid
            if (note.Uid == Guid.Empty)
                note.Uid = Guid.NewGuid();

            if(updateVersioningInformation)
                note.Timestamp = DateTime.UtcNow;

            // extract indexing information
            var textContent = await PostProcessChangedNoteAsync(note, body);

            // update sync information
            var hmac = await syncService.ComputeSyncHmac(note);
            if (hmac != note.SyncHmac)
                note.SyncHmac = hmac;
            else
                note.IsSyncPending = false;

            // store it
            await repo.InsertOrUpdateNoteAsync(note, textContent);

            // Update corresponding summary object
            var summary = notesSource.FirstOrDefault(x => x.Id == note.Id.Value);
            if (summary != null)
                UpdateSummaryFromNote(summary, note);

            // notify
            if (supressNoteUpdatedNotificationCount == 0)
                noteUpdatedSubject.OnNext(note);
            else
                queuedNoteUpdatedNotifications.Add(note);
        }

        public async Task ResolveNoteConflict(long noteId)
        {
            var note = notesSource.FirstOrDefault(x => x.Id == noteId);
            var relatedNote = notesSource.FirstOrDefault(x => x.ConflictingNoteId.HasValue && x.ConflictingNoteId.Value == noteId);

            await repo.ClearNoteConflict(noteId);

            if(note != null)
                note.ConflictingNoteId = null;

            if(relatedNote != null)
                relatedNote.ConflictingNoteId = null;
        }

        private void UpdateSummaryFromNote(NoteSummary summary, Note note)
        {
            Contract.RequiresNonNull(summary, nameof(summary));
            Contract.RequiresNonNull(note, nameof(note));

            if (note.Id.HasValue)
                summary.Id = note.Id.Value;

            summary.Title = note.Title;
            summary.Excerpt = note.Excerpt;
            summary.ThumbnailUri = note.ThumbnailUri;
            summary.Timestamp = note.Timestamp;
            summary.TodoProgress = note.TodoProgress;
            summary.ConflictingNoteId = note.ConflictingNoteId;
            summary.IsSyncPending = note.IsSyncPending;
            summary.Tags = note.Tags.ToArray();
        }

        public IDisposable SuppressNoteUpdatedNotifications()
        {
            Interlocked.Increment(ref supressNoteUpdatedNotificationCount);

            queuedNoteUpdatedNotifications = new HashSet<Note>();

            return new CompositeDisposable(Disposable.Create(() => {
                if (Interlocked.Decrement(ref supressNoteUpdatedNotificationCount) == 0)
                {
                    var arr = queuedNoteUpdatedNotifications.ToArray();
                    queuedNoteUpdatedNotifications = null;

                    foreach (var note in arr)
                        noteUpdatedSubject.OnNext(note);
                }
            }));
        }

        private void GenerateTitle(Note note, Document body)
        {
            Contract.RequiresNonNull(note, nameof(note));
            Contract.RequiresNonNull(body, nameof(body));

            var firstNonEmptyBlock = body.Blocks.FirstOrDefault(x => !string.IsNullOrEmpty(x.Text));

            if (firstNonEmptyBlock == null)
                note.Title = coreStrings.UntitledNote;
            else
                note.Title = firstNonEmptyBlock.Text.TruncateAtWord(32);
        }

        private void GenerateExcerpt(Note note, StringBuilder textContent)
        {
            Contract.RequiresNonNull(note, nameof(note));
            Contract.RequiresNonNull(textContent, nameof(textContent));

            if (!string.IsNullOrEmpty(note.Title) && textContent.ToString().StartsWith(note.Title))
                textContent.Remove(0, note.Title.Length);

            note.Excerpt = textContent.ToString().TrimStart().TruncateAtWord(160);
        }

        public async Task<NoteSummary> LoadSummary(long? noteId)
        {
            if (noteId.HasValue)
            {
                var summary = await repo.GetNoteSummaryAsync(noteId.Value);

                if (summary != null)
                {
                    // replace if existing
                    var existingSummary = notesSource.FirstOrDefault(x => x.Id == noteId.Value);

                    if (existingSummary != null)
                    {
                        var index = notesSource.IndexOf(existingSummary);
                        notesSource[index] = summary;
                    }

                    else
                        notesSource.Add(summary);

                    return summary;
                }
            }

            return null;
        }

        public async Task CreateIntroNoteAsync()
        {
            try
            {
                var dom = await CreateIntroNoteDocumentAsync();

                var note = new Note
                {
                    Title = coreStrings.WelcomeNoteTitle,
                    BodyMimeType = AppCoreConstants.DomMimeType,
                    Source = AppCoreConstants.NoteSourceCiphernote,
                    Tags = new ReactiveList<string>()
                };

                note.Tags.Add("Example");
                note.TodoProgress = dom.CalculateTodoProgress();

                await SaveNoteAsync(note, dom);

                var summary = new NoteSummary();
                UpdateSummaryFromNote(summary, note);

                notesSource.Insert(0, summary);
                SelectedNote = summary;
            }

            catch (Exception ex)
            {
                this.Log().Error(() => nameof(CreateIntroNoteAsync), ex);

                // TODO: publish toast or something
            }
        }

        private async Task<Document> CreateIntroNoteDocumentAsync()
        {
            this.Log().Info(() => $"{nameof(CreateIntroNoteDocumentAsync)}");

            var dom = new Document();

            var block = dom.AddHeading(1);
            block.AddText("Welcome to Ciphernote");

            block = dom.AddParagraph();
            block.AddText("Ciphernote is the only solution that integrates rich note composition with automatic cloud based synching while protecting your thoughts and ideas with ");
            block.AddText("strong ", StyleType.Bold);
            block.AddText("client-side encryption. Client-side encryption ensures that data stored in the cloud can only be viewed by its owner while still preventing data loss and the unauthorized disclosure of private or personal files, providing increased peace of mind for both personal and business users.");

            block = dom.AddHeading(3);
            block.AddText("Don't limit yourself to plaintext");

            block = dom.AddParagraph();
            var mediaUrl = await mediaManager.AddMediaAsync(GetResourceStream("Intro.space.jpg"), MimeTypeProvider.MimeTypeImageJpg);
            block.AddImage(dom, mediaUrl, MimeTypeProvider.MimeTypeImageJpg, 700);

            block = dom.AddParagraph();
            block.AddText("Enrich your notes with images ");
            block.AddLink(dom, "links", "https://www.ciphernote.net/");
            block.AddText(", and audio-recordings ...");

            block = dom.AddParagraph();
            mediaUrl = await mediaManager.AddMediaAsync(GetResourceStream("Intro.a13-cronkite.spx"), MimeTypeProvider.MimeTypeAudioSpeex);
            block.AddAudio(dom, mediaUrl, MimeTypeProvider.MimeTypeAudioSpeex, DateTime.Now.ToString("G"), 5300);

            block = dom.AddHeading(3);
            block.AddText("Organize and provide context to your notes using tags");
            block = dom.AddParagraph();
            block.AddText("Use the Tag-Panel at the very top of the note editor to assign tags to your notes. You can assign one or more tags to your notes to classify notes by topic. Every tag you've assingned to your notes will appear in the tag list on the very left side of the application. To quickly filter your note list by one or more tags just click or shift-click a tag in the list. You can also search notes by tag be preprending a tag name with a hash (#) sign.");

            block = dom.AddHeading(3);
            block.AddText("Keep your tasks in check");

            block = dom.AddParagraph();
            block.AddTodo(dom, false);
            block.AddText("Clean and tidy todo lists directly in your notes\n");
            block.AddTodo(dom, true);
            block.AddText("... with an overall progress bar in the note list");

            block = dom.AddParagraph();
            block.AddText("Feel free to visit our ");
            block.AddLink(dom, "Support Site", "https://ciphernote.userecho.com");
            block.AddText(" to report an issues, get support or submit feedback and suggestions.");

            block = dom.AddParagraph();
            block.AddText("That's it for now. Be sure to drop us a like on ");
            block.AddLink(dom, "Twitter", "https://twitter.com/ciphernote");
            block.AddText(", ");
            block.AddLink(dom, "Facebook", "https://www.facebook.com/ciphernote");
            block.AddText(" or ");
            block.AddLink(dom, "Google+", "https://plus.google.com/b/106975910025979056536/106975910025979056536");
            block.AddText(" if you enjoy using the app.");

            block = dom.AddParagraph();
            block.AddText("---\nThe Ciphernote Team");

            block.ApplyBuilder();
            return dom;
        }

        private Stream GetResourceStream(string name)
        {
            var assembly = GetType().GetTypeInfo().Assembly;
            var resourceName = $"Ciphernote.Resources.{name}";

            var stream = assembly.GetManifestResourceStream(resourceName);
            return stream;
        }

        private void ReplaceNotes(IEnumerable<NoteSummary> notes)
        {
            Contract.RequiresNonNull(notes, nameof(notes));

            using (notesSource.SuppressChangeNotifications())
            {
                var items = notesSource.ToArray();
                notesSource.Clear();

                foreach (var item in items)
                    item.Dispose();

                notesSource.AddRange(notes);
            }

            RestoreSelection();
        }

        private void AppendNotes(IEnumerable<NoteSummary> notes)
        {
            Contract.RequiresNonNull(notes, nameof(notes));

            notesSource.AddRange(notes);
        }

        private void ReplaceTags(string[] tags)
        {
            using (Tags.SuppressChangeNotifications())
            {
                Tags.Clear();
                Tags.AddRange(tags);
            }
        }

        public void RestoreSelection()
        {
            // restore selection by id
            if (SelectedNote != null)
            {
                var newInstanceOfSameNote = notesSource.FirstOrDefault(x => x.Id == SelectedNote.Id);
                if (newInstanceOfSameNote != null)
                {
                    SelectedNote = newInstanceOfSameNote;

                    // ReSharper disable once ExplicitCallerInfoArgument
                    this.RaisePropertyChanged(nameof(SelectedNote));
                }
            }
        }

        public async Task<string> PostProcessChangedNoteAsync(Note note, Document body)
        {
            Contract.RequiresNonNull(note, nameof(note));

            if (body == null)
                body = await Document.LoadAsync(note.Body);
            else
                note.Body = await body.SaveAsync();

            CollectMediaRefs(note, body);

            // extract text content for FTS, Title and Excerpt
            var textContent = await CollectNoteTextContentAsync(note, body);

            // generate title if note was not imported 
            if (note.Source != null && note.Source.StartsWith(AppCoreConstants.NoteSourceCiphernote) ||
                string.IsNullOrEmpty(note.Title))
            {
                var previousTitle = note.Title;
                GenerateTitle(note, body);

                if (note.Title != previousTitle)
                {
                    // since text content includes the title we must re-create the textContent
                    textContent = await CollectNoteTextContentAsync(note, body);
                }
            }

            // generate excerpt
            GenerateExcerpt(note, new StringBuilder(textContent));

            // assign thumbnail
            if (note.MediaRefs.Any())
            {
                var mediaId = note.MediaRefs.FirstOrDefault(x =>
                {
                    var extension = Path.GetExtension(x);
                    var mimeType = mimeTypeProvider.GetMimeTypeForExtension(extension);

                    switch (mimeType)
                    {
                        case MimeTypeProvider.MimeTypeImageJpg:
                        case MimeTypeProvider.MimeTypeImagePng:
                        case MimeTypeProvider.MimeTypeImageGif:
                        case MimeTypeProvider.MimeTypeImageBmp:
                            return true;

                        default:
                            return false;
                    }
                });

                if (mediaId != null)
                    note.ThumbnailUri = mediaManager.BuildMediaUri(mediaId);
            }

            return textContent;
        }

        private void CollectMediaRefs(Note note, Document body)
        {
            Contract.RequiresNonNull(note, nameof(note));
            Contract.RequiresNonNull(body, nameof(body));

            var mediaRefs = new HashSet<string>();

            var images = body.Entities.Values.Where(x => x.Type == EntityType.Image)
                .Select(x => x.DataAsImageInfo())
                .ToArray();

            foreach (var img in images)
            {
                Uri uri;
                if (!Uri.TryCreate(img.Url, UriKind.RelativeOrAbsolute, out uri))
                    continue;

                if (!uri.IsAbsoluteUri)
                    continue;

                switch (uri.Scheme)
                {
                    case AppCoreConstants.MediaUriScheme:
                        mediaRefs.Add(uri.AbsolutePath);
                        break;
                }
            }

            var audios = body.Entities.Values.Where(x => x.Type == EntityType.Audio)
                .Select(x => x.DataAsAudioInfo())
                .ToArray();

            foreach (var audio in audios)
            {
                Uri uri;
                if (!Uri.TryCreate(audio.Url, UriKind.RelativeOrAbsolute, out uri))
                    continue;

                if (!uri.IsAbsoluteUri)
                    continue;

                switch (uri.Scheme)
                {
                    case AppCoreConstants.MediaUriScheme:
                        mediaRefs.Add(uri.AbsolutePath);
                        break;
                }
            }

            note.MediaRefs = mediaRefs.ToArray();
        }

        public async Task DumpNoteBodiesAsync(SpecialFolders folder, string path)
        {
            using (var stm = await filex.OpenFileForWriteAsync(folder, path))
            {
                using (var writer = new BinaryWriter(stm, Encoding.UTF8))
                {
                    foreach (var note in notesSource)
                    {
                        var n = await repo.GetNoteAsync(note.Id);
                        writer.Write(n.Body);
                    }
                }
            }
        }

        private async Task<string> CollectNoteTextContentAsync(Note note, Document dom)
        {
            Contract.RequiresNonNull(note, nameof(note));
            Contract.RequiresNonNull(dom, nameof(dom));

            this.Log().Info(() => $"{nameof(CollectNoteTextContentAsync)}");

            var sb = new StringBuilder();

            // append title
            sb.Append(note.Title);
            sb.Append(" ");

            switch (note.BodyMimeType)
            {
                case AppCoreConstants.DomMimeType:
                    foreach (var block in dom.Blocks)
                    {
                        sb.Append(block.Text
                            .Replace(Entity.ImagePlaceholderCharacter, "")
                            .Replace(Entity.AudioPlaceholderCharacter, "")
                            .Replace(Entity.TodoPlaceholderCharacter, ""));

                        sb.Append(" ");
                    }

                    break;

                case "text/html":
                    var content =
                        "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\"><html><body>" +
                        note.Body + "</body></html>";
                    var configuration = new Configuration();
                    var parser = new HtmlParser(configuration);
                    var doc = await parser.ParseAsync(content);

                    sb.Append(doc.TextContent);
                    break;
            }

            // tags
            foreach (var tag in note.Tags)
            {
                sb.Append(tag);
                sb.Append(" ");
            }

            return sb.ToString();
        }

        private void RefreshTimestamps()
        {
            foreach (var note in notesSource)
            {
                // ReSharper disable once ExplicitCallerInfoArgument
                note.RaisePropertyChanged(nameof(NoteSummary.Timestamp));
            }
        }

        private async Task ExecuteNewNote()
        {
            try
            {
                ExecuteResetSearch();

                var note = new Note
                {
                    Title = coreStrings.UntitledNote,
                    BodyMimeType = AppCoreConstants.DomMimeType,
                    Source = AppCoreConstants.NoteSourceCiphernote
                };

                await SaveNoteAsync(note, new Document());

                var summary = new NoteSummary();
                UpdateSummaryFromNote(summary, note);

                notesSource.Insert(0, summary);
                SelectedNote = summary;
            }

            catch (Exception ex)
            {
                this.Log().Error(() => nameof(ExecuteNewNote), ex);

                // TODO: publish toast or something
            }
        }

        private async Task ExecuteDeleteNotes(IList<object> _notesToDelete, bool silent = false)
        {
            Contract.RequiresNonNull(_notesToDelete, nameof(_notesToDelete));

            try
            {
                this.Log().Info(() => $"{nameof(ExecuteDeleteNotes)}: Deleting {_notesToDelete.Count} notes");

                var notesToDelete = _notesToDelete
                    .OfType<NoteSummary>()
                    .ToArray();

                if (notesToDelete.Any())
                {
                    var notes = await repo.GetNotesByIdAsync(notesToDelete.Select(x => x.Id));

                    // check if we are dealing with soft-deleted notes (in @trash)
                    var isTrash = notes.Any(x => x.IsDeleted);

                    if (!isTrash)
                    {
                        // mark as deleted
                        foreach (var note in notes)
                        {
                            note.IsDeleted = true;
                            note.Timestamp = DateTime.UtcNow;

                            // update sync information
                            note.SyncHmac = await syncService.ComputeSyncHmac(note);
                        }

                        // update db
                        await repo.UpdateNotesAsync(notes);
                    }

                    else
                    {
                        if (!silent)
                        {
                            var promptResult = await promptFactory.Confirm(PromptType.OkCancel,
                                string.Format(coreStrings.ConfirmDeleteNotesTitle, notes.Length),
                                string.Format(coreStrings.ConfirmDeleteNotesMessage, notes.Length));

                            if (promptResult != PromptResult.Ok)
                                return;
                        }

                        // update db
                        var entries = notes.Select(x => new DeletionQueueEntry
                        {
                            Uid = x.Uid,
                            EntryType = DeletionQueueEntryType.Note
                        }).ToArray();

                        await repo.InsertDeletionQueueEntriesAsync(entries);
                        await repo.DeleteNotesAsync(notes);

                        // deselect current note if deleted now
                        if (SelectedNote != null && notes.Any(x => x.Id == SelectedNote.Id))
                            SelectedNote = null;
                    }

                    // remember index of first deleted note
                    var newSelectedIndex = notesSource.IndexOf(notesToDelete.First());

                    // remove from list
                    foreach (var note in notesToDelete)
                        notesSource.Remove(note);

                    // Select first remaining note
                    if (newSelectedIndex < 0 || newSelectedIndex > notesSource.Count - 1)
                        newSelectedIndex = 0;

                    if (notesSource.Any())
                        SelectedNote = notesSource[newSelectedIndex];
                    else
                        SelectedNote = null;
                }
            }

            catch (Exception ex)
            {
                this.Log().Error(() => nameof(ExecuteDeleteNotes), ex);

                // TODO: publish toast or something
            }
        }

        private Task ExecuteSync()
        {
            syncService.Sync();

            return Task.FromResult(true);
        }

        private Task PostProcessOutboundNoteAsync(Note note)
        {
            var summary = notesSource.FirstOrDefault(x => x.Id == note.Id);

            // Reset sync-pending indicator
            if (summary != null)
                summary.IsSyncPending = note.IsSyncPending;

            return Task.FromResult(true);
        }

        private async Task PostProcessInboundNoteAsync(Note note)
        {
            // fetch existing note 
            var existingNote = await repo.GetNoteByUidAsync(note.Uid);

            // compare revisions
            if (existingNote == null || !existingNote.IsSyncPending)
            {
                // adopt id of existing note if we have a replacement
                if (existingNote != null)
                    note.Id = existingNote.Id;

                // store
                try
                {
                    var tcs = new TaskCompletionSource<Unit>();

                    dispatcher.Invoke(() =>
                    {
                        TaskUtil.RunWithCompletionSource(tcs, async () =>
                        {
                            await SaveNoteAsync(note, null, false);

                            // do not load summaries for deleted notes unless we're inside trash
                            if (!note.IsDeleted || (!string.IsNullOrEmpty(SearchTerm) && SearchTerm.ToLower().Contains(
                                SearchQueryProcessingInstructionPrefix + coreStrings.DeletedNotesSearchProcessingInstructionName)))
                            {
                                await LoadSummary(note.Id);
                            }
                        });
                    });

                    await tcs.Task;
                }

                catch (Exception ex)
                {
                    this.Log().Error(() => $"{nameof(PostProcessInboundNoteAsync)}: Error saving note {note.Uid}", ex);
                }
            }

            else
            {
                try
                {
                    var tcs = new TaskCompletionSource<Unit>();

                    dispatcher.Invoke(() =>
                    {
                        TaskUtil.RunWithCompletionSource(tcs, async () =>
                        {
                            // save revision
                            var incomingRevison = note.Revision;

                            // assign new uid
                            note.Uid = Guid.NewGuid();
                            note.Revision = 0;  // new uid implies intial revision
                            await SaveNoteAsync(note, null, false);

                            // adopt incoming revision
                            existingNote.Revision = incomingRevison;
                            await SaveNoteAsync(existingNote, null, false);

                            // mark incoming note as conflicted
                            await repo.SetNotesConflict(note.Id.Value, existingNote.Id.Value);

                            // reload
                            await LoadSummary(note.Id);
                            await LoadSummary(existingNote.Id);

                            // Force conflicts to the top
                            SearchTerm = "";
                            await LoadNotesAsync();
                        });
                    });

                    await tcs.Task;
                }

                catch (Exception ex)
                {
                    this.Log().Error(() => $"{nameof(PostProcessInboundNoteAsync)}: Error saving CONFLICTED note {note.Uid}", ex);
                }
            }
        }

        private async Task PostProcessDeleteRequestForNoteAsync(Guid noteUid)
        {
            // fetch existing note 
            var existingNote = await repo.GetNoteByUidAsync(noteUid);

            // delete from db
            await repo.DeleteNoteAsync(existingNote);

            if (existingNote != null)
            {
                // remove summary
                var summary = notesSource.FirstOrDefault(x => x.Id == existingNote.Id);

                if (summary != null)
                {
                    notesSource.Remove(summary);

                    // remember index of first deleted note
                    var newSelectedIndex = notesSource.IndexOf(summary);

                    // Select first remaining note
                    if (newSelectedIndex < 0 || newSelectedIndex > notesSource.Count - 1)
                        newSelectedIndex = 0;

                    if (notesSource.Any())
                        SelectedNote = notesSource[newSelectedIndex];
                    else
                        SelectedNote = null;
                }
            }
        }
    }
}
