using System.IO;

namespace Utility
{
    public static class DirectoryExtensions
    {
        /// Returns the directory path if it exists, otherwise returns null.
        /// Used for null checking
        public static string DirectoryExists(this string path) => Directory.Exists(path) ? path : null;
        /// <inheritdoc cref="Directory.CreateDirectory(string)" />
        public static DirectoryInfo CreateDirectory(this string path) => Directory.CreateDirectory(path);
    }
}