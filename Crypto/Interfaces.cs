namespace Ciphernote.Crypto
{
    public interface IRandomNumberGenerator
    {
        byte[] GenerateRandomBytes(int requestedBytes);
    }

    public interface IPasswordSanitizer
    {
        string SanitizePassword(string password);
    }
}
