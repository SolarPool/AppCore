using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ciphernote.Dom;
using Ciphernote.Importers.Dom;
using Ciphernote.Importers.Notes;
using Ciphernote.Model;

namespace Ciphernote.Importers
{
    public interface INoteImporter
    {
        string Title { get; }
        string[] SupportedFileExtensions { get; }
        string[] HelpImages { get; }

        Task<int> GetImportStatsAsync(Stream stm, CancellationToken ct);
        IObservable<Note> ImportNotes(Stream stm, NoteImportOptions options, CancellationToken ct);
    }

    public interface IDomImporter
    {
        string Title { get; }
        string[] SupportedMimeTypes { get; }

        Task<Document> ImportDomAsync(object content, DomImportOptions options);
    }
}
