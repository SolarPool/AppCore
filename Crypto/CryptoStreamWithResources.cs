using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Disposables;
using System.Security.Cryptography;

namespace Ciphernote.Crypto
{
    public class CryptoStreamWithResources : CryptoStream
    {
        public CryptoStreamWithResources(Stream stream, ICryptoTransform transform, CryptoStreamMode mode,
            IEnumerable<IDisposable> disposables) : base(stream, transform, mode)
        {
            this.disposables = new CompositeDisposable(disposables);
        }

        private CompositeDisposable disposables;

        protected override void Dispose(bool disposing)
        {
            if (disposables != null)
            {
                try
                {
                    base.Dispose(disposing);
                }

                catch (CryptographicException)
                {
                    // https://github.com/dotnet/corefx/issues/7779#issuecomment-261035347
                }

                disposables.Dispose();
                disposables = null;
            }
        }
    }
}
