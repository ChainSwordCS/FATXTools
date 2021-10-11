﻿using FATX.FileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FATXTools.Tasks
{
    /// <summary>
    /// This class is similar to RecoveryTask and should both eventually be merged into a single class.
    /// It is responsible for saving files that have not been deleted from the file system. Deleted files are
    /// skipped (<see cref="SaveDeleted(string, DirectoryEntry)"/> 
    /// </summary>
    public class SaveContentTask
    {
        private CancellationToken cancellationToken;
        private IProgress<(int, string)> progress;
        private Volume volume;

        private string currentFile = String.Empty;
        private int numSaved = 0;
        private long numFiles;

        public SaveContentTask(Volume volume, CancellationToken cancellationToken, IProgress<(int, string)> progress)
        {
            this.volume = volume;
            this.cancellationToken = cancellationToken;
            this.progress = progress;
        }

        /// <summary>
        /// Save a single file node to the specified path.
        /// </summary>
        /// <param name="path">The path to save the file to.</param>
        /// <param name="node">The file node to save.</param>
        public void Save(string path, DirectoryEntry node)
        {
            numFiles = node.CountFiles();

            Console.WriteLine($"Saving {numFiles} files.");

            SaveNode(path, node);
        }

        /// <summary>
        /// Save a list of file nodes to the specified path.
        /// </summary>
        /// <param name="path">The path to save the file to.</param>
        /// <param name="nodes">The list of files to save.</param>
        public void SaveAll(string path, List<DirectoryEntry> nodes)
        {
            numFiles = volume.CountFiles();

            Console.WriteLine($"Saving {numFiles} files.");

            foreach (var node in nodes)
            {
                SaveNode(path, node);
            }
        }

        /// <summary>
        /// Displays an error dialog window that asks whether or not to retry the IO operation.
        /// </summary>
        /// <param name="e">The exception that occured.</param>
        /// <returns>The user's response as a DialogResult.</returns>
        private DialogResult ShowIOErrorDialog(Exception e)
        {
            return MessageBox.Show($"{e.Message}",
                "Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
        }

        /// <summary>
        /// Writes the file's data blocks to the specified path.
        /// </summary>
        /// <param name="path">The path to save the file to.</param>
        /// <param name="node">The file node to save.</param>
        private void WriteFile(string path, DirectoryEntry node)
        {
            using (FileStream outFile = File.OpenWrite(path))
            {
                uint bytesLeft = node.FileSize;

                var chainMap = this.volume.FileAllocationTable.GetClusterChain(node);

                foreach (uint cluster in chainMap)
                {
                    byte[] clusterData = this.volume.ClusterReader.ReadCluster(cluster);

                    var writeSize = Math.Min(bytesLeft, this.volume.BytesPerCluster);
                    outFile.Write(clusterData, 0, (int)writeSize);

                    bytesLeft -= writeSize;
                }
            }
        }

        /// <summary>
        /// Sets a file's timestamps.
        /// </summary>
        /// <param name="path">The path to the file.</param>
        /// <param name="node">The file node that holds the timestamps.</param>
        private void FileSetTimeStamps(string path, DirectoryEntry node)
        {
            File.SetCreationTime(path, node.CreationTime.AsDateTime());
            File.SetLastWriteTime(path, node.LastWriteTime.AsDateTime());
            File.SetLastAccessTime(path, node.LastAccessTime.AsDateTime());
        }

        /// <summary>
        /// Set's a directory's timestamps. This should be done after all files inside the directory
        /// have been written.
        /// </summary>
        /// <param name="path">The path to the folder.</param>
        /// <param name="node">The file node that holds the timestamps.</param>
        private void DirectorySetTimestamps(string path, DirectoryEntry node)
        {
            Directory.SetCreationTime(path, node.CreationTime.AsDateTime());
            Directory.SetLastWriteTime(path, node.LastWriteTime.AsDateTime());
            Directory.SetLastAccessTime(path, node.LastAccessTime.AsDateTime());
        }

        /// <summary>
        /// Executes an IO operation until it succeeds or until the user decides not to retry.
        /// </summary>
        /// <param name="action">The IO operation to execute.</param>
        private void TryIOOperation(Action action)
        {
            var dialogResult = DialogResult.None;

            while (true)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    dialogResult = ShowIOErrorDialog(e);
                }

                if (dialogResult != DialogResult.Retry)
                    break;
            }
        }

        private void ReportProgress()
        {
            var percent = (int)(((float)numSaved / (float)numFiles) * 100);
            progress.Report((percent, $"{numSaved}/{numFiles}: {currentFile}"));
        }

        /// <summary>
        /// Save the file node to the specified path.
        /// </summary>
        /// <param name="path">The path to save the file to.</param>
        /// <param name="node">The file node to save.</param>
        private void SaveFile(string path, DirectoryEntry node)
        {
            path = path + "\\" + node.FileName;
            Console.WriteLine(path);

            // Report where we are at
            currentFile = node.FileName;
            numSaved++;
            ReportProgress();

            TryIOOperation(() =>
            {
                WriteFile(path, node);

                FileSetTimeStamps(path, node);
            });

            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Save the file node for a directory to the specified path.
        /// </summary>
        /// <param name="path">The path to save the directory into.</param>
        /// <param name="node">The directory's file node.</param>
        private void SaveDirectory(string path, DirectoryEntry node)
        {
            path = path + "\\" + node.FileName;
            Console.WriteLine(path);

            // Report our current progress
            currentFile = node.FileName;
            numSaved++;
            ReportProgress();

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            foreach (DirectoryEntry child in node.Children)
            {
                SaveFile(path, child);
            }

            TryIOOperation(() =>
            {
                DirectorySetTimestamps(path, node);
            });

            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Processes a deleted file. This shouldn't do anything since we don't expect to have information
        /// for this deleted file.
        /// </summary>
        /// <param name="path">The path to save the deleted file.</param>
        /// <param name="node">The file node for this deleted file.</param>
        private void SaveDeleted(string path, DirectoryEntry node)
        {
            path = path + "\\" + node.FileName;

            currentFile = node.GetFullPath();

            Console.WriteLine($"{path}: Cannot save deleted files.");
        }

        /// <summary>
        /// Save a file node. This will save either directory or a file depending on the node's type.
        /// </summary>
        /// <param name="path">The path to save the file node to.</param>
        /// <param name="node">The file node to save.</param>
        private void SaveNode(string path, DirectoryEntry node)
        {
            if (node.IsDeleted())
            {
                SaveDeleted(path, node);
                return;
            }

            if (node.IsDirectory())
            {
                SaveDirectory(path, node);
            }
            else
            {
                SaveFile(path, node);
            }
        }
    }
}