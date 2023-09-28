using Ionic.Zip;
using Ionic.Zlib;
using System.Text;
using YamlDotNet.Serialization;

namespace ZipBackup;

public class Program
{
    public static Serializer yamlSerializer = new();
    public static Deserializer yamlDeserializer = new();
    public static ProgressDisplay disp = new(minUpdateIntervalMs: 100);

    public abstract record PathAttr()
    {
        public record Dir() : PathAttr() { }
        public record File(long fileLength, DateTime modTime) : PathAttr() { }
    }

    public record struct APath(string PathName, bool IsDirectory)
    {
        public bool IsFile => !IsDirectory;

        public static APath Dir(string dirPathName) => new APath(dirPathName, IsDirectory: true);
        public static APath File(string filePathName) => new APath(filePathName, IsDirectory: false);

        public override string ToString()
        {
            var typeStr = IsDirectory ? "Dir" : "File";
            return $"{typeStr}({PathName})";
        }
    }

    public record ZipOp
    {
        public record Del(ZipFile zip, ZipEntry entry) : ZipOp();
        public record NOp(string path) : ZipOp();
    }

    public class ConfigItem
    {
        public string RootFrom { get; set; } = "";
        public string RootTo { get; set; } = "";
        public long SplitSize { get; set; } = 0;
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Default;
        public string[] Add { get; set; } = Array.Empty<string>();
        public string[] Ignore { get; set; } = Array.Empty<string>();

        public void Validate(string configFilePath)
        {
            var configDir = Path.GetDirectoryName(configFilePath)!;

            RootFrom = PathUtil.ResolveDir(RootFrom);
            RootTo = PathUtil.ResolveOrCreateDir(RootTo, configDir, true);
            Add = Add.SelectMany(dirPattern => PathUtil.ResolveDirs(dirPattern, RootFrom)).Distinct().ToArray();
            Ignore = Ignore.SelectMany(dirPattern => PathUtil.ResolveDirs(dirPattern, RootFrom)).Distinct().ToArray();
        }
    }

    public static void Main(string[] args)
    {
        // Needed for text encoding to work - do not delete
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (args.Length == 1)
        {
            var configFilePath = args[0];
            RunAll(configFilePath);

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine();
            Console.Write("Done. Press enter to exit. ");
            Console.ReadLine();
        }
        else
        {
            Console.Error.WriteLine($"Usage:");
            Console.Error.WriteLine($"    {Path.GetFileName(Environment.ProcessPath)} <config.yaml>");
        }
    }

    public static void RunAll(string? configFilePath)
    {
        configFilePath = configFilePath ?? throw new ArgumentNullException(nameof(configFilePath));
        var yamlText = File.ReadAllText(configFilePath, Encoding.UTF8);

        var configs = yamlDeserializer.Deserialize<ConfigItem[]>(yamlText);
        foreach (var config in configs)
            Run(config, configFilePath);
    }

    public static void Run(ConfigItem config, string configFilePath)
    {
        disp.WriteLine(CStr.W(new string('═', Math.Max(0, Console.BufferWidth - 1))));
        config.Validate(configFilePath);
        disp.WriteLine(CStr.DS(yamlSerializer.Serialize(config)));

        foreach (string srcBaseDir in config.Add)
        {
            // List files in input currDir
            disp.Write($"{srcBaseDir} ");
            HashSet<string> ignoredDirs = config.Add.Concat(config.Ignore)
                .Where(dir => dir.StartsWith(srcBaseDir) && dir != srcBaseDir)
                .ToHashSet();

            disp.Write($"{srcBaseDir} ", CStr.Y($"0"));
            IEnumerable<string> SrcTraverseRec(string currDir)
            {
                if (ignoredDirs.Contains(currDir))
                    return Enumerable.Empty<string>();

                return Enumerable
                    .Repeat(
                        Path.GetRelativePath(srcBaseDir, currDir),
                        currDir != srcBaseDir ? 1 : 0)
                    .Concat(
                        Directory.EnumerateFiles(currDir)
                        .Select(filePath => Path.GetRelativePath(srcBaseDir, filePath)))
                    .Concat(
                        Directory.EnumerateDirectories(currDir)
                        .SelectMany(subDir => SrcTraverseRec(subDir)));
            }

            var srcPathQuery = SrcTraverseRec(srcBaseDir).AsParallel().Select(path =>
            {
                var fullPath = Path.Combine(srcBaseDir, path);
                if (Directory.Exists(fullPath))
                    return new KeyValuePair<string, PathAttr>(path, new PathAttr.Dir());

                else
                {
                    var fileInfo = new FileInfo(fullPath);
                    return new KeyValuePair<string, PathAttr>(path, new PathAttr.File(fileInfo.Length, fileInfo.LastWriteTimeUtc));
                }
            });

            Dictionary<string, PathAttr> srcPathDict = new();
            foreach (var srcPathPair in srcPathQuery)
            {
                srcPathDict.TryAdd(srcPathPair.Key, srcPathPair.Value);
                if (srcPathPair.Value is not PathAttr.Dir)
                    srcPathDict.TryAdd(Path.GetDirectoryName(srcPathPair.Key)!, new PathAttr.Dir());

                disp.Tick($"{srcBaseDir} ", CStr.Y($"{srcPathDict.Count}"));
            }
            srcPathDict.Remove("");

            var srcPathCount = srcPathDict.Count;
            disp.Write($"{srcBaseDir} ", CStr.Y($"{srcPathCount}"));

            // Traverse changes
            string zipDir = Path.Combine(config.RootTo, Path.GetRelativePath(config.RootFrom, srcBaseDir));
            Directory.CreateDirectory(zipDir);

            string outZipName = $"Contents.{DateTime.Now:yyMM}.zip";
            string outZipPath = Path.Combine(zipDir, outZipName);

            ZipFile[] zips = Directory.EnumerateFiles(zipDir, "Contents.*.zip")
                .Where(path => path != outZipPath)
                .OrderBy(path => path)
                .Append(outZipPath)
                .Select(zipPath =>
                {
                    var zip = File.Exists(zipPath) ?
                        ZipFile.Read(zipPath, new ReadOptions { Encoding = Encoding.UTF8 }) :
                        new ZipFile(zipPath, Encoding.UTF8);
                    zip.CompressionLevel = config.CompressionLevel;
                    zip.MaxOutputSegmentSize64 = config.SplitSize;
                    zip.UseZip64WhenSaving = Zip64Option.AsNecessary;
                    return zip;
                })
                .ToArray();

            var zipsWithEntries = zips.SelectMany(zip => zip.Entries.Select(entry => (zip, entry))).ToList();
            disp.Write($"{srcBaseDir} ", CStr.Y($"{srcPathCount} "), CStr.DY($"{zipsWithEntries.Count}"));

            var zipOps = zipsWithEntries.AsParallel()
                .Select(zipWithEntry =>
                {
                    var (zip, entry) = zipWithEntry;
                    var path = entry.FileName.Replace('/', '\\').TrimEnd('\\');
                    var fullPath = Path.Combine(srcBaseDir, path);

                    if (srcPathDict.TryGetValue(path, out var pathAttr))
                    {
                        if (pathAttr is PathAttr.File fileAttr)
                            if (fileAttr.fileLength != entry.UncompressedSize || fileAttr.modTime != entry.ModifiedTime)    // different version
                                return (ZipOp)new ZipOp.Del(zip, entry);

                        return (ZipOp)new ZipOp.NOp(path);
                    }
                    else    // does not exist on disk
                        return (ZipOp)new ZipOp.Del(zip, entry);
                })
                .ToList();

            int deleteCount = 0;
            HashSet<ZipFile> pendingZips = new();
            foreach (ZipOp zipOp in zipOps)
            {
                switch (zipOp)
                {
                    case ZipOp.Del(var zip, var entry):
                        zip.RemoveEntry(entry);
                        deleteCount++;
                        pendingZips.Add(zip);
                        break;

                    case ZipOp.NOp(var path):
                        srcPathDict.Remove(path);
                        break;
                }
            }

            var outZip = zips[^1];
            if (srcPathDict.Count > 0)
                pendingZips.Add(outZip);

            // Add new files
            foreach (var pathPair in srcPathDict.OrderBy(pathPair => pathPair.Key))
            {
                var (path, pathAttr) = pathPair;
                if (pathAttr is PathAttr.Dir)
                    outZip.AddDirectoryByName(path);
                else
                    outZip.AddFile(Path.Combine(srcBaseDir, path), Path.GetDirectoryName(path));
            }

            disp.WriteLine(
                $"{srcBaseDir} ", CStr.Y($"{srcPathCount} "), CStr.DY($"{zipsWithEntries.Count} "),
                CStr.G($"+{srcPathDict.Count} "), CStr.R($"-{deleteCount}"));

            // Save changes
            foreach (ZipFile zip in pendingZips.OrderBy(zip => (zip.Name != outZipPath, zip.Name)))
            {
                string zipFileName = Path.GetFileName(zip.Name);
                disp.Write("  ", CStr.Y("0"), $"/? >> ", CStr.W($"{zipFileName}"));

                int entriesTotal = 0;
                zip.SaveProgress += (sender, e) =>
                {
                    if (e.EventType == ZipProgressEventType.Saving_BeforeWriteEntry)
                    {
                        entriesTotal = e.EntriesTotal;
                        disp.Tick($"  ", CStr.Y($"{e.EntriesSaved}"), $"/{entriesTotal} ",
                            CStr.DY($"{e.CurrentEntry.FileName} "), ">> ", CStr.W($"{zipFileName}"));
                    }
                };

                // TODO remove empty archive
                zip.SaveSafe();
                disp.WriteLine($"  ", CStr.Y($"{entriesTotal}"), $"/{entriesTotal} >> ", CStr.W($"{zipFileName}"));
            }

            foreach (ZipFile zip in zips)
                zip.Dispose();
        }

        // Look for unlisted directories
        var zipDirs = config.Add
            .Concat(config.Ignore)
            .Select(srcBaseDir => Path.Combine(config.RootTo, Path.GetRelativePath(config.RootFrom, srcBaseDir)))
            .ToList();

        var zipDirsToDel = Directory.EnumerateDirectories(config.RootTo, "*", SearchOption.AllDirectories)
            .Except(zipDirs)
            .OrderByDescending(path => path.Length)
            .ToList();

        disp.WriteLine();
        foreach (var zipDir in zipDirsToDel)
        {
            // Clear files in directory (not recursive)
            foreach (var zipPath in Directory.EnumerateFiles(zipDir))
            {
                disp.WriteLine(CStr.DR("DEL "), CStr.DY(zipPath));
                File.Delete(zipPath);
            }

            // Remove dir if empty
            if (!(Directory.EnumerateDirectories(zipDir).Any() || Directory.EnumerateFiles(zipDir).Any()))
            {
                disp.WriteLine(CStr.DR("DEL "), zipDir);
                Directory.Delete(zipDir);
            }
        }

        // Export validated config
        File.WriteAllText(config.RootTo + ".yaml", yamlSerializer.Serialize(config), Encoding.UTF8);
    }
}