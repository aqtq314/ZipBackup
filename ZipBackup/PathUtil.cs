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
        public static string GetPhysicalDirPath(string dirPath)
        {
            var dirInfo = new DirectoryInfo(dirPath);

            if (dirInfo.Parent != null)
            {
                var outDirInfo = dirInfo.Parent.EnumerateDirectories(dirInfo.Name).FirstOrDefault() ??
                    throw new DirectoryNotFoundException(dirPath);

                return Path.Combine(GetPhysicalDirPath(dirInfo.Parent.FullName), outDirInfo.Name);
            }
            else
            {
                return dirInfo.Name.ToUpper();
            }
        }

        public static string GetValidatedDirPath(string dirPath, string? relativePathBase = null, bool createOnNeed = false)
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

                return GetPhysicalDirPath(dirPath);
            }
            catch (Exception ex)
            {
                throw new FormatException($@"Error resolving dir {dirPath}", ex);
            }
        }
    }
}
