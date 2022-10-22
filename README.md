# ZipBakcup

A utility program for one-directional sync to zip archives. Requires .NET 6 on Windows to run.

I created this program for my own data backup needs (photos, docs, etc.). Backed-up data are zipped and optionally split into multiple file segments for easier transfer afterwards (e.g. upload to an online drive).

Specifically, say we're trying to backup everything in directory `D:\Photos` to `Z:\Backup\Current\D\Photos`. This program will create a series of file named `Z:\Backup\Current\D\Photos\Contents.####.zip` where `####` is a token automatically generated based on current month, meaning a new zip archive will be created once we step into another month/year. This is to prevent rewriting the entire zip archive during periodic backup, especially when the directory contains a lot of ancient files while still being constantly added with new ones. The archive files are designed to recover the same directory structure when unzipped altogether. Old archive files may still be modified in case where files need to be deleted.

This program is NOT thoroughly tested. Use at your own risk.

## Usage

```
ZipBackup <config.yaml>
```

## Config File Spec

```yaml
- RootFrom: C:\
  RootTo: Current\C         # Equivalent to "Z:\Backup\Current\C" if this config
                            #   file locates in "Z:\Backup"
  SplitSize: 4290772992     # Split output zip archive into files of ≤ 3.99 GB
  CompressionLevel: Level3  # As defined in enum Ionic.Zlib.CompressionLevel

  Add:                      # Only dirs in "C:\" listed below will be backed-up
  - C:\Users\User\Desktop   # Create backup files to
                            #   "Current\C\Users\User\Desktop\Contents.####.zip"
  - C:\Users\User\Documents\TencentMeeting
  - C:\Users\User\Documents # Ignores contents in "TencentMeeting" as those are
                            #   already handled separately in the previous item
  - C:\Users\User\Downloads

  Ignore:
  - C:\Users\User\Documents\Cache

- RootFrom: D:\             # Another config item with slightly different settings
  RootTo: Current\D
  SplitSize: 4290772992
  CompressionLevel: Level6

  Add:
  - D:\Git
  - D:\Git\*                # This will backup every folder separately
  - D:\Games
  - D:\Games\*

  Ignore: []

```


