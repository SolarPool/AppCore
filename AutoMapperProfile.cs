using System;
using System.Linq;
using AutoMapper;
using Ciphernote.Data;
using Ciphernote.Services.Dto;
using ReactiveUI;

// ReSharper disable once CheckNamespace
namespace Ciphernote.Core
{
    public class AutoMapperProfile : Profile
    {
        public AutoMapperProfile()
        {
            // outgoing mappings
            CreateMap<Repository.Note, Model.Note>()
                .ForMember(dest => dest.Uid, opt => opt.MapFrom(src => Guid.Parse(src.Uid)))
                .ForMember(dest => dest.Tags, opt => opt.MapFrom(src => new ReactiveList<string>(
                    !string.IsNullOrEmpty(src.Tags) ? src.Tags.Split(Repository.Note.TagSeperator[0]).ToArray() : new string[0])))
                .ForMember(dest => dest.MediaRefs, opt => opt.MapFrom(src => !string.IsNullOrEmpty(src.MediaRefs) ?
                    src.MediaRefs.Split(Repository.Note.TagSeperator[0]).ToArray() : new string[0]))
                .ForMember(dest => dest.Timestamp, opt=> opt.MapFrom(src => DateTimeOffset.FromUnixTimeSeconds(src.Timestamp).UtcDateTime));

            CreateMap<Repository.NoteSummary, Model.Projections.NoteSummary>()
                .ForMember(dest => dest.Tags, opt => opt.MapFrom(src => 
                    !string.IsNullOrEmpty(src.Tags) ? src.Tags.Split(Repository.Note.TagSeperator[0]).ToArray() : new string[0]))
                .ForMember(dest => dest.Timestamp, opt => opt.MapFrom(src => DateTimeOffset.FromUnixTimeSeconds(src.Timestamp).UtcDateTime));

            CreateMap<Model.Note, StoredNote>()
                .ForMember(dest => dest.Tags, opt => opt.MapFrom(src => src.Tags.ToArray()));

            // incoming mappings
            CreateMap<Model.Note, Repository.Note>()
                .ForMember(dest => dest.Uid, opt => opt.MapFrom(src => src.Uid.ToString()))
                .ForMember(dest => dest.Tags, opt => opt.MapFrom(src => src.Tags != null ? string.Join(
                    Repository.Note.TagSeperator, src.Tags) : ""))
                .ForMember(dest => dest.MediaRefs, opt => opt.MapFrom(src => src.MediaRefs != null ? string.Join(
                    Repository.Note.TagSeperator, src.MediaRefs) : ""))
                .ForMember(dest => dest.Timestamp, opt => opt.MapFrom(src => ((DateTimeOffset)src.Timestamp).ToUnixTimeSeconds()));

            CreateMap<Model.Projections.NoteSummary, Repository.NoteSummary>()
                .ForMember(dest => dest.Tags, opt => opt.MapFrom(src => src.Tags != null ? string.Join(
                    Repository.Note.TagSeperator, src.Tags) : ""))
                .ForMember(dest => dest.Timestamp, opt => opt.MapFrom(src => ((DateTimeOffset)src.Timestamp).ToUnixTimeSeconds()));

            CreateMap<StoredNote, Model.Note>()
                .ForMember(dest => dest.Tags, opt => opt.MapFrom(src => new ReactiveList<string>(src.Tags)));
        }
    }
}
