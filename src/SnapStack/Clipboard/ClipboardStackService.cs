using System.Net;
using System.Text;
using SnapStack.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SnapStack.Clipboard;

public sealed class ClipboardStackService
{
    private const string ClipboardRootFolderName = "SnapStackClipboard";

    internal async Task<PreparedClipboardStack> PrepareAsync(
        IReadOnlyList<CapturedImage> captures)
    {
        if (captures.Count == 0)
        {
            throw new ArgumentException(
                "At least one captured image is required.",
                nameof(captures));
        }

        var orderedCaptures = captures
            .OrderBy(item => item.Sequence)
            .ToArray();

        var clipboardRoot = await ApplicationData.Current.TemporaryFolder
            .CreateFolderAsync(
                ClipboardRootFolderName,
                CreationCollisionOption.OpenIfExists);

        var publishFolder = await clipboardRoot.CreateFolderAsync(
            Guid.NewGuid().ToString("N"),
            CreationCollisionOption.FailIfExists);

        try
        {
            var files = await WriteCaptureFilesAsync(
                publishFolder,
                orderedCaptures);

            return new PreparedClipboardStack(
                BuildDataPackage(orderedCaptures, files),
                clipboardRoot,
                publishFolder);
        }
        catch
        {
            await publishFolder.DeleteAsync(StorageDeleteOption.PermanentDelete);
            throw;
        }
    }

    // Clipboard APIs are invoked on the app's UI thread. Disk I/O and the
    // potentially large RTF/HTML payload have already been prepared off it.
    internal static void PublishPrepared(PreparedClipboardStack prepared)
    {
        var options = new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false
        };

        if (!Windows.ApplicationModel.DataTransfer.Clipboard.SetContentWithOptions(
                prepared.Package,
                options))
        {
            throw new InvalidOperationException(
                "Windows could not set the SnapStack clipboard content.");
        }

        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
    }

    internal static Task CleanupAsync(PreparedClipboardStack prepared) =>
        RemoveOlderPublishFoldersAsync(prepared.Root, prepared.Folder.Name);

    internal static async Task DiscardAsync(PreparedClipboardStack prepared)
    {
        try
        {
            await prepared.Folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch
        {
            // Cleanup is best effort after a failed clipboard publication.
        }
    }

    private static async Task<List<StorageFile>> WriteCaptureFilesAsync(
        StorageFolder folder,
        IReadOnlyList<CapturedImage> captures)
    {
        var files = new List<StorageFile>(captures.Count);

        foreach (var capture in captures)
        {
            var file = await folder.CreateFileAsync(
                $"capture-{capture.Sequence:D4}.png",
                CreationCollisionOption.ReplaceExisting);

            await FileIO.WriteBytesAsync(
                file,
                capture.PngBytes.ToArray());

            files.Add(file);
        }

        return files;
    }

    private static DataPackage BuildDataPackage(
        IReadOnlyList<CapturedImage> captures,
        IReadOnlyList<StorageFile> files)
    {
        var dataPackage = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };

        dataPackage.Properties.Title = "SnapStack capture stack";
        dataPackage.Properties.Description =
            $"{captures.Count} screenshot{(captures.Count == 1 ? string.Empty : "s")}";

        dataPackage.SetRtf(RtfImageStackFormatter.Format(captures));
        dataPackage.SetStorageItems(files, readOnly: true);
        dataPackage.SetBitmap(
            RandomAccessStreamReference.CreateFromFile(files[0]));

        var html = new StringBuilder("<div>");

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var capture = captures[index];
            var resourceUri = new Uri(file.Path).AbsoluteUri;

            html
                .Append("<img src=\"")
                .Append(WebUtility.HtmlEncode(resourceUri))
                .Append("\" width=\"")
                .Append(capture.PixelWidth)
                .Append("\" height=\"")
                .Append(capture.PixelHeight)
                .Append("\" style=\"display:block;max-width:100%;height:auto\" />");

            dataPackage.ResourceMap[resourceUri] =
                RandomAccessStreamReference.CreateFromFile(file);
        }

        html.Append("</div>");

        dataPackage.SetHtmlFormat(
            HtmlFormatHelper.CreateHtmlFormat(html.ToString()));

        return dataPackage;
    }

    private static async Task RemoveOlderPublishFoldersAsync(
        StorageFolder clipboardRoot,
        string currentFolderName)
    {
        var folders = await clipboardRoot.GetFoldersAsync();

        foreach (var folder in folders)
        {
            if (!string.Equals(
                    folder.Name,
                    currentFolderName,
                    StringComparison.Ordinal))
            {
                try
                {
                    await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
                catch
                {
                    // Temporary cleanup is best effort and must never invalidate
                    // an otherwise successful clipboard publication.
                }
            }
        }
    }
}

internal sealed record PreparedClipboardStack(
    DataPackage Package,
    StorageFolder Root,
    StorageFolder Folder);
