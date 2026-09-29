using System;
using System.IO;
using System.Linq;
using YARG.Core.Logging;
using YARG.Helpers;

namespace YARG.Recommendations
{
    /// <summary>
    /// Test builds keep their data in a separate "dev" folder so they cannot damage a player's real install.
    /// For trying the recommender on real history, the first launch copies the small files that hold the
    /// player's setup and history from the release (or nightly) data folder. Songs are never copied (the
    /// settings only point at the song folders), and neither are replays, venues or logs. The original is
    /// only read, never changed.
    /// </summary>
    public static class ExistingDataImporter
    {
        private const string MARKER_FILE = ".imported-existing-data";

        // The song cache only saves a rescan, so skip it if it is unexpectedly large
        private const long MAX_SONG_CACHE_BYTES = 512L * 1024 * 1024;

        private static readonly string[] Files =
        {
            "settings.json",
            "song_offsets.json",
            Path.Combine("scores", "scores.db"),
        };

        // Each of these holds only small JSON files
        private static readonly string[] Directories = { "profiles", "playlists", "custom" };

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

                int copied = 0;
                long bytes = 0;
                void Copy(string relativePath)
                {
                    string from = Path.Combine(source, relativePath);
                    string to = Path.Combine(target, relativePath);
                    if (!File.Exists(from) || File.Exists(to))
                    {
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to);
                    copied++;
                    bytes += new FileInfo(from).Length;
                }

                foreach (string file in Files)
                {
                    Copy(file);
                }

                foreach (string directory in Directories)
                {
                    string from = Path.Combine(source, directory);
                    if (!Directory.Exists(from))
                    {
                        continue;
                    }

                    foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                    {
                        Copy(Path.GetRelativePath(source, file));
                    }
                }

                string songCache = Path.Combine(source, "songcache.bin");
                if (File.Exists(songCache) && new FileInfo(songCache).Length <= MAX_SONG_CACHE_BYTES)
                {
                    Copy("songcache.bin");
                }

                File.WriteAllText(marker, $"Copied {copied} files ({bytes / 1024} KB) from {source} on {DateTime.Now:u}\n");
                YargLogger.LogFormatInfo<int, string>("Imported {0} files of existing YARG data from {1}", copied, source);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to import existing YARG data.");
            }
#endif
        }
    }
}
