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
            return new DataMigrator().AddStep(1, UpgradeV1ToV2);
        }

        /// <summary>
        /// v2 introduces membership policies. v1 reserved an always-false IsDynamic flag, so every
        /// existing wheel becomes a manual snapshot and keeps behaving exactly as it did.
        /// </summary>
        private static void UpgradeV1ToV2(JObject document)
        {
            if (!(document[nameof(RandomiserData.Wheels)] is JArray wheels))
            {
                return;
            }

            foreach (var token in wheels)
            {
                if (!(token is JObject wheel))
                {
                    continue;
                }

                var flag = wheel["IsDynamic"];
                var wasDynamic = flag != null && flag.Type == JTokenType.Boolean && flag.Value<bool>()
                    && wheel[nameof(RandomiserWheel.Population)] is JObject;
                wheel.Remove("IsDynamic");
                if (wheel[nameof(RandomiserWheel.MembershipPolicy)] == null)
                {
                    wheel[nameof(RandomiserWheel.MembershipPolicy)] =
                        (int)(wasDynamic ? MembershipPolicy.StrictCriteria : MembershipPolicy.ManualSnapshot);
                }

                if (wheel[nameof(RandomiserWheel.PinnedGameIds)] == null)
                {
                    wheel[nameof(RandomiserWheel.PinnedGameIds)] = new JArray();
                }

                if (wheel[nameof(RandomiserWheel.ExcludedGameIds)] == null)
                {
                    wheel[nameof(RandomiserWheel.ExcludedGameIds)] = new JArray();
                }
            }
        }
    }
}
