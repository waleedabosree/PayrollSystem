using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Payroll.Api.Storage;

public sealed record StoredBlob(string Container, string Name, string ETag);

public interface IPayrollBlobStore
{
    Task<StoredBlob> UploadAsync(
        string blobName, Stream content, IDictionary<string, string> metadata, CancellationToken cancellationToken);

    Task DeleteAsync(string blobName, CancellationToken cancellationToken);
}

public sealed class BlobStorageOptions
{
    public const string Section = "Storage";

    /// <summary>"UseDevelopmentStorage=true" targets Azurite; an Azure connection string targets the cloud.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Used with managed identity (DefaultAzureCredential) when ConnectionString is empty, e.g. "stpayrolltbgvkn".</summary>
    public string AccountName { get; set; } = string.Empty;

    public string ContainerName { get; set; } = "payroll-uploads";
}

public sealed class PayrollBlobStore : IPayrollBlobStore
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public PayrollBlobStore(BlobServiceClient service, IOptions<BlobStorageOptions> options)
    {
        _container = service.GetBlobContainerClient(options.Value.ContainerName);
    }

    public async Task<StoredBlob> UploadAsync(
        string blobName, Stream content, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);

        var blob = _container.GetBlobClient(blobName);

        // IfNoneMatch=* makes this create-only: the name embeds a fresh GUID, so a collision
        // means a bug or replay -- failing loudly beats silently replacing another batch's file.
        var response = await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                Metadata = metadata,
                HttpHeaders = new BlobHttpHeaders { ContentType = "text/csv" },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            },
            cancellationToken);

        return new StoredBlob(_container.Name, blobName, response.Value.ETag.ToString());
    }

    public async Task DeleteAsync(string blobName, CancellationToken cancellationToken) =>
        await _container.DeleteBlobIfExistsAsync(blobName, cancellationToken: cancellationToken);

    // Private by default (no public access). Created lazily so the API still boots when
    // storage is briefly unavailable; the failure then surfaces on the upload that needs it.
    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_initialised) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialised) return;
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
            _initialised = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
