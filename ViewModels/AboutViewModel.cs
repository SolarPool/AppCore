using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Crypto;
using Ciphernote.Extensions;
using Ciphernote.Net;
using Ciphernote.Resources;
using Ciphernote.Services;
using FluentValidation;
using ReactiveUI;
using Splat;

namespace Ciphernote.ViewModels
{
    public class AboutViewModel : ViewModelBase
    {
        public AboutViewModel(IComponentContext ctx, ICoreStrings coreStrings, 
            IAppCoreSettings appSettings) : base(ctx)
        {
            this.coreStrings = coreStrings;
            this.appSettings = appSettings;

            versionString = this.WhenAny(x => x.Version, x => x.Value)
                .Select(FormatVersionString)
                .ToProperty(this, x => x.VersionString);
        }

        private readonly ICoreStrings coreStrings;
        private readonly IAppCoreSettings appSettings;

        private readonly ObservableAsPropertyHelper<string> versionString;

        private Version version;

        public Version Version
        {
            get { return version; }
            set { this.RaiseAndSetIfChanged(ref version, value); }
        }

        public string CopyrightString => $"Copyright © {DateTime.Now.Year} Oliver Weichhold";

        public string VersionString => versionString.Value;

        private string FormatVersionString(Version version)
        {
            if (version == null)
                return string.Empty;

            return $"Build {version.Build} (Beta)";
        }
    }
}
