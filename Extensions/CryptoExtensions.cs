using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ciphernote.Extensions
{
    public static class CryptoExtensions
    {
        /// <summary>
        /// A constant time equals comparison - does not terminate early if
        /// test will fail.
        /// </summary>
        /// <param name="a">first array</param>
        /// <param name="b">second array</param>
        /// <returns>true if arrays equal, false otherwise.</returns>
        public static bool ConstantTimeAreEqual(
            this byte[] a,
            byte[] b)
        {
            int i = a.Length;
            if (i != b.Length)
                return false;
            int cmp = 0;
            while (i != 0)
            {
                --i;
                cmp |= (a[i] ^ b[i]);
            }
            return cmp == 0;
        }
    }
}
