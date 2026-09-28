using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using NUnit.Framework;

namespace NzbWebDAV.Tests;

[NonParallelizable]
public class DuplicateUploadTests
{
    private string _directory = null!;
    private string? _originalConfigPath;
    private string? _originalApiKey;

    [OneTimeSetUp]
    public async Task SetUpDatabase()
    {
        _directory = Directory.CreateTempSubdirectory("nzbdav-upload-tests-").FullName;
        _originalConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        _originalApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("CONFIG_PATH", _directory);
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");
        await using var db = new DavDatabaseContext();
        await db.Database.EnsureCreatedAsync();
        // These API tests exercise queue notification, never background processing or providers.
        SigtermUtil.Cancel();
    }

    [OneTimeTearDown]
    public void CleanUpDatabase()
    {
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _originalConfigPath);
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", _originalApiKey);
        Directory.Delete(_directory, recursive: true);
    }

    private static async Task Seed(string filename, string category = "test")
    {
        await using var db = new DavDatabaseContext();
        db.QueueItems.Add(new QueueItem
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
            FileName = filename, Category = category, JobName = "Test import"
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task ExistingDuplicateReturnsConflictAndKeepsExistingItem()
    {
        const string filename = "existing.nzb";
        await Seed(filename);
        await using var db = new DavDatabaseContext();
        using var stream = new ObservedStream(() => throw new AssertionException("Duplicate upload should not be copied"));
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?mode=addfile&cat=test&apikey=test-api-key");
        context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new FormFileCollection
            {
                new FormFile(stream, 0, stream.Length, "nzbFile", filename) { Headers = new HeaderDictionary() }
            });
        // Duplicates must return before touching the queue worker or websocket manager.
        var controller = new SabApiController(new DavDatabaseClient(db), new ConfigManager(), null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.HandleApiRequests();
        Assert.That(result, Is.TypeOf<ConflictObjectResult>());
        var body = (SabBaseResponse)((ConflictObjectResult)result).Value!;
        Assert.That(body.Status, Is.False);
        Assert.That(body.Error, Does.Contain(filename).And.Contain("already queued").And.Contain("test"));
        Assert.That(await db.QueueItems.CountAsync(x => x.FileName == filename), Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentDuplicateIsTranslatedAndLosingBlobIsRemoved()
    {
        var beforeBlobs = GetBlobFiles();
        const string filename = "concurrent.nzb";
        await using var db = new DavDatabaseContext();
        // Simulate the other request winning after the precheck but before this request saves.
        var stream = new ObservedStream(() => Seed(filename));
        var controller = new AddFileController(new DefaultHttpContext(), new DavDatabaseClient(db),
            null!, new ConfigManager(), new WebsocketManager());

        var error = Assert.ThrowsAsync<DuplicateQueuedNzbException>(() => controller.AddFileAsync(new AddFileRequest
        {
            FileName = filename, Category = "test", NzbFileStream = stream
        }));

        Assert.That(error!.InnerException, Is.TypeOf<DbUpdateException>());
        Assert.That(stream.WasDisposed, Is.True);
        await using var check = new DavDatabaseContext();
        Assert.That(await check.QueueItems.CountAsync(x => x.FileName == filename), Is.EqualTo(1));
        Assert.That(await check.NzbNames.CountAsync(x => x.FileName == filename), Is.Zero);
        Assert.That(GetBlobFiles(), Is.EquivalentTo(beforeBlobs));
    }

    private string[] GetBlobFiles()
    {
        var blobs = Path.Combine(_directory, "blobs");
        return Directory.Exists(blobs) ? Directory.GetFiles(blobs, "*", SearchOption.AllDirectories) : [];
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OptionalBackupFailureStillReturnsSuccessAndRetainsInternalNzb(bool emptyLocation)
    {
        var filename = $"backup-{emptyLocation}.nzb";
        var location = Path.Combine(_directory, "blocked-backup");
        await File.WriteAllTextAsync(location, "not a directory");
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = "api.nzb-backup-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "api.nzb-backup-location", ConfigValue = emptyLocation ? "" : location }
        ]);
        var websocket = new WebsocketManager();
        using var queue = new QueueManager(null!, config, websocket);
        using var stream = new ObservedStream(() => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?mode=addfile&cat=test&apikey=test-api-key");
        context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new FormFileCollection
            {
                new FormFile(stream, 0, stream.Length, "nzbFile", filename) { Headers = new HeaderDictionary() }
            });
        await using var db = new DavDatabaseContext();
        var controller = new SabApiController(new DavDatabaseClient(db), config, queue, websocket)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.HandleApiRequests();

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        var response = (AddFileResponse)((OkObjectResult)result).Value!;
        Assert.That(response.Status, Is.True);
        var item = await db.QueueItems.SingleAsync(x => x.FileName == filename);
        Assert.That(response.NzoIds, Does.Contain(item.Id.ToString()));
        Assert.That(await db.NzbNames.AnyAsync(x => x.Id == item.Id), Is.True);
        await using var blob = BlobStore.ReadBlob(item.Id);
        Assert.That(blob, Is.Not.Null);
        using var reader = new StreamReader(blob!);
        Assert.That(await reader.ReadToEndAsync(), Does.Contain("<nzb>"));
    }

    [Test]
    public async Task RequiredInternalStorageFailureStillRejectsUpload()
    {
        await using var db = new DavDatabaseContext();
        var controller = new AddFileController(new DefaultHttpContext(), new DavDatabaseClient(db),
            null!, new ConfigManager(), new WebsocketManager());
        using var source = new ObservedStream(() => throw new IOException("Simulated required storage failure"));
        Assert.ThrowsAsync<IOException>(() => controller.AddFileAsync(new AddFileRequest
        {
            FileName = "failed-storage.nzb", Category = "test", NzbFileStream = source
        }));
        Assert.That(await db.QueueItems.AnyAsync(x => x.FileName == "failed-storage.nzb"), Is.False);
    }

    [Test]
    public async Task SameFilenameInDifferentCategoriesIsAllowed()
    {
        await Seed("categories.nzb", "first");
        await Seed("categories.nzb", "second");
        await using var db = new DavDatabaseContext();
        Assert.That(await db.QueueItems.CountAsync(x => x.FileName == "categories.nzb"), Is.EqualTo(2));
    }

    [TestCase("UNIQUE constraint failed: NzbNames.Id", 2067)]
    [TestCase("UNIQUE constraint failed: QueueItems.Id", 1555)]
    [TestCase("database is locked", 5)]
    public void UnrelatedDatabaseFailuresAreNotReportedAsDuplicates(string message, int code)
    {
        var error = new DbUpdateException("Save failed", new SqliteException(message, code & 255, code));
        Assert.That(DuplicateQueuedNzbException.IsQueueDuplicate(error), Is.False);
    }

    private sealed class ObservedStream(Func<Task> beforeCopy)
        : MemoryStream(Encoding.UTF8.GetBytes("<nzb><file><segments><segment bytes=\"42\">test</segment></segments></file></nzb>"))
    {
        public bool WasDisposed { get; private set; }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await beforeCopy();
            await base.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
