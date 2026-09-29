using System;
using System.Collections.Generic;
using System.IO;
using SQLite;
using YARG.Core;
using YARG.Core.Logging;
using YARG.Helpers;

namespace YARG.Recommendations
{
    /// <summary>
    /// A like or pass given to a song outside of gameplay (for example from Song Swipe).
    /// </summary>
    [Table("SongFeedback")]
    public class SongFeedbackRecord
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public Guid ProfileId { get; set; }

        [Indexed]
        public byte[] SongChecksum { get; set; }

        public bool Liked { get; set; }

        public DateTime Date { get; set; }
    }

    /// <summary>
    /// A song that was started but left before the end. Completed songs are already in the score database.
    /// </summary>
    [Table("SongQuits")]
    public class SongQuitRecord
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public Guid ProfileId { get; set; }

        [Indexed]
        public byte[] SongChecksum { get; set; }

        public Instrument Instrument { get; set; }
        public Difficulty Difficulty { get; set; }

        /// <summary>
        /// How far into the song the player got, from 0 to 1.
        /// </summary>
        public float Progress { get; set; }

        public DateTime Date { get; set; }
    }

    /// <summary>
    /// Stores recommendation signals that the score database does not track. Kept in its own database
    /// so the score database schema is left untouched.
    /// </summary>
    public static class RecommendationStore
    {
        private static SQLiteConnection _db;

        public static string DatabasePath { get; private set; }

        public static void Init()
        {
            string directory = Path.Combine(PathHelper.PersistentDataPath, "recommendations");
            DatabasePath = Path.Combine(directory, "recommendations.db");

            try
            {
                Directory.CreateDirectory(directory);
                _db = new SQLiteConnection(DatabasePath);
                _db.CreateTable<SongFeedbackRecord>();
                _db.CreateTable<SongQuitRecord>();
            }
            catch (Exception e)
            {
                _db = null;
                YargLogger.LogException(e, "Failed to open the recommendation database.");
            }
        }

        public static void Destroy()
        {
            _db?.Dispose();
            _db = null;
        }

        /// <summary>
        /// Saves a like or pass and returns its record ID (for undo), or -1 if it could not be saved.
        /// </summary>
        public static int RecordFeedback(Guid profileId, byte[] songChecksum, bool liked)
        {
            if (_db == null) return -1;

            try
            {
                var record = new SongFeedbackRecord
                {
                    ProfileId = profileId,
                    SongChecksum = songChecksum,
                    Liked = liked,
                    Date = DateTime.Now,
                };
                _db.Insert(record);
                return record.Id;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to save song feedback.");
                return -1;
            }
        }

        public static void DeleteFeedback(int id)
        {
            if (_db == null || id < 0) return;

            try
            {
                _db.Delete<SongFeedbackRecord>(id);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to undo song feedback.");
            }
        }

        public static void RecordQuit(Guid profileId, byte[] songChecksum, Instrument instrument,
            Difficulty difficulty, float progress)
        {
            if (_db == null) return;

            try
            {
                _db.Insert(new SongQuitRecord
                {
                    ProfileId = profileId,
                    SongChecksum = songChecksum,
                    Instrument = instrument,
                    Difficulty = difficulty,
                    Progress = progress,
                    Date = DateTime.Now,
                });
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to save song quit.");
            }
        }

        public static List<SongFeedbackRecord> GetFeedback(Guid profileId)
        {
            if (_db == null) return new List<SongFeedbackRecord>();

            try
            {
                return _db.Query<SongFeedbackRecord>(
                    "SELECT * FROM SongFeedback WHERE ProfileId = ? ORDER BY Date", profileId);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to read song feedback.");
                return new List<SongFeedbackRecord>();
            }
        }

        public static List<SongQuitRecord> GetQuits(Guid profileId)
        {
            if (_db == null) return new List<SongQuitRecord>();

            try
            {
                return _db.Query<SongQuitRecord>(
                    "SELECT * FROM SongQuits WHERE ProfileId = ? ORDER BY Date", profileId);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to read song quits.");
                return new List<SongQuitRecord>();
            }
        }
    }
}
