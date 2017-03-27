using System.Linq;
using System.Reflection;
using Autofac;
using Ciphernote.Crypto;
using Ciphernote.Data;
using Ciphernote.Importers;
using Ciphernote.IO;
using Ciphernote.Media;
using Ciphernote.Services;
using Ciphernote.ViewModels;
using Module = Autofac.Module;

// ReSharper disable once CheckNamespace
namespace Ciphernote.Core
{
    public class AutofacModule : Module
    {
        /// <summary>
        /// Override to add registrations to the container.
        /// </summary>
        /// <remarks>
        /// Note that the ContainerBuilder parameter is unique to this module.
        /// </remarks>
        /// <param name="builder">The builder through which components can be registered.</param>
        protected override void Load(ContainerBuilder builder)
        {
            var thisAssembly = typeof(AutofacModule).GetTypeInfo().Assembly;

            builder.RegisterType<UriStreamResolver>()
                .AsSelf()
                .SingleInstance();

            builder.RegisterType<MainViewModel>()
                .AsSelf()
                .SingleInstance();

            builder.RegisterAssemblyTypes(thisAssembly)
                .Where(t => typeof(ViewModelBase).GetTypeInfo().IsAssignableFrom(t.GetTypeInfo()) &&
                    t != typeof(MainViewModel))
                .AsSelf();

            builder.RegisterType<CryptoService>()
                .AsSelf()
                .SingleInstance();

            builder.RegisterType<MimeTypeProvider>()
                .AsSelf()
                .SingleInstance();

            builder.RegisterType<MediaManager>()
                .AsSelf()
                .SingleInstance();

            builder.RegisterAssemblyTypes(thisAssembly)
                .Where(t => typeof(INoteImporter).GetTypeInfo().IsAssignableFrom(t.GetTypeInfo()))
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterAssemblyTypes(thisAssembly)
                .Where(t => typeof(IDomImporter).GetTypeInfo().IsAssignableFrom(t.GetTypeInfo()))
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterAssemblyTypes(thisAssembly)
                .Where(x => x.Name.EndsWith("Service"))
                .AsSelf()
                .AsImplementedInterfaces()
                .SingleInstance();

            base.Load(builder);
        }
    }
}
