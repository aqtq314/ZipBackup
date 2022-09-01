﻿using Ionic.Zip;
using Ionic.Zlib;
using System.CommandLine;
using System.Collections.Immutable;
using System.Text;
using YamlDotNet.Serialization;

namespace ZipBackup;

public class Program
{
    public static Serializer yamlSerializer = new();
    public static Deserializer yamlDeserializer = new();
    public static ProgressDisplay disp = new(minUpdateIntervalMs: 100);

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
        public record NOp(APath path) : ZipOp();
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

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
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
        disp.WriteLine(CStr.W(new string('═', Math.Max(0, Console.BufferWidth - 1))));
        config.Validate(configFilePath);
        disp.WriteLine(CStr.DS(yamlSerializer.Serialize(config)));

        foreach (string srcBaseDir in config.Add)
        {
            // List files in input currDir
            disp.Write(CStr.Y($"{srcBaseDir} "), "(comparing)");
            HashSet<string> ignoredDirs = config.Add.Concat(config.Ignore)
                .Where(dir => dir.StartsWith(srcBaseDir) && dir != srcBaseDir)
                .ToHashSet();

            IEnumerable<APath> ListFilesP(string currDir)
            {
                if (ignoredDirs.Contains(currDir))
                    return Enumerable.Empty<APath>();

                return Enumerable
                    .Repeat(
                        APath.Dir(Path.GetRelativePath(srcBaseDir, currDir)),
                        currDir != srcBaseDir ? 1 : 0)
                    .Concat(
                        Directory.EnumerateFiles(currDir)
                        .Select(filePath => APath.File(Path.GetRelativePath(srcBaseDir, filePath))))
                    .Concat(
                        Directory.EnumerateDirectories(currDir)
                        .SelectMany(subDir => ListFilesP(subDir)));
            }

            HashSet<APath> srcPathSet = ListFilesP(srcBaseDir).ToHashSet();
            srcPathSet.UnionWith(
                srcPathSet
                    .Where(path => path.IsFile)
                    .Select(filePath => APath.Dir(Path.GetDirectoryName(filePath.PathName)!))
                    .ToList());
            srcPathSet.Remove(APath.Dir(""));

            //var physicalPathSet = physicalPaths.ToImmutableHashSet();

            //////////////////
            //HashSet<string> dirsToAdd = new();
            //HashSet<string> filesToAdd = new();
            //void ListFiles(string currDir)
            //{
            //    if (ignoredDirs.Contains(currDir)) return;

            //    if (currDir != baseDir)
            //        dirsToAdd.Add(Path.GetRelativePath(baseDir, currDir));

            //    foreach (string filePath in Directory.EnumerateFiles(currDir))  // slow
            //        filesToAdd.Add(Path.GetRelativePath(baseDir, filePath));

            //    foreach (string subDir in Directory.EnumerateDirectories(currDir))
            //        ListFiles(subDir);
            //}
            //ListFiles(baseDir);

            //foreach (string filePath in filesToAdd)
            //    dirsToAdd.Add(Path.GetDirectoryName(filePath)!);
            //dirsToAdd.Remove("");
            ///////////////////

            // Traversing changes
            string zipDir = Path.Combine(config.RootTo, Path.GetRelativePath(config.RootFrom, srcBaseDir));
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

            var zipsWithEntries = zips.SelectMany(zip => zip.Entries.Select(entry => (zip, entry))).ToList();

            var zipOps = zipsWithEntries.AsParallel()
                .Select(zipWithEntry =>
                {
                    var (zip, entry) = zipWithEntry;
                    var pathName = entry.FileName.Replace('/', '\\').TrimEnd('\\');
                    var path = new APath(pathName, IsDirectory: entry.IsDirectory);
                    var diskPath = Path.Combine(srcBaseDir, pathName);

                    if (!srcPathSet.Contains(path))    // does not exist on disk
                        return (ZipOp)new ZipOp.Del(zip, entry);
                    else if (!entry.IsDirectory &&
                        (File.GetLastWriteTimeUtc(diskPath) != entry.ModifiedTime ||
                            new FileInfo(diskPath).Length != entry.UncompressedSize))    // different version
                        return new ZipOp.Del(zip, entry);
                    else
                        return new ZipOp.NOp(path);
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
                        srcPathSet.Remove(path);
                        break;
                }
            }

            var outZip = zips[^1];
            if (srcPathSet.Count > 0)
                pendingZips.Add(outZip);

            ////////////////////////////////////
            //int filesToDelete = 0;
            //int dirsToDelete = 0;
            //List<ZipFile> zipsToUpdate = new();

            //foreach (ZipFile zip in zips)
            //{
            //    bool zipChanged = false;

            //    //var zipEntries = zip.Entries.ToDictionary(entry => entry.FileName.Replace('/', '\\').TrimEnd('\\'));
            //    //var zipEntriesQuery = zipEntries.AsParallel();

            //    foreach (ZipEntry entry in zip.Entries.ToList())
            //    {
            //        var entryName = entry.FileName.Replace('/', '\\').TrimEnd('\\');
            //        var diskPath = Path.Combine(baseDir, entryName);

            //        if (entry.IsDirectory)
            //        {
            //            if (!dirsToAdd.Contains(entryName))     // does not exist on disk
            //            {
            //                zip.RemoveEntry(entry);
            //                dirsToDelete++;
            //                zipChanged = true;
            //            }
            //            else
            //                dirsToAdd.Remove(entryName);
            //        }
            //        else
            //        {
            //            if (!filesToAdd.Contains(entryName))    // does not exist on disk
            //            {
            //                zip.RemoveEntry(entry);
            //                filesToDelete++;
            //                zipChanged = true;
            //            }
            //            else if (File.GetLastWriteTimeUtc(diskPath) != entry.ModifiedTime ||
            //                new FileInfo(diskPath).Length != entry.UncompressedSize)    // too slow
            //            {
            //                zip.RemoveEntry(entry);
            //                zipChanged = true;
            //            }
            //            else
            //                filesToAdd.Remove(entryName);
            //        }
            //    }

            //    if (zipChanged)
            //        zipsToUpdate.Add(zip);
            //}
            ////////////////////////////////////

            // Adding new files
            foreach (var path in srcPathSet.OrderBy(path => path.PathName))
            {
                if (path.IsDirectory)
                    outZip.AddDirectoryByName(path.PathName);
                else
                    outZip.AddFile(Path.Combine(srcBaseDir, path.PathName), Path.GetDirectoryName(path.PathName));
            }

            disp.WriteLine(CStr.Y($"{srcBaseDir} "), CStr.G($"+{srcPathSet.Count} "), CStr.R($"-{deleteCount}"));

            //////////////////////////////////////
            //foreach (string dirPath in dirsToAdd.OrderBy(path => path))
            //{
            //    outZip.AddDirectoryByName(dirPath);
            //    outZipChanged = true;
            //}

            //foreach (string filePath in filesToAdd.OrderBy(path => path))
            //{
            //    outZip.AddFile(Path.Combine(baseDir, filePath), Path.GetDirectoryName(filePath));
            //    outZipChanged = true;
            //}

            //if (outZipChanged && !zipsToUpdate.Contains(outZip))
            //    zipsToUpdate.Add(outZip);

            //Console.WriteLine($@"  - Files: {filesToAdd.Count} to add/update, {filesToDelete} to delete");
            //Console.WriteLine($@"  - Dirs: {dirsToAdd.Count} to add/update, {dirsToDelete} to delete");
            //Console.WriteLine($@"  - Archives: {zipsToUpdate.Count} to update");
            //////////////////////////////////////

            // Saving changes
            foreach (ZipFile zip in pendingZips.OrderBy(zip => (zip.Name != outZipPath, zip.Name)))
            {
                string zipFileName = Path.GetFileName(zip.Name);
                disp.Write("  ", CStr.Y("0"), $"/? >> ", CStr.W($"{zipFileName}"));

                int entriesTotal = 0;
                zip.SaveProgress += (sender, e) =>
                {
                    if (e.EventType == ZipProgressEventType.Saving_AfterWriteEntry)
                    {
                        entriesTotal = e.EntriesTotal;
                        disp.Tick($"  ", CStr.Y($"{e.EntriesSaved}"), $"/{entriesTotal} ",
                            CStr.DY($"{e.CurrentEntry.FileName} "), ">> ", CStr.W($"{zipFileName}"));
                    }
                };

                zip.Save();
                disp.WriteLine($"  ", CStr.Y($"{entriesTotal}"), $"/{entriesTotal} >> ", CStr.W($"{zipFileName}"));
            }

            //////////////////////////////////////
            //foreach (ZipFile zip in zipsToUpdate.Reverse<ZipFile>())
            //{
            //    string zipFileName = Path.GetFileName(zip.Name);

            //    using var zipPb = new ProgressBar(0, $@"[0/0] Starting ...");

            //    zip.SaveProgress += (sender, e) =>
            //    {
            //        if (e.EventType == ZipProgressEventType.Saving_BeforeWriteEntry)
            //            if (zipPb.MaxTicks != e.EntriesTotal)
            //                zipPb.MaxTicks = e.EntriesTotal;

            //        if (e.EventType == ZipProgressEventType.Saving_AfterWriteEntry)
            //            zipPb.Tick(e.EntriesSaved, $@"[{e.EntriesSaved}/{e.EntriesTotal}] {e.CurrentEntry.FileName}");
            //    };

            //    zip.Save();
            //}
            //////////////////////////////////////

            foreach (ZipFile zip in zips)
                zip.Dispose();
        }
    }
}