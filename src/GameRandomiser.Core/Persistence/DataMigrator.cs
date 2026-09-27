using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Persistence
{
    /// <summary>
    /// Upgrades raw persisted JSON from older schema versions, one version at a time.
    /// Migrations operate on JObject rather than typed models so old shapes never need C# classes.
    /// To change the schema: bump RandomiserData.CurrentSchemaVersion and register a step
    /// from (new - 1) that rewrites the JSON into the new shape.
    /// </summary>
    public sealed class DataMigrator
    {
        public const string VersionProperty = nameof(RandomiserData.SchemaVersion);

        private readonly Dictionary<int, Action<JObject>> steps = new Dictionary<int, Action<JObject>>();
        private readonly int targetVersion;

        public DataMigrator(int targetVersion = RandomiserData.CurrentSchemaVersion)
        {
            this.targetVersion = targetVersion;
        }

        /// <summary>Registers a step that upgrades a document from <paramref name="fromVersion"/> to fromVersion + 1.</summary>
        public DataMigrator AddStep(int fromVersion, Action<JObject> step)
        {
            steps[fromVersion] = step ?? throw new ArgumentNullException(nameof(step));
            return this;
        }

        public static int ReadVersion(JObject document)
        {
            var token = document[VersionProperty];
            // Documents written before versioning existed are treated as version 1.
            return token != null && token.Type == JTokenType.Integer ? token.Value<int>() : 1;
        }

        /// <summary>Migrates in place. Returns true if the document was changed.</summary>
        public bool Migrate(JObject document)
        {
            var version = ReadVersion(document);
            if (version >= targetVersion)
            {
                return false;
            }

            while (version < targetVersion)
            {
                if (!steps.TryGetValue(version, out var step))
                {
                    throw new InvalidOperationException($"No migration registered from schema version {version}.");
                }

                step(document);
                version++;
                document[VersionProperty] = version;
            }

            return true;
        }

        /// <summary>Migrator with all production steps registered.</summary>
        public static DataMigrator CreateDefault()
        {
            // Schema version 1 is the initial release; add future steps here, e.g.
            // .AddStep(1, doc => { /* rename/reshape v1 properties into v2 */ });
            return new DataMigrator();
        }
    }
}
