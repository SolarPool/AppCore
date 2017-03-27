using System;
using Splat;

namespace Ciphernote.Logging
{
    public static class LoggingExtensions
    {
        public static void Debug(this ILogger logger, Func<string> output)
        {
            if (logger.Level <= LogLevel.Debug)
                logger.Write(output(), LogLevel.Debug);
        }

        public static void Info(this ILogger logger, Func<string> output)
        {
            if (logger.Level <= LogLevel.Info)
                logger.Write(output(), LogLevel.Info);
        }

        public static void Warning(this ILogger logger, Func<string> output)
        {
            if (logger.Level <= LogLevel.Warn)
                logger.Write(output(), LogLevel.Warn);
        }

        public static void Error(this ILogger logger, Func<string> output, Exception ex = null)
        {
            if (logger.Level <= LogLevel.Error)
            {
                if (ex == null)
                    logger.Write(output(), LogLevel.Error);
                else
                    logger.Write(output() + " => " + ex, LogLevel.Error);
            }
        }

        public static void Error(this ILogger logger, Exception ex)
        {
            if (logger.Level <= LogLevel.Error)
            {
                logger.Write(ex.ToString(), LogLevel.Fatal);
            }
        }

        public static void Critical(this ILogger logger, Func<string> output)
        {
            if (logger.Level <= LogLevel.Fatal)
                logger.Write(output(), LogLevel.Fatal);
        }
    }
}
