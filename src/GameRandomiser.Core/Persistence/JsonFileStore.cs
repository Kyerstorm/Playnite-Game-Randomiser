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

        public JsonFileStore(string filePath, DataMigrator migrator = null, Func<DateTime> now = null)
        {
            this.filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            this.migrator = migrator ?? DataMigrator.CreateDefault();
            this.now = now ?? (() => DateTime.Now);
        }

        public string FilePath => filePath;

        public StoreLoadResult Load()
        {
            if (!File.Exists(filePath))
            {
                return new StoreLoadResult(new RandomiserData());
            }

            string text;
            try
            {
                text = File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return new StoreLoadResult(new RandomiserData(), $"Game Randomiser could not read its data file: {e.Message}");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return new StoreLoadResult(new RandomiserData());
            }

            try
            {
                var document = JObject.Parse(text);
                string warning = null;
                var version = DataMigrator.ReadVersion(document);
                if (version > RandomiserData.CurrentSchemaVersion)
                {
                    // Written by a newer extension version: load what we understand, but keep the original safe.
                    var backup = BackupFile("newer-v" + version);
                    warning = $"Game Randomiser data was created by a newer version. A backup was saved to {backup}.";
                }
                else if (migrator.Migrate(document))
                {
                    BackupFile("pre-migration-v" + version);
                }

                var data = document.ToObject<RandomiserData>(JsonSerializer.Create(SerializerSettings));
                data = DataSanitizer.Sanitize(data);
                data.SchemaVersion = RandomiserData.CurrentSchemaVersion;
                return new StoreLoadResult(data, warning);
            }
            catch (Exception e) when (e is JsonException || e is InvalidOperationException || e is ArgumentException || e is FormatException)
            {
                var backup = BackupFile("corrupt");
                return new StoreLoadResult(
                    new RandomiserData(),
                    $"Game Randomiser data was unreadable and has been reset. The old file was saved to {backup ?? "(backup failed)"}.");
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
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-swap so a crash mid-write can never leave a truncated data file.
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, filePath + ".bak", true);
            }
            else
            {
                File.Move(tempPath, filePath);
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
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
