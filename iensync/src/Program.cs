using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.Json;

using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace iensync
{
    static class Program
    {
        public static readonly string[] FORBIDDEN_FILENAMES = new string[]
        {
            "desktop.ini"
        };

        public static void Main(string[] args)
        {
            IenTD.Context.Instance.SetDefaults();
            if (args.Length == 0)
            {
                Console.WriteLine("No settings file provided!");
                return;
            }

            string settingsFilePath = args[0];

            Dictionary<string, SyncEntry> entries = new Dictionary<string, SyncEntry>();
            try
            {
                var parsedSettings = JsonSerializer.Deserialize<Dictionary<string, SyncEntry>>(File.ReadAllText(settingsFilePath));
                if(parsedSettings == null)
                {
                    throw new Exception("Unable to parse settings JSON");
                }
                entries = parsedSettings;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unable to parse settings file JSON");
                Console.WriteLine(ex);
                return;
            }

            while (true)
            {
                Sync(SelectEntry(entries));
            }
        }

        public static SyncEntry SelectEntry(Dictionary<string, SyncEntry> entries)
        {
            return entries.ElementAt(
                new IenTD.SelectionDialog("Select an option", entries.Keys).ShowDialog()
            ).Value;
        }

        public static void Sync(SyncEntry entry)
        {          
            if(entry.SyncToRemote)
            {
                SyncToRemote(entry);
            }
            if(entry.SyncToLocal)
            {
                SyncToLocal(entry);
            }
        }

        private static SftpClient? GetConnectedSftpClient(SyncEntry entry)
        {
            SftpClient client = new SftpClient(entry.Host, entry.Port, entry.Username, entry.Password);
            try
            {
                client.Connect();
                return client;
            }
            catch
            {
                using (var _ = IenTD.Utils.SetTemporaryColors(ConsoleColor.DarkRed, ConsoleColor.White))
                {
                    new IenTD.MessageDialog("Error", "Connection failed!").ShowDialog();
                }
                return null;
            }
        }

        private static HashSet<string> GetLocalFiles(string dir)
        {
            try
            {
                return Directory.EnumerateFiles(dir, "", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileName(f))
                    .Where(f => !FORBIDDEN_FILENAMES.Contains(f))
                    .ToHashSet();
            } 
            catch
            {
                return new HashSet<string>();
            }
        }

        private static HashSet<string> GetRemoteFiles(SftpClient client, string dir)
        {
            try
            {
                return client.ListDirectory(dir)
                    .Where(f => f.IsRegularFile && !FORBIDDEN_FILENAMES.Contains(f.Name))
                    .Select(f => f.Name)
                    .ToHashSet();
            }
            catch
            {
                return new HashSet<string>();
            }
        }

        private static List<string> GetDiff(SftpClient client, SyncEntry entry, IEnumerable<string> srcFiles, IEnumerable<string> dstFiles, bool toRemote)
        {
            IenTD.ProgressDialog progressDialog = new IenTD.ProgressDialog(
                IenTD.ProgressMode.Finite,
                "",
                "Calculating upload...",
                srcFiles.Count(),
                0
            );
            progressDialog.ShowDialog();

            object processLock = new object();

            int processed = 0;
            List<string> result = new List<string>();
            Parallel.ForEach(srcFiles, (f) =>
            {
                if (!dstFiles.Contains(f))
                {
                    result.Add(f);
                }
                else
                {
                    DateTime remoteDate = client.GetLastWriteTimeUtc(entry.RemoteDir + "/" + f);
                    DateTime localDate = File.GetLastWriteTimeUtc(entry.LocalDir + "/" + f);

                    if (toRemote)
                    {
                        if (remoteDate < localDate)
                        {
                            result.Add(f);
                        }
                    }
                    else
                    {
                        if (localDate < remoteDate)
                        {
                            result.Add(f);
                        }
                    }
                }
                lock (processLock)
                {
                    progressDialog.SetProgress(processed++, false);
                }
            });
            return result;
        }

        private static string GetFormattedFileSizeLocal(string path)
        {
            var sizeBytes = new FileInfo(path).Length;
            float sizeMB = (float)sizeBytes / (1024 * 1024);
            return string.Format("{1}MB", sizeMB.ToString("0.00"));
        }

        private static string GetFormattedFileSizeRemote(SftpClient client, string path)
        {
            var sizeBytes = client.Get(path).Length;
            float sizeMB = (float)sizeBytes / (1024 * 1024);
            return string.Format("{0}MB", sizeMB.ToString("0.00"));
        }

        public static void SyncToRemote(SyncEntry entry)
        {
            IenTD.ProgressDialog progressDialog = new IenTD.ProgressDialog(IenTD.ProgressMode.Undefined, "", "", 0, 0);
            progressDialog.ShowDialog();

            progressDialog.SetMessage(string.Format("Connecting to {0} on port {1}", entry.Host, entry.Port));
            SftpClient? client = GetConnectedSftpClient(entry);
            if(client == null)
            {
                return;
            }

            progressDialog.SetMessage("Ensuring remote directory exists...");
            if(!client.Exists(entry.RemoteDir))
            {
                client.CreateDirectory(entry.RemoteDir);
            }

            progressDialog.SetMessage("Fetching remote files...");
            HashSet<string> remoteFiles = GetRemoteFiles(client, entry.RemoteDir);

            progressDialog.SetMessage("Fetching local files...");
            HashSet<string> localFiles = GetLocalFiles(entry.LocalDir);

            List<string> diff = GetDiff(client, entry, localFiles, remoteFiles, true);

            diff.Sort((a, b) =>
            {
                var timeA = File.GetLastWriteTime(entry.LocalDir + "/" + a);
                var timeB = File.GetLastWriteTime(entry.LocalDir + "/" + b);
                return timeA.CompareTo(timeB);
            });

            progressDialog.SetMessage("Syncing files");
            progressDialog.SetMaxProgress(diff.Count);
            progressDialog.SetProgress(0);
            int processed = 0;
            foreach (var f in diff)
            {
                string localPath = entry.LocalDir + "/" + f;
                string remotePath = entry.RemoteDir + "/" + f;                

                progressDialog.SetProgress(++processed, false);
                progressDialog.SetMessage(string.Format("Uploading {0}\n[{1}]", f, GetFormattedFileSizeLocal(localPath)));

                client.UploadFile(File.Open(localPath, FileMode.Open), remotePath);                
            }
            progressDialog.SetProgress(diff.Count);
            
            using (var _ = IenTD.Utils.SetTemporaryColors(ConsoleColor.DarkGreen, ConsoleColor.White))
            {
                new IenTD.MessageDialog("Finished!", string.Format("Uploaded {0} files", diff.Count)).ShowDialog();
            }

            return;
        }

        public static void SyncToLocal(SyncEntry entry)
        {
            IenTD.ProgressDialog progressDialog = new IenTD.ProgressDialog(IenTD.ProgressMode.Undefined, "", "", 0, 0);
            progressDialog.ShowDialog();

            progressDialog.SetMessage(string.Format("Connecting to {0} on port {1}", entry.Host, entry.Port));
            SftpClient? client = GetConnectedSftpClient(entry);
            if (client == null)
            {
                return;
            }

            progressDialog.SetMessage("Checking remote directory...");
            if (!client.Exists(entry.RemoteDir))
            {
                using(var _ = IenTD.Utils.SetTemporaryColors(ConsoleColor.DarkRed, ConsoleColor.White))
                {
                    new IenTD.MessageDialog("Error!", "Remote directory does not exist").ShowDialog();
                }
                return;
            }

            progressDialog.SetMessage("Ensuring local directory exists...");
            if(!Directory.Exists(entry.LocalDir))
            {
                Directory.CreateDirectory(entry.LocalDir);
            }

            progressDialog.SetMessage("Fetching remote files...");
            HashSet<string> remoteFiles = GetRemoteFiles(client, entry.RemoteDir);

            progressDialog.SetMessage("Fetching local files...");
            HashSet<string> localFiles = GetLocalFiles(entry.LocalDir);

            List<string> diff = GetDiff(client, entry, remoteFiles, localFiles, false);

            progressDialog.SetMessage("Sorting download...");
            progressDialog.SetMode(IenTD.ProgressMode.Undefined);
            diff.Sort((a, b) =>
            {
                var timeA = client.GetLastWriteTime(entry.RemoteDir + "/" + a);
                var timeB = client.GetLastWriteTime(entry.RemoteDir + "/" + b);
                return timeA.CompareTo(timeB);
            });

            progressDialog.SetMode(IenTD.ProgressMode.Finite);
            progressDialog.SetTitle("Downloading");
            progressDialog.SetMessage("Syncing files");
            progressDialog.SetMaxProgress(diff.Count);
            progressDialog.SetProgress(0);
            int processed = 0;
            foreach (var f in diff)
            {
                string localPath = entry.LocalDir + "/" + f;
                string remotePath = entry.RemoteDir + "/" + f;

                progressDialog.SetProgress(++processed, false);
                progressDialog.SetMessage(string.Format("{0}\n[{1}]", f, GetFormattedFileSizeRemote(client, remotePath)));

                client.DownloadFile(remotePath, File.Open(localPath, FileMode.CreateNew));
            }
            progressDialog.SetProgress(diff.Count);

            using (var _ = IenTD.Utils.SetTemporaryColors(ConsoleColor.DarkGreen, ConsoleColor.White))
            {
                new IenTD.MessageDialog("Finished!", string.Format("Downloaded {0} files", diff.Count)).ShowDialog();
            }

            return;
        }
    }
}