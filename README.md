# LargeFileCleaner

Windows desktop tool for finding and cleaning large files.

## Features

- Select a folder or drive to scan.
- Set a custom large-file threshold in MB or GB.
- Sort results by size, name, modified time, type, or path.
- Filter results by file name, extension, or path.
- Select files and delete them either to the Recycle Bin or permanently.
- Confirm deletion with file count and estimated released space.
- Write deletion history to `%LOCALAPPDATA%\LargeFileCleaner\delete-history.json`.

## Build

```powershell
dotnet build LargeFileCleaner.sln -c Release
```

## Publish Single EXE

```powershell
dotnet publish LargeFileCleaner\LargeFileCleaner.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:DebugType=None /p:DebugSymbols=false
```

The single-file executable is generated at:

```text
LargeFileCleaner\bin\Release\net8.0-windows\win-x64\publish\LargeFileCleaner.exe
```
