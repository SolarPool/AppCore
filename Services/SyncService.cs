using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Ciphernote.Data;
using Ciphernote.Extensions;
using Ciphernote.Logging;
using Ciphernote.Media;
using Ciphernote.Model;
using Ciphernote.Net;
using Ciphernote.Resources;
using Ciphernote.Services.Dto;
using Ciphernote.UI;
using Ciphernote.ViewModels;
using CodeContracts;
using Newtonsoft.Json;
using ReactiveUI;
using Splat;
using HttpUtility = System.Net.WebUtility;

namespace Ciphernote.Services
{
    public class SyncService : ReactiveObject
    {
        public SyncService(
            Repository repo, 
            HttpClient httpClient,
            IAppCoreSettings appSettings,
            AuthService authService, 
            CryptoService cryptoService,
            MediaManager mediaManager,
            MimeTypeProvider mimeTypeProvider,
            ICoreStrings coreStrings,
            IPromptFactory promptFactory,
            IAppActivationListener appActivationListener,
            IMapper mapper)
        {
            this.repo = repo;
            this.httpClient = httpClient;
            this.client = new RestClient(AppCoreConstants.ApiEndpoint, httpClient);
            this.appSettings = appSettings;
            this.authService = authService;
            this.cryptoService = cryptoService;
            this.mediaManager = mediaManager;
            this.mimeTypeProvider = mimeTypeProvider;
            this.coreStrings = coreStrings;
            this.promptFactory = promptFactory;
            this.mapper = mapper;

            appActivationListener.Activated
                .Subscribe(_ =>
                {
#if DEBUG
                    if (!appSettings.NoAutoSync)
                        Sync();
#else
                    Sync();
#endif
                });

            // kick off thread
            var thread = new Thread(DoSync);
            thread.IsBackground = true;
            thread.Name = "Sync Queue";
            thread.Start();
        }

        private readonly Repository repo;
        private readonly HttpClient httpClient;
        private readonly RestClient client;
        private readonly AuthService authService;
        private readonly CryptoService cryptoService;
        private readonly MediaManager mediaManager;
        private readonly MimeTypeProvider mimeTypeProvider;
        private readonly AutoResetEvent trigger = new AutoResetEvent(false);
        private readonly IAppCoreSettings appSettings;
        private readonly ICoreStrings coreStrings;
        private readonly IPromptFactory promptFactory;
        private readonly IMapper mapper;
        private string clientPlatform;

        private int syncItemCountTotal;
        private int syncItemCountRemaining;
        private readonly object syncLock = new object();
        private IDisposable autoSyncRetrySub = null;
        private readonly HashSet<string> nonTransientErrorToastsShownFor = new HashSet<string>();

        private readonly HashSet<string> nonTransientErrors = new HashSet<string>
        {
            ServicesErrors.ApiErrorSubscriptionExpired,
            ServicesErrors.ApiErrorEmailConfirmationRequired,
        };

        enum CompressionMethod
        {
            None = 1,
            Deflate = 2,
        }

        #region API Surface

        public void Sync()
        {
            trigger.Set();
        }

        public async Task<string> ComputeSyncHmac(Model.Note note)
        {
            var storedNote = mapper.Map<StoredNote>(note);
            var json = JsonConvert.SerializeObject(storedNote);
            var storedNoteHmac = await cryptoService.ComputeContentHmacAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            var result = Convert.ToBase64String(storedNoteHmac);
            return result;
        }

        public Func<Model.Note, Task> PostProcessInboundNoteHandler { get; set; }
        public Func<Model.Note, Task> PostProcessOutboundNoteHandler { get; set; }
        public Func<Guid, Task> PostProcessDeleteRequestForNoteHandler { get; set; }

        private bool isSynching;

        public bool IsSynching
        {
            get { return isSynching; }
            set { this.RaiseAndSetIfChanged(ref isSynching, value); }
        }

        private double synchProgress;

        public double SynchProgress
        {
            get { return synchProgress; }
            set { this.RaiseAndSetIfChanged(ref synchProgress, value); }
        }

        public void SetClientPlatform(string info)
        {
            this.clientPlatform = info;
        }

        #endregion API Surface

        private void DoSync()
        {
            while (true)
            {
                try
                {
                    trigger.WaitOne();

                    this.Log().Debug(()=> $"{nameof(DoSync)}: Woke up for sync");

                    lock (syncLock)
                    {
                        autoSyncRetrySub?.Dispose();
                        autoSyncRetrySub = null;
                    }

                    IsSynching = true;

                    ProcessDeletionQueueAsync().Wait();

                    // Query State
                    var revisions = repo.GetNoteRevisionsAsync().WaitForResult();
                    var response = QuerySyncStateAsync(revisions).WaitForResult();

                    syncItemCountTotal = 0;
                    syncItemCountRemaining = 0;

                    if (response.Success)
                    {
                        syncItemCountTotal = syncItemCountRemaining = 
                            response.UpdatesAvailable.Length + 
                            response.UpdatesRequested.Length +
                            response.DeleteRequested.Length;

                        this.Log().Info(() => $"Server requests {response.UpdatesRequested.Length} updates, {response.DeleteRequested.Length} deletes and has {response.UpdatesAvailable.Length} updates available.");

                        // down
                        if (response.UpdatesAvailable.Any())
                            ProcessInboundNotesAsync(response.UpdatesAvailable).Wait();

                        // up
                        if (response.UpdatesRequested.Any())
                            ProcessOutboundNotesAsync(response.UpdatesRequested).Wait();

                        // delete
                        if (response.DeleteRequested.Any())
                            ProcessDeletedNoteAsync(response.DeleteRequested).Wait();
                    }

                    else
                    {
                        this.Log().Warning(() => $"QuerySyncStateAsync returned failure response: {response.ResponseMessageType} {(!string.IsNullOrEmpty(response.ResponseMessageId) ? string.Format(response.ResponseMessageId, response.ResponseMessageArgs ?? new object[0]) : "")}");

                        if (!string.IsNullOrEmpty(response.ResponseMessageId))
                        {
                            if (!nonTransientErrors.Contains(response.ResponseMessageId) ||
                                !nonTransientErrorToastsShownFor.Contains(response.ResponseMessageId))
                            {
                                promptFactory.NotifyFromReponse(response, coreStrings, null);

                                nonTransientErrorToastsShownFor.Add(response.ResponseMessageId);
                            }
                        }

                        // reset auth token if non-transient error
                        if (nonTransientErrors.Contains(response.ResponseMessageId))
                        {
                            authService.ResetToken();
                        }
                    }
                }

                catch (Exception ex)
                {
                    this.Log().Error(() => nameof(DoSync), ex);
                }

                finally
                {
                    IsSynching = false;
                }
            }
        }

        private async Task ProcessDeletionQueueAsync()
        {
            var entries = await repo.GetDeletionQueueEntriesAsync();
            var noteEntries = entries.Where(x => x.EntryType == DeletionQueueEntryType.Note).Select(x=> x.Uid).ToArray();

            if (noteEntries.Any())
            {
                this.Log().Info(() => $"{nameof(ProcessDeletionQueueAsync)} deleting notes {string.Join(", ", noteEntries)}");

                var response = await DeleteNotesAsync(noteEntries);

                var deletedEntries = entries
                    .Where(x => response.NotesDeleted.Contains(x.Uid))
                    .ToArray();

                if (deletedEntries.Length > 0)
                    await repo.DeleteDeletionQueueEntriesAsync(deletedEntries);
            }
        }

        private async Task ProcessInboundNotesAsync(Guid[] noteUids)
        {
            await noteUids.ForEachAsync(4, async (noteUid) =>
            {
                this.Log().Debug(() => $"{nameof(ProcessInboundNotesAsync)} note {noteUid}");

                try
                {
                    // do not sync down notes which are already in conflicted state
                    if (await repo.HasNoteConflictAsync(noteUid))
                        return;

                    // get it
                    var action = $"/api/notes/{noteUid}";
                    var request = RestRequest.Create(action, HttpMethod.Post);
                    await authService.SetupAuth(request);
                    var response = (await client.ExecuteAsync<GetNoteResponse>(request)).Content;

                    // download attachments
                    foreach (var attachmentInfo in response.Attachments)
                    {
                        if (!(await mediaManager.HasValidMediaAsync(attachmentInfo.MediaId)))
                        {
                            using (var stream = await httpClient.GetStreamAsync(attachmentInfo.Uri))
                            {
                                await mediaManager.AddEncryptedMediaAsync(stream, attachmentInfo.MediaId, attachmentInfo.ContentType);
                            }
                        }
                    }

                    // reconstruct note
                    var note = await DeserializeNoteFromStorageAsync(response.EncryptedContent, noteUid,
                        response.Revision, response.Hmac, response.Attachments, response.Timestamp);

                    if (PostProcessInboundNoteHandler != null)
                        await PostProcessInboundNoteHandler(note);
                }

                catch (Exception ex)
                {
                    this.Log().Error(() => $"{nameof(ProcessInboundNotesAsync)}: Error while synching note {noteUid}", ex);

                    ScheduleRetrySync();
                }

                NotifyItemDone();
            });
        }

        private async Task ProcessOutboundNotesAsync(Guid[] noteUids)
        {
            await noteUids.ForEachAsync(4, async (noteUid) =>
            {
                this.Log().Debug(() => $"{nameof(ProcessOutboundNotesAsync)} note {noteUid}");

                try
                {
                    var note = await repo.GetNoteByUidAsync(noteUid);
                    if (note == null)
                        return;

                    // query attachment state
                    if (note.MediaRefs.Any())
                    {
                        var queryAttachmentStateResponse = await QueryAttachmentStateAsync(note);

                        // upload attachments
                        foreach (var attachment in queryAttachmentStateResponse.MissingAttachments)
                            await UploadAttachmentAsync(note, attachment);
                    }

                    // package for storage
                    var storedNote = mapper.Map<StoredNote>(note);
                    var json = JsonConvert.SerializeObject(storedNote);
                    var serializedStoredNote = await SerializeJsonForStorageAsync(json);

                    // update
                    var updateNoteResponse = await UpdateNoteAsync(note, serializedStoredNote);

                    if (updateNoteResponse.Success)
                    {
                        // update revision 
                        note.Revision = updateNoteResponse.Revision;
                        note.IsSyncPending = false;

                        // persist it
                        await repo.InsertOrUpdateNoteAsync(note, null);

                        if (PostProcessOutboundNoteHandler != null)
                            await PostProcessOutboundNoteHandler(note);

                        this.Log().Info(() => $"UpdateNoteAsync successful for note {note.Uid} revison {note.Revision}");
                    }

                    else
                        this.Log().Warning(() => $"UpdateNoteAsync returned failure response: {updateNoteResponse.ResponseMessageType} {(!string.IsNullOrEmpty(updateNoteResponse.ResponseMessageId) ? string.Format(updateNoteResponse.ResponseMessageId, updateNoteResponse.ResponseMessageArgs) : "")}");
                }

                catch (Exception ex)
                {
                    this.Log().Error(() => $"{nameof(ProcessInboundNotesAsync)}: Error while synching note {noteUid}", ex);

                    ScheduleRetrySync();
                }

                NotifyItemDone();
            });
        }

        private async Task ProcessDeletedNoteAsync(Guid[] noteUids)
        {
            await noteUids.ForEachAsync(4, async (noteUid) =>
            {
                this.Log().Debug(() => $"{nameof(ProcessDeletedNoteAsync)} note {noteUid}");

                try
                {
                    await PostProcessDeleteRequestForNoteHandler(noteUid);
                }

                catch (Exception ex)
                {
                    this.Log().Error(() => $"{nameof(ProcessDeletedNoteAsync)}: Error while deleting note {noteUid}", ex);

                    ScheduleRetrySync();
                }

                NotifyItemDone();
            });
        }

        private async Task<byte[]> SerializeJsonForStorageAsync(string json)
        {
            // compress
            using (var source = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                using (var destination = new MemoryStream())
                {
                    using (var deflateStream = new DeflateStream(destination, CompressionLevel.Optimal, true))
                    {
                        source.CopyTo(deflateStream);
                    }

                    source.Seek(0, SeekOrigin.Begin);
                    destination.Seek(0, SeekOrigin.Begin);

                    using (var tmp = new MemoryStream())
                    {
                        // did we gain anything from compression?
                        if (destination.Length < source.Length)
                        {
                            tmp.WriteByte((byte) CompressionMethod.Deflate);
                            destination.CopyTo(tmp);
                        }

                        else
                        {
                            tmp.WriteByte((byte) CompressionMethod.None);
                            source.CopyTo(tmp);
                        }

                        tmp.Seek(0, SeekOrigin.Begin);
                        destination.SetLength(0);
                        destination.Seek(0, SeekOrigin.Begin);

                        // encrypt
                        await cryptoService.EncryptContentAsync(tmp, destination);
                        destination.Seek(0, SeekOrigin.Begin);

                        return destination.ToArray();
                    }
                }
            }
        }

        private async Task<Model.Note> DeserializeNoteFromStorageAsync(string encryptedContent, 
            Guid uid, uint revision, string syncHmac, AttachmentInfo[] attachments, DateTime timestamp)
        {
            // decrypt
            using (var source = new MemoryStream(Convert.FromBase64String(encryptedContent)))
            {
                using (var destination = new MemoryStream())
                {
                    await cryptoService.DecryptContentAsync(source, destination);

                    // decompress
                    using (var tmp = new MemoryStream())
                    {
                        destination.Seek(0, SeekOrigin.Begin);
                        var compressionMethod = (CompressionMethod) destination.ReadByte();

                        switch (compressionMethod)
                        {
                            case CompressionMethod.Deflate:
                                using (var deflateStream = new DeflateStream(destination, CompressionMode.Decompress, true))
                                {
                                    deflateStream.CopyTo(tmp);
                                }
                                break;

                            case CompressionMethod.None:
                                destination.CopyTo(tmp);
                                break;
                        }

                        // deserialize
                        var json = Encoding.UTF8.GetString(tmp.ToArray());
                        var storedNote = JsonConvert.DeserializeObject<StoredNote>(json);

                        // map
                        var note = mapper.Map<Model.Note>(storedNote);

                        // fill in blanks
                        note.Uid = uid;
                        note.Revision = revision;
                        note.SyncHmac = syncHmac;
                        note.MediaRefs = attachments.Select(x => x.MediaId).ToArray();
                        note.Timestamp = timestamp;

                        return note;
                    }
                }
            }
        }

        private string GetClientId()
        {
            return appSettings.DeviceId.ToString();
        }

        private string GetClientPlatform()
        {
            return clientPlatform;
        }

        private void NotifyItemDone()
        {
            syncItemCountRemaining--;
            if (syncItemCountRemaining < 0)
                syncItemCountRemaining = 0;

            SynchProgress = ((double) syncItemCountTotal - syncItemCountRemaining)/syncItemCountTotal;
        }

        private void ScheduleRetrySync()
        {
            lock (syncLock)
            {
                if (autoSyncRetrySub != null)
                {
                    autoSyncRetrySub = Observable.Timer(TimeSpan.FromMinutes(5))
                        .Subscribe(_ => trigger.Set());
                }
            }
        }

        #region Rest Requests

        private async Task<QuerySyncStateResponse> QuerySyncStateAsync(NoteRevision[] revisions)
        {
            var querySyncStateRequest = new QuerySyncStateRequest
            {
                Revisions = revisions,

                // Metadata
                ClientId = GetClientId(),
                ClientPlatform = GetClientPlatform(),
            };

            var request = RestRequest.Create("/api/notes/querysyncstate", HttpMethod.Post, querySyncStateRequest, true);
            await authService.SetupAuth(request);
            var response = await client.ExecuteAsync<QuerySyncStateResponse>(request);
            return response.Content;
        }

        private async Task<QueryAttachmentStateResponse> QueryAttachmentStateAsync(Model.Note note)
        {
            var queryAttachmentStateRequest = new QueryAttachmentStateRequest
            {
                Attachments = note.MediaRefs,

                // Metadata
                ClientId = GetClientId(),
                ClientPlatform = GetClientPlatform(),
            };

            var request = RestRequest.Create("/api/notes/queryattachmentstate", HttpMethod.Post, queryAttachmentStateRequest);
            await authService.SetupAuth(request);
            var response = await client.ExecuteAsync<QueryAttachmentStateResponse>(request);
            return response.Content;
        }

        private async Task<UpdateNoteResponse> UpdateNoteAsync(Model.Note note, byte[] serializedStoredNote)
        {
            var updateNoteRequest = new UpdateNoteRequest
            {
                Revision = note.Revision,
                Hmac = note.SyncHmac,
                EncryptedContent = Convert.ToBase64String(serializedStoredNote),
                Timestamp = note.Timestamp,
                Attachments = note.MediaRefs.Select(x => new AttachmentInfo
                {
                    MediaId = x,
                    ContentType = mimeTypeProvider.GetMimeTypeForExtension(Path.GetExtension(x))
                }).ToArray(),

                // Metadata
                ClientId = GetClientId(),
                ClientPlatform = GetClientPlatform(),
            };

            var action = $"/api/notes/{note.Uid}/update";
            var request = RestRequest.Create(action, HttpMethod.Post, updateNoteRequest);
            await authService.SetupAuth(request);
            var response = await client.ExecuteAsync<UpdateNoteResponse>(request);
            return response.Content;
        }

        private async Task<ResponseBase> UploadAttachmentAsync(Model.Note note, string mediaId)
        {
            // open content
            using (var encryptedContentStream = await mediaManager.OpenEncryptedMediaAsync(mediaId))
            {
                // compute hash
                string hash;

                using (var mac = SHA256.Create())
                {
                    hash = Convert.ToBase64String(mac.ComputeHash(encryptedContentStream));
                }

                encryptedContentStream.Seek(0, SeekOrigin.Begin);

                // build request
                var path = $"/api/notes/{note.Uid}/attach-media";
                var parameters = new[]
                {
                    new KeyValuePair<string, string>("mediaId", mediaId),
                    new KeyValuePair<string, string>("hash", hash),
                    new KeyValuePair<string, string>("clientId", GetClientId()),
                    new KeyValuePair<string, string>("clientPlatform", GetClientPlatform()),
                };

                var pathAndQuery = $"{path}?{string.Join("&", parameters.Select(x => $"{x.Key}={HttpUtility.UrlEncode(x.Value)}"))}";

                var msg = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(new Uri(AppCoreConstants.ApiEndpoint), pathAndQuery),
                    Content = new StreamContent(encryptedContentStream)
                };

                await authService.SetupAuth(msg);
                var httpResponse = await httpClient.SendAsync(msg);

                if(!httpResponse.IsSuccessStatusCode)
                    throw new RestRequestException(httpResponse.StatusCode, await httpResponse.Content.ReadAsStringAsync());
            }

            return new ResponseBase { Success = true };
        }

        private async Task<DeleteNotesResponse> DeleteNotesAsync(Guid[] noteUid)
        {
            var deleteNotesRequest = new DeleteNotesRequest
            {
                NoteIds = noteUid
            };

            var action = $"/api/notes/delete";
            var request = RestRequest.Create(action, HttpMethod.Post, deleteNotesRequest);
            await authService.SetupAuth(request);
            var response = await client.ExecuteAsync<DeleteNotesResponse>(request);
            return response.Content;
        }

        #endregion // Rest Requests
    }
}
