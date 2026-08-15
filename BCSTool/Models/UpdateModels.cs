using System;

namespace BCSTool.Models;

public enum UpdateCheckState
{
    NotChecked,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    Installing,
    Failed
}

public sealed record UpdateRelease(
    Version Version,
    string TagName,
    Uri ReleasePageUri,
    string ExecutableFileName,
    Uri ExecutableDownloadUri,
    Uri ChecksumDownloadUri);
