using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Ciphernote.Extensions
{
    public static class ObservableStreamExtensions
    {
        public static IObservable<T[]> ToObservable<T>(this Stream stream, int bufSize = 0x10000)
        {
            return Observable.Create<T[]>(o =>
            {
                var buffer = new byte[bufSize];
                try
                {
                    while (true)
                    {
                        var cb = stream.Read(buffer, 0, buffer.Length);
                        if (cb == 0)
                            break;

                        var samples = new T[cb / Marshal.SizeOf(default(T))];
                        Buffer.BlockCopy(buffer, 0, samples, 0, cb);

                        o.OnNext(samples);
                    }
                }

                catch (Exception ex)
                {
                    o.OnError(ex);
                }

                finally
                {
                    o.OnCompleted();
                }

                return Disposable.Empty;
            });


        }
    }
}
