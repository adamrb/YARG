using System;
using System.IO;
using System.Linq;
using YARG.Core.Logging;
using YARG.Helpers;

namespace YARG.Recommendations
{
    /// <summary>
    /// Test builds keep their data in a separate "dev" folder so they cannot damage a player's real install.
    /// For trying the recommender on real history, the first launch copies the release (or nightly) data
    /// folder into the test build's folder. The original is only read, never changed.
    /// </summary>
    public static class ExistingDataImporter
    {
        private const string MARKER_FILE = ".imported-existing-data";

        private static readonly string[] SkippedDirectories = { "logs", "dev", "release", "nightly" };

        public static void ImportIfNeeded()
        {
#if YARG_TEST_BUILD && !UNITY_EDITOR
            try
            {
                string target = PathHelper.PersistentDataPath;
                string marker = Path.Combine(target, MARKER_FILE);
                if (File.Exists(marker))
                {
                    return;
                }

                string root = PathHelper.RealPersistentDataPath;
                string source = new[] { "release", "nightly" }
                    .Select(name => Path.Combine(root, name))
                    .Where(dir => File.Exists(Path.Combine(dir, "scores", "scores.db")))
                    .OrderByDescending(dir => File.GetLastWriteTimeUtc(Path.Combine(dir, "scores", "scores.db")))
                    .FirstOrDefault();

                Directory.CreateDirectory(target);
                if (source == null)
                {
                    File.WriteAllText(marker, "No existing YARG data found.\n");
                    return;
                }

                int copied = CopyDirectory(source, target);
                File.WriteAllText(marker, $"Copied {copied} files from {source} on {DateTime.Now:u}\n");
                YargLogger.LogFormatInfo<int, string>("Imported {0} files of existing YARG data from {1}", copied, source);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to import existing YARG data.");
            }
#endif
        }

        private static int CopyDirectory(string source, string target)
        {
            int copied = 0;
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(source))
            {
                string destination = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(destination))
                {
                    File.Copy(file, destination);
                    copied++;
                }
            }

            foreach (string directory in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(directory);
                if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                copied += CopyDirectory(directory, Path.Combine(target, name));
            }

            return copied;
        }
    }
}
