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

            if (!TableExists<Note>(con))
                con.CreateTable<Note>();

            if (!TableExists<DeletionQueueEntry>(con))
                con.CreateTable<DeletionQueueEntry>();

            if (!TableExists<NoteFts>(con))
                con.CreateTable<NoteFts>(CreateFlags.FullTextSearch4);
        }

        private void MigrateSchema(SQLiteConnection con)
        {
            var schemaVersion = con.ExecuteScalar<int>("PRAGMA user_version");

            if (schemaVersion != CurrentSchemaVersion)
            {
                switch (schemaVersion)
                {
                    case 0:
                    case 1:
                        SafeCreateIndex<Note>(con, "NotePagingOrder_Conflict_Timestamp", false,
                            nameof(Note.IsDeleted), 
                            nameof(Note.ConflictingNoteId), 
                            nameof(Note.Timestamp));
                        break;
                }

                //con.Execute("ALTER TABLE notes ADD revision INT NOT NULL DEFAULT 1");

                con.Execute($"PRAGMA user_version = {CurrentSchemaVersion}");
            }
        }

        private bool TableExists<TTable>(SQLiteConnection con)
        {
            Contract.RequiresNonNull(con, nameof(con));

            var tableName = GetTableName<TTable>();

            var result = con.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=?;", tableName);
            return result > 0;
        }

        private static void SafeCreateIndex<TTable>(SQLiteConnection con, string indexName, bool unique, params string[] columnNames)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(indexName), $"{nameof(indexName)} must not be empty");
            Contract.RequiresNonNull(columnNames, nameof(columnNames));
            Contract.Requires<ArgumentException>(columnNames.Length > 0, $"{nameof(columnNames)} must not be empty");

            try
            {
                var tableName = GetTableName<TTable>();
                con.CreateIndex(indexName, tableName, columnNames, unique);
            }

            catch (Exception)
            {
                // ignored
            }
        }

        private static string GetTableName<T>()
        {
            var attr = typeof(T).GetTypeInfo().GetCustomAttribute<TableAttribute>();
            var tableName = attr.Name;
            return tableName;
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

            public bool IsDeleted { get; set; }

            [Indexed]
            public bool IsSyncPending { get; set; }

            public string Title { get; set; }
            public string Excerpt { get; set; }
            public string ThumbnailUri { get; set; }
            public string Body { get; set; }
            public string BodyMimeType { get; set; }
            public string MediaRefs { get; set; }    // semicolon separated list of mediaRefs

            [Indexed]
            [Collation("NOCASE")]
            public string Tags { get; set; }

            public string Source { get; set; }
            public bool HasLocation { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public double? Altitude { get; set; }
            public double? TodoProgress { get; set; }
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
            public string Tags { get; set; }
            public bool HasLocation { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public double? TodoProgress { get; set; }
            public long? ConflictingNoteId { get; set; }
            public long Timestamp { get; set; }
        }

        // Projection over "notes" table
        [Table("notes")]
        internal class NoteTags
        {
            public string Tags { get; set; }
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

        private string[] GetTags(SQLiteConnection con, string whereClause, params object[] parameters)
        {
            Contract.RequiresNonNull(con, nameof(con));

            var query = $"SELECT DISTINCT tags FROM notes  ";
            if (!string.IsNullOrEmpty(whereClause))
                query += whereClause;

            var tags = con.Query<NoteTags>(query, parameters)
                .ToList()
                .SelectMany(x=> x.Tags.Split(Note.TagSeperator[0]).ToArray())
                .Where(x=> x != string.Empty)
                .Distinct()
                .OrderBy(x=> x)
                .ToArray();

            return tags;
        }

        private const string FtsSearchQuery = 
            "SELECT n.* FROM note_fts INNER JOIN notes n ON docid = n.id " +
            "WHERE n.isdeleted = ? AND content MATCH ?";

        private List<NoteSummary> SearchNotesFts(SQLiteConnection con, 
            int offset, int limit, string searchTerm, bool deleted)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(searchTerm), $"{nameof(searchTerm)} must not be empty");

            var query = FtsSearchQuery;

            query += $" ORDER BY conflictingnoteid DESC, timestamp DESC ";
            query += $" LIMIT ? OFFSET ?";
            
            var notes = con.Query<NoteSummary>(query, deleted, searchTerm, limit, offset)
                .ToList();

            return notes;
        }

        private string[] SearchNoteTagsFts(SQLiteConnection con, string searchTerm, bool deleted)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(searchTerm), $"{nameof(searchTerm)} must not be empty");

            var query = FtsSearchQuery;

            return GetTags(con, $"WHERE id IN (SELECT id FROM ({query}))", deleted, searchTerm);
        }

        private static readonly Func<string, string> BuildSubstringSearchQuery = (searchTerm)=>
            $"SELECT n.* FROM note_fts INNER JOIN notes n ON docid = n.id " +
            $"WHERE n.isdeleted = ? AND content LIKE '%{searchTerm}%'";

        private List<NoteSummary> SearchNotesSubstring(SQLiteConnection con, 
            int offset, int limit, string searchTerm, bool deleted)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(searchTerm), $"{nameof(searchTerm)} must not be empty");

            var query = BuildSubstringSearchQuery(searchTerm);

            query += $" ORDER BY conflictingnoteid DESC, timestamp DESC ";
            query += $" LIMIT ? OFFSET ?";

            var notes = con.Query<NoteSummary>(query, deleted, limit, offset)
                .ToList();

            return notes;
        }

        private string[] SearchNoteTagsSubstring(SQLiteConnection con, string searchTerm, bool deleted)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(searchTerm), $"{nameof(searchTerm)} must not be empty");

            var query = FtsSearchQuery;

            return GetTags(con, $"WHERE id IN (SELECT id FROM ({query}))", deleted, searchTerm);
        }

        private static readonly Func<string[], string> BuildTagSearchWhereClause = (tagsToQuery) =>
            "WHERE notes.isdeleted = ? AND " +
                string.Join("AND ", tagsToQuery.Select(x =>
                $"(tags = '{x}' OR tags LIKE '{x}{Note.TagSeperator}%' " +
                $"OR tags LIKE '%{Note.TagSeperator}{x}{Note.TagSeperator}%' " +
                $"OR tags LIKE '%{Note.TagSeperator}{x}')"));

        private List<NoteSummary> SearchNotesTagged(SQLiteConnection con, 
            int offset, int limit, string[] tagsToQuery, bool deleted)
        {
            Contract.RequiresNonNull(con, nameof(con));
            Contract.RequiresNonNull(con, nameof(tagsToQuery));
            Contract.Requires<ArgumentException>(tagsToQuery.Length > 0, $"{nameof(tagsToQuery)} must not be empty");

            // build where clause
            var whereClause = BuildTagSearchWhereClause(tagsToQuery);

            var query = $"SELECT * FROM notes {whereClause} " +
                        $"ORDER BY conflictingnoteid DESC, timestamp DESC " +
                        $"LIMIT ? OFFSET ?";

            var notes = con.Query<NoteSummary>(query, deleted, limit, offset)
                .ToList();

            return notes;
        }

        private string[] SearchNoteTagsTagged(SQLiteConnection con, string[] tagsToQuery, bool deleted)
        {
            Contract.RequiresNonNull(con, nameof(con));

            return GetTags(con, BuildTagSearchWhereClause(tagsToQuery), deleted);
        }

        #region Note API

        public async Task<Model.Projections.NoteSummary[]> GetNotesAsync(int offset, int limit, bool deleted = false)
        {
            var entities = await RunWithConnection(con =>
            {
                return con.Table<NoteSummary>()
                    .Where(x=> x.IsDeleted == deleted)
                    .OrderByDescending(x => x.ConflictingNoteId)
                    .ThenByDescending(x => x.Timestamp)
                    .Skip(offset)
                    .Take(limit)
                    .ToList();
            });

            return await Task.Run(() =>
            {
                var notes = entities
                    .Select(mapper.Map<Model.Projections.NoteSummary>)
                    .ToArray();

                return notes;
            });
        }

        public async Task<Model.Projections.NoteSummary[]> SearchNotesAsync(int offset, int limit, 
            string searchTerm, bool deleted)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(searchTerm), $"{nameof(searchTerm)} must not be empty");

            var result = await RunWithConnection(con =>
            {
                List<NoteSummary> tmp = null;
                var notes = new List<NoteSummary>();
                long[] noteIds = null;
                var sourcesCount = 0;

                // check for tag queries
                var tagsToQuery = new List<string>();

                searchTerm = regexTag.Replace(searchTerm, m =>
                {
                    tagsToQuery.Add(m.Groups[1].Value);
                    return "";
                }).Trim();

                if (tagsToQuery.Count > 0)
                {
                    tmp = SearchNotesTagged(con, offset, limit, tagsToQuery.ToArray(), deleted);
                    noteIds = tmp.Select(x => x.Id).ToArray();

                    notes.AddRange(tmp);
                    sourcesCount++;
                }

                // if there's still something to search add FTS/Substring results
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    switch (appSettings.DefaultSearchMode)
                    {
                        case SearchModeSetting.FtsSearch:
                            if (searchTerm[0] != SearchModeSwitchCharacter)
                                tmp = SearchNotesFts(con, offset, limit, searchTerm, deleted);
                            else
                                tmp = SearchNotesSubstring(con, offset, limit, searchTerm.Substring(1), deleted);

                            sourcesCount++;
                            break;

                        case SearchModeSetting.SubstringSearch:
                            if (searchTerm[0] != SearchModeSwitchCharacter)
                                tmp = SearchNotesSubstring(con, offset, limit, searchTerm, deleted);
                            else
                                tmp = SearchNotesFts(con, offset, limit, searchTerm.Substring(1), deleted);

                            sourcesCount++;
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

                    notes.AddRange(tmp);
                }

                return Tuple.Create(notes, noteIds, sourcesCount);
            });

            return await Task.Run(() =>
            {
                // extract result items
                var entities = result.Item1;
                var _noteIds = result.Item2;
                var _sourcesCount = result.Item3;

                // make distinct
                if (_noteIds.Length != entities.Count)
                    entities = _noteIds.Select(x => entities.First(y => y.Id == x)).ToList();

                // Map 
                var notes = entities
                    .Select(mapper.Map<Model.Projections.NoteSummary>)
                    .ToArray();

                // sort
                if (_sourcesCount > 1)
                {
                    notes = notes
                        .OrderByDescending(x => x.ConflictingNoteId.HasValue)
                        .ThenByDescending(x => x.Timestamp)
                        .ToArray();
                }

                return notes;
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

        #region Tag API

        public Task<string[]> GetTagsAsync(bool deleted = false)
        {
            return RunWithConnection(con =>
            {
                return GetTags(con, "WHERE isdeleted = ?", deleted);
            });
        }

        public Task<string[]> SearchTagsAsync(string searchTerm, bool deleted)
        {
            return RunWithConnection(con =>
            {
                string[] tmp = null;
                var tags = new HashSet<string>();

                // check for tag queries
                var tagsToQuery = new List<string>();

                searchTerm = regexTag.Replace(searchTerm, m =>
                {
                    tagsToQuery.Add(m.Groups[1].Value);
                    return "";
                }).Trim();

                if (tagsToQuery.Count > 0)
                {
                    tmp = SearchNoteTagsTagged(con, tagsToQuery.ToArray(), deleted);

                    foreach (var tag in tmp)
                        tags.Add(tag);
                }

                // if there's still something to search add FTS/Substring results
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    switch (appSettings.DefaultSearchMode)
                    {
                        case SearchModeSetting.FtsSearch:
                            if (searchTerm[0] != SearchModeSwitchCharacter)
                                tmp = SearchNoteTagsFts(con, searchTerm, deleted);
                            else
                                tmp = SearchNoteTagsSubstring(con, searchTerm.Substring(1), deleted);
                            break;

                        case SearchModeSetting.SubstringSearch:
                            if (searchTerm[0] != SearchModeSwitchCharacter)
                                tmp = SearchNoteTagsSubstring(con, searchTerm, deleted);
                            else
                                tmp = SearchNoteTagsFts(con, searchTerm.Substring(1), deleted);
                            break;
                    }

                    foreach (var tag in tmp)
                        tags.Add(tag);
                }

                return tags.ToArray();
            });
        }

        #endregion // Tag API

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