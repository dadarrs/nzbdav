using System.Text;
using NzbWebDAV.Utils;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests;

[NonParallelizable]
public class NzbBackupTests
{
    private string _directory = null!;
    private ILogger _originalLogger = null!;
    private Logger _logger = null!;
    private readonly List<LogEvent> _events = [];

    [SetUp]
    public void SetUp()
    {
        _directory = Directory.CreateTempSubdirectory("nzbdav-backup-tests-").FullName;
        _events.Clear();
        _originalLogger = Log.Logger;
        Log.Logger = _logger = new LoggerConfiguration().WriteTo.Sink(new CaptureSink(_events)).CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        Log.Logger = _originalLogger;
        _logger.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task SuccessfulBackupsPreserveExistingFilesAndSourceOwnership()
    {
        foreach (var content in new[] { "first", "second" })
        {
            using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await NzbBackupUtil.TryBackupAsync(source, "example.nzb", "test", _directory);
            Assert.That(source.CanRead, Is.True);
        }

        Assert.That(await File.ReadAllTextAsync(Path.Combine(_directory, "test", "example.nzb")), Is.EqualTo("first"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(_directory, "test", "example (2).nzb")), Is.EqualTo("second"));
        Assert.That(_events, Is.Empty);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task MissingLocationWarnsWithoutThrowing(string? location)
    {
        using var source = new MemoryStream();
        await NzbBackupUtil.TryBackupAsync(source, "example.nzb", "test", location);
        Assert.That(_events.Single().Level, Is.EqualTo(LogEventLevel.Warning));
        Assert.That(_events.Single().RenderMessage(), Does.Contain("no backup directory"));
    }

    [Test]
    public async Task ConcurrentBackupsKeepEveryCopy()
    {
        var contents = Enumerable.Range(0, 12).Select(i => $"copy-{i}").ToArray();
        await Task.WhenAll(contents.Select(async content =>
        {
            using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await NzbBackupUtil.TryBackupAsync(source, "example.nzb", "test", _directory);
        }));

        var files = Directory.GetFiles(Path.Combine(_directory, "test"));
        Assert.That(files.Length, Is.EqualTo(contents.Length));
        Assert.That(await Task.WhenAll(files.Select(path => File.ReadAllTextAsync(path))), Is.EquivalentTo(contents));
        Assert.That(_events, Is.Empty);
    }

    [Test]
    public async Task CleanupFailureIsAlsoOnlyAWarning()
    {
        using var source = new FailingCopyStream(destination =>
        {
            // Replace the open destination with a directory to force File.Delete to fail.
            var path = ((FileStream)destination).Name;
            destination.Dispose();
            File.Delete(path);
            Directory.CreateDirectory(path);
        });
        await NzbBackupUtil.TryBackupAsync(source, "example.nzb", "test", _directory);
        Assert.That(_events.Count, Is.EqualTo(2));
        Assert.That(_events.All(x => x.Level == LogEventLevel.Warning), Is.True);
        Assert.That(_events[1].RenderMessage(), Does.Contain("Could not remove partial NZB backup"));
    }

    [Test]
    public async Task UnusableLocationWarnsWithoutTouchingExistingFile()
    {
        var blocked = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "keep");
        using var source = new MemoryStream();
        await NzbBackupUtil.TryBackupAsync(source, "example.nzb", "test", blocked);
        Assert.That(await File.ReadAllTextAsync(blocked), Is.EqualTo("keep"));
        Assert.That(_events.Single().Level, Is.EqualTo(LogEventLevel.Warning));
        Assert.That(_events.Single().Exception, Is.Not.Null);
    }

    [Test]
    public async Task MidCopyFailureRemovesOnlyTheNewPartialBackup()
    {
        var category = Directory.CreateDirectory(Path.Combine(_directory, "test")).FullName;
        var existing = Path.Combine(category, "example.nzb");
        await File.WriteAllTextAsync(existing, "keep");
        using var source = new FailingCopyStream();

        await NzbBackupUtil.TryBackupAsync(source, "example.nzb", "test", _directory);

        Assert.That(await File.ReadAllTextAsync(existing), Is.EqualTo("keep"));
        Assert.That(Directory.GetFiles(category), Is.EquivalentTo(new[] { existing }));
        Assert.That(_events.Single().Exception, Is.TypeOf<IOException>());
        Assert.That(_events.Single().RenderMessage(), Does.Contain("Import will continue"));
    }

    private sealed class FailingCopyStream(Action<Stream>? beforeFailure = null) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(Encoding.UTF8.GetBytes("partial"), cancellationToken);
            beforeFailure?.Invoke(destination);
            throw new IOException("Simulated mid-copy storage failure");
        }
    }

    private sealed class CaptureSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
