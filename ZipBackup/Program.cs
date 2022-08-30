using Ionic.Zip;
using Ionic.Zlib;
using ShellProgressBar;
using System.CommandLine;
using System.Text;
using YamlDotNet.Serialization;

namespace ZipBackup;

public class Program
{
    public static Serializer yamlSerializer = new();
    public static Deserializer yamlDeserializer = new();

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
            Add = Add.SelectMany(dirPattern => PathUtil.ResolveDirs(dirPattern, RootFrom)).ToArray();
            Ignore = Ignore.SelectMany(dirPattern => PathUtil.ResolveDirs(dirPattern, RootFrom)).ToArray();
        }
    }

    public static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var optionConfigFilePath = new Option<string?>(aliases: new[] { "--config", "-c" })
        {
            Description = "Input YAML config file",
            IsRequired = true,
        };

        var argparse = new RootCommand("A utility program for one-directional sync to zip archives.");
        argparse.Add(optionConfigFilePath);

        argparse.SetHandler(RunAll, optionConfigFilePath);
        argparse.Invoke(args);

        Console.Write("Done. Press enter to exit. ");
        Console.ReadLine();
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
        config.Validate(configFilePath);
        Console.WriteLine(yamlSerializer.Serialize(config));

        foreach (var baseDir in config.Add)
        {
            // List files in input currDir
            Console.WriteLine($@"Listing files in {baseDir} ...");
            HashSet<string> ignoredDirs = config.Add.Concat(config.Ignore)
                .Where(dir => dir.StartsWith(baseDir) && dir != baseDir)
                .ToHashSet();

            HashSet<string> dirsToAdd = new();
            HashSet<string> filesToAdd = new();
            void ListFiles(string currDir)
            {
                if (ignoredDirs.Contains(currDir)) return;

                if (currDir != baseDir)
                    dirsToAdd.Add(Path.GetRelativePath(baseDir, currDir));

                foreach (string filePath in Directory.EnumerateFiles(currDir))
                    filesToAdd.Add(Path.GetRelativePath(baseDir, filePath));

                foreach (string subDir in Directory.EnumerateDirectories(currDir))
                    ListFiles(subDir);
            }
            ListFiles(baseDir);

            // Exclude/Delete existing files in archives
            string zipDir = Path.Combine(config.RootTo, Path.GetRelativePath(config.RootFrom, baseDir));
            Directory.CreateDirectory(zipDir);

            string outZipName = $"Contents.{DateTime.Now:yyMM}.zip";
            string outZipPath = Path.Combine(zipDir, outZipName);

            ZipFile[] zips = Directory.EnumerateFiles(zipDir, "Contents.*.zip")
                .Where(path => path != outZipPath)
                .OrderBy(path => path)
                .Append(outZipPath)
                .Select(zipPath => new ZipFile(zipPath, Encoding.UTF8)
                {
                    CompressionLevel = config.CompressionLevel,
                    MaxOutputSegmentSize64 = config.SplitSize,
                    UseZip64WhenSaving = Zip64Option.AsNecessary,
                })
                .ToArray();

            int filesToDelete = 0;
            int dirsToDelete = 0;
            List<ZipFile> zipsToUpdate = new();

            foreach (ZipFile zip in zips)
            {
                bool zipChanged = false;

                foreach (ZipEntry entry in zip.Entries.ToList())
                {
                    var entryName = entry.FileName.Replace('/', '\\').TrimEnd('\\');
                    var diskPath = Path.Combine(baseDir, entryName);

                    if (entry.IsDirectory)
                    {
                        if (!dirsToAdd.Contains(entryName))     // does not exist on disk
                        {
                            zip.RemoveEntry(entry);
                            dirsToDelete++;
                            zipChanged = true;
                        }
                        else
                            dirsToAdd.Remove(entryName);
                    }
                    else
                    {
                        if (!filesToAdd.Contains(entryName))    // does not exist on disk
                        {
                            zip.RemoveEntry(entry);
                            filesToDelete++;
                            zipChanged = true;
                        }
                        else if (File.GetLastWriteTimeUtc(diskPath) != entry.ModifiedTime ||
                            new FileInfo(diskPath).Length != entry.UncompressedSize)
                        {
                            zip.RemoveEntry(entry);
                            zipChanged = true;
                        }
                        else
                            filesToAdd.Remove(entryName);
                    }
                }

                if (zipChanged)
                    zipsToUpdate.Add(zip);
            }

            // Adding new files
            foreach (string filePath in filesToAdd)
                dirsToAdd.Add(Path.GetDirectoryName(filePath)!);
            dirsToAdd.Remove("");

            var outZip = zips[^1];
            bool outZipChanged = false;
            foreach (string dirPath in dirsToAdd.OrderBy(path => path))
            {
                outZip.AddDirectoryByName(dirPath);
                outZipChanged = true;
            }

            foreach (string filePath in filesToAdd.OrderBy(path => path))
            {
                outZip.AddFile(Path.Combine(baseDir, filePath), Path.GetDirectoryName(filePath));
                outZipChanged = true;
            }

            if (outZipChanged && !zipsToUpdate.Contains(outZip))
                zipsToUpdate.Add(outZip);

            Console.WriteLine($@"  - Files: {filesToAdd.Count} to add/update, {filesToDelete} to delete");
            Console.WriteLine($@"  - Dirs: {dirsToAdd.Count} to add/update, {dirsToDelete} to delete");
            Console.WriteLine($@"  - Archives: {zipsToUpdate.Count} to update");

            // Saving changes
            foreach (ZipFile zip in zipsToUpdate.Reverse<ZipFile>())
            {
                string zipFileName = Path.GetFileName(zip.Name);

                using var zipPb = new ProgressBar(0, $@"[0/0] Starting ...");

                zip.SaveProgress += (sender, e) =>
                {
                    if (e.EventType == ZipProgressEventType.Saving_BeforeWriteEntry)
                    {
                        if (zipPb.MaxTicks != e.EntriesTotal)
                            zipPb.MaxTicks = e.EntriesTotal;

                        zipPb.Tick(e.EntriesSaved, $@"[{e.EntriesSaved}/{e.EntriesTotal}] {e.CurrentEntry.FileName}");
                    }

                    if (e.EventType == ZipProgressEventType.Saving_Completed)
                        zipPb.Tick(e.EntriesSaved, $@"[{e.EntriesSaved}/{e.EntriesTotal}] Done");
                };

                zip.Save();
            }

            foreach (ZipFile zip in zips)
                zip.Dispose();

            Console.WriteLine();
        }
    }
}