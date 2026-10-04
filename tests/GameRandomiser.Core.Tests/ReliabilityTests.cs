using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameRandomiser.Core.Diagnostics;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Persistence;
using GameRandomiser.Core.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GameRandomiser.Core.Tests
{
    /// <summary>Data safety: migrations, failed writes, backup recovery and save-failure handling.</summary>
    public class ReliabilityTests
    {
        private static readonly Guid WheelId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid GameA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid GameB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        /// <summary>A data file exactly as version 1.0 of the extension wrote it.</summary>
        private static string V1Document(bool isDynamic = false) => $@"{{
  ""SchemaVersion"": 1,
  ""Wheels"": [
    {{
      ""Id"": ""{WheelId}"",
      ""Name"": ""Backlog"",
      ""Icon"": ""⭐"",
      ""GameIds"": [ ""{GameA}"", ""{GameB}"" ],
      ""SortMode"": 2,
      ""History"": [ {{ ""GameId"": ""{GameA}"", ""GameName"": ""Hades"", ""SpunAtUtc"": ""2026-01-05T10:30:00Z"" }} ],
      ""Population"": {{ ""Source"": 12, ""ItemIds"": [], ""Amount"": null, ""Description"": ""Installed"" }},
      ""IsDynamic"": {(isDynamic ? "true" : "false")},
      ""CreatedUtc"": ""2026-01-01T08:00:00Z"",
      ""ModifiedUtc"": ""2026-01-02T08:00:00Z""
    }}
  ],
  ""ActiveWheelId"": ""{WheelId}""
}}";

        // ---- Migration and compatibility ----

        [Fact]
        public void V1File_LoadsUnchanged_AsManualSnapshot()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                File.WriteAllText(path, V1Document());

                var result = Build.FileStore(path).Load();

                Assert.Null(result.Warning);
                var wheel = Assert.Single(result.Data.Wheels);
                Assert.Equal(WheelId, wheel.Id);
                Assert.Equal("Backlog", wheel.Name);
                Assert.Equal("⭐", wheel.Icon);
                Assert.Equal(new[] { GameA, GameB }, wheel.GameIds);
                Assert.Equal(SortMode.Library, wheel.SortMode);
                Assert.Equal("Hades", wheel.History.Single().GameName);
                Assert.Equal(PopulationSource.Installed, wheel.Population.Source);
                Assert.Equal(WheelId, result.Data.ActiveWheelId);

                // Existing wheels keep their snapshot behaviour: nothing becomes dynamic by upgrading.
                Assert.Equal(MembershipPolicy.ManualSnapshot, wheel.MembershipPolicy);
                Assert.False(wheel.IsDynamic);
                Assert.Empty(wheel.PinnedGameIds);
                Assert.Empty(wheel.ExcludedGameIds);
                Assert.Null(wheel.Reroll);
            }
        }

        [Fact]
        public void V1File_IsUpgradedOnDiskOnce_WithTheOriginalBackedUp()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                var original = V1Document();
                File.WriteAllText(path, original);
                var store = new JsonFileStore(path, now: () => new DateTime(2026, 2, 3, 4, 5, 6));

                store.Load();

                var upgraded = JObject.Parse(File.ReadAllText(path));
                Assert.Equal(RandomiserData.CurrentSchemaVersion, (int)upgraded["SchemaVersion"]);
                Assert.Null(upgraded["Wheels"][0]["IsDynamic"]);
                Assert.Equal(original, File.ReadAllText(dir.File("data.pre-migration-v1-20260203-040506.json")));
                Assert.Equal(original, File.ReadAllText(path + ".bak"));

                // A second load finds a current file: no further migration, backup or write.
                var second = new JsonFileStore(path, now: () => new DateTime(2030, 1, 1));
                second.Load();
                Assert.Equal(0, second.WriteCount);
                Assert.DoesNotContain(Directory.GetFiles(dir.Path), f => f.Contains("20300101"));
            }
        }

        [Fact]
        public void V1DynamicFlag_WithCriteria_BecomesStrict()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                File.WriteAllText(path, V1Document(isDynamic: true));
                Assert.Equal(MembershipPolicy.StrictCriteria, Build.FileStore(path).Load().Data.Wheels.Single().MembershipPolicy);
            }
        }

        [Fact]
        public void Migration_IsDeterministic()
        {
            var first = JObject.Parse(V1Document());
            var second = JObject.Parse(V1Document());
            DataMigrator.CreateDefault().Migrate(first);
            DataMigrator.CreateDefault().Migrate(second);

            Assert.Equal(first.ToString(), second.ToString());
            Assert.False(DataMigrator.CreateDefault().Migrate(first));
        }

        [Fact]
        public void FailedMigration_NeverOverwritesTheOriginal()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                var original = V1Document();
                File.WriteAllText(path, original);
                var failing = new DataMigrator(targetVersion: 2).AddStep(1, doc =>
                {
                    doc["Wheels"] = "half migrated";
                    throw new InvalidOperationException("step failed");
                });
                var store = new JsonFileStore(path, failing, () => new DateTime(2026, 2, 3, 4, 5, 6));

                var result = store.Load();

                Assert.Empty(result.Data.Wheels);
                Assert.Contains("could not be upgraded", result.Warning);
                Assert.Equal(original, File.ReadAllText(path));
                Assert.Equal(original, File.ReadAllText(dir.File("data.migration-failed-20260203-040506.json")));
            }
        }

        // ---- Corruption and backup recovery ----

        [Fact]
        public void CorruptFile_IsRecoveredFromBackup_AndKeptAside()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                var store = Build.FileStore(path);
                store.Save(new RandomiserData { Wheels = { new RandomiserWheel { Name = "Kept", GameIds = { GameA } } } });
                store.Save(new RandomiserData { Wheels = { new RandomiserWheel { Name = "Latest" } } });
                const string Garbage = "{ \"Wheels\": [ { \"Name\": \"Latest\" ";
                File.WriteAllText(path, Garbage);

                var result = new JsonFileStore(path, now: () => new DateTime(2026, 2, 3, 4, 5, 6)).Load();

                // The .bak holds the save before the last one: recoverable data beats an empty reset.
                Assert.Equal("Kept", result.Data.Wheels.Single().Name);
                Assert.Equal(new[] { GameA }, result.Data.Wheels.Single().GameIds);
                Assert.Contains("restored from the last automatic backup", result.Warning);
                Assert.Equal(Garbage, File.ReadAllText(dir.File("data.corrupt-20260203-040506.json")));
            }
        }

        [Fact]
        public void CorruptFile_WithUnusableBackup_ResetsButKeepsTheOldFile()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                File.WriteAllText(path, "not json at all");
                File.WriteAllText(path + ".bak", "also not json");

                var result = Build.FileStore(path).Load();

                Assert.Empty(result.Data.Wheels);
                Assert.Contains("has been reset", result.Warning);
                Assert.Contains(Directory.GetFiles(dir.Path), f => f.Contains("data.corrupt-") && File.ReadAllText(f) == "not json at all");
            }
        }

        [Fact]
        public void StaleTempFile_FromAnInterruptedWrite_IsIgnored()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                var store = Build.FileStore(path);
                store.Save(new RandomiserData { Wheels = { new RandomiserWheel { Name = "Real" } } });
                File.WriteAllText(path + ".tmp", "{ \"Wheels\": [ { \"Name\": \"Half writ");

                Assert.Equal("Real", Build.FileStore(path).Load().Data.Wheels.Single().Name);

                store.Save(new RandomiserData { Wheels = { new RandomiserWheel { Name = "Next" } } });
                Assert.False(File.Exists(path + ".tmp"));
                Assert.Equal("Next", Build.FileStore(path).Load().Data.Wheels.Single().Name);
            }
        }

        // ---- Writes ----

        [Fact]
        public void UnchangedData_IsNotWrittenAgain()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                var store = Build.FileStore(path);
                var data = new RandomiserData { Wheels = { new RandomiserWheel { Name = "A" } } };

                store.Save(data);
                store.Save(data);
                store.Save(data);
                Assert.Equal(1, store.WriteCount);
                Assert.False(File.Exists(path + ".bak"));

                data.Wheels[0].Name = "B";
                store.Save(data);
                Assert.Equal(2, store.WriteCount);
            }
        }

        [Fact]
        public void FailedWrite_LeavesTheExistingFileIntact()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                var store = Build.FileStore(path);
                store.Save(new RandomiserData { Wheels = { new RandomiserWheel { Name = "Saved" } } });
                var before = File.ReadAllText(path);
                var changed = new RandomiserData { Wheels = { new RandomiserWheel { Name = "Not saved" } } };

                // Another process holding the file makes the final swap fail.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.ThrowsAny<IOException>(() => store.Save(changed));
                }

                Assert.Equal(before, File.ReadAllText(path));
                Assert.False(File.Exists(path + ".tmp"));

                // The same save succeeds once the file is free: a failure is never remembered as success.
                store.Save(changed);
                Assert.Equal("Not saved", Build.FileStore(path).Load().Data.Wheels.Single().Name);
            }
        }

        [Fact]
        public void UnreadableFile_IsNotOverwrittenBlindly()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                Build.FileStore(path).Save(new RandomiserData { Wheels = { new RandomiserWheel { Name = "Precious" } } });
                var before = File.ReadAllText(path);
                var store = new JsonFileStore(path, now: () => new DateTime(2026, 2, 3, 4, 5, 6));

                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var result = store.Load();
                    Assert.Empty(result.Data.Wheels);
                    Assert.Contains("have not been changed", result.Warning);

                    // It still can't be copied aside, so the store refuses to replace it.
                    Assert.ThrowsAny<IOException>(() => store.Save(new RandomiserData()));
                }

                Assert.Equal(before, File.ReadAllText(path));

                store.Save(new RandomiserData());
                Assert.Equal(before, File.ReadAllText(dir.File("data.unreadable-20260203-040506.json")));
            }
        }

        // ---- Validation ----

        [Fact]
        public void DuplicateAndEmptyGameIds_AreRemovedOnLoad()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                File.WriteAllText(path, $@"{{ ""SchemaVersion"": 2, ""Wheels"": [ {{ ""Name"": ""Dupes"",
                    ""GameIds"": [ ""{GameA}"", ""{GameA}"", ""{Guid.Empty}"", ""{GameB}"", ""{GameA}"" ] }} ] }}");

                Assert.Equal(new[] { GameA, GameB }, Build.FileStore(path).Load().Data.Wheels.Single().GameIds);
            }
        }

        [Fact]
        public void Sanitizer_RepairsMembershipAndProtectionFields()
        {
            var data = new RandomiserData
            {
                Wheels = new List<RandomiserWheel>
                {
                    new RandomiserWheel { Name = "No criteria", MembershipPolicy = MembershipPolicy.StrictCriteria },
                    new RandomiserWheel
                    {
                        Name = "Bad values",
                        MembershipPolicy = (MembershipPolicy)42,
                        Population = new PopulationSpec { Source = PopulationSource.PlaytimeLessThan, Amount = double.NaN, ItemIds = null },
                        PinnedGameIds = new List<Guid> { GameA, GameA, Guid.Empty },
                        ExcludedGameIds = null,
                        RefreshError = "   ",
                        Reroll = new RerollState
                        {
                            RecentWinnerIds = new List<Guid> { GameA, Guid.Empty, GameA },
                            RerollCount = -5,
                            SessionId = Guid.Empty,
                            CooldownUntilUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
                        }
                    },
                    new RandomiserWheel
                    {
                        Name = "Unknown rule",
                        MembershipPolicy = MembershipPolicy.CriteriaPlusPinned,
                        GameIds = { GameB },
                        Population = new PopulationSpec { Source = (PopulationSource)999, Description = "From the future" }
                    }
                }
            };

            var clean = DataSanitizer.Sanitize(data);

            Assert.Equal(MembershipPolicy.ManualSnapshot, clean.Wheels[0].MembershipPolicy);

            var bad = clean.Wheels[1];
            Assert.Equal(MembershipPolicy.ManualSnapshot, bad.MembershipPolicy);
            Assert.Null(bad.Population.Amount);
            Assert.NotNull(bad.Population.ItemIds);
            Assert.Equal(new[] { GameA }, bad.PinnedGameIds);
            Assert.Empty(bad.ExcludedGameIds);
            Assert.Null(bad.RefreshError);
            Assert.Equal(new[] { GameA }, bad.Reroll.RecentWinnerIds);
            Assert.Equal(0, bad.Reroll.RerollCount);
            Assert.Null(bad.Reroll.SessionId);
            Assert.Null(bad.Reroll.CooldownUntilUtc);

            // An unknown rule is never evaluated automatically, but nothing the user had is thrown away.
            var unknown = clean.Wheels[2];
            Assert.Equal(MembershipPolicy.ManualSnapshot, unknown.MembershipPolicy);
            Assert.Equal(new[] { GameB }, unknown.GameIds);
            Assert.Equal("From the future", unknown.Population.Description);
        }

        [Fact]
        public void MissingGameReferences_AreSkipped_ThenCleanedUp()
        {
            var catalog = new FakeCatalog();
            var present = catalog.Add("Present");
            var store = new InMemoryStore();
            store.Save(new RandomiserData
            {
                Wheels =
                {
                    new RandomiserWheel
                    {
                        Name = "Mixed",
                        GameIds = { GameA, present.Id },
                        PinnedGameIds = { GameA },
                        ExcludedGameIds = { GameB }
                    }
                }
            });
            var service = Build.Service(catalog, store);
            var wheel = service.Wheels.Single();

            Assert.Equal(new[] { present.Id }, service.ResolveEntries(wheel.Id).Select(g => g.Id));

            Assert.Equal(1, service.CleanupInvalidEntries());
            Assert.Equal(new[] { present.Id }, wheel.GameIds);
            Assert.Empty(wheel.PinnedGameIds);
            Assert.Empty(wheel.ExcludedGameIds);
        }

        [Fact]
        public void NewDefaults_ChangeNothingForExistingUsers()
        {
            var wheel = new RandomiserWheel();
            Assert.Equal(MembershipPolicy.ManualSnapshot, wheel.MembershipPolicy);
            Assert.False(wheel.IsDynamic);
            Assert.Null(wheel.Reroll);

            var options = new RerollProtectionOptions();
            Assert.False(options.Enabled);
            Assert.Equal(0, options.ExcludedWinnerCount);
        }

        // ---- Save failures in the service ----

        [Fact]
        public void SaveFailure_IsReported_NotThrown_AndRetriedWithTheNextChange()
        {
            var store = new FlakyStore { Fail = true };
            var service = new WheelService(store, new FakeCatalog(), new Abstractions.SeededRandomSource(1), new FixedClock());
            var failures = new List<Exception>();
            var changes = new List<WheelChangeKind>();
            service.SaveFailed += (s, e) => failures.Add(e.Exception);
            service.Changed += (s, e) => changes.Add(e.Kind);

            var wheel = service.CreateWheel("Unsaved");

            // The UI keeps working from memory and is told the truth about the save.
            Assert.Single(failures);
            Assert.True(service.HasUnsavedChanges);
            Assert.Equal(new[] { WheelChangeKind.WheelAdded }, changes);
            Assert.Equal("Unsaved", service.GetWheel(wheel.Id).Name);
            Assert.Equal(0, store.SaveCount);

            store.Fail = false;
            service.RenameWheel(wheel.Id, "Saved");

            Assert.False(service.HasUnsavedChanges);
            Assert.Null(service.LastSaveError);
            Assert.Equal("Saved", store.Saved.Wheels.Single().Name);
        }

        [Fact]
        public void Batch_SavesOnce_AndRaisesEveryChangeInOrder()
        {
            var catalog = new FakeCatalog();
            var game = catalog.Add("Hades");
            var store = new InMemoryStore();
            var service = Build.Service(catalog, store);
            var wheel = service.CreateWheel("Batch", new[] { game.Id });
            var history = new HistoryService(service);
            var before = store.SaveCount;
            var changes = new List<WheelChangeKind>();
            service.Changed += (s, e) => changes.Add(e.Kind);

            using (service.Batch())
            {
                history.Record(wheel.Id, game);
                service.RenameWheel(wheel.Id, "Renamed");
                Assert.Empty(changes);
            }

            Assert.Equal(before + 1, store.SaveCount);
            Assert.Equal(new[] { WheelChangeKind.HistoryChanged, WheelChangeKind.WheelRenamed }, changes);
        }

        // ---- Structured errors ----

        [Fact]
        public void ErrorLogLine_IsStructured_AndOmitsUserText()
        {
            var error = new RandomiserError(ErrorCategory.WheelRefresh, "refresh wheel", "Shown to the user",
                new InvalidOperationException("boom"), WheelId, "kept previous game list", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            var line = error.ToLogString();

            Assert.Contains("[WheelRefresh]", line);
            Assert.Contains("operation=\"refresh wheel\"", line);
            Assert.Contains("wheel=" + WheelId, line);
            Assert.Contains("exception=InvalidOperationException", line);
            Assert.Contains("recovery=\"kept previous game list\"", line);
            Assert.Contains("2026-01-02T03:04:05", line);
            Assert.DoesNotContain("Shown to the user", line);
        }

        [Fact]
        public void RefreshFailures_MapToActionableMessages()
        {
            Assert.Equal(UserMessages.InvalidPopulationRule,
                UserMessages.ForRefreshFailure(new InvalidPopulationRuleException("x"), out var rule));
            Assert.Equal(ErrorCategory.PopulationRule, rule);

            Assert.Equal(UserMessages.LibraryUnavailable,
                UserMessages.ForRefreshFailure(new LibraryUnavailableException("x"), out var library));
            Assert.Equal(ErrorCategory.GameResolution, library);

            Assert.Equal(UserMessages.RefreshFailed, UserMessages.ForRefreshFailure(new Exception("x"), out var other));
            Assert.Equal(ErrorCategory.WheelRefresh, other);
            Assert.Contains("previous game list has been retained", UserMessages.RefreshFailed);
            Assert.Contains("previous saved configuration has been preserved", UserMessages.SaveFailed);
        }
    }
}
