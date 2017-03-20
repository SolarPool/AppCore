using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Ciphernote.Crypto;
using Ciphernote.Extensions;
using Ciphernote.Logging;
using Ciphernote.Services;
using Ciphernote.Services.Dto;
using CodeContracts;
using Splat;
using SQLite;

// ReSharper disable ConvertToLambdaExpression

namespace Ciphernote.Data
{
    public class Repository : IEnableLogger
    {
        public Repository(string dbBasePath, IMapper mapper, CryptoService cryptoService, IAppCoreSettings appSettings)
        {
            Contract.RequiresNonNull(dbBasePath, nameof(dbBasePath));
            Contract.RequiresNonNull(mapper, nameof(mapper));
            Contract.RequiresNonNull(cryptoService, nameof(cryptoService));
            Contract.RequiresNonNull(appSettings, nameof(appSettings));

            this.dbBasePath = dbBasePath;
            this.mapper = mapper;
            this.cryptoService = cryptoService;
            this.appSettings = appSettings;
        }

        private void SetupTables(SQLiteConnection con)
        {
            Contract.RequiresNonNull(con, nameof(con));

            if (!ExistsTable(con, typeof(Note)))
                con.CreateTable<Note>();

            if (!ExistsTable(con, typeof(DeletionQueueEntry)))
                con.CreateTable<DeletionQueueEntry>();

            if (!ExistsTable(con, typeof(NoteFts)))
                con.CreateTable<NoteFts>(CreateFlags.FullTextSearch4);
        }

        private void MigrateSchema(SQLiteConnection con)
        {
            var schemaVersion = con.ExecuteScalar<int>("PRAGMA user_version");

            if (schemaVersion != CurrentSchemaVersion)
            {
                //con.Execute("ALTER TABLE notes ADD revision INT NOT NULL DEFAULT 1");

                con.Execute($"PRAGMA user_version = {CurrentSchemaVersion}");
            }
        }

        private bool ExistsTable(SQLiteConnection con, Type type)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.RequiresNonNull(type, nameof(type));

            var attr = type.GetTypeInfo().GetCustomAttribute<TableAttribute>();
            var tableName = attr.Name;

            var result = con.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=?;", tableName);
            return result > 0;
        }

        private const int CurrentSchemaVersion = 1;
        private readonly CryptoService cryptoService;
        private readonly string dbBasePath;
        private readonly IMapper mapper;
        private BlockingCollection<Action<SQLiteConnection>> requestQueue = new BlockingCollection<Action<SQLiteConnection>>();

        const SQLiteOpenFlags ConOpenFlags = SQLiteOpenFlags.Create | SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.NoMutex;
        private const int SqlCipherKeyLength = 32;

        private readonly Regex regexTag = new Regex(@"#(\w+)", RegexOptions.Compiled);
        private readonly IAppCoreSettings appSettings;

        public const char SearchModeSwitchCharacter = '!';

        #region Entities

        [Table("notes")]
        internal class Note
        {
            [PrimaryKey, AutoIncrement]
            public long Id { get; set; }

            [Indexed]
            public string Uid { get; set; }

            [Indexed]
            public bool IsDeleted { get; set; }

            [Indexed]
            public bool IsSyncPending { get; set; }

            public string Title { get; set; }
            public string Excerpt { get; set; }
            public string ThumbnailUri { get; set; }
            public string Body { get; set; }
            public string BodyMimeType { get; set; }
            public string MediaRefs { get; set; }    // semicolon separated list of mediaRefs

            [Collation("NOCASE")]
            public string Tags { get; set; }

            public string Source { get; set; }
            public bool HasLocation { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public double? Altitude { get; set; }
            public double? TodoProgress { get; set; }

            [Indexed]
            public long Timestamp { get; set; }

            // Synching
            public uint Revision { get; set; }
            public string SyncHmac { get; set; }
            public long? ConflictingNoteId { get; set; }

            internal const string TagSeperator = ";";
        }

        // Projection over "notes" table
        [Table("notes")]
        internal class NoteSummary
        {
            [PrimaryKey]
            public long Id { get; set; }

            public bool IsDeleted { get; set; }
            public bool IsSyncPending { get; set; }

            public string Title { get; set; }
            public string Excerpt { get; set; }
            public string ThumbnailUri { get; set; }
            public string BodyMimeType { get; set; }
            public string MediaRefs { get; set; }    // semicolon separated list of mediaRefs
            public string Tags { get; set; }
            public bool HasLocation { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public double? Altitude { get; set; }
            public double? TodoProgress { get; set; }
            public long? ConflictingNoteId { get; set; }
            public long Timestamp { get; set; }
        }

        [Table("note_fts")]
        internal class NoteFts
        {
            public string Content { get; set; }    // Body in plaintext, title, tags 
        }

        [Table("deletion_queue")]
        internal class DeletionQueueEntry
        {
            [PrimaryKey, AutoIncrement]
            public long Id { get; set; }

            [Indexed]
            public string Uid { get; set; }

            [Indexed]
            public int EntryType { get; set; }
        }

        #endregion // Entities

        private string ComputeDbPath()
        {
            var dbPath = Path.Combine(
                dbBasePath,
                AppCoreConstants.ProfileDataBaseFolder,
                cryptoService.ProfileName,
                AppCoreConstants.DatabaseFilename);
            return dbPath;
        }

        public void Open()
        {
            var dbPath = ComputeDbPath();

            // ensure database folder exists
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath));

            ServiceRequests(dbPath);
        }

        public void Close()
        {
            requestQueue?.Dispose();
            requestQueue = null;
        }

        private static void SetConnectionKey(byte[] key, SQLiteConnection con)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.RequiresNonNull(key, nameof(key));
            Contract.Requires<ArgumentException>(key.Length == SqlCipherKeyLength, $"key must be exactly {SqlCipherKeyLength} bytes long");

            var keyString = key.ToHex();
            var query = $"PRAGMA key = \"x'{keyString}'\"";
            con.Execute(query);
        }

        private void ServiceRequests(string dbPath)
        {
            var thread = new Thread(() =>
            {
                var initialized = false;

                using (var con = new SQLiteConnection(dbPath, ConOpenFlags))
                {
                    while (true)
                    {
                        try
                        {
                            this.Log().Debug(() => $"{nameof(ServiceRequests)}: Waiting for request...");

                            var request = requestQueue.Take();

                            this.Log().Debug(() => $"{nameof(ServiceRequests)}: Got request");

                            // set key
                            SetConnectionKey(cryptoService.DbEncryptionKey, con);

                            // lazy init
                            if (!initialized)
                            {
                                SetupTables(con);
                                MigrateSchema(con);

                                initialized = true;
                            }

                            this.Log().Debug(() => $"{nameof(ServiceRequests)}: Executing request...");

                            // execute
                            request(con);

                            this.Log().Debug(() => $"{nameof(ServiceRequests)}: Request executed");
                        }

                        catch (ObjectDisposedException)
                        {
                            this.Log().Info(() => $"{nameof(ServiceRequests)}: Queue has been disposed. Exiting.");
                            break;
                        }

                        catch (Exception ex)
                        {
                            this.Log().Error(() => nameof(ServiceRequests), ex);

                            // bail on initialization error
                            if (!initialized)
                            {
                                this.Log().Critical(() => $"{nameof(ServiceRequests)}: Exception during initialization. Exiting");
                                throw;
                            }
                        }
                    }
                }
            });

            thread.IsBackground = true;
            thread.Name = "Repository Request Queue";
            thread.Start();
        }

        private Task RunWithConnection(Action<SQLiteConnection> action)
        {
            var tcs = new TaskCompletionSource<Unit>();

            requestQueue.Add(con =>
            {
                try
                {
                    action(con);

                    tcs.SetResult(Unit.Default);
                }

                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        private Task<T> RunWithConnection<T>(Func<SQLiteConnection, T> action)
        {
            var tcs = new TaskCompletionSource<T>();

            requestQueue.Add(con =>
            {
                try
                {
                    T result = action(con);

                    tcs.SetResult(result);
                }

                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        private Task InTransaction(Action<SQLiteConnection> action)
        {
            var tcs = new TaskCompletionSource<Unit>();

            requestQueue.Add(con =>
            {
                try
                {
                    con.RunInTransaction(() =>
                    {
                        action(con);
                        tcs.SetResult(Unit.Default);
                    });
                }

                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        private Task<T> InTransaction<T>(Func<SQLiteConnection, T> action)
        {
            var tcs = new TaskCompletionSource<T>();

            requestQueue.Add(con =>
            {
                try
                {
                    con.RunInTransaction(() =>
                    {
                        T result = action(con);
                        tcs.SetResult(result);
                    });
                }

                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        private Model.Projections.NoteSummary[] SearchNotesFts(SQLiteConnection con, string searchTerm, bool searchInDeleted)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(searchTerm), $"{nameof(searchTerm)} must not be empty");

            var query = "SELECT n.* FROM note_fts INNER JOIN notes n ON docid = n.id " +
                        "WHERE n.isdeleted = ? AND content MATCH ?;";

            var notes = con.Query<NoteSummary>(query, searchInDeleted, searchTerm)
                .ToList()
                .Select(mapper.Map<Model.Projections.NoteSummary>)
                .ToArray();

            return notes;
        }

        private Model.Projections.NoteSummary[] SearchNotesSubstring(SQLiteConnection con, string searchTerm, bool searchInDeleted)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(searchTerm), $"{nameof(searchTerm)} must not be empty");

            // build where clause
            var query = $"SELECT n.* FROM note_fts INNER JOIN notes n ON docid = n.id " +
                        $"WHERE n.isdeleted = ? AND content LIKE '%{searchTerm}%'";

            var notes = con.Query<NoteSummary>(query, searchInDeleted)
                .ToList()
                .Select(mapper.Map<Model.Projections.NoteSummary>)
                .ToArray();

            return notes;
        }

        private Model.Projections.NoteSummary[] SearchNotesTaggged(SQLiteConnection con, string[] tags, bool searchInDeleted)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.RequiresNonNull(con, nameof(tags));
            Contract.Requires<ArgumentException>(tags.Length > 0, $"{nameof(tags)} must not be empty");

            // build where clause
            var whereClause = string.Join("AND ", tags.Select(x => 
                $"(tags = '{x}' OR tags LIKE '{x}{Note.TagSeperator}%' " +
                $"OR tags LIKE '%{Note.TagSeperator}{x}{Note.TagSeperator}%' " +
                $"OR tags LIKE '%{Note.TagSeperator}{x}')"));

            var query = $"SELECT * FROM notes WHERE notes.isdeleted = ? AND {whereClause};";

            var notes = con.Query<NoteSummary>(query, searchInDeleted)
                .ToList()
                .Select(mapper.Map<Model.Projections.NoteSummary>)
                .ToArray();

            return notes;
        }

        #region Note API

        public Task<Model.Projections.NoteSummary[]> GetNotesAsync(bool deleted = false)
        {
            return RunWithConnection(con =>
            {
                var query = con.Table<NoteSummary>();

                var notes = query
                    .Where(x => x.IsDeleted == deleted)
                    .ToList()
                    .Select(mapper.Map<Model.Projections.NoteSummary>)
                    .OrderByDescending(x => x.ConflictingNoteId.HasValue)
                    .ThenByDescending(x => x.Timestamp)
                    .ToArray();

                return notes;
            });
        }

        public Task<Model.Projections.NoteSummary[]> SearchNotesAsync(string searchTerm, bool searchInDeleted)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(searchTerm), $"{nameof(searchTerm)} must not be empty");

            return RunWithConnection(con =>
            {
                var result = new List<Model.Projections.NoteSummary>();
                Model.Projections.NoteSummary[] tmp = null;
                var shouldReorder = false;
                long[] noteIds = null;

                // check for tag queries
                var tags = new List<string>();

                searchTerm = regexTag.Replace(searchTerm, m =>
                {
                    tags.Add(m.Groups[1].Value);
                    return "";
                }).Trim();

                if (tags.Count > 0)
                {
                    tmp = SearchNotesTaggged(con, tags.ToArray(), searchInDeleted);
                    noteIds = tmp.Select(x => x.Id).ToArray();

                    result.AddRange(tmp);
                    shouldReorder = true;
                }

                // if there's still something to search add FTS/Substring results
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    switch (appSettings.DefaultSearchMode)
                    {
                        case SearchModeSetting.FtsSearch:
                            if (searchTerm[0] != SearchModeSwitchCharacter)
                                tmp = SearchNotesFts(con, searchTerm, searchInDeleted);
                            else
                            {
                                tmp = SearchNotesSubstring(con, searchTerm.Substring(1), searchInDeleted);
                                shouldReorder = true;
                            }
                            break;

                        case SearchModeSetting.SubstringSearch:
                            if (searchTerm[0] != SearchModeSwitchCharacter)
                            {
                                tmp = SearchNotesSubstring(con, searchTerm, searchInDeleted);
                                shouldReorder = true;
                            }
                            else
                                tmp = SearchNotesFts(con, searchTerm.Substring(1), searchInDeleted);
                            break;
                    }

                    if (noteIds != null)
                    {
                        noteIds = tmp.Select(x => x.Id)
                            .ToArray()
                            .Intersect(noteIds)
                            .ToArray();
                    }

                    else
                        noteIds = tmp.Select(x => x.Id).ToArray();

                    result.AddRange(tmp);
                }

                // make distinct
                if (noteIds.Length != result.Count)
                    result = noteIds.Select(x => result.First(y => y.Id == x)).ToList();

                // sort
                if (shouldReorder)
                {
                    result = result
                        .OrderByDescending(x => x.Timestamp)
                        .ToList();
                }

                return result.ToArray();
            });
        }

        public Task<Model.Note> GetNoteAsync(long id)
        {
            Contract.Requires<ArgumentException>(id != 0, $"{nameof(id)} must not be empty");

            return RunWithConnection(con =>
            {
                var note = con.Find<Note>(id);
                if (note == null)
                    return null;

                var result = mapper.Map<Model.Note>(note);
                return result;
            });
        }

        public Task<Model.Note> GetNoteByUidAsync(Guid uid)
        {
            Contract.Requires<ArgumentException>(uid != Guid.Empty, $"{nameof(uid)} must not be empty");

            return RunWithConnection(con =>
            {
                var id = uid.ToString();

                var note = con.Table<Note>()
                    .Where(x => x.Uid == id)
                    .Select(mapper.Map<Model.Note>)
                    .FirstOrDefault();

                return note;
            });
        }

        public Task<Model.Projections.NoteSummary> GetNoteSummaryAsync(long id)
        {
            Contract.Requires<ArgumentException>(id != 0, $"{nameof(id)} must not be empty");

            return RunWithConnection(con =>
            {
                var note = con.Find<NoteSummary>(id);
                if (note == null)
                    return null;

                var result = mapper.Map<Model.Projections.NoteSummary>(note);
                return result;
            });
        }

        public Task<Model.Note[]> GetNotesByIdAsync(IEnumerable<long> ids)
        {
            Contract.RequiresNonNull(ids, nameof(ids));

            return RunWithConnection(con =>
            {
                var query = $"SELECT * FROM notes WHERE id IN({string.Join(", ", ids)})";

                var notes = con.Query<Note>(query)
                    .ToList()
                    .Select(mapper.Map<Model.Note>)
                    .ToArray();

                return notes;
            });
        }

        public Task<Model.Note[]> GetNotesByUidAsync(IEnumerable<Guid> uids)
        {
            Contract.RequiresNonNull(uids, nameof(uids));

            return RunWithConnection(con =>
            {
                var query = $"SELECT * FROM notes WHERE uid IN({string.Join(", ", uids.ToString())})";

                var notes = con.Query<Note>(query)
                    .ToList()
                    .Select(mapper.Map<Model.Note>)
                    .ToArray();

                return notes;
            });
        }

        public Task<bool> NoteExistsAsync(long id)
        {
            Contract.Requires<ArgumentException>(id != 0, $"{nameof(id)} must not be empty");

            return RunWithConnection(con =>
            {
                var note = con.Find<Note>(id);
                return note != null;
            });
        }

        public Task<long> InsertOrUpdateNoteAsync(Model.Note note, string textContent)
        {
            Contract.RequiresNonNull(note, nameof(note));

            return InTransaction(con =>
            {
                var mapped = mapper.Map<Note>(note);

                if (note.Id.HasValue)
                {
                    con.Update(mapped);

                    if (!string.IsNullOrEmpty(textContent))
                        con.ExecuteScalar<int>("UPDATE note_fts SET Content = ? WHERE docid = ?", textContent, mapped.Id);
                }

                else
                {
                    con.Insert(mapped);
                    note.Id = mapped.Id;

                    var foo = con.Find<Note>(mapped.Id);

                    if (!string.IsNullOrEmpty(textContent))
                        con.ExecuteScalar<int>("INSERT INTO note_fts(docid, Content) VALUES(?, ?)", mapped.Id, textContent);
                }

                return note.Id.Value;
            });
        }

        public Task<int> UpdateNoteAsync(Model.Note note)
        {
            Contract.RequiresNonNull(note, nameof(note));

            return RunWithConnection(con =>
            {
                var mapped = mapper.Map<Note>(note);

                return con.Update(mapped);
            });
        }

        public Task<int> InsertNotesAsync(IEnumerable<Model.Note> notes)
        {
            Contract.RequiresNonNull(notes, nameof(notes));

            return RunWithConnection(con =>
            {
                return con.InsertAll(notes.Select(mapper.Map<Note>));
            });
        }

        public Task<int> UpdateNotesAsync(IEnumerable<Model.Note> notes)
        {
            Contract.RequiresNonNull(notes, nameof(notes));

            return RunWithConnection(con =>
            {
                return con.UpdateAll(notes.Select(mapper.Map<Note>));
            });
        }

        public Task InsertNotesSafeAsync(IEnumerable<Model.Note> notes)
        {
            Contract.RequiresNonNull(notes, nameof(notes));

            return InTransaction(con =>
            {
                var result = notes
                    .Where(x => con.Find<Note>(x.Id) == null)
                    .Select(mapper.Map<Note>);

                con.InsertAll(result);
            });
        }

        public Task DeleteNoteAsync(Model.Note note)
        {
            Contract.RequiresNonNull(note, nameof(note));

            return RunWithConnection(con =>
            {
                return con.Delete<Note>(note.Id);
            });
        }

        public Task DeleteNotesAsync(IEnumerable<Model.Note> notes)
        {
            Contract.RequiresNonNull(notes, nameof(notes));

            return InTransaction(con =>
            {
                foreach (var note in notes)
                    con.Delete<Note>(note.Id);
            });
        }

        public Task DeleteNotesAsync(IEnumerable<Model.Projections.NoteSummary> notes)
        {
            Contract.RequiresNonNull(notes, nameof(notes));

            return InTransaction(con =>
            {
                foreach (var note in notes)
                    con.Delete<Note>(note.Id);
            });
        }

        public Task<NoteRevision[]> GetNoteRevisionsAsync()
        {
            return RunWithConnection(con =>
            {
                var query = $"SELECT uid, revision, synchmac as 'hmac', timestamp FROM notes WHERE conflictingnoteid IS NULL ORDER BY timestamp DESC";

                var notes = con.Query<NoteRevision>(query)
                    .ToArray();

                return notes;
            });
        }

        public Task SetNotesConflict(long id1, long id2)
        {
            Contract.Requires<ArgumentOutOfRangeException>(id1 != 0, nameof(id1));
            Contract.Requires<ArgumentOutOfRangeException>(id2 != 0, nameof(id2));

            return InTransaction(con =>
            {
                var note1 = con.Find<Note>(id1);
                var note2 = con.Find<Note>(id2);

                if (note1 == null || note2 == null)
                    throw new Exception("One of the specified note ids does not exist");

                if (note1.ConflictingNoteId.HasValue || note2.ConflictingNoteId.HasValue)
                    throw new Exception("One of the specified notes is already in conflict-state");

                note1.ConflictingNoteId = note2.Id;
                note2.ConflictingNoteId = note1.Id;

                con.Update(note1);
                con.Update(note2);
            });
        }

        public Task<bool> HasNoteConflictAsync(Guid noteId)
        {
            return RunWithConnection(con =>
            {
                var query = $"SELECT count(*) FROM notes where uid = ? AND conflictingnoteid IS NOT NULL";
                var count = con.ExecuteScalar<int>(query, noteId.ToString());
                return count == 1;
            });
        }

        public Task ClearNoteConflict(long id)
        {
            Contract.Requires<ArgumentOutOfRangeException>(id != 0, nameof(id));

            return InTransaction(con =>
            {
                var note1 = con.Find<Note>(id);

                if (note1?.ConflictingNoteId == null)
                    return;

                // find related note
                var note2 = con.Find<Note>(note1.ConflictingNoteId.Value);

                if (note2 != null)
                {
                    note2.ConflictingNoteId = null;
                    con.Update(note2);
                }

                note1.ConflictingNoteId = null;
                con.Update(note1);
            });
        }

        #endregion  // Note API

        #region Delete Queue API

        public Task<int> InsertDeletionQueueEntriesAsync(IEnumerable<Model.DeletionQueueEntry> entries)
        {
            Contract.RequiresNonNull(entries, nameof(entries));

            return RunWithConnection(con =>
            {
                return con.InsertAll(entries.Select(mapper.Map<DeletionQueueEntry>));
            });
        }

        public Task<Model.DeletionQueueEntry[]> GetDeletionQueueEntriesAsync()
        {
            return RunWithConnection(con =>
            {
                var query = con.Table<DeletionQueueEntry>();

                var entries = query
                    .Select(mapper.Map<Model.DeletionQueueEntry>)
                    .ToArray();

                return entries;
            });
        }

        public Task DeleteDeletionQueueEntriesAsync(IEnumerable<Model.DeletionQueueEntry> entries)
        {
            Contract.RequiresNonNull(entries, nameof(entries));

            return InTransaction(con =>
            {
                foreach (var entry in entries)
                    con.Delete<DeletionQueueEntry>(entry.Id);
            });
        }

        #endregion  // Delete Queue API
    }
}