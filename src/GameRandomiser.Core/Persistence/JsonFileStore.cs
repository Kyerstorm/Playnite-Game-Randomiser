using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Persistence
{
    /// <summary>
    /// Stores <see cref="RandomiserData"/> as a single JSON file with atomic writes,
    /// schema migration and corrupt-file recovery. Never throws from Load.
    /// A save either fully replaces the file or leaves it untouched, and a file that could not be
    /// understood is always copied aside before anything is written over it.
    /// </summary>
    public sealed class JsonFileStore : IRandomiserStore
    {
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            Formatting = Formatting.Indented,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Include
        };

        private readonly string filePath;
        private readonly DataMigrator migrator;
        private readonly Func<DateTime> now;
        private string lastWrittenJson;
        private string pendingBackupReason;
        private bool migratedOnLoad;

        public JsonFileStore(string filePath, DataMigrator migrator = null, Func<DateTime> now = null)
        {
            this.filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            this.migrator = migrator ?? DataMigrator.CreateDefault();
            this.now = now ?? (() => DateTime.Now);
        }

        public string FilePath => filePath;

        /// <summary>The previous successfully saved document, kept by every atomic replace.</summary>
        public string BackupPath => filePath + ".bak";

        /// <summary>Number of times the file was actually written (unchanged saves are skipped).</summary>
        public int WriteCount { get; private set; }

        public StoreLoadResult Load()
        {
            lastWrittenJson = null;
            pendingBackupReason = null;
            if (!File.Exists(filePath))
            {
                return new StoreLoadResult(new RandomiserData());
            }

            string text;
            try
            {
                text = File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The file exists but can't be read right now. It may be perfectly valid, so the next
                // save must copy it aside before replacing it.
                pendingBackupReason = "unreadable";
                return new StoreLoadResult(new RandomiserData(),
                    $"Game Randomiser could not read its data file ({e.Message}). Your saved wheels have not been changed.");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return new StoreLoadResult(new RandomiserData());
            }

            try
            {
                migratedOnLoad = false;
                var result = Parse(text, true);
                if (migratedOnLoad)
                {
                    PersistMigration(result.Data);
                }

                return result;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                var reason = e is MigrationFailedException ? "migration-failed" : "corrupt";
                var backup = BackupFile(reason);
                if (backup == null)
                {
                    pendingBackupReason = reason;
                }

                var where = backup ?? "(backup failed; the file will be copied before it is replaced)";
                var recovered = TryRecoverFromBackup();
                if (recovered != null)
                {
                    return new StoreLoadResult(recovered,
                        $"Game Randomiser data was unreadable, so your wheels were restored from the last automatic backup. The damaged file was saved to {where}.");
                }

                return new StoreLoadResult(
                    new RandomiserData(),
                    e is MigrationFailedException
                        ? $"Game Randomiser data could not be upgraded and has been reset. The original file was saved to {where}."
                        : $"Game Randomiser data was unreadable and has been reset. The old file was saved to {where}.");
            }
        }

        public void Save(RandomiserData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            data.SchemaVersion = RandomiserData.CurrentSchemaVersion;
            var json = JsonConvert.SerializeObject(data, SerializerSettings);
            if (json == lastWrittenJson && File.Exists(filePath))
            {
                // Nothing changed since the last write: don't touch the disk or rotate the backup.
                return;
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (pendingBackupReason != null && File.Exists(filePath))
            {
                if (BackupFile(pendingBackupReason) == null)
                {
                    throw new IOException("The existing data file could not be backed up, so it was not replaced.");
                }

                pendingBackupReason = null;
            }

            // Write-then-swap so a crash mid-write can never leave a truncated data file.
            var tempPath = filePath + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, BackupPath, true);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            lastWrittenJson = json;
            WriteCount++;
        }

        private StoreLoadResult Parse(string text, bool isPrimaryFile)
        {
            var document = JObject.Parse(text);
            string warning = null;
            var version = DataMigrator.ReadVersion(document);
            if (version > RandomiserData.CurrentSchemaVersion)
            {
                // Written by a newer extension version: load what we understand, but keep the original safe.
                var backup = isPrimaryFile ? BackupFile("newer-v" + version) : null;
                if (isPrimaryFile && backup == null)
                {
                    pendingBackupReason = "newer-v" + version;
                }

                warning = $"Game Randomiser data was created by a newer version. A backup was saved to {backup ?? "(pending)"}.";
            }
            else
            {
                // Migrate a copy so a step that fails half-way can never leave a partly upgraded document.
                var migrated = (JObject)document.DeepClone();
                bool changed;
                try
                {
                    changed = migrator.Migrate(migrated);
                }
                catch (Exception e)
                {
                    throw new MigrationFailedException(version, e);
                }

                if (changed)
                {
                    if (isPrimaryFile && BackupFile("pre-migration-v" + version) == null)
                    {
                        pendingBackupReason = "pre-migration-v" + version;
                    }

                    document = migrated;
                    migratedOnLoad = isPrimaryFile;
                }
            }

            var data = document.ToObject<RandomiserData>(JsonSerializer.Create(SerializerSettings));
            data = DataSanitizer.Sanitize(data);
            data.SchemaVersion = RandomiserData.CurrentSchemaVersion;
            return new StoreLoadResult(data, warning);
        }

        /// <summary>
        /// Writes a successfully migrated document back once, so the upgrade (and its backup) happens a
        /// single time. If the write fails the old file is untouched and the upgrade is retried next load.
        /// </summary>
        private void PersistMigration(RandomiserData data)
        {
            try
            {
                Save(data);
            }
            catch (Exception e) when (IsFileError(e))
            {
            }
        }

        private RandomiserData TryRecoverFromBackup()
        {
            try
            {
                if (!File.Exists(BackupPath))
                {
                    return null;
                }

                var text = File.ReadAllText(BackupPath, Encoding.UTF8);
                return string.IsNullOrWhiteSpace(text) ? null : Parse(text, false).Data;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return null;
            }
        }

        private string BackupFile(string reason)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
                var name = Path.GetFileNameWithoutExtension(filePath);
                var backupPath = Path.Combine(directory, $"{name}.{reason}-{now():yyyyMMdd-HHmmss}.json");
                File.Copy(filePath, backupPath, true);
                return backupPath;
            }
            catch (Exception e) when (IsFileError(e))
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // A stray temp file is harmless: it is overwritten by the next save and never loaded.
            }
        }

        private static bool IsFileError(Exception e) =>
            e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException;

        private sealed class MigrationFailedException : Exception
        {
            public MigrationFailedException(int fromVersion, Exception inner)
                : base($"Migration from schema version {fromVersion} failed.", inner)
            {
            }
        }
    }
}
