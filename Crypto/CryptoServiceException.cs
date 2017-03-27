using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ciphernote.Crypto
{
    public enum CryptoServiceExceptionType
    {
        HmacMismatch = 1,
        AccountKeyNotInitialized,
        MasterKeyNotInitialized,
        CredentialsNotInitialized,
        UnsupportedFlag,
    }

    public class CryptoServiceException : Exception
    {
        public CryptoServiceException(CryptoServiceExceptionType type)
        {
            ExceptionType = type;
        }

        public CryptoServiceExceptionType ExceptionType { get; private set; }
    }
}
