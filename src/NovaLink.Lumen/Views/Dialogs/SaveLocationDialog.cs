using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>Uses the Windows shell dialogs so every download asks for a real save location.</summary>
public static class SaveLocationDialog
{
    private const uint FosOverwritePrompt = 0x2;
    private const uint FosPathMustExist = 0x8;
    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint SigdnFileSysPath = 0x80058000;

    /// <summary>Native folder picker, used by the detail page's "更改" button.</summary>
    public static Task<string?> PickFolderAsync(string? initialPath = null)
    {
        if (MainWindow.Current is null) return Task.FromResult<string?>(null);
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
            return Task.FromResult(ShowFolderDialog(hwnd, initialPath));
        }
        catch
        {
            return PickFolderFallbackAsync();
        }
    }

    /// <summary>Native Save As dialog: suggested folder and file name come from the selected instance.</summary>
    public static Task<string?> PickSaveFileAsync(string initialFolder, string suggestedName)
    {
        if (MainWindow.Current is null) return Task.FromResult<string?>(null);
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
            return Task.FromResult(ShowSaveDialog(hwnd, initialFolder, suggestedName));
        }
        catch
        {
            return PickSaveFileFallbackAsync(suggestedName);
        }
    }

    private static string? ShowFolderDialog(IntPtr hwnd, string? initialPath)
    {
        IFileOpenDialog? dialog = null;
        IShellItem? initial = null;
        IShellItem? result = null;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialogRCW();
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist);
            dialog.SetTitle("选择保存位置");
            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                initial = CreateShellItem(initialPath);
                dialog.SetFolder(initial);
            }
            if (dialog.Show(hwnd) != 0) return null;
            dialog.GetResult(out result);
            result.GetDisplayName(SigdnFileSysPath, out var path);
            return path;
        }
        finally
        {
            if (result is not null) Marshal.ReleaseComObject(result);
            if (initial is not null) Marshal.ReleaseComObject(initial);
            if (dialog is not null) Marshal.ReleaseComObject(dialog);
        }
    }

    private static string? ShowSaveDialog(IntPtr hwnd, string initialFolder, string suggestedName)
    {
        IFileSaveDialog? dialog = null;
        IShellItem? initial = null;
        IShellItem? result = null;
        try
        {
            dialog = (IFileSaveDialog)new FileSaveDialogRCW();
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosOverwritePrompt | FosForceFileSystem | FosPathMustExist);
            if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
            {
                initial = CreateShellItem(initialFolder);
                dialog.SetFolder(initial);
            }
            var name = Regex.Replace(Path.GetFileName(suggestedName), "[\\/:*?\"<>|]", "_");
            if (!string.IsNullOrWhiteSpace(name))
            {
                dialog.SetFileName(name);
                var extension = Path.GetExtension(name).TrimStart('.');
                if (!string.IsNullOrWhiteSpace(extension)) dialog.SetDefaultExtension(extension);
            }
            dialog.SetTitle("选择保存位置");
            dialog.SetOkButtonLabel("保存");
            if (dialog.Show(hwnd) != 0) return null;
            dialog.GetResult(out result);
            result.GetDisplayName(SigdnFileSysPath, out var path);
            return path;
        }
        finally
        {
            if (result is not null) Marshal.ReleaseComObject(result);
            if (initial is not null) Marshal.ReleaseComObject(initial);
            if (dialog is not null) Marshal.ReleaseComObject(dialog);
        }
    }

    private static IShellItem CreateShellItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        return item;
    }

    private static async Task<string?> PickFolderFallbackAsync()
    {
        try
        {
            if (MainWindow.Current is null) return null;
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> PickSaveFileFallbackAsync(string suggestedName)
    {
        try
        {
            if (MainWindow.Current is null) return null;
            var picker = new FileSavePicker
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName)
            };
            var extension = Path.GetExtension(suggestedName);
            picker.FileTypeChoices.Add("文件", new List<string>
            {
                string.IsNullOrWhiteSpace(extension) ? ".jar" : extension
            });
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("84bccd23-5fde-4cdb-aea4-af64b83d78ab")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileSaveDialog : IFileDialog
    {
        void SetSaveAsItem(IShellItem psi);
        void SetProperties(IntPtr pStore);
        void SetCollectedProperties(IntPtr pList, int fAppendDefault);
        void GetProperties(out IntPtr ppStore);
        void ApplyProperties(IShellItem psi, IntPtr pStore, IntPtr hwnd, IntPtr pSink);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOpenDialogRCW
    {
    }

    [ComImport]
    [Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileSaveDialogRCW
    {
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}