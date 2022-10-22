using Ionic.Zip;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace ZipBackup
{
    public static class PathUtil
    {
        public static IEnumerable<string> ResolveDirs(string dirPattern)
        {
            var parentDirPattern = Path.GetDirectoryName(dirPattern);

            if (parentDirPattern == null)  // drive root
                yield return dirPattern.ToUpper();

            else
            {
                var parentDirs = ResolveDirs(parentDirPattern);
                foreach (var parentDir in parentDirs)
                    foreach (var dir in Directory.EnumerateDirectories(parentDir, Path.GetFileName(dirPattern)))
                        yield return dir;
            }
        }

        public static IEnumerable<string> ResolveDirs(string dirPattern, string? relativePathBase = null)
        {
            try
            {
                if (!Path.IsPathRooted(dirPattern))
                {
                    if (string.IsNullOrEmpty(relativePathBase))
                        dirPattern = Path.GetFullPath(dirPattern);
                    else
                        dirPattern = Path.GetFullPath(dirPattern, relativePathBase);
                }

                return ResolveDirs(dirPattern);
            }
            catch (Exception ex)
            {
                throw new FormatException($@"Error resolving dir {dirPattern}", ex);
            }
        }

        public static string ResolveDir(string dirPath)
        {
            return ResolveDirs(dirPath).FirstOrDefault() ?? throw new DirectoryNotFoundException(dirPath);
        }

        public static string ResolveOrCreateDir(string dirPath, string? relativePathBase = null, bool createOnNeed = false)
        {
            try
            {
                if (!Path.IsPathRooted(dirPath))
                {
                    if (string.IsNullOrEmpty(relativePathBase))
                        dirPath = Path.GetFullPath(dirPath);
                    else
                        dirPath = Path.GetFullPath(dirPath, relativePathBase);
                }

                if (!Directory.Exists(dirPath) && createOnNeed)
                    Directory.CreateDirectory(dirPath);

                return ResolveDir(dirPath);
            }
            catch (Exception ex)
            {
                throw new FormatException($@"Error resolving dir {dirPath}", ex);
            }
        }

        public static void SaveSafe(this ZipFile zip, string? fileName = null)
        {
            var zipPath = fileName ?? zip.Name ?? throw new ArgumentNullException(nameof(fileName), "Save path cannot be null.");

            var zipPathTemp = Path.Combine(Path.GetDirectoryName(zipPath)!, $"__temp.{Path.GetFileName(zipPath)}");
            zip.Save(zipPathTemp);

            var filesToDelete = Directory.GetFiles(Path.GetDirectoryName(zipPath)!, $"{Path.GetFileName(zipPath)[..^2]}??");
            var filesToRename = Directory.GetFiles(Path.GetDirectoryName(zipPath)!, $"{Path.GetFileName(zipPathTemp)[..^2]}??")
                .ToDictionary(file => file, file => Path.Combine(
                    Path.GetDirectoryName(zipPath)!, $"{Path.GetFileNameWithoutExtension(zipPath)}{Path.GetExtension(file)}"));

            foreach (var file in filesToDelete)
                File.Delete(file);

            foreach (var (fileTemp, file) in filesToRename)
                File.Move(fileTemp, file);
        }
    }
}
