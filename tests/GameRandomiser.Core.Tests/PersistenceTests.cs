using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Persistence;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GameRandomiser.Core.Tests
{
    public class PersistenceTests
    {
        [Fact]
        public void MissingFile_LoadsEmptyData_WithoutWarning()
        {
            using (var dir = new TempDirectory())
            {
                var result = Build.FileStore(dir.File("data.json")).Load();
                Assert.Empty(result.Data.Wheels);
                Assert.Null(result.Warning);
            }
        }

        [Fact]
        public void RoundTrip_PreservesEverything()
        {
            using (var dir = new TempDirectory())
            {
                var store = Build.FileStore(dir.File("data.json"));
                var gameId = Guid.NewGuid();
                var when = new DateTime(2026, 9, 27, 10, 30, 0, DateTimeKind.Utc);
                var data = new RandomiserData
                {
                    Wheels =
                    {
                        new RandomiserWheel
                        {
                            Name = "Backlog",
                            Icon = "⭐",
                            GameIds = { gameId },
                            SortMode = SortMode.Library,
                            History = { new SpinHistoryEntry { GameId = gameId, GameName = "Hades", SpunAtUtc = when } },
                            Population = new PopulationSpec { Source = PopulationSource.PlaytimeLessThan, Amount = 2.5, Description = "Under 2.5h" }
                        }
                    }
                };
                data.ActiveWheelId = data.Wheels[0].Id;

                store.Save(data);
                var loaded = store.Load().Data;

                var wheel = Assert.Single(loaded.Wheels);
                Assert.Equal("Backlog", wheel.Name);
                Assert.Equal("⭐", wheel.Icon);
                Assert.Equal(new[] { gameId }, wheel.GameIds);
                Assert.Equal(SortMode.Library, wheel.SortMode);
                Assert.Equal(when, wheel.History.Single().SpunAtUtc);
                Assert.Equal(DateTimeKind.Utc, wheel.History.Single().SpunAtUtc.Kind);
                Assert.Equal(PopulationSource.PlaytimeLessThan, wheel.Population.Source);
                Assert.Equal(2.5, wheel.Population.Amount);
                Assert.Equal(data.ActiveWheelId, loaded.ActiveWheelId);
                Assert.Equal(RandomiserData.CurrentSchemaVersion, loaded.SchemaVersion);
            }
        }

        [Fact]
        public void Save_IsAtomic_AndKeepsBackup()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                var store = Build.FileStore(path);
                store.Save(new RandomiserData());
                store.Save(new RandomiserData { Wheels = { new RandomiserWheel { Name = "Second save" } } });

                Assert.True(File.Exists(path));
                Assert.True(File.Exists(path + ".bak"));
                Assert.False(File.Exists(path + ".tmp"));
            }
        }

        [Fact]
        public void CorruptFile_IsBackedUp_AndResetWithWarning()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                File.WriteAllText(path, "{ this is not json");
                var store = new JsonFileStore(path, now: () => new DateTime(2026, 9, 27, 8, 0, 0));

                var result = store.Load();

                Assert.Empty(result.Data.Wheels);
                Assert.NotNull(result.Warning);
                Assert.True(File.Exists(dir.File("data.corrupt-20260927-080000.json")));
            }
        }

        [Fact]
        public void WrongShape_IsTreatedAsCorrupt()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                File.WriteAllText(path, "{ \"Wheels\": \"should be an array\" }");
                var result = Build.FileStore(path).Load();
                Assert.NotNull(result.Warning);
                Assert.Empty(result.Data.Wheels);
            }
        }

        [Fact]
        public void EmptyFile_LoadsEmptyData()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                File.WriteAllText(path, "   ");
                var result = Build.FileStore(path).Load();
                Assert.Empty(result.Data.Wheels);
                Assert.Null(result.Warning);
            }
        }

        [Fact]
        public void NewerSchema_LoadsBestEffort_WithBackupAndWarning()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                File.WriteAllText(path, "{ \"SchemaVersion\": 99, \"Wheels\": [ { \"Name\": \"Future\", \"NewThing\": 1 } ] }");
                var result = Build.FileStore(path).Load();
                Assert.Equal("Future", result.Data.Wheels.Single().Name);
                Assert.Contains("newer version", result.Warning);
                Assert.Contains(Directory.GetFiles(dir.Path), f => f.Contains("newer-v99"));
            }
        }

        [Fact]
        public void Migrator_RunsStepsInOrder()
        {
            var calls = new List<int>();
            var migrator = new DataMigrator(targetVersion: 3)
                .AddStep(1, doc => { calls.Add(1); doc["Renamed"] = doc["Old"]; doc.Remove("Old"); })
                .AddStep(2, doc => calls.Add(2));
            var document = JObject.Parse("{ \"SchemaVersion\": 1, \"Old\": \"x\" }");

            Assert.True(migrator.Migrate(document));

            Assert.Equal(new[] { 1, 2 }, calls);
            Assert.Equal(3, (int)document["SchemaVersion"]);
            Assert.Equal("x", (string)document["Renamed"]);
            Assert.False(migrator.Migrate(document));
        }

        [Fact]
        public void Migrator_UnversionedDocument_IsVersionOne()
        {
            Assert.Equal(1, DataMigrator.ReadVersion(JObject.Parse("{}")));
        }

        [Fact]
        public void Migrator_MissingStep_Throws()
        {
            var migrator = new DataMigrator(targetVersion: 2);
            Assert.Throws<InvalidOperationException>(() => migrator.Migrate(JObject.Parse("{ \"SchemaVersion\": 1 }")));
        }

        [Fact]
        public void Sanitizer_RepairsInvalidData()
        {
            var duplicateId = Guid.NewGuid();
            var gameId = Guid.NewGuid();
            var data = new RandomiserData
            {
                ActiveWheelId = Guid.NewGuid(), // dangling
                Wheels = new List<RandomiserWheel>
                {
                    new RandomiserWheel { Id = duplicateId, Name = "  ", GameIds = new List<Guid> { gameId, gameId, Guid.Empty }, History = null },
                    new RandomiserWheel { Id = duplicateId, Name = "Second", GameIds = null, SortMode = (SortMode)42 },
                    null
                }
            };

            var clean = DataSanitizer.Sanitize(data);

            Assert.Equal(2, clean.Wheels.Count);
            Assert.NotEqual(clean.Wheels[0].Id, clean.Wheels[1].Id);
            Assert.Equal("Untitled wheel", clean.Wheels[0].Name);
            Assert.Equal(new[] { gameId }, clean.Wheels[0].GameIds);
            Assert.NotNull(clean.Wheels[0].History);
            Assert.NotNull(clean.Wheels[1].GameIds);
            Assert.Equal(SortMode.Alphabetical, clean.Wheels[1].SortMode);
            Assert.Equal(clean.Wheels[0].Id, clean.ActiveWheelId);
        }
    }
}
